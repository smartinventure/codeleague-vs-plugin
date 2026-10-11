import * as assert from 'assert';
import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { after, beforeEach, describe, it } from 'node:test';
import { Git } from '../core/git';
import { RepoTracker } from '../core/repoTracker';
import { normalizeRoot, StateStore, stateFileName } from '../core/stateStore';

const tempRoots: string[] = [];

function tempDir(prefix: string): string {
    const d = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
    tempRoots.push(d);
    return d;
}

function git(cwd: string, ...args: string[]): string {
    return execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
}

class Fixture {
    readonly repo: string;
    readonly storage: string;
    private readonly g = new Git();

    constructor() {
        this.repo = tempDir('cl-repo-');
        this.storage = tempDir('cl-state-');
        git(this.repo, 'init', '-q', '-b', 'main');
        this.configUser('dev@example.com', 'Dev');
        git(this.repo, 'config', 'commit.gpgsign', 'false');
        git(this.repo, 'config', 'core.autocrlf', 'false');
        this.write('README.md', 'readme\n');
        this.write('src/a.ts', 'a\n');
        this.write('src/b.ts', 'b\n');
        this.write('src/c.ts', 'c\n');
        this.commitAll('initial');
    }

    configUser(email: string, name: string): void {
        git(this.repo, 'config', 'user.email', email);
        git(this.repo, 'config', 'user.name', name);
    }

    write(rel: string, content: string): void {
        const p = path.join(this.repo, rel);
        fs.mkdirSync(path.dirname(p), { recursive: true });
        fs.writeFileSync(p, content);
    }

    read(rel: string): string {
        return fs.readFileSync(path.join(this.repo, rel), 'utf8');
    }

    commitAll(message: string): void {
        git(this.repo, 'add', '-A');
        git(this.repo, 'commit', '-q', '-m', message);
    }

    tracker(): RepoTracker {
        return new RepoTracker(this.repo, this.g, StateStore.forRepository(this.repo, this.storage));
    }

    async tracked(t: RepoTracker): Promise<string[]> {
        await t.refresh();
        return t.getFiles().map(f => f.path);
    }
}

after(() => {
    for (const d of tempRoots) {
        fs.rmSync(d, { recursive: true, force: true });
    }
});

