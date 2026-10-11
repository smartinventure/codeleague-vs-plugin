import { execFile } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';

/**
 * Thin, read-only wrapper around the git CLI.
 *
 * Every command runs with --no-optional-locks so that background status checks never take
 * the index lock and never interfere with git operations the developer is running.
 */

export interface GitResult {
    code: number;
    stdout: string;
    stderr: string;
}

export type GitOperation = 'merge' | 'rebase' | 'cherry-pick' | 'revert';

export interface HeadInfo {
    /** Branch name, or "(detached)" when HEAD is detached outside of a rebase. */
    branch: string;
    /** Commit SHA of HEAD, or "" for an unborn branch. */
    sha: string;
    /** Set while a merge / rebase / cherry-pick / revert is in progress. */
    operation?: GitOperation;
}

export interface StatusEntry {
    /** Repository-relative path with forward slashes. */
    path: string;
    /** Original path for renames / copies. */
    origPath?: string;
    /** Index status column of `git status --porcelain`. */
    x: string;
    /** Work-tree status column of `git status --porcelain`. */
    y: string;
}

export interface ChangedFile {
    path: string;
    origPath?: string;
}

export const DETACHED_BRANCH = '(detached)';

export class Git {
    constructor(private readonly gitPath: string = 'git') {}

    run(cwd: string, args: string[]): Promise<GitResult> {
        return new Promise(resolve => {
            execFile(
                this.gitPath,
                ['--no-optional-locks', '-c', 'core.quotepath=false', ...args],
                {
                    cwd,
                    maxBuffer: 256 * 1024 * 1024,
                    windowsHide: true,
                    env: { ...process.env, GIT_OPTIONAL_LOCKS: '0', GIT_TERMINAL_PROMPT: '0', LC_ALL: 'C' },
                },
                (err, stdout, stderr) => {
                    const code = err ? (typeof (err as { code?: unknown }).code === 'number' ? (err as { code: number }).code : -1) : 0;
                    resolve({ code, stdout: stdout ?? '', stderr: stderr ?? '' });
                }
            );
        });
    }

    private async runOrThrow(cwd: string, args: string[]): Promise<string> {
        const r = await this.run(cwd, args);
        if (r.code !== 0) {
            throw new Error(`git ${args.join(' ')} failed (${r.code}): ${r.stderr.trim()}`);
        }
        return r.stdout;
    }

    /** Returns the work-tree root (forward slashes) containing `dir`, or undefined if not in a repository. */
    async getRepositoryRoot(dir: string): Promise<string | undefined> {
        const r = await this.run(dir, ['rev-parse', '--show-toplevel']);
        return r.code === 0 ? r.stdout.trim() : undefined;
    }

    async getGitDir(root: string): Promise<string> {
        return (await this.runOrThrow(root, ['rev-parse', '--absolute-git-dir'])).trim();
    }

    async getHead(root: string, gitDir: string): Promise<HeadInfo> {
        const [sym, sha] = await Promise.all([
            this.run(root, ['symbolic-ref', '-q', '--short', 'HEAD']),
            this.run(root, ['rev-parse', '-q', '--verify', 'HEAD']),
        ]);
        const operation = detectOperation(gitDir);
        let branch = sym.code === 0 ? sym.stdout.trim() : '';
        if (!branch && operation === 'rebase') {
            branch = readRebaseHeadName(gitDir) ?? '';
        }
        return {
            branch: branch || DETACHED_BRANCH,
            sha: sha.code === 0 ? sha.stdout.trim() : '',
            operation,
        };
    }

    async getStatus(root: string): Promise<StatusEntry[]> {
        const out = await this.runOrThrow(root, ['status', '--porcelain=v1', '-z', '--untracked-files=all']);
        return parseStatus(out);
    }

    async getUserEmail(root: string): Promise<string> {
        return (await this.run(root, ['config', 'user.email'])).stdout.trim();
    }

