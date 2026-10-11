using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodeLeague.PersistentChanges.Core
{
    public enum FileStatus
    {
        Modified,
        Added,
        Deleted,
        Renamed,
        Untracked,
        Conflicted,
        Committed,
    }

    public sealed class TrackedFileView
    {
        public TrackedFileView(string path, string absolutePath, FileStatus status, string firstSeen, string lastSeen)
        {
            Path = path;
            AbsolutePath = absolutePath;
            Status = status;
            FirstSeen = firstSeen;
            LastSeen = lastSeen;
        }

        /// <summary>Repository-relative path with forward slashes.</summary>
        public string Path { get; }
        public string AbsolutePath { get; }
        public FileStatus Status { get; }
        public string FirstSeen { get; }
        public string LastSeen { get; }
    }

    /// <summary>
    /// Tracks files changed in one repository work tree, per branch, independently of commits.
    /// This is a port of vscode/src/core/repoTracker.ts – keep both implementations in sync (see docs/ARCHITECTURE.md).
    /// <list type="bullet">
    /// <item>Every refresh reads HEAD and <c>git status</c>; every dirty file is a candidate.</item>
    /// <item>If HEAD moved on the same branch through the developer's own linear commits, the files of those commits are candidates too.</item>
    /// <item>While a merge / rebase / cherry-pick / revert is in progress, only work-tree changes and conflicts are candidates.</item>
    /// <item>A candidate is skipped if its fingerprint equals the one recorded in the baseline snapshot (dirty at Reset and untouched since).</item>
    /// <item>Files changed by a checkout never appear in <c>git status</c>, so branch switches don't add files.</item>
    /// </list>
    /// </summary>
    public sealed class RepoTracker
    {
        private static readonly long UnixEpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        private readonly Git _git;
        private readonly StateStore _store;
        private readonly Func<DateTime> _utcNow;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly object _pendingLock = new object();
        private Task? _pendingRefresh;
        private string? _gitDir;
        private volatile HeadInfo? _head;
        private volatile Dictionary<string, FileStatus> _liveStatus = new Dictionary<string, FileStatus>();
        private string _lastSignature = "";

        public RepoTracker(string root, Git git, StateStore store, Func<DateTime>? utcNow = null)
        {
            Root = Path.GetFullPath(root.Replace('/', Path.DirectorySeparatorChar));
            _git = git;
            _store = store;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public event EventHandler? Changed;

        public string Root { get; }

        public string Name => Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar));

        public string? Branch => _head?.Branch;

        public string? Operation => _head?.Operation;

        public string? Error { get; private set; }

        /// <summary>Re-scans the repository. Concurrent calls are coalesced; the task completes after a scan that started after the call.</summary>
        public Task RefreshAsync()
        {
            lock (_pendingLock)
            {
                if (_pendingRefresh != null)
                {
                    return _pendingRefresh;
                }
                return _pendingRefresh = RunExclusiveAsync(() =>
                {
                    lock (_pendingLock)
                    {
                        _pendingRefresh = null;
                    }
                    return DoRefreshAsync();
                });
            }
        }

        /// <summary>
        /// Clears the list for the current branch and makes all currently dirty files part of the new baseline.
        /// Only the extension's state file is written; files and git are never touched.
        /// </summary>
        public Task ResetAsync() => RunExclusiveAsync(async () =>
        {
            var head = await ReadHeadAsync().ConfigureAwait(false);
            var status = await _git.GetStatusAsync(Root).ConfigureAwait(false);
            var snapshot = new Dictionary<string, string>();
            foreach (var e in status)
            {
                snapshot[e.Path] = Fingerprint(e.Path);
            }
            _store.Update(s =>
            {
                s.Branches[head.Branch] = new BranchState { Baseline = Baseline.Create(snapshot, _utcNow()), LastHead = head.Sha };
                return true;
            });
            _head = head;
            SetLiveStatus(status);
            EmitIfChanged(force: true);
        });

        /// <summary>Removes single files (or folders, by prefix) from the current branch's list.</summary>
        public void Remove(IEnumerable<string> relativePaths)
        {
            var branch = _head?.Branch;
            if (branch == null)
            {
                return;
            }
            var paths = relativePaths.ToList();
            _store.Update(s =>
            {
                if (!s.Branches.TryGetValue(branch, out var b))
                {
                    return false;
                }
                var remove = b.Files.Keys.Where(k => paths.Any(p => k == p || k.StartsWith(p.TrimEnd('/') + "/", StringComparison.Ordinal))).ToList();
                remove.ForEach(k => b.Files.Remove(k));
                return remove.Count > 0;
            });
            EmitIfChanged();
        }

        /// <summary>Called by the IDE when it renamed/moved a file or folder, so the entry follows instead of being duplicated.</summary>
        public void NotifyRenamed(string oldAbsolute, string newAbsolute)
        {
            var oldRel = ToRelative(oldAbsolute);
            var newRel = ToRelative(newAbsolute);
            var branch = _head?.Branch;
            if (oldRel == null || newRel == null || branch == null)
            {
                return;
            }
            _store.Update(s =>
            {
                if (!s.Branches.TryGetValue(branch, out var b))
                {
                    return false;
                }
                var changed = false;
                foreach (var key in b.Files.Keys.ToList())
                {
                    string? target = null;
                    if (key == oldRel)
                    {
                        target = newRel;
                    }
                    else if (key.StartsWith(oldRel + "/", StringComparison.Ordinal))
                    {
                        target = newRel + key.Substring(oldRel.Length);
                    }
                    if (target != null)
                    {
                        if (!b.Files.ContainsKey(target))
                        {
                            b.Files[target] = b.Files[key];
                        }
                        b.Files.Remove(key);
                        changed = true;
                    }
                }
                return changed;
            });
            EmitIfChanged();
        }

        /// <summary>The tracked files of the current branch, sorted by path.</summary>
        public IReadOnlyList<TrackedFileView> GetFiles()
        {
            var branch = _head?.Branch;
            if (branch == null)
            {
                return Array.Empty<TrackedFileView>();
            }
            var files = _store.Read(s => s.Branches.TryGetValue(branch, out var b)
                ? b.Files.Select(kv => new KeyValuePair<string, TrackedFile>(kv.Key, kv.Value)).ToList()
                : new List<KeyValuePair<string, TrackedFile>>());
            var live = _liveStatus;
            return files
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv =>
                {
                    var absolute = ToAbsolute(kv.Key);
                    var status = live.TryGetValue(kv.Key, out var s) ? s : File.Exists(absolute) || Directory.Exists(absolute) ? FileStatus.Committed : FileStatus.Deleted;
                    return new TrackedFileView(kv.Key, absolute, status, kv.Value.FirstSeen, kv.Value.LastSeen);
                })
                .ToList();
        }

        public string? ToRelative(string absolute)
        {
            string full;
            try
            {
                full = Path.GetFullPath(absolute);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
            var prefix = Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return full.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/').TrimEnd('/');
        }

        public string ToAbsolute(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        private async Task DoRefreshAsync()
        {
            var changed = _store.ReloadIfChanged();
            try
            {
                var previous = _head;
                var head = await ReadHeadAsync().ConfigureAwait(false);
                var status = await _git.GetStatusAsync(Root).ConfigureAwait(false);

                var candidates = new List<ChangedFile>();
                foreach (var e in status)
                {
                    if (IsUntrackedTooling(e))
                    {
                        continue;
                    }
                    if (head.Operation != null && e.Y == ' ' && !e.IsConflicted)
                    {
                        // Staged by git itself while merging/rebasing changes from elsewhere.
                        continue;
                    }
                    candidates.Add(new ChangedFile(e.Path, e.OrigPath));
                }

                var lastSha = previous != null && previous.Branch == head.Branch && previous.Operation == null
                    ? previous.Sha
                    : _store.Read(s => s.Branches.TryGetValue(head.Branch, out var b) ? b.LastHead : null);
                if (head.Operation == null && !string.IsNullOrEmpty(lastSha) && head.Sha.Length > 0 && lastSha != head.Sha)
                {
                    candidates.AddRange(await _git.GetOwnCommitChangesAsync(Root, lastSha!, head.Sha).ConfigureAwait(false));
                }

                var timestamp = StateStore.ToIso(_utcNow());
                changed |= _store.Update(s =>
                {
                    var dirty = false;
                    if (!s.Branches.TryGetValue(head.Branch, out var b))
                    {
                        b = s.Branches[head.Branch] = new BranchState { Baseline = Baseline.Create(null, _utcNow()) };
                        dirty = true;
                    }
                    if (b.LastHead != head.Sha && head.Operation == null)
                    {
                        b.LastHead = head.Sha;
                        dirty = true;
                    }
                    foreach (var c in candidates)
                    {
                        dirty |= Track(b, c, timestamp);
                    }
                    return dirty;
                });

                _head = head;
                SetLiveStatus(status);
                Error = null;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                changed = true;
            }
            EmitIfChanged(changed);
        }

        private bool Track(BranchState b, ChangedFile c, string timestamp)
        {
            var fp = Fingerprint(c.Path);
            if (b.Baseline.Snapshot.TryGetValue(c.Path, out var baselineFp) && baselineFp == fp)
            {
                return false;
            }
            if (c.OrigPath != null && c.OrigPath != c.Path && b.Files.TryGetValue(c.OrigPath, out var renamed) && !b.Files.ContainsKey(c.Path))
            {
                b.Files.Remove(c.OrigPath);
                renamed.Fingerprint = fp;
                renamed.LastSeen = timestamp;
                b.Files[c.Path] = renamed;
                return true;
            }
            if (!b.Files.TryGetValue(c.Path, out var existing))
            {
                b.Files[c.Path] = new TrackedFile { FirstSeen = timestamp, LastSeen = timestamp, Fingerprint = fp };
                return true;
            }
            if (existing.Fingerprint != fp)
            {
                existing.Fingerprint = fp;
                existing.LastSeen = timestamp;
                return true;
            }
            return false;
        }

        private async Task<HeadInfo> ReadHeadAsync()
        {
            _gitDir ??= await _git.GetGitDirAsync(Root).ConfigureAwait(false);
            return await _git.GetHeadAsync(Root, _gitDir).ConfigureAwait(false);
        }

        /// <summary>"size:mtimeMs" – identical to the TypeScript implementation so both IDEs agree on baselines.</summary>
        private string Fingerprint(string relativePath)
        {
            var absolute = ToAbsolute(relativePath);
            var file = new FileInfo(absolute);
            if (file.Exists)
            {
                return $"{file.Length}:{(file.LastWriteTimeUtc.Ticks - UnixEpochTicks) / TimeSpan.TicksPerMillisecond}";
            }
            var dir = new DirectoryInfo(absolute);
            return dir.Exists ? $"0:{(dir.LastWriteTimeUtc.Ticks - UnixEpochTicks) / TimeSpan.TicksPerMillisecond}" : "deleted";
        }

        private void SetLiveStatus(IReadOnlyList<StatusEntry> status)
        {
            var map = new Dictionary<string, FileStatus>();
            foreach (var e in status)
            {
                map[e.Path] = ToFileStatus(e);
            }
            _liveStatus = map;
        }

        private void EmitIfChanged(bool force = false)
        {
            var signature = string.Join("|", new[] { _head?.Branch, _head?.Operation, Error }
                .Concat(GetFiles().Select(f => f.Path + ":" + f.Status)));
            lock (_pendingLock)
            {
                if (!force && signature == _lastSignature)
                {
                    return;
                }
                _lastSignature = signature;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Runs <paramref name="action"/> on the thread pool, one operation at a time.</summary>
        private Task RunExclusiveAsync(Func<Task> action) => Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await action().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        });

        /// <summary>Folders whose untracked files are IDE state or build output, never "files I am working on" (for repos with an incomplete .gitignore).</summary>
        private static readonly HashSet<string> ToolingFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".vs", ".idea", "bin", "obj", "node_modules" };

        public static bool IsUntrackedTooling(StatusEntry e)
        {
            if (e.X != '?' || e.Y != '?')
            {
                return false;
            }
            var segments = e.Path.Split('/');
            return segments.Take(segments.Length - 1).Any(ToolingFolders.Contains);
        }

        public static FileStatus ToFileStatus(StatusEntry e)
        {
            if (e.IsConflicted) return FileStatus.Conflicted;
            if (e.X == '?' && e.Y == '?') return FileStatus.Untracked;
            if (e.X == 'D' || e.Y == 'D') return FileStatus.Deleted;
            if (e.X == 'R' || e.Y == 'R') return FileStatus.Renamed;
            if (e.X == 'A' || e.X == 'C') return FileStatus.Added;
            return FileStatus.Modified;
        }
    }
}