describe('RepoTracker acceptance criteria', () => {
    let f: Fixture;
    let t: RepoTracker;

    beforeEach(async () => {
        f = new Fixture();
        t = f.tracker();
        await t.refresh();
    });

    it('1. modifying a file adds it to the list', async () => {
        assert.deepStrictEqual(await f.tracked(t), []);
        f.write('src/a.ts', 'a changed\n');
        assert.deepStrictEqual(await f.tracked(t), ['src/a.ts']);
        assert.strictEqual(t.getFiles()[0].status, 'modified');
    });

    it('2. modifying the same file again does not create duplicates', async () => {
        f.write('src/a.ts', 'a1\n');
        await t.refresh();
        f.write('src/a.ts', 'a22\n');
        assert.deepStrictEqual(await f.tracked(t), ['src/a.ts']);
    });

    it('3. committing does not remove files from the list', async () => {
        f.write('src/a.ts', 'a1\n');
        f.write('src/new.ts', 'new\n');
        await t.refresh();
        f.commitAll('work');
        assert.deepStrictEqual(await f.tracked(t), ['src/a.ts', 'src/new.ts']);
        assert.ok(t.getFiles().every(x => x.status === 'committed'));
    });

    it('4. pushing does not remove files from the list', async () => {
        const remote = tempDir('cl-remote-');
        git(remote, 'init', '-q', '--bare');
        git(f.repo, 'remote', 'add', 'origin', remote);
        git(f.repo, 'checkout', '-q', '-b', 'feature/x');
        f.write('src/a.ts', 'a1\n');
        await t.refresh();
        f.commitAll('work');
        git(f.repo, 'push', '-q', '-u', 'origin', 'feature/x');
        assert.deepStrictEqual(await f.tracked(t), ['src/a.ts']);
    });

    it('5. modifying additional files adds them to the existing list', async () => {
        f.write('src/a.ts', 'a1\n');
        await t.refresh();
        f.commitAll('work');
        await t.refresh();
        f.write('src/b.ts', 'b1\n');
        f.write('docs/new.md', 'new\n');
        assert.deepStrictEqual(await f.tracked(t), ['docs/new.md', 'src/a.ts', 'src/b.ts']);
    });

    it('7. restarting the IDE restores the list', async () => {
        f.write('src/a.ts', 'a1\n');
        await t.refresh();
        f.commitAll('work');
        await t.refresh();
        const restarted = f.tracker();
        assert.deepStrictEqual(await f.tracked(restarted), ['src/a.ts']);
    });

    it('8. reset clears the list without touching files or git', async () => {
        f.write('src/a.ts', 'a1\n');
        f.write('src/b.ts', 'b1\n');
        await t.refresh();
        git(f.repo, 'add', 'src/b.ts');
        f.commitAll('partial');
        f.write('src/c.ts', 'c1\n');
        git(f.repo, 'add', 'src/c.ts');
        f.write('untracked.txt', 'u\n');
        await t.refresh();

        const before = {
            head: git(f.repo, 'rev-parse', 'HEAD'),
            status: git(f.repo, 'status', '--porcelain'),
            log: git(f.repo, 'log', '--oneline', '--all'),
            branches: git(f.repo, 'branch', '-a'),
            a: f.read('src/a.ts'),
            c: f.read('src/c.ts'),
        };
        await t.reset();
        assert.deepStrictEqual(t.getFiles(), []);
        assert.deepStrictEqual(
            {
                head: git(f.repo, 'rev-parse', 'HEAD'),
                status: git(f.repo, 'status', '--porcelain'),
                log: git(f.repo, 'log', '--oneline', '--all'),
                branches: git(f.repo, 'branch', '-a'),
                a: f.read('src/a.ts'),
                c: f.read('src/c.ts'),
            },
            before
        );
        assert.deepStrictEqual(await f.tracked(t), [], 'already-dirty files are part of the new baseline');
    });

    it('9. new modifications after reset are detected', async () => {
        f.write('src/a.ts', 'a1\n');
        f.write('src/b.ts', 'b1\n');
        await t.refresh();
        await t.reset();
        f.write('src/a.ts', 'a1 more\n'); // dirty before reset, modified again
        f.write('src/c.ts', 'c1\n'); // clean before reset
        assert.deepStrictEqual(await f.tracked(t), ['src/a.ts', 'src/c.ts']);
        f.commitAll('commit including untouched b');
        assert.deepStrictEqual(await f.tracked(t), ['src/a.ts', 'src/c.ts'], 'committing an untouched baseline file does not add it');
    });

    it('10. switching branches restores the branch-specific list and ignores checkout changes', async () => {
        git(f.repo, 'checkout', '-q', '-b', 'feature/one');
        f.write('src/a.ts', 'one\n');
        f.write('src/one-only.ts', 'one\n');
        await t.refresh();
        f.commitAll('one');
        assert.deepStrictEqual(await f.tracked(t), ['src/a.ts', 'src/one-only.ts']);

        git(f.repo, 'checkout', '-q', 'main');
        git(f.repo, 'checkout', '-q', '-b', 'feature/two');
        assert.strictEqual((await t.refresh(), t.branch), 'feature/two');
        assert.deepStrictEqual(t.getFiles(), [], 'files rewritten or removed by checkout are not tracked');
        f.write('src/b.ts', 'two\n');
        assert.deepStrictEqual(await f.tracked(t), ['src/b.ts']);
        f.commitAll('two');

        git(f.repo, 'checkout', '-q', 'feature/one');
        assert.deepStrictEqual(await f.tracked(t), ['src/a.ts', 'src/one-only.ts']);
        git(f.repo, 'checkout', '-q', 'feature/two');
        assert.deepStrictEqual(await f.tracked(t), ['src/b.ts']);
    });
});

