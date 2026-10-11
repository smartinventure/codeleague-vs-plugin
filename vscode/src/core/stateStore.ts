import * as crypto from 'crypto';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

/**
 * Persistent tracking state for one repository work tree.
 *
 * The file format and location are shared with the Visual Studio extension, so a developer who uses
 * both IDEs sees the same list. See docs/ARCHITECTURE.md ("State file").
 */

export const STATE_VERSION = 1;

export interface TrackedFile {
    /** ISO timestamp when the file was first observed as changed after the baseline. */
    firstSeen: string;
    /** ISO timestamp of the latest observed modification. */
    lastSeen: string;
    /** Fingerprint ("size:mtimeMs" or "deleted") at the latest observation. */
    fingerprint?: string;
}

export interface Baseline {
    id: string;
    createdAt: string;
    /** Fingerprints of files that were already dirty when the baseline was created. */
    snapshot: Record<string, string>;
}

export interface BranchState {
    baseline: Baseline;
    /** Tracked files keyed by repository-relative path (forward slashes). */
    files: Record<string, TrackedFile>;
    /** HEAD commit at the last refresh on this branch. */
    lastHead?: string;
}

export interface PersistedState {
    version: number;
    repositoryRoot: string;
    branches: Record<string, BranchState>;
}

export function defaultStorageDirectory(): string {
    let base: string;
    if (process.platform === 'win32') {
        base = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
    } else if (process.platform === 'darwin') {
        base = path.join(os.homedir(), 'Library', 'Application Support');
    } else {
        base = process.env.XDG_DATA_HOME || path.join(os.homedir(), '.local', 'share');
    }
    return path.join(base, 'CodeLeague', 'PersistentChanges');
}

/** Normalizes a repository root so both IDEs derive the same key: forward slashes, no trailing slash, lower-case on Windows. */
export function normalizeRoot(root: string, platform: NodeJS.Platform = process.platform): string {
    let n = root.replace(/\\/g, '/').replace(/\/+$/, '');
    if (platform === 'win32') {
        n = n.toLowerCase();
    }
    return n;
}

/** File name of the state file for a repository: "<folder>-<sha1(normalizedRoot)[0..16]>.json". */
export function stateFileName(root: string, platform: NodeJS.Platform = process.platform): string {
    const normalized = normalizeRoot(root, platform);
    const hash = crypto.createHash('sha1').update(normalized, 'utf8').digest('hex').slice(0, 16);
    const name = (normalized.split('/').pop() || 'repo').replace(/[^A-Za-z0-9._-]/g, '_');
    return `${name}-${hash}.json`;
}

export function newBaseline(snapshot: Record<string, string> = {}, now = new Date()): Baseline {
    return { id: crypto.randomUUID(), createdAt: now.toISOString(), snapshot };
}

export class StateStore {
    private _state: PersistedState;
    private loadedVersion = '';

    constructor(readonly filePath: string, private readonly repositoryRoot: string) {
        this._state = this.empty();
        this.load();
    }

    static forRepository(root: string, storageDirectory = defaultStorageDirectory()): StateStore {
        return new StateStore(path.join(storageDirectory, stateFileName(root)), root);
    }

    get state(): PersistedState {
        return this._state;
    }

    /** Re-reads the file if another process (e.g. the other IDE) changed it. Returns true if reloaded. */
    reloadIfChanged(): boolean {
        if (this.currentVersion() !== this.loadedVersion) {
            this.load();
            return true;
        }
        return false;
    }

    /** Read-modify-write. The mutator returns true if it changed the state. */
    update(mutator: (state: PersistedState) => boolean): boolean {
        this.reloadIfChanged();
        if (!mutator(this._state)) {
            return false;
        }
        this.write();
        return true;
    }

    private load(): void {
        const version = this.currentVersion();
        let state: PersistedState | undefined;
        if (version) {
            try {
                const parsed = JSON.parse(fs.readFileSync(this.filePath, 'utf8')) as PersistedState;
                if (parsed && typeof parsed === 'object' && parsed.branches && typeof parsed.branches === 'object') {
                    state = parsed;
                }
            } catch {
                // Corrupt or partially written file: start over rather than failing.
            }
        }
        this._state = state ?? this.empty();
        this.loadedVersion = version;
    }

    private write(): void {
        fs.mkdirSync(path.dirname(this.filePath), { recursive: true });
        const tmp = `${this.filePath}.${process.pid}.${Date.now()}.tmp`;
        fs.writeFileSync(tmp, JSON.stringify(this._state, null, 2), 'utf8');
        fs.renameSync(tmp, this.filePath);
        this.loadedVersion = this.currentVersion();
    }

    /** Cheap change detector for the state file: "" when it does not exist. */
    private currentVersion(): string {
        try {
            const st = fs.statSync(this.filePath);
            return `${st.mtimeMs}:${st.size}`;
        } catch {
            return '';
        }
    }

    private empty(): PersistedState {
        return { version: STATE_VERSION, repositoryRoot: this.repositoryRoot, branches: {} };
    }
}
