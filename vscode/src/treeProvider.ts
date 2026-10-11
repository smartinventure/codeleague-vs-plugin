import * as path from 'path';
import * as vscode from 'vscode';
import { FileStatus, RepoTracker, TrackedFileView } from './core/repoTracker';
import { TrackerManager } from './trackerManager';

export type ViewMode = 'tree' | 'list';

export interface RepositoryNode {
    kind: 'repository';
    tracker: RepoTracker;
}
export interface FolderNode {
    kind: 'folder';
    tracker: RepoTracker;
    /** Repository-relative folder path. */
    path: string;
    label: string;
    children: Node[];
}
export interface FileNode {
    kind: 'file';
    tracker: RepoTracker;
    file: TrackedFileView;
}
export type Node = RepositoryNode | FolderNode | FileNode;

/** URI scheme used only so that our FileDecorationProvider (not git's) decorates the items. */
const DECORATION_SCHEME = 'codeleague-tracked';

const STATUS_INFO: Record<FileStatus, { letter: string; color?: string; text: string }> = {
    modified: { letter: 'M', color: 'gitDecoration.modifiedResourceForeground', text: 'Modified (uncommitted)' },
    added: { letter: 'A', color: 'gitDecoration.addedResourceForeground', text: 'Added (staged)' },
    deleted: { letter: 'D', color: 'gitDecoration.deletedResourceForeground', text: 'Deleted' },
    renamed: { letter: 'R', color: 'gitDecoration.renamedResourceForeground', text: 'Renamed' },
    untracked: { letter: 'U', color: 'gitDecoration.untrackedResourceForeground', text: 'Untracked' },
    conflicted: { letter: '!', color: 'gitDecoration.conflictingResourceForeground', text: 'Conflicted' },
    committed: { letter: '✓', text: 'Committed – no uncommitted changes' },
};

export class PersistentChangesProvider implements vscode.TreeDataProvider<Node>, vscode.FileDecorationProvider {
    private readonly treeEmitter = new vscode.EventEmitter<Node | undefined>();
    private readonly decorationEmitter = new vscode.EventEmitter<undefined>();
    private readonly repositoryNodes = new Map<RepoTracker, RepositoryNode>();
    private view: vscode.TreeView<Node> | undefined;

    readonly onDidChangeTreeData = this.treeEmitter.event;
    readonly onDidChangeFileDecorations = this.decorationEmitter.event;

    constructor(private readonly manager: TrackerManager, private viewMode: ViewMode) {
        manager.onDidChange(() => this.refresh());
    }

    attach(view: vscode.TreeView<Node>): void {
        this.view = view;
        this.updateHeader();
    }

    setViewMode(mode: ViewMode): void {
        this.viewMode = mode;
        this.refresh();
    }

    refresh(): void {
        this.treeEmitter.fire(undefined);
        this.decorationEmitter.fire(undefined);
        this.updateHeader();
    }

    getTreeItem(node: Node): vscode.TreeItem {
        switch (node.kind) {
            case 'repository': {
                const t = node.tracker;
                const item = new vscode.TreeItem(t.name, vscode.TreeItemCollapsibleState.Expanded);
                item.id = `repo:${t.root}`;
                item.iconPath = new vscode.ThemeIcon('repo');
                item.description = `${t.branch ?? ''}${t.operation ? ` (${t.operation})` : ''} · ${countText(t.getFiles().length)}`;
                item.tooltip = `${t.root}\nBranch: ${t.branch ?? '?'}${t.error ? `\n\n${t.error}` : ''}`;
                item.contextValue = 'repository';
                return item;
            }
            case 'folder': {
                const item = new vscode.TreeItem(node.label, vscode.TreeItemCollapsibleState.Expanded);
                item.id = `folder:${node.tracker.root}:${node.path}`;
                item.resourceUri = vscode.Uri.file(path.join(node.tracker.root, node.path));
                item.iconPath = vscode.ThemeIcon.Folder;
                item.contextValue = 'folder';
                return item;
            }
            case 'file': {
                const { file } = node;
                const info = STATUS_INFO[file.status];
                const item = new vscode.TreeItem(path.posix.basename(file.path), vscode.TreeItemCollapsibleState.None);
                item.id = `file:${node.tracker.root}:${file.path}`;
                item.resourceUri = vscode.Uri.file(file.absolutePath).with({ scheme: DECORATION_SCHEME, query: file.status });
                item.iconPath = vscode.ThemeIcon.File;
                if (this.viewMode === 'list') {
                    const dir = path.posix.dirname(file.path);
                    item.description = dir === '.' ? '' : dir;
                }
                item.tooltip = new vscode.MarkdownString(
                    `**${file.path}**\n\n${info.text}\n\nTracked since ${formatDate(file.firstSeen)} · last change seen ${formatDate(file.lastSeen)}`
                );
                item.contextValue = file.status === 'committed' || file.status === 'deleted' ? 'file' : 'file.dirty';
                item.command = { command: 'codeleague.persistentChanges.click', title: 'Open', arguments: [node] };
                return item;
            }
        }
    }

