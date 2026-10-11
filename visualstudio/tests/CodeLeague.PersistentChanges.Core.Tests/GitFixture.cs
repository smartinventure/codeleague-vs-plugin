using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CodeLeague.PersistentChanges.Core.Tests
{
    /// <summary>A throw-away git repository plus a private state directory.</summary>
    public sealed class GitFixture : IDisposable
    {
        private readonly Core.Git _git = new Core.Git();

        public GitFixture()
        {
            Repo = NewTempDir("cl-repo-");
            Storage = NewTempDir("cl-state-");
            Run(Repo, "init", "-q", "-b", "main");
            ConfigUser("dev@example.com", "Dev");
            Run(Repo, "config", "commit.gpgsign", "false");
            Run(Repo, "config", "core.autocrlf", "false");
            Write("README.md", "readme\n");
            Write("src/a.ts", "a\n");
            Write("src/b.ts", "b\n");
            Write("src/c.ts", "c\n");
            CommitAll("initial");
        }

        public string Repo { get; }
        public string Storage { get; }

        public static string NewTempDir(string prefix)
        {
            var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string Run(string cwd, params string[] args)
        {
            var psi = new ProcessStartInfo("git", string.Join(" ", args.Select(Core.Git.QuoteArgument)))
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {stderr}");
            }
            return stdout.Result;
        }

        public string Git(params string[] args) => Run(Repo, args);

        public void ConfigUser(string email, string name)
        {
            Git("config", "user.email", email);
            Git("config", "user.name", name);
        }

        public void Write(string rel, string content)
        {
            var p = Path.Combine(Repo, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content);
        }

        public string Read(string rel) => File.ReadAllText(Path.Combine(Repo, rel));

        public void CommitAll(string message)
        {
            Git("add", "-A");
            Git("commit", "-q", "-m", message);
        }

        public RepoTracker Tracker() => new RepoTracker(Repo, _git, StateStore.ForRepository(Repo, Storage));

        public async Task<string[]> Tracked(RepoTracker t)
        {
            await t.RefreshAsync();
            return t.GetFiles().Select(f => f.Path).ToArray();
        }

        public void Dispose()
        {
            foreach (var dir in new[] { Repo, Storage })
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(f, FileAttributes.Normal); // git object files are read-only
                    }
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