    /**
     * Returns the files changed by the commits between `oldSha` and `newSha`, but only if those commits
     * are a plain linear continuation of `oldSha` made by the local user (i.e. ordinary commits made
     * between two refreshes). Pulls of other people's work, merges, rebases, resets and amends return [].
     */
    async getOwnCommitChanges(root: string, oldSha: string, newSha: string, maxCommits = 50): Promise<ChangedFile[]> {
        const log = await this.run(root, ['log', `--max-count=${maxCommits + 1}`, '--format=%H%x09%P%x09%ce', `${oldSha}..${newSha}`]);
        if (log.code !== 0) {
            return [];
        }
        const commits = log.stdout
            .split('\n')
            .filter(l => l.trim())
            .map(l => {
                const [sha, parents, email] = l.split('\t');
                return { sha, parents: (parents ?? '').split(' ').filter(Boolean), email: (email ?? '').trim().toLowerCase() };
            });
        if (commits.length === 0 || commits.length > maxCommits) {
            return [];
        }
        // git log lists newest first: verify the chain newSha -> ... -> oldSha is linear.
        let expected = newSha;
        for (const c of commits) {
            if (c.sha !== expected || c.parents.length !== 1) {
                return [];
            }
            expected = c.parents[0];
        }
        if (expected !== oldSha) {
            return [];
        }
        const userEmail = (await this.getUserEmail(root)).toLowerCase();
        if (userEmail && commits.some(c => c.email !== userEmail)) {
            return [];
        }
        const diff = await this.run(root, ['diff', '--name-status', '-z', '-M', oldSha, newSha]);
        return diff.code === 0 ? parseNameStatus(diff.stdout) : [];
    }
}

/** Parses `git status --porcelain=v1 -z` output. */
export function parseStatus(out: string): StatusEntry[] {
    const parts = out.split('\0');
    const entries: StatusEntry[] = [];
    for (let i = 0; i < parts.length; i++) {
        const p = parts[i];
        if (p.length < 4) {
            continue;
        }
        const x = p[0];
        const y = p[1];
        const entry: StatusEntry = { path: p.slice(3), x, y };
        if (x === 'R' || x === 'C' || y === 'R' || y === 'C') {
            entry.origPath = parts[++i];
        }
        entries.push(entry);
    }
    return entries;
}

/** Parses `git diff --name-status -z` output. */
export function parseNameStatus(out: string): ChangedFile[] {
    const parts = out.split('\0');
    const files: ChangedFile[] = [];
    for (let i = 0; i < parts.length; i++) {
        const code = parts[i];
        if (!code) {
            continue;
        }
        if (code[0] === 'R' || code[0] === 'C') {
            const origPath = parts[++i];
            const newPath = parts[++i];
            if (newPath) {
                files.push({ path: newPath, origPath });
            }
        } else {
            const p = parts[++i];
            if (p) {
                files.push({ path: p });
            }
        }
    }
    return files;
}

export function isConflicted(e: StatusEntry): boolean {
    return e.x === 'U' || e.y === 'U' || (e.x === 'A' && e.y === 'A') || (e.x === 'D' && e.y === 'D');
}

export function detectOperation(gitDir: string): GitOperation | undefined {
    const has = (name: string) => fs.existsSync(path.join(gitDir, name));
    if (has('rebase-merge') || has('rebase-apply')) {
        return 'rebase';
    }
    if (has('MERGE_HEAD')) {
        return 'merge';
    }
    if (has('CHERRY_PICK_HEAD')) {
        return 'cherry-pick';
    }
    if (has('REVERT_HEAD')) {
        return 'revert';
    }
    return undefined;
}

function readRebaseHeadName(gitDir: string): string | undefined {
    for (const dir of ['rebase-merge', 'rebase-apply']) {
        try {
            const ref = fs.readFileSync(path.join(gitDir, dir, 'head-name'), 'utf8').trim();
            if (ref && ref !== 'detached HEAD') {
                return ref.replace(/^refs\/heads\//, '');
            }
        } catch {
            // not present
        }
    }
    return undefined;
}