describe('RepoTracker edge cases', () => {
    let f: Fixture;
    let t: RepoTracker;

    beforeEach(async () => {
        f = new Fixture();
        t = f.tracker();
        await t.refresh();
    });

    it('detects files that were edited and committed between two refreshes', async () => {
        f.write('src/a.ts', 'fast\n');
        f.commitAll('quick commit');
        f.write('src/b.ts', 'fast\n');
        f.commitAll('second quick commit');
        assert.deepStrictEqual(await f.tracked(t), ['src/a.ts', 'src/b.ts']);
    });

    it('detects own commits made while the IDE was closed', async () => {
        f.write('src/a.ts', 'offline\n');
        f.commitAll('offline commit');
        assert.deepStrictEqual(await f.tracked(f.tracker()), ['src/a.ts']);
    });

    it("does not track colleagues' changes merged in from main", async () => {
        git(f.repo, 'checkout', '-q', '-b', 'feature/x');
        f.write('src/a.ts', 'mine\n');
        await t.refresh();
        f.commitAll('mine');
        await t.refresh();

        git(f.repo, 'checkout', '-q', 'main');
        f.configUser('colleague@example.com', 'Colleague');
        f.write('src/theirs.ts', 'theirs\n');
        f.write('src/c.ts', 'theirs\n');
        f.commitAll('theirs');
        f.configUser('dev@example.com', 'Dev');
        git(f.repo, 'checkout', '-q', 'feature/x');
        assert.deepStrictEqual(await f.tracked(t), ['src/a.ts']);

        git(f.repo, 'merge', '-q', '--no-edit', 'main');
        assert.deepStrictEqual(await f.tracked(t), ['src/a.ts']);
    });

    it('during a conflicted merge, only conflicts and own edits are tracked', async () => {
        git(f.repo, 'checkout', '-q', '-b', 'feature/x');
        f.write('src/a.ts', 'mine\n');
        await t.refresh();
        f.commitAll('mine');
        git(f.repo, 'checkout', '-q', 'main');
        f.write('src/a.ts', 'theirs\n');
        f.write('src/c.ts', 'theirs\n');
        f.commitAll('theirs');
        git(f.repo, 'checkout', '-q', 'feature/x');
        await t.refresh();
        try {
            git(f.repo, 'merge', '-q', 'main');
        } catch {
            // conflict expected
        }
        assert.deepStrictEqual(await f.tracked(t), ['src/a.ts']);
        assert.strictEqual(t.getFiles()[0].status, 'conflicted');
        assert.strictEqual(t.branch, 'feature/x');
    });

    it("does not track a fast-forward pull of a colleague's commit", async () => {
        git(f.repo, 'checkout', '-q', '-b', 'feature/x');
        await t.refresh();
        f.configUser('colleague@example.com', 'Colleague');
        git(f.repo, 'checkout', '-q', '-b', 'colleague');
        f.write('src/c.ts', 'colleague\n');
        f.commitAll('colleague');
        f.configUser('dev@example.com', 'Dev');
        git(f.repo, 'checkout', '-q', 'feature/x');
        await t.refresh();
        git(f.repo, 'merge', '-q', '--ff-only', 'colleague');
        assert.deepStrictEqual(await f.tracked(t), []);
    });

    it('keeps deleted files and shows them as deleted', async () => {
        fs.rmSync(path.join(f.repo, 'src/b.ts'));
        await t.refresh();
        f.commitAll('delete');
        assert.deepStrictEqual(await f.tracked(t), ['src/b.ts']);
        assert.strictEqual(t.getFiles()[0].status, 'deleted');
    });

    it('a reverted file stays in the list', async () => {
        f.write('src/a.ts', 'temp\n');
        await t.refresh();
        git(f.repo, 'checkout', '--', 'src/a.ts');
        assert.deepStrictEqual(await f.tracked(t), ['src/a.ts']);
        assert.strictEqual(t.getFiles()[0].status, 'committed');
    });

    it('a staged rename moves the entry instead of duplicating it', async () => {
        f.write('src/a.ts', 'a modified\n');
        await t.refresh();
        git(f.repo, 'mv', 'src/a.ts', 'src/renamed.ts');
        assert.deepStrictEqual(await f.tracked(t), ['src/renamed.ts']);
    });

    it('IDE rename notifications move file and folder entries', async () => {
        f.write('src/a.ts', 'a modified\n');
        f.write('src/b.ts', 'b modified\n');
        await t.refresh();
        f.commitAll('x');
        await t.refresh();
        fs.renameSync(path.join(f.repo, 'src'), path.join(f.repo, 'lib'));
        t.notifyRenamed(path.join(f.repo, 'src'), path.join(f.repo, 'lib'));
        assert.deepStrictEqual(
            t.getFiles().map(x => x.path),
            ['lib/a.ts', 'lib/b.ts']
        );
    });

    it('remove() drops single entries without touching the file', async () => {
        f.write('src/a.ts', 'a1\n');
        f.write('src/b.ts', 'b1\n');
        await t.refresh();
        t.remove(['src/a.ts']);
        assert.deepStrictEqual(t.getFiles().map(x => x.path), ['src/b.ts']);
        assert.strictEqual(f.read('src/a.ts'), 'a1\n');
    });

    it('state is shared between two IDE instances (same state file)', async () => {
        const other = f.tracker();
        f.write('src/a.ts', 'a1\n');
        await t.refresh();
        assert.deepStrictEqual(await f.tracked(other), ['src/a.ts']);
        await other.reset();
        assert.deepStrictEqual(await f.tracked(t), []);
    });

    it('ignores untracked IDE state and build output even without a .gitignore', async () => {
        f.write('.vs/Smoke/v18/.suo', 'x');
        f.write('src/obj/Debug/x.cache', 'x');
        f.write('src/bin/app.dll', 'x');
        f.write('node_modules/pkg/index.js', 'x');
        f.write('src/binder.ts', 'real work');
        assert.deepStrictEqual(await f.tracked(t), ['src/binder.ts']);
    });

    it('works in a repository without commits', async () => {
        const repo = tempDir('cl-empty-');
        git(repo, 'init', '-q', '-b', 'main');
        fs.writeFileSync(path.join(repo, 'first.txt'), 'x');
        const tracker = new RepoTracker(repo, new Git(), StateStore.forRepository(repo, tempDir('cl-state-')));
        await tracker.refresh();
        assert.strictEqual(tracker.branch, 'main');
        assert.deepStrictEqual(tracker.getFiles().map(x => x.path), ['first.txt']);
    });

    it('refresh calls are serialized and coalesced', async () => {
        f.write('src/a.ts', 'a1\n');
        await Promise.all([t.refresh(), t.refresh(), t.refresh(), t.reset(), t.refresh()]);
        assert.deepStrictEqual(t.getFiles(), []);
    });
});

describe('state file naming (shared with the Visual Studio extension)', () => {
    it('normalizes the repository root', () => {
        assert.strictEqual(normalizeRoot('D:\\Repos\\CodeLeague\\', 'win32'), 'd:/repos/codeleague');
        assert.strictEqual(normalizeRoot('/home/dev/CodeLeague/', 'linux'), '/home/dev/CodeLeague');
    });

    it('produces the same file name as the C# implementation', () => {
        // Same vector is asserted in StateStoreTests.cs.
        assert.strictEqual(stateFileName('D:/Repos/CodeLeague', 'win32'), 'codeleague-c61f656b7a2db7bb.json');
        assert.strictEqual(stateFileName('d:\\repos\\codeleague', 'win32'), stateFileName('D:/Repos/CodeLeague', 'win32'));
    });
});
