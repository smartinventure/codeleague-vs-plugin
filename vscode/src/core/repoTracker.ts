import * as fs from 'fs';
import * as path from 'path';
import { ChangedFile, Git, HeadInfo, isConflicted, StatusEntry } from './git';
import { BranchState, newBaseline, PersistedState, StateStore } from './stateStore';

export type FileStatus = 'modified' | 'added' | 'deleted' | 'renamed' | 'untracked' | 'conflicted' | 'committed';

export interface TrackedFileView {
    /** Repository-relative path with forward slashes. */
    path: string;
    absolutePath: string;
    status: FileStatus;
    firstSeen: string;
    lastSeen: string;
}

/**
 * Tracks files changed in one repository work tree, per branch, independently of commits.
 *
 * Algorithm (identical in the Visual Studio extension, see docs/ARCHITECTURE.md):
 *  - Every refresh reads HEAD and `git status`. Every dirty file is a candidate.
 *  - If HEAD moved on the same branch through the developer's own linear commits, the files of
 *    those commits are candidates too (covers "edit + commit" between two refreshes).
 *  - While a merge / rebase / cherry-pick / revert is in progress, only work-tree changes and conflicts
 *    are candidates, never changes that were merely staged by git from another branch.
 *  - A candidate is skipped if its fingerprint (size + mtime) equals the fingerprint recorded in the
 *    baseline snapshot, i.e. it was already dirty at Reset and has not been touched since.
 *  - Files changed by a checkout never appear in `git status`, so branch switches don't add files.
 */
export class RepoTracker {
    private readonly listeners: Array<() => void> = [];
    private gitDir: string | undefined;
    private head: HeadInfo | undefined;
    private liveStatus = new Map<string, FileStatus>();
    private queue: Promise<unknown> = Promise.resolve();
    private pendingRefresh: Promise<void> | undefined;
    private lastSignature = '';
    private _error: string | undefined;

    constructor(
        readonly root: string,
        private readonly git: Git,
        private readonly store: StateStore,
        private readonly now: () => Date = () => new Date()
    ) {}

    get name(): string {
        return path.basename(this.root);
    }

    get branch(): string | undefined {
        return this.head?.branch;
    }

    get operation(): string | undefined {
        return this.head?.operation;
    }

    get error(): string | undefined {
        return this._error;
    }

    onDidChange(listener: () => void): { dispose(): void } {
        this.listeners.push(listener);
        return {
            dispose: () => {
                const i = this.listeners.indexOf(listener);
                if (i >= 0) {
                    this.listeners.splice(i, 1);
                }
            },
        };
    }

    /** Re-scans the repository. Concurrent calls are coalesced; the returned promise resolves after a scan that started after the call. */
    refresh(): Promise<void> {
        if (!this.pendingRefresh) {
            this.pendingRefresh = this.enqueue(async () => {
                this.pendingRefresh = undefined;
                await this.doRefresh();
            });
        }
        return this.pendingRefresh;
    }

    /**
     * Clears the list for the current branch and makes all currently dirty files part of the new baseline.
     * Only the extension's state file is written; files and git are never touched.
     */
    reset(): Promise<void> {
        return this.enqueue(async () => {
            const head = await this.readHead();
            const status = await this.git.getStatus(this.root);
            const snapshot: Record<string, string> = {};
            for (const e of status) {
                snapshot[e.path] = this.fingerprint(e.path);
            }
            this.store.update(s => {
                s.branches[head.branch] = { baseline: newBaseline(snapshot, this.now()), files: {}, lastHead: head.sha };
                return true;
            });
            this.head = head;
            this.setLiveStatus(status);
            this.emitIfChanged(true);
        });
    }

    /** Removes single files (or folders, by prefix) from the current branch's list. */
    remove(relativePaths: string[]): void {
        const branch = this.head?.branch;
        if (!branch) {
            return;
        }
        this.store.update(s => {
            const b = s.branches[branch];
            if (!b) {
                return false;
            }
            let changed = false;
            for (const key of Object.keys(b.files)) {
                if (relativePaths.some(p => key === p || key.startsWith(p.replace(/\/?$/, '/')))) {
                    delete b.files[key];
                    changed = true;
                }
            }
            return changed;
        });
        this.emitIfChanged();
    }

    /** Called by the IDE when it renamed/moved a file or folder, so the entry follows instead of being duplicated. */
    notifyRenamed(oldAbsolute: string, newAbsolute: string): void {
        const oldRel = this.toRelative(oldAbsolute);
        const newRel = this.toRelative(newAbsolute);
        const branch = this.head?.branch;
        if (!oldRel || !newRel || !branch) {
            return;
        }
        this.store.update(s => {
            const b = s.branches[branch];
            if (!b) {
                return false;
            }
            let changed = false;
            for (const key of Object.keys(b.files)) {
                let target: string | undefined;
                if (key === oldRel) {
                    target = newRel;
                } else if (key.startsWith(oldRel + '/')) {
                    target = newRel + key.slice(oldRel.length);
                }
                if (target !== undefined) {
                    b.files[target] ??= b.files[key];
                    delete b.files[key];
                    changed = true;
                }
            }
            return changed;
        });
        this.emitIfChanged();
    }

