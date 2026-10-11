import * as assert from 'assert';
import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import type { PersistentChangesApi } from '../extension';
import type { FileNode, Node } from '../treeProvider';

/** Runs inside the VS Code extension host (see runIntegrationTests.ts). */
export async function run(): Promise<void> {
    const repo = process.env.CL_TEST_REPO!;
    const git = (...args: string[]) => execFileSync('git', args, { cwd: repo, stdio: 'ignore' });

    const ext = vscode.extensions.getExtension<PersistentChangesApi>('smartinventure.codeleague-persistent-changes');
    assert.ok(ext, 'extension is installed');
    const api = await ext.activate();

    const files = (): FileNode[] => {
        const flatten = (nodes: Node[]): FileNode[] =>
            nodes.flatMap(n => (n.kind === 'file' ? [n] : flatten(api.provider.getChildren(n))));
        return flatten(api.provider.getChildren());
    };
    const waitFor = async (what: string, predicate: () => boolean) => {
        const deadline = Date.now() + 15_000;
        while (!predicate()) {
            if (Date.now() > deadline) {
                throw new Error(`Timed out waiting for: ${what} (have ${files().map(f => f.file.path).join(', ') || 'nothing'})`);
            }
            await new Promise(r => setTimeout(r, 100));
        }
    };

    await waitFor('repository discovered', () => api.manager.trackers.length === 1);
    assert.deepStrictEqual(files(), []);

    // Edit through the editor and save: the file must appear automatically (no manual refresh).
    const doc = await vscode.workspace.openTextDocument(path.join(repo, 'src', 'a.ts'));
    const editor = await vscode.window.showTextDocument(doc);
    await editor.edit(e => e.insert(new vscode.Position(0, 0), '// edited\n'));
    await doc.save();
    await waitFor('saved file tracked', () => files().some(f => f.file.path === 'src/a.ts'));

    // External tool creates a file: file-system watcher picks it up.
    fs.writeFileSync(path.join(repo, 'generated.txt'), 'generated\n');
    await waitFor('external file tracked', () => files().some(f => f.file.path === 'generated.txt'));

    // Commit + list stays.
    git('add', '-A');
    git('commit', '-q', '-m', 'work');
    await api.manager.refreshAll();
    assert.deepStrictEqual(files().map(f => [f.file.path, f.file.status]), [
        ['src/a.ts', 'committed'],
        ['generated.txt', 'committed'],
    ]);

    // Folder grouping: "src" folder node, then root-level file.
    const top = api.provider.getChildren();
    assert.strictEqual(top[0].kind, 'folder');

    // Double-click opens the file pinned.
    await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    const node = files().find(f => f.file.path === 'generated.txt')!;
    await vscode.commands.executeCommand('codeleague.persistentChanges.click', node);
    await vscode.commands.executeCommand('codeleague.persistentChanges.click', node);
    await waitFor('file opened', () => vscode.window.activeTextEditor?.document.uri.fsPath.toLowerCase() === node.file.absolutePath.toLowerCase());

    // Tree item rendering works for every node.
    for (const n of [...top, ...files()]) {
        assert.ok(api.provider.getTreeItem(n).label);
    }

    // Reset (confirmation disabled for the test) clears the list but not the files.
    await vscode.workspace.getConfiguration('codeleague.persistentChanges').update('confirmReset', false, vscode.ConfigurationTarget.Global);
    await vscode.commands.executeCommand('codeleague.persistentChanges.reset');
    assert.deepStrictEqual(files(), []);
    assert.ok(fs.existsSync(path.join(repo, 'generated.txt')));
}
