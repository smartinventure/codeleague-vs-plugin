import * as path from 'path';
import * as vscode from 'vscode';
import { Git } from './core/git';
import { RepoTracker } from './core/repoTracker';
import { normalizeRoot, StateStore } from './core/stateStore';

/** Minimal subset of the built-in Git extension API (extensions/git/src/api/git.d.ts). */
interface GitRepositoryLike {
    readonly rootUri: vscode.Uri;
    readonly state: { onDidChange: vscode.Event<void> };
}
interface GitApiLike {
    readonly git: { readonly path: string };
    readonly repositories: GitRepositoryLike[];
    readonly onDidOpenRepository: vscode.Event<GitRepositoryLike>;
}

const REFRESH_DEBOUNCE_MS = 400;
const SAFETY_REFRESH_MS = 30_000;
/** Paths whose changes alone never need a refresh (build output etc.); git status remains the source of truth. */
const NOISE = /[\\/](node_modules|bin|obj|out|dist|\.vs|\.idea)[\\/]|[\\/]\.git[\\/](objects|logs|lfs|modules|hooks)[\\/]/i;

interface Entry {
    tracker: RepoTracker;
    timer?: NodeJS.Timeout;
    disposables: vscode.Disposable[];
}

/** Discovers git repositories in the workspace and keeps one RepoTracker per repository up to date. */
export class TrackerManager implements vscode.Disposable {
    private readonly entries = new Map<string, Entry>();
    private readonly disposables: vscode.Disposable[] = [];
    private readonly changeEmitter = new vscode.EventEmitter<void>();
    private git = new Git();
    private interval: NodeJS.Timeout | undefined;

    readonly onDidChange = this.changeEmitter.event;

    get trackers(): RepoTracker[] {
        return [...this.entries.values()].map(e => e.tracker).sort((a, b) => a.name.localeCompare(b.name));
    }

    async start(): Promise<void> {
        const gitApi = await this.getGitApi();
        if (gitApi) {
            this.git = new Git(gitApi.git.path || 'git');
            gitApi.repositories.forEach(r => this.addGitExtensionRepository(r));
            this.disposables.push(gitApi.onDidOpenRepository(r => this.addGitExtensionRepository(r)));
        }
        await this.discoverWorkspaceFolders();

        this.disposables.push(
            this.changeEmitter,
            vscode.workspace.onDidChangeWorkspaceFolders(() => this.discoverWorkspaceFolders()),
            vscode.workspace.onDidSaveTextDocument(doc => this.scheduleFor(doc.uri.fsPath)),
            vscode.workspace.onDidRenameFiles(e => {
                for (const { oldUri, newUri } of e.files) {
                    this.trackerFor(oldUri.fsPath)?.notifyRenamed(oldUri.fsPath, newUri.fsPath);
                }
            }),
            vscode.window.onDidChangeWindowState(s => s.focused && this.refreshAll())
        );
        this.interval = setInterval(() => this.refreshAll(), SAFETY_REFRESH_MS);
    }

    trackerFor(fsPath: string): RepoTracker | undefined {
        let best: RepoTracker | undefined;
        for (const { tracker } of this.entries.values()) {
            if (tracker.toRelative(fsPath) !== undefined && (!best || tracker.root.length > best.root.length)) {
                best = tracker;
            }
        }
        return best;
    }

    refreshAll(): Promise<void> {
        return Promise.all(this.trackers.map(t => t.refresh())).then(() => undefined);
    }

    dispose(): void {
        if (this.interval) {
            clearInterval(this.interval);
        }
        for (const e of this.entries.values()) {
            clearTimeout(e.timer);
            e.disposables.forEach(d => d.dispose());
        }
        this.entries.clear();
        this.disposables.forEach(d => d.dispose());
    }

    private async getGitApi(): Promise<GitApiLike | undefined> {
        try {
            const ext = vscode.extensions.getExtension<{ getAPI(version: 1): GitApiLike }>('vscode.git');
            if (!ext) {
                return undefined;
            }
            const exports = ext.isActive ? ext.exports : await ext.activate();
            return exports.getAPI(1);
        } catch {
            return undefined; // git extension disabled: fall back to workspace folder discovery
        }
    }

    private addGitExtensionRepository(repo: GitRepositoryLike): void {
        void this.addRepository(repo.rootUri.fsPath).then(entry => {
            entry?.disposables.push(repo.state.onDidChange(() => this.schedule(entry)));
        });
    }

    private async discoverWorkspaceFolders(): Promise<void> {
        for (const folder of vscode.workspace.workspaceFolders ?? []) {
            await this.addRepository(folder.uri.fsPath);
        }
    }

    private async addRepository(dir: string): Promise<Entry | undefined> {
        const root = await this.git.getRepositoryRoot(dir);
        if (!root) {
            return undefined;
        }
        const key = normalizeRoot(root);
        const existing = this.entries.get(key);
        if (existing) {
            return existing;
        }
        const tracker = new RepoTracker(path.normalize(root), this.git, StateStore.forRepository(root));
        const entry: Entry = { tracker, disposables: [] };
        this.entries.set(key, entry);

        const watcher = vscode.workspace.createFileSystemWatcher(new vscode.RelativePattern(vscode.Uri.file(tracker.root), '**/*'));
        const onFsEvent = (uri: vscode.Uri) => {
            if (!NOISE.test(uri.fsPath)) {
                this.schedule(entry);
            }
        };
        entry.disposables.push(
            watcher,
            watcher.onDidChange(onFsEvent),
            watcher.onDidCreate(onFsEvent),
            watcher.onDidDelete(onFsEvent),
            tracker.onDidChange(() => this.changeEmitter.fire())
        );
        this.changeEmitter.fire();
        await tracker.refresh();
        return entry;
    }

    private scheduleFor(fsPath: string): void {
        const tracker = this.trackerFor(fsPath);
        const entry = tracker && this.entries.get(normalizeRoot(tracker.root));
        if (entry) {
            this.schedule(entry);
        }
    }

    private schedule(entry: Entry): void {
        clearTimeout(entry.timer);
        entry.timer = setTimeout(() => void entry.tracker.refresh(), REFRESH_DEBOUNCE_MS);
    }
}
