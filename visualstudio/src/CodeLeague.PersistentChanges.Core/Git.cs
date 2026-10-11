using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodeLeague.PersistentChanges.Core
{
    public sealed class GitResult
    {
        public GitResult(int code, string stdout, string stderr)
        {
            Code = code;
            Stdout = stdout;
            Stderr = stderr;
        }

        public int Code { get; }
        public string Stdout { get; }
        public string Stderr { get; }
    }

    public sealed class HeadInfo
    {
        public HeadInfo(string branch, string sha, string? operation)
        {
            Branch = branch;
            Sha = sha;
            Operation = operation;
        }

        /// <summary>Branch name, or "(detached)" when HEAD is detached outside of a rebase.</summary>
        public string Branch { get; }

        /// <summary>Commit SHA of HEAD, or "" for an unborn branch.</summary>
        public string Sha { get; }

        /// <summary>"merge", "rebase", "cherry-pick" or "revert" while such an operation is in progress.</summary>
        public string? Operation { get; }
    }

    public sealed class StatusEntry
    {
        public StatusEntry(string path, string? origPath, char x, char y)
        {
            Path = path;
            OrigPath = origPath;
            X = x;
            Y = y;
        }

        /// <summary>Repository-relative path with forward slashes.</summary>
        public string Path { get; }
        public string? OrigPath { get; }
        public char X { get; }
        public char Y { get; }

        public bool IsConflicted => X == 'U' || Y == 'U' || (X == 'A' && Y == 'A') || (X == 'D' && Y == 'D');
    }

    public sealed class ChangedFile
    {
        public ChangedFile(string path, string? origPath = null)
        {
            Path = path;
            OrigPath = origPath;
        }

        public string Path { get; }
        public string? OrigPath { get; }
    }

    /// <summary>
    /// Thin, read-only wrapper around the git CLI. Every command runs with --no-optional-locks so that
    /// background status checks never take the index lock and never interfere with the developer's git operations.
    /// Mirrors vscode/src/core/git.ts.
    /// </summary>
    public class Git
    {
        public const string DetachedBranch = "(detached)";

        public Git(string gitPath = "git")
        {
            GitPath = gitPath;
        }

        public string GitPath { get; }

        public virtual Task<GitResult> RunAsync(string cwd, params string[] args)
        {
            var all = new[] { "--no-optional-locks", "-c", "core.quotepath=false" }.Concat(args);
            var psi = new ProcessStartInfo(GitPath)
            {
                Arguments = string.Join(" ", all.Select(QuoteArgument)),
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            psi.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["LC_ALL"] = "C";

            return Task.Run(async () =>
            {
                Process process;
                try
                {
                    process = Process.Start(psi)!;
                }
                catch (Exception ex)
                {
                    return new GitResult(-1, "", ex.Message);
                }
                using (process)
                {
                    process.StandardInput.Close();
                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    var stdout = await stdoutTask.ConfigureAwait(false);
                    var stderr = await stderrTask.ConfigureAwait(false);
                    process.WaitForExit();
                    return new GitResult(process.ExitCode, stdout, stderr);
                }
            });
        }

        private async Task<string> RunOrThrowAsync(string cwd, params string[] args)
        {
            var r = await RunAsync(cwd, args).ConfigureAwait(false);
            if (r.Code != 0)
            {
                throw new InvalidOperationException($"git {string.Join(" ", args)} failed ({r.Code}): {r.Stderr.Trim()}");
            }
            return r.Stdout;
        }

        /// <summary>Returns the work-tree root (forward slashes) containing <paramref name="dir"/>, or null.</summary>
        public async Task<string?> GetRepositoryRootAsync(string dir)
        {
            if (!Directory.Exists(dir))
            {
                return null;
            }
            var r = await RunAsync(dir, "rev-parse", "--show-toplevel").ConfigureAwait(false);
            return r.Code == 0 ? r.Stdout.Trim() : null;
        }

        public async Task<string> GetGitDirAsync(string root) =>
            (await RunOrThrowAsync(root, "rev-parse", "--absolute-git-dir").ConfigureAwait(false)).Trim();

        public async Task<HeadInfo> GetHeadAsync(string root, string gitDir)
        {
            var symTask = RunAsync(root, "symbolic-ref", "-q", "--short", "HEAD");
            var shaTask = RunAsync(root, "rev-parse", "-q", "--verify", "HEAD");
            var sym = await symTask.ConfigureAwait(false);
            var sha = await shaTask.ConfigureAwait(false);
            var operation = DetectOperation(gitDir);
            var branch = sym.Code == 0 ? sym.Stdout.Trim() : "";
            if (branch.Length == 0 && operation == "rebase")
            {
                branch = ReadRebaseHeadName(gitDir) ?? "";
            }
            return new HeadInfo(
                branch.Length > 0 ? branch : DetachedBranch,
                sha.Code == 0 ? sha.Stdout.Trim() : "",
                operation);
        }

        public async Task<IReadOnlyList<StatusEntry>> GetStatusAsync(string root) =>
            ParseStatus(await RunOrThrowAsync(root, "status", "--porcelain=v1", "-z", "--untracked-files=all").ConfigureAwait(false));

        public async Task<string> GetUserEmailAsync(string root) =>
            (await RunAsync(root, "config", "user.email").ConfigureAwait(false)).Stdout.Trim();

        /// <summary>
        /// Files changed by the commits oldSha..newSha, but only if those commits are a plain linear continuation of
        /// oldSha made by the local user. Pulls of other people's work, merges, rebases, resets and amends return nothing.
        /// </summary>
        public async Task<IReadOnlyList<ChangedFile>> GetOwnCommitChangesAsync(string root, string oldSha, string newSha, int maxCommits = 50)
        {
            var log = await RunAsync(root, "log", $"--max-count={maxCommits + 1}", "--format=%H%x09%P%x09%ce", $"{oldSha}..{newSha}").ConfigureAwait(false);
            if (log.Code != 0)
            {
                return Array.Empty<ChangedFile>();
            }
            var commits = log.Stdout
                .Split('\n')
                .Where(l => l.Trim().Length > 0)
                .Select(l => l.Split('\t'))
                .Select(p => new
                {
                    Sha = p[0],
                    Parents = (p.Length > 1 ? p[1] : "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries),
                    Email = (p.Length > 2 ? p[2] : "").Trim().ToLowerInvariant(),
                })
                .ToList();
            if (commits.Count == 0 || commits.Count > maxCommits)
            {
                return Array.Empty<ChangedFile>();
            }
            var expected = newSha;
            foreach (var c in commits)
            {
                if (c.Sha != expected || c.Parents.Length != 1)
                {
                    return Array.Empty<ChangedFile>();
                }
                expected = c.Parents[0];
            }
            if (expected != oldSha)
            {
                return Array.Empty<ChangedFile>();
            }
            var userEmail = (await GetUserEmailAsync(root).ConfigureAwait(false)).ToLowerInvariant();
            if (userEmail.Length > 0 && commits.Any(c => c.Email != userEmail))
            {
                return Array.Empty<ChangedFile>();
            }
            var diff = await RunAsync(root, "diff", "--name-status", "-z", "-M", oldSha, newSha).ConfigureAwait(false);
            return diff.Code == 0 ? ParseNameStatus(diff.Stdout) : Array.Empty<ChangedFile>();
        }

        /// <summary>Parses <c>git status --porcelain=v1 -z</c> output.</summary>
        public static IReadOnlyList<StatusEntry> ParseStatus(string output)
        {
            var parts = output.Split('\0');
            var entries = new List<StatusEntry>();
            for (var i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p.Length < 4)
                {
                    continue;
                }
                char x = p[0], y = p[1];
                string? orig = null;
                if (x == 'R' || x == 'C' || y == 'R' || y == 'C')
                {
                    orig = i + 1 < parts.Length ? parts[++i] : null;
                }
                entries.Add(new StatusEntry(p.Substring(3), orig, x, y));
            }
            return entries;
        }

        /// <summary>Parses <c>git diff --name-status -z</c> output.</summary>
        public static IReadOnlyList<ChangedFile> ParseNameStatus(string output)
        {
            var parts = output.Split('\0');
            var files = new List<ChangedFile>();
            for (var i = 0; i < parts.Length; i++)
            {
                var code = parts[i];
                if (code.Length == 0)
                {
                    continue;
                }
                if (code[0] == 'R' || code[0] == 'C')
                {
                    var orig = i + 1 < parts.Length ? parts[++i] : "";
                    var path = i + 1 < parts.Length ? parts[++i] : "";
                    if (path.Length > 0)
                    {
                        files.Add(new ChangedFile(path, orig));
                    }
                }
                else
                {
                    var path = i + 1 < parts.Length ? parts[++i] : "";
                    if (path.Length > 0)
                    {
                        files.Add(new ChangedFile(path));
                    }
                }
            }
            return files;
        }

        public static string? DetectOperation(string gitDir)
        {
            bool Has(string name) => File.Exists(Path.Combine(gitDir, name)) || Directory.Exists(Path.Combine(gitDir, name));
            if (Has("rebase-merge") || Has("rebase-apply")) return "rebase";
            if (Has("MERGE_HEAD")) return "merge";
            if (Has("CHERRY_PICK_HEAD")) return "cherry-pick";
            if (Has("REVERT_HEAD")) return "revert";
            return null;
        }

        private static string? ReadRebaseHeadName(string gitDir)
        {
            foreach (var dir in new[] { "rebase-merge", "rebase-apply" })
            {
                try
                {
                    var file = Path.Combine(gitDir, dir, "head-name");
                    if (!File.Exists(file))
                    {
                        continue;
                    }
                    var reference = File.ReadAllText(file).Trim();
                    if (reference.Length > 0 && reference != "detached HEAD")
                    {
                        return reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference.Substring("refs/heads/".Length) : reference;
                    }
                }
                catch (IOException)
                {
                }
            }
            return null;
        }

        /// <summary>Quotes a command-line argument following the MSVCRT / CommandLineToArgvW rules.</summary>
        public static string QuoteArgument(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return arg;
            }
            var sb = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                }
                else
                {
                    sb.Append('\\', backslashes);
                }
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            return sb.Append('"').ToString();
        }
    }
}
