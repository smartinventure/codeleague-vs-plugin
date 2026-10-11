import * as path from 'path';
import * as vscode from 'vscode';
import { RepoTracker } from './core/repoTracker';
import { TrackerManager } from './trackerManager';
import { FileNode, Node, PersistentChangesProvider, ViewMode } from './treeProvider';

const SECTION = 'codeleague.persistentChanges';
const DOUBLE_CLICK_MS = 500;

/** Returned from activate() so that integration tests can inspect the view. */
export interface PersistentChangesApi {
    manager: TrackerManager;
    provider: PersistentChangesProvider;
}

export async function activate(context: vscode.ExtensionContext): Promise<PersistentChangesApi> {
    const manager = new TrackerManager();
    const provider = new PersistentChangesProvider(manager, getViewMode());
    const view = vscode.window.createTreeView<Node>('codeleague.persistentChanges', {
        treeDataProvider: provider,
        showCollapseAll: true,
        canSelectMany: true,
    });
    provider.attach(view);
    void vscode.commands.executeCommand('setContext', `${SECTION}.viewMode`, getViewMode());

    let lastClick: { id: string; time: number } | undefined;

    const command = (id: string, fn: (...args: any[]) => unknown) => vscode.commands.registerCommand(`${SECTION}.${id}`, fn);

    context.subscriptions.push(
        manager,
        view,
        vscode.window.registerFileDecorationProvider(provider),

        command('reset', (node?: Node) => resetTracking(manager, node)),
        command('refresh', () => manager.refreshAll()),
        command('viewAsTree', () => setViewMode('tree')),
        command('viewAsList', () => setViewMode('list')),

        command('click', async (node: FileNode) => {
            const now = Date.now();
            const id = `${node.tracker.root}:${node.file.path}`;
            const isDouble = lastClick?.id === id && now - lastClick.time < DOUBLE_CLICK_MS;
            lastClick = isDouble ? undefined : { id, time: now };
            const mode = vscode.workspace.getConfiguration(SECTION).get<string>('openMode', 'singleClick');
            if (isDouble) {
                await openFile(node, false);
            } else if (mode === 'singleClick') {
                await openFile(node, true);
            }
        }),
        command('open', (node: FileNode, selection?: Node[]) => forEachFile(node, selection, n => openFile(n, false))),
        command('openChanges', (node: FileNode) => vscode.commands.executeCommand('git.openChange', vscode.Uri.file(node.file.absolutePath))),
        command('remove', (node: Node, selection?: Node[]) => {
            for (const n of selected(node, selection)) {
                if (n.kind === 'file' || n.kind === 'folder') {
                    n.tracker.remove([n.kind === 'file' ? n.file.path : n.path]);
                }
            }
        }),
        command('revealInExplorer', (node: Node) => vscode.commands.executeCommand('revealInExplorer', nodeUri(node))),
        command('revealInOS', (node: Node) => vscode.commands.executeCommand('revealFileInOS', nodeUri(node))),
        command('copyPath', (node: Node, selection?: Node[]) =>
            vscode.env.clipboard.writeText(selected(node, selection).map(n => nodeUri(n)?.fsPath).filter(Boolean).join('\n'))
        ),
        command('copyRelativePath', (node: Node, selection?: Node[]) =>
            vscode.env.clipboard.writeText(
                selected(node, selection)
                    .map(n => (n.kind === 'file' ? n.file.path : n.kind === 'folder' ? n.path : ''))
                    .filter(Boolean)
                    .join('\n')
            )
        ),

        vscode.workspace.onDidChangeConfiguration(e => {
            if (e.affectsConfiguration(`${SECTION}.viewMode`)) {
                provider.setViewMode(getViewMode());
                void vscode.commands.executeCommand('setContext', `${SECTION}.viewMode`, getViewMode());
            }
        })
    );

    await manager.start();
    return { manager, provider };
}

export function deactivate(): void {
    // Everything is disposed through context.subscriptions.
}

function getViewMode(): ViewMode {
    return vscode.workspace.getConfiguration(SECTION).get<ViewMode>('viewMode', 'tree') === 'list' ? 'list' : 'tree';
}

async function setViewMode(mode: ViewMode): Promise<void> {
    await vscode.workspace.getConfiguration(SECTION).update('viewMode', mode, vscode.ConfigurationTarget.Global);
}

async function openFile(node: FileNode, preview: boolean): Promise<void> {
    if (node.file.status === 'deleted') {
        void vscode.window.showInformationMessage(`${node.file.path} has been deleted.`);
        return;
    }
    await vscode.commands.executeCommand('vscode.open', vscode.Uri.file(node.file.absolutePath), { preview, preserveFocus: preview });
}

async function resetTracking(manager: TrackerManager, node?: Node): Promise<void> {
    let targets: RepoTracker[];
    if (node && node.kind === 'repository') {
        targets = [node.tracker];
    } else if (manager.trackers.length <= 1) {
        targets = manager.trackers;
    } else {
        const all = '$(layers) All repositories';
        const pick = await vscode.window.showQuickPick(
            [{ label: all }, ...manager.trackers.map(t => ({ label: `$(repo) ${t.name}`, description: t.branch, tracker: t }))],
            { placeHolder: 'Reset tracking for which repository?' }
        );
        if (!pick) {
            return;
        }
        targets = 'tracker' in pick ? [pick.tracker] : manager.trackers;
    }
    if (targets.length === 0) {
        return;
    }
    if (vscode.workspace.getConfiguration(SECTION).get<boolean>('confirmReset', true)) {
        const what = targets.length === 1 ? `'${targets[0].name}' (branch ${targets[0].branch})` : `${targets.length} repositories`;
        const answer = await vscode.window.showWarningMessage(
            `Clear the Persistent Changes list for ${what}?`,
            {
                modal: true,
                detail: 'This only clears the tracked file list. It does not affect your files, staged changes, commits or branches. Files changed from now on will be tracked again.',
            },
            'Reset Tracking'
        );
        if (answer !== 'Reset Tracking') {
            return;
        }
    }
    await Promise.all(targets.map(t => t.reset()));
}

function selected(node: Node, selection?: Node[]): Node[] {
    return selection && selection.includes(node) ? selection : [node];
}

async function forEachFile(node: Node, selection: Node[] | undefined, fn: (n: FileNode) => Promise<void>): Promise<void> {
    for (const n of selected(node, selection)) {
        if (n.kind === 'file') {
            await fn(n);
        }
    }
}

function nodeUri(node: Node): vscode.Uri | undefined {
    if (node.kind === 'file') {
        return vscode.Uri.file(node.file.absolutePath);
    }
    if (node.kind === 'folder') {
        return vscode.Uri.file(path.join(node.tracker.root, node.path));
    }
    return vscode.Uri.file(node.tracker.root);
}