    getChildren(node?: Node): Node[] {
        if (!node) {
            const trackers = this.manager.trackers;
            if (trackers.length === 1) {
                return this.filesOf(trackers[0]);
            }
            return trackers.map(t => this.repositoryNode(t));
        }
        if (node.kind === 'repository') {
            return this.filesOf(node.tracker);
        }
        return node.kind === 'folder' ? node.children : [];
    }

    getParent(): undefined {
        return undefined;
    }

    provideFileDecoration(uri: vscode.Uri): vscode.FileDecoration | undefined {
        if (uri.scheme !== DECORATION_SCHEME) {
            return undefined;
        }
        const info = STATUS_INFO[uri.query as FileStatus];
        if (!info) {
            return undefined;
        }
        return new vscode.FileDecoration(info.letter, info.text, info.color ? new vscode.ThemeColor(info.color) : undefined);
    }

    private repositoryNode(tracker: RepoTracker): RepositoryNode {
        let node = this.repositoryNodes.get(tracker);
        if (!node) {
            node = { kind: 'repository', tracker };
            this.repositoryNodes.set(tracker, node);
        }
        return node;
    }

    private filesOf(tracker: RepoTracker): Node[] {
        const files = tracker.getFiles().map<FileNode>(file => ({ kind: 'file', tracker, file }));
        return this.viewMode === 'list' ? files : buildFolderTree(tracker, files);
    }

    private updateHeader(): void {
        if (!this.view) {
            return;
        }
        const trackers = this.manager.trackers;
        const total = trackers.reduce((n, t) => n + t.getFiles().length, 0);
        this.view.description = countText(total);
        if (trackers.length === 0) {
            this.view.message = 'No Git repository found in this workspace.';
        } else if (trackers.length === 1) {
            const t = trackers[0];
            const parts = [`Repository: ${t.name}`, `Branch: ${t.branch ?? '…'}${t.operation ? ` (${t.operation} in progress)` : ''}`];
            if (t.error) {
                parts.push(`⚠ ${t.error}`);
            } else if (total === 0) {
                parts.push('No changes tracked yet. Modified files will appear here and stay after commit and push.');
            }
            this.view.message = parts.join('\n');
        } else {
            this.view.message = total === 0 ? 'No changes tracked yet.' : undefined;
        }
    }
}

/** Groups files by folder; chains of single-child folders are compacted ("src/components") like the SCM view. */
export function buildFolderTree(tracker: RepoTracker, files: FileNode[]): Node[] {
    interface Dir {
        dirs: Map<string, Dir>;
        files: FileNode[];
    }
    const root: Dir = { dirs: new Map(), files: [] };
    for (const f of files) {
        const segments = f.file.path.split('/');
        segments.pop();
        let d = root;
        for (const s of segments) {
            let next = d.dirs.get(s);
            if (!next) {
                next = { dirs: new Map(), files: [] };
                d.dirs.set(s, next);
            }
            d = next;
        }
        d.files.push(f);
    }
    const toNodes = (d: Dir, prefix: string): Node[] => {
        const folders: Node[] = [...d.dirs.entries()]
            .sort(([a], [b]) => a.localeCompare(b, undefined, { sensitivity: 'base' }))
            .map(([name, child]) => {
                let label = name;
                let folderPath = prefix ? `${prefix}/${name}` : name;
                while (child.files.length === 0 && child.dirs.size === 1) {
                    const [nextName, next] = [...child.dirs.entries()][0];
                    label += `/${nextName}`;
                    folderPath += `/${nextName}`;
                    child = next;
                }
                return { kind: 'folder', tracker, path: folderPath, label, children: toNodes(child, folderPath) } as FolderNode;
            });
        return [...folders, ...d.files];
    };
    return toNodes(root, '');
}

function countText(n: number): string {
    return n === 1 ? '1 tracked file' : `${n} tracked files`;
}

function formatDate(iso: string): string {
    const d = new Date(iso);
    return isNaN(d.getTime()) ? iso : d.toLocaleString();
}
