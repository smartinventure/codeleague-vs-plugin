import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { runTests } from '@vscode/test-electron';

/** Launches a real VS Code instance on a temporary git repository and runs suite.ts inside it. */
async function main(): Promise<void> {
    // Set when this is started from a VS Code terminal; it would make VS Code start as plain Node.
    delete process.env.ELECTRON_RUN_AS_NODE;
    const repo = fs.mkdtempSync(path.join(os.tmpdir(), 'cl-int-repo-'));
    const storage = fs.mkdtempSync(path.join(os.tmpdir(), 'cl-int-state-'));
    const git = (...args: string[]) => execFileSync('git', args, { cwd: repo, stdio: 'ignore' });
    git('init', '-q', '-b', 'main');
    git('config', 'user.email', 'dev@example.com');
    git('config', 'user.name', 'Dev');
    git('config', 'commit.gpgsign', 'false');
    fs.mkdirSync(path.join(repo, 'src'));
    fs.writeFileSync(path.join(repo, 'src', 'a.ts'), 'a\n');
    fs.writeFileSync(path.join(repo, 'README.md'), 'readme\n');
    git('add', '-A');
    git('commit', '-q', '-m', 'initial');

    try {
        await runTests({
            extensionDevelopmentPath: path.resolve(__dirname, '../..'),
            extensionTestsPath: path.resolve(__dirname, 'suite'),
            launchArgs: [repo, '--disable-workspace-trust', '--skip-welcome', '--skip-release-notes'],
            // Keep the developer's real state directory untouched.
            extensionTestsEnv: { LOCALAPPDATA: storage, XDG_DATA_HOME: storage, CL_TEST_REPO: repo },
        });
    } finally {
        fs.rmSync(repo, { recursive: true, force: true });
        fs.rmSync(storage, { recursive: true, force: true });
    }
}

main().catch(err => {
    console.error(err);
    process.exit(1);
});
