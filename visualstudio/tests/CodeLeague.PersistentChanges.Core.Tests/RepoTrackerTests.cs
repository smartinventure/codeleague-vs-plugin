using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace CodeLeague.PersistentChanges.Core.Tests
{
    /// <summary>Acceptance criteria from the specification. Mirrors vscode/src/test/repoTracker.test.ts.</summary>
    public sealed class AcceptanceTests : IAsyncLifetime, IDisposable
    {
        private readonly GitFixture f = new GitFixture();
        private RepoTracker t = null!;

        public async Task InitializeAsync()
        {
            t = f.Tracker();
            await t.RefreshAsync();
        }

        public Task DisposeAsync() => Task.CompletedTask;

        public void Dispose() => f.Dispose();

        [Fact]
        public async Task C01_ModifyingAFileAddsIt()
        {
            Assert.Empty(await f.Tracked(t));
            f.Write("src/a.ts", "a changed\n");
            Assert.Equal(new[] { "src/a.ts" }, await f.Tracked(t));
            Assert.Equal(FileStatus.Modified, t.GetFiles()[0].Status);
        }

        [Fact]
        public async Task C02_ModifyingAgainDoesNotDuplicate()
        {
            f.Write("src/a.ts", "a1\n");
            await t.RefreshAsync();
            f.Write("src/a.ts", "a22\n");
            Assert.Equal(new[] { "src/a.ts" }, await f.Tracked(t));
        }

        [Fact]
        public async Task C03_CommittingKeepsFiles()
        {
            f.Write("src/a.ts", "a1\n");
            f.Write("src/new.ts", "new\n");
            await t.RefreshAsync();
            f.CommitAll("work");
            Assert.Equal(new[] { "src/a.ts", "src/new.ts" }, await f.Tracked(t));
            Assert.All(t.GetFiles(), x => Assert.Equal(FileStatus.Committed, x.Status));
        }

        [Fact]
        public async Task C04_PushingKeepsFiles()
        {
            var remote = GitFixture.NewTempDir("cl-remote-");
            GitFixture.Run(remote, "init", "-q", "--bare");
            f.Git("remote", "add", "origin", remote);
            f.Git("checkout", "-q", "-b", "feature/x");
            f.Write("src/a.ts", "a1\n");
            await t.RefreshAsync();
            f.CommitAll("work");
            f.Git("push", "-q", "-u", "origin", "feature/x");
            Assert.Equal(new[] { "src/a.ts" }, await f.Tracked(t));
        }

        [Fact]
        public async Task C05_AdditionalFilesAreAppended()
        {
            f.Write("src/a.ts", "a1\n");
            await t.RefreshAsync();
            f.CommitAll("work");
            await t.RefreshAsync();
            f.Write("src/b.ts", "b1\n");
            f.Write("docs/new.md", "new\n");
            Assert.Equal(new[] { "docs/new.md", "src/a.ts", "src/b.ts" }, await f.Tracked(t));
        }

        [Fact]
        public async Task C07_RestartRestoresList()
        {
            f.Write("src/a.ts", "a1\n");
            await t.RefreshAsync();
            f.CommitAll("work");
            await t.RefreshAsync();
            Assert.Equal(new[] { "src/a.ts" }, await f.Tracked(f.Tracker()));
        }

        [Fact]
        public async Task C08_ResetClearsListWithoutTouchingFilesOrGit()
        {
            f.Write("src/a.ts", "a1\n");
            f.Write("src/b.ts", "b1\n");
            await t.RefreshAsync();
            f.Git("add", "src/b.ts");
            f.CommitAll("partial");
            f.Write("src/c.ts", "c1\n");
            f.Git("add", "src/c.ts");
            f.Write("untracked.txt", "u\n");
            await t.RefreshAsync();

            string Snapshot() => string.Join("\n---\n",
                f.Git("rev-parse", "HEAD"), f.Git("status", "--porcelain"), f.Git("log", "--oneline", "--all"),
                f.Git("branch", "-a"), f.Read("src/a.ts"), f.Read("src/c.ts"));
            var before = Snapshot();
            await t.ResetAsync();
            Assert.Empty(t.GetFiles());
            Assert.Equal(before, Snapshot());
            Assert.Empty(await f.Tracked(t));
        }

        [Fact]
        public async Task C09_NewModificationsAfterResetAreDetected()
        {
            f.Write("src/a.ts", "a1\n");
            f.Write("src/b.ts", "b1\n");
            await t.RefreshAsync();
            await t.ResetAsync();
            f.Write("src/a.ts", "a1 more\n");
            f.Write("src/c.ts", "c1\n");
            Assert.Equal(new[] { "src/a.ts", "src/c.ts" }, await f.Tracked(t));
            f.CommitAll("commit including untouched b");
            Assert.Equal(new[] { "src/a.ts", "src/c.ts" }, await f.Tracked(t));
        }

        [Fact]
        public async Task C10_BranchSwitchRestoresBranchListAndIgnoresCheckoutChanges()
        {
            f.Git("checkout", "-q", "-b", "feature/one");
            f.Write("src/a.ts", "one\n");
            f.Write("src/one-only.ts", "one\n");
            await t.RefreshAsync();
            f.CommitAll("one");
            Assert.Equal(new[] { "src/a.ts", "src/one-only.ts" }, await f.Tracked(t));

            f.Git("checkout", "-q", "main");
            f.Git("checkout", "-q", "-b", "feature/two");
            await t.RefreshAsync();
            Assert.Equal("feature/two", t.Branch);
            Assert.Empty(t.GetFiles());
            f.Write("src/b.ts", "two\n");
            Assert.Equal(new[] { "src/b.ts" }, await f.Tracked(t));
            f.CommitAll("two");

            f.Git("checkout", "-q", "feature/one");
            Assert.Equal(new[] { "src/a.ts", "src/one-only.ts" }, await f.Tracked(t));
            f.Git("checkout", "-q", "feature/two");
            Assert.Equal(new[] { "src/b.ts" }, await f.Tracked(t));
        }
    }

    public sealed class EdgeCaseTests : IAsyncLifetime, IDisposable
    {
        private readonly GitFixture f = new GitFixture();
        private RepoTracker t = null!;

        public async Task InitializeAsync()
        {
            t = f.Tracker();
            await t.RefreshAsync();
        }

        public Task DisposeAsync() => Task.CompletedTask;

        public void Dispose() => f.Dispose();

        [Fact]
        public async Task EditAndCommitBetweenRefreshesIsDetected()
        {
            f.Write("src/a.ts", "fast\n");
            f.CommitAll("quick commit");
            f.Write("src/b.ts", "fast\n");
            f.CommitAll("second quick commit");
            Assert.Equal(new[] { "src/a.ts", "src/b.ts" }, await f.Tracked(t));
        }

        [Fact]
        public async Task OwnCommitsWhileIdeWasClosedAreDetected()
        {
            f.Write("src/a.ts", "offline\n");
            f.CommitAll("offline commit");
            Assert.Equal(new[] { "src/a.ts" }, await f.Tracked(f.Tracker()));
        }

        [Fact]
        public async Task ColleagueChangesMergedFromMainAreNotTracked()
        {
            f.Git("checkout", "-q", "-b", "feature/x");
            f.Write("src/a.ts", "mine\n");
            await t.RefreshAsync();
            f.CommitAll("mine");
            await t.RefreshAsync();

            f.Git("checkout", "-q", "main");
            f.ConfigUser("colleague@example.com", "Colleague");
            f.Write("src/theirs.ts", "theirs\n");
            f.Write("src/c.ts", "theirs\n");
            f.CommitAll("theirs");
            f.ConfigUser("dev@example.com", "Dev");
            f.Git("checkout", "-q", "feature/x");
            Assert.Equal(new[] { "src/a.ts" }, await f.Tracked(t));

            f.Git("merge", "-q", "--no-edit", "main");
            Assert.Equal(new[] { "src/a.ts" }, await f.Tracked(t));
        }

        [Fact]
        public async Task ConflictedMergeTracksOnlyConflictsAndOwnEdits()
        {
            f.Git("checkout", "-q", "-b", "feature/x");
            f.Write("src/a.ts", "mine\n");
            await t.RefreshAsync();
            f.CommitAll("mine");
            f.Git("checkout", "-q", "main");
            f.Write("src/a.ts", "theirs\n");
            f.Write("src/c.ts", "theirs\n");
            f.CommitAll("theirs");
            f.Git("checkout", "-q", "feature/x");
            await t.RefreshAsync();
            Assert.ThrowsAny<InvalidOperationException>(() => f.Git("merge", "-q", "main"));
            Assert.Equal(new[] { "src/a.ts" }, await f.Tracked(t));
            Assert.Equal(FileStatus.Conflicted, t.GetFiles()[0].Status);
            Assert.Equal("feature/x", t.Branch);
        }

        [Fact]
        public async Task FastForwardPullOfColleagueCommitIsNotTracked()
        {
            f.Git("checkout", "-q", "-b", "feature/x");
            await t.RefreshAsync();
            f.ConfigUser("colleague@example.com", "Colleague");
            f.Git("checkout", "-q", "-b", "colleague");
            f.Write("src/c.ts", "colleague\n");
            f.CommitAll("colleague");
            f.ConfigUser("dev@example.com", "Dev");
            f.Git("checkout", "-q", "feature/x");
            await t.RefreshAsync();
            f.Git("merge", "-q", "--ff-only", "colleague");
            Assert.Empty(await f.Tracked(t));
        }

        [Fact]
        public async Task DeletedFilesStayAndShowAsDeleted()
        {
            File.Delete(Path.Combine(f.Repo, "src", "b.ts"));
            await t.RefreshAsync();
            f.CommitAll("delete");
            Assert.Equal(new[] { "src/b.ts" }, await f.Tracked(t));
            Assert.Equal(FileStatus.Deleted, t.GetFiles()[0].Status);
        }

        [Fact]
        public async Task RevertedFileStays()
        {
            f.Write("src/a.ts", "temp\n");
            await t.RefreshAsync();
            f.Git("checkout", "--", "src/a.ts");
            Assert.Equal(new[] { "src/a.ts" }, await f.Tracked(t));
            Assert.Equal(FileStatus.Committed, t.GetFiles()[0].Status);
        }

        [Fact]
        public async Task StagedRenameMovesEntry()
        {
            f.Write("src/a.ts", "a modified\n");
            await t.RefreshAsync();
            f.Git("mv", "src/a.ts", "src/renamed.ts");
            Assert.Equal(new[] { "src/renamed.ts" }, await f.Tracked(t));
        }

        [Fact]
        public async Task RenameNotificationsMoveFileAndFolderEntries()
        {
            f.Write("src/a.ts", "a modified\n");
            f.Write("src/b.ts", "b modified\n");
            await t.RefreshAsync();
            f.CommitAll("x");
            await t.RefreshAsync();
            Directory.Move(Path.Combine(f.Repo, "src"), Path.Combine(f.Repo, "lib"));
            t.NotifyRenamed(Path.Combine(f.Repo, "src"), Path.Combine(f.Repo, "lib"));
            Assert.Equal(new[] { "lib/a.ts", "lib/b.ts" }, t.GetFiles().Select(x => x.Path));
        }

        [Fact]
        public async Task RemoveDropsEntriesWithoutTouchingFiles()
        {
            f.Write("src/a.ts", "a1\n");
            f.Write("src/b.ts", "b1\n");
            await t.RefreshAsync();
            t.Remove(new[] { "src/a.ts" });
            Assert.Equal(new[] { "src/b.ts" }, t.GetFiles().Select(x => x.Path));
            Assert.Equal("a1\n", f.Read("src/a.ts"));
        }

        [Fact]
        public async Task StateIsSharedBetweenTwoIdeInstances()
        {
            var other = f.Tracker();
            f.Write("src/a.ts", "a1\n");
            await t.RefreshAsync();
            Assert.Equal(new[] { "src/a.ts" }, await f.Tracked(other));
            await other.ResetAsync();
            Assert.Empty(await f.Tracked(t));
        }

        [Fact]
        public async Task IgnoresUntrackedIdeStateAndBuildOutputWithoutGitignore()
        {
            f.Write(".vs/Smoke/v18/.suo", "x");
            f.Write("src/obj/Debug/x.cache", "x");
            f.Write("src/bin/app.dll", "x");
            f.Write("node_modules/pkg/index.js", "x");
            f.Write("src/binder.ts", "real work");
            Assert.Equal(new[] { "src/binder.ts" }, await f.Tracked(t));
        }

        [Fact]
        public async Task WorksInRepositoryWithoutCommits()
        {
            var repo = GitFixture.NewTempDir("cl-empty-");
            GitFixture.Run(repo, "init", "-q", "-b", "main");
            File.WriteAllText(Path.Combine(repo, "first.txt"), "x");
            var tracker = new RepoTracker(repo, new Git(), StateStore.ForRepository(repo, GitFixture.NewTempDir("cl-state-")));
            await tracker.RefreshAsync();
            Assert.Equal("main", tracker.Branch);
            Assert.Equal(new[] { "first.txt" }, tracker.GetFiles().Select(x => x.Path));
        }

        [Fact]
        public async Task RefreshCallsAreSerializedAndCoalesced()
        {
            f.Write("src/a.ts", "a1\n");
            await Task.WhenAll(t.RefreshAsync(), t.RefreshAsync(), t.RefreshAsync(), t.ResetAsync(), t.RefreshAsync());
            Assert.Empty(t.GetFiles());
        }

        [Fact]
        public async Task ChangedEventIsRaised()
        {
            var raised = 0;
            t.Changed += (_, _) => raised++;
            f.Write("src/a.ts", "a1\n");
            await t.RefreshAsync();
            Assert.Equal(1, raised);
            await t.RefreshAsync();
            Assert.Equal(1, raised);
        }
    }

    public sealed class StateStoreTests
    {
        [Fact]
        public void NormalizesRoot()
        {
            Assert.Equal("d:/repos/codeleague", StateStore.NormalizeRoot(@"D:\Repos\CodeLeague\"));
            Assert.Equal("/home/dev/CodeLeague", StateStore.NormalizeRoot("/home/dev/CodeLeague/", windows: false));
        }

        [Fact]
        public void FileNameMatchesTypeScriptImplementation()
        {
            // Same vector is asserted in vscode/src/test/repoTracker.test.ts.
            Assert.Equal("codeleague-c61f656b7a2db7bb.json", StateStore.StateFileName("D:/Repos/CodeLeague"));
            Assert.Equal(StateStore.StateFileName("D:/Repos/CodeLeague"), StateStore.StateFileName(@"d:\repos\codeleague"));
        }

        [Fact]
        public void ReadsStateWrittenByTypeScript()
        {
            var dir = GitFixture.NewTempDir("cl-state-");
            var file = Path.Combine(dir, "x.json");
            File.WriteAllText(file, @"{
  ""version"": 1,
  ""repositoryRoot"": ""D:/Repos/x"",
  ""branches"": {
    ""feature/x"": {
      ""baseline"": { ""id"": ""6a1f"", ""createdAt"": ""2026-10-10T10:00:00.000Z"", ""snapshot"": { ""src/old.ts"": ""12:1700000000000"" } },
      ""files"": { ""src/a.ts"": { ""firstSeen"": ""2026-10-10T10:01:00.000Z"", ""lastSeen"": ""2026-10-10T10:02:00.000Z"", ""fingerprint"": ""3:1700000000001"" } },
      ""lastHead"": ""abc""
    }
  }
}");
            var store = new StateStore(file, "D:/Repos/x");
            var b = store.State.Branches["feature/x"];
            Assert.Equal("12:1700000000000", b.Baseline.Snapshot["src/old.ts"]);
            Assert.Equal("2026-10-10T10:01:00.000Z", b.Files["src/a.ts"].FirstSeen);
            Assert.Equal("abc", b.LastHead);

            store.Update(s => { s.Branches["main"] = new BranchState { Baseline = Baseline.Create(null, DateTime.UtcNow) }; return true; });
            var json = File.ReadAllText(file);
            Assert.Contains("\"src\\/a.ts\":{\"firstSeen\"", json); // simple dictionary format, same shape as TypeScript
            Assert.Equal(2, new StateStore(file, "D:/Repos/x").State.Branches.Count);
            Directory.Delete(dir, true);
        }
    }
}