    /** The tracked files of the current branch, sorted by path. */
    getFiles(): TrackedFileView[] {
        const branch = this.head?.branch;
        const b = branch ? this.store.state.branches[branch] : undefined;
        if (!b) {
            return [];
        }
        return Object.keys(b.files)
            .sort((a, c) => a.localeCompare(c, undefined, { sensitivity: 'base' }))
            .map(p => {
                const absolutePath = path.join(this.root, p);
                return {
                    path: p,
                    absolutePath,
                    status: this.liveStatus.get(p) ?? (fs.existsSync(absolutePath) ? 'committed' : 'deleted'),
                    firstSeen: b.files[p].firstSeen,
                    lastSeen: b.files[p].lastSeen,
                };
            });
    }

    toRelative(absolute: string): string | undefined {
        const rel = path.relative(this.root, absolute);
        if (!rel || rel.startsWith('..') || path.isAbsolute(rel)) {
            return undefined;
        }
        return rel.replace(/\\/g, '/');
    }

    private async doRefresh(): Promise<void> {
        let changed = this.store.reloadIfChanged();
        try {
            const previous = this.head;
            const head = await this.readHead();
            const status = await this.git.getStatus(this.root);

            const candidates: ChangedFile[] = [];
            for (const e of status) {
                if (isUntrackedTooling(e)) {
                    continue;
                }
                if (head.operation && e.y === ' ' && !isConflicted(e)) {
                    // Staged by git itself while merging/rebasing changes from elsewhere.
                    continue;
                }
                candidates.push({ path: e.path, origPath: e.origPath });
            }

            const branchState = this.store.state.branches[head.branch];
            const lastSha = previous?.branch === head.branch && !previous.operation ? previous.sha : branchState?.lastHead;
            if (!head.operation && lastSha && head.sha && lastSha !== head.sha) {
                candidates.push(...(await this.git.getOwnCommitChanges(this.root, lastSha, head.sha)));
            }

            const timestamp = this.now().toISOString();
            changed =
                this.store.update(s => {
                    const isNew = !s.branches[head.branch];
                    const b = this.ensureBranch(s, head.branch);
                    let dirty = isNew;
                    if (b.lastHead !== head.sha && !head.operation) {
                        b.lastHead = head.sha;
                        dirty = true;
                    }
                    for (const c of candidates) {
                        if (this.track(b, c, timestamp)) {
                            dirty = true;
                        }
                    }
                    return dirty;
                }) || changed;

            this.head = head;
            this.setLiveStatus(status);
            this._error = undefined;
        } catch (err) {
            this._error = err instanceof Error ? err.message : String(err);
            changed = true;
        }
        this.emitIfChanged(changed);
    }

    private track(b: BranchState, c: ChangedFile, timestamp: string): boolean {
        const fp = this.fingerprint(c.path);
        if (b.baseline.snapshot[c.path] === fp) {
            return false;
        }
        if (c.origPath && c.origPath !== c.path && b.files[c.origPath] && !b.files[c.path]) {
            b.files[c.path] = b.files[c.origPath];
            delete b.files[c.origPath];
            b.files[c.path].fingerprint = fp;
            b.files[c.path].lastSeen = timestamp;
            return true;
        }
        const existing = b.files[c.path];
        if (!existing) {
            b.files[c.path] = { firstSeen: timestamp, lastSeen: timestamp, fingerprint: fp };
            return true;
        }
        if (existing.fingerprint !== fp) {
            existing.fingerprint = fp;
            existing.lastSeen = timestamp;
            return true;
        }
        return false;
    }

    private ensureBranch(s: PersistedState, branch: string): BranchState {
        return (s.branches[branch] ??= { baseline: newBaseline({}, this.now()), files: {} });
    }

    private async readHead(): Promise<HeadInfo> {
        this.gitDir ??= await this.git.getGitDir(this.root);
        return this.git.getHead(this.root, this.gitDir);
    }

    private fingerprint(relativePath: string): string {
        try {
            const st = fs.statSync(path.join(this.root, relativePath));
            return `${st.size}:${Math.floor(st.mtimeMs)}`;
        } catch {
            return 'deleted';
        }
    }

    private setLiveStatus(status: StatusEntry[]): void {
        const map = new Map<string, FileStatus>();
        for (const e of status) {
            map.set(e.path, toFileStatus(e));
        }
        this.liveStatus = map;
    }

    private emitIfChanged(force = false): void {
        const signature = JSON.stringify([this.head?.branch, this.head?.operation, this._error, this.getFiles().map(f => [f.path, f.status])]);
        if (!force && signature === this.lastSignature) {
            return;
        }
        this.lastSignature = signature;
        for (const l of [...this.listeners]) {
            l();
        }
    }

    private enqueue<T>(fn: () => Promise<T>): Promise<T> {
        const p = this.queue.then(fn, fn);
        this.queue = p.catch(() => undefined);
        return p;
    }
}

/** Folders whose untracked files are IDE state or build output, never "files I am working on" (for repos with an incomplete .gitignore). */
const TOOLING_FOLDERS = new Set(['.vs', '.idea', 'bin', 'obj', 'node_modules']);

export function isUntrackedTooling(e: StatusEntry): boolean {
    return e.x === '?' && e.y === '?' && e.path.split('/').slice(0, -1).some(s => TOOLING_FOLDERS.has(s.toLowerCase()));
}

export function toFileStatus(e: StatusEntry): FileStatus {
    if (isConflicted(e)) {
        return 'conflicted';
    }
    if (e.x === '?' && e.y === '?') {
        return 'untracked';
    }
    if (e.x === 'D' || e.y === 'D') {
        return 'deleted';
    }
    if (e.x === 'R' || e.y === 'R') {
        return 'renamed';
    }
    if (e.x === 'A' || e.x === 'C') {
        return 'added';
    }
    return 'modified';
}
