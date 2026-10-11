using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using CodeLeague.PersistentChanges.Core;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using SolutionEvents = Microsoft.VisualStudio.Shell.Events.SolutionEvents;
using Task = System.Threading.Tasks.Task;

namespace CodeLeague.PersistentChanges.VisualStudio
{
    /// <summary>
    /// Finds the git repositories of the open solution / folder and keeps one <see cref="RepoTracker"/> per repository
    /// up to date using file-system notifications (debounced) plus a periodic safety refresh.
    /// </summary>
    public sealed class VsTrackerManager : IDisposable
    {
        private const int RefreshDebounceMs = 500;
        private const int SafetyRefreshMs = 30_000;

        /// <summary>Paths whose changes alone never need a refresh (build output etc.); git status remains the source of truth.</summary>
        private static readonly Regex Noise = new Regex(
            @"[\\/](node_modules|bin|obj|out|dist|packages|\.vs|\.idea)[\\/]|[\\/]\.git[\\/](objects|logs|lfs|modules|hooks)[\\/]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly AsyncPackage _package;
        private readonly Git _git = new Git(FindGit());
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private Timer? _safetyTimer;
        private bool _disposed;

        public VsTrackerManager(AsyncPackage package)
        {
            _package = package;
        }

        /// <summary>Raised (on any thread) when the set of repositories or any tracked list changes.</summary>
        public event EventHandler? Changed;

        public IReadOnlyList<RepoTracker> Trackers
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Values.Select(e => e.Tracker).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
        }

        public JoinableTaskFactory JoinableTaskFactory => _package.JoinableTaskFactory;

        public IServiceProvider ServiceProvider => _package;

        public async Task StartAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            SolutionEvents.OnAfterOpenSolution += OnSolutionChanged;
            SolutionEvents.OnAfterCloseSolution += OnSolutionChanged;
            SolutionEvents.OnAfterOpenProject += OnSolutionChanged;
            SolutionEvents.OnAfterBackgroundSolutionLoadComplete += OnSolutionChanged;
            SolutionEvents.OnAfterOpenFolder += OnSolutionChanged;
            SolutionEvents.OnAfterCloseFolder += OnSolutionChanged;
            _safetyTimer = new Timer(_ => _ = RefreshAllAsync(), null, SafetyRefreshMs, SafetyRefreshMs);
            await RescanAsync();
        }

        public Task RefreshAllAsync() => Task.WhenAll(Trackers.Select(t => t.RefreshAsync()));

        public RepoTracker? TrackerFor(string path) =>
            Trackers.Where(t => t.ToRelative(path) != null).OrderByDescending(t => t.Root.Length).FirstOrDefault();

        private void OnSolutionChanged(object sender, EventArgs e) =>
            _package.JoinableTaskFactory.RunAsync(RescanAsync).FileAndForget("CodeLeague/PersistentChanges/Rescan");

        /// <summary>Collects the solution directory and all project directories and maps them to git work trees.</summary>
        private async Task RescanAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var directories = GetSolutionDirectories();
            await System.Threading.Tasks.TaskScheduler.Default;

            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in directories.OrderBy(d => d.Length))
            {
                if (roots.Any(r => IsUnder(dir, r)))
                {
                    continue; // avoid one git call per project in large solutions
                }
                var root = await _git.GetRepositoryRootAsync(dir);
                if (root != null)
                {
                    roots.Add(Path.GetFullPath(root.Replace('/', Path.DirectorySeparatorChar)));
                }
            }

            var added = new List<RepoTracker>();
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                foreach (var key in _entries.Keys.Where(k => !roots.Contains(k)).ToList())
                {
                    _entries[key].Dispose();
                    _entries.Remove(key);
                }
                foreach (var root in roots.Where(r => !_entries.ContainsKey(r)))
                {
                    var entry = new Entry(this, new RepoTracker(root, _git, StateStore.ForRepository(root)));
                    _entries[root] = entry;
                    added.Add(entry.Tracker);
                }
            }
            Changed?.Invoke(this, EventArgs.Empty);
            await Task.WhenAll(added.Select(t => t.RefreshAsync()));
        }

        private List<string> GetSolutionDirectories()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new List<string>();
            if (!(Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution solution))
            {
                return result;
            }
            if (ErrorHandler.Succeeded(solution.GetSolutionInfo(out var solutionDir, out _, out _)) && !string.IsNullOrEmpty(solutionDir))
            {
                result.Add(solutionDir);
            }
            var flags = (uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION;
            var none = Guid.Empty;
            if (ErrorHandler.Succeeded(solution.GetProjectEnum(flags, ref none, out var projects)))
            {
                var buffer = new IVsHierarchy[1];
                while (projects.Next(1, buffer, out var fetched) == VSConstants.S_OK && fetched == 1)
                {
                    if (ErrorHandler.Succeeded(buffer[0].GetProperty((uint)VSConstants.VSITEMID.Root, (int)__VSHPROPID.VSHPROPID_ProjectDir, out var dir))
                        && dir is string d && d.Length > 0)
                    {
                        result.Add(d);
                    }
                }
            }
            return result.Where(Directory.Exists).Select(d => Path.GetFullPath(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsUnder(string path, string root) =>
            path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        /// <summary>git on PATH, otherwise the copy that ships with Visual Studio.</summary>
        private static string FindGit()
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    if (dir.Length > 0 && File.Exists(Path.Combine(dir.Trim('"'), "git.exe")))
                    {
                        return Path.Combine(dir.Trim('"'), "git.exe");
                    }
                }
                catch (ArgumentException)
                {
                }
            }
            var devenvDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!;
            var bundled = Path.Combine(devenvDir, @"CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe");
            return File.Exists(bundled) ? bundled : "git";
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                foreach (var e in _entries.Values)
                {
                    e.Dispose();
                }
                _entries.Clear();
            }
            _safetyTimer?.Dispose();
            SolutionEvents.OnAfterOpenSolution -= OnSolutionChanged;
            SolutionEvents.OnAfterCloseSolution -= OnSolutionChanged;
            SolutionEvents.OnAfterOpenProject -= OnSolutionChanged;
            SolutionEvents.OnAfterBackgroundSolutionLoadComplete -= OnSolutionChanged;
            SolutionEvents.OnAfterOpenFolder -= OnSolutionChanged;
            SolutionEvents.OnAfterCloseFolder -= OnSolutionChanged;
        }

        private sealed class Entry : IDisposable
        {
            private readonly VsTrackerManager _owner;
            private readonly FileSystemWatcher _watcher;
            private readonly Timer _debounce;

            public Entry(VsTrackerManager owner, RepoTracker tracker)
            {
                _owner = owner;
                Tracker = tracker;
                _debounce = new Timer(_ => _ = Tracker.RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
                tracker.Changed += OnTrackerChanged;
                _watcher = new FileSystemWatcher(tracker.Root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                _watcher.Changed += OnFsEvent;
                _watcher.Created += OnFsEvent;
                _watcher.Deleted += OnFsEvent;
                _watcher.Renamed += OnRenamed;
                _watcher.Error += (_, _) => Schedule(); // buffer overflow: just rescan
                _watcher.EnableRaisingEvents = true;
            }

            public RepoTracker Tracker { get; }

            private void OnTrackerChanged(object sender, EventArgs e) => _owner.Changed?.Invoke(_owner, EventArgs.Empty);

            private void OnFsEvent(object sender, FileSystemEventArgs e)
            {
                if (!Noise.IsMatch(e.FullPath))
                {
                    Schedule();
                }
            }

            private void OnRenamed(object sender, RenamedEventArgs e)
            {
                if (!Noise.IsMatch(e.FullPath))
                {
                    Tracker.NotifyRenamed(e.OldFullPath, e.FullPath);
                    Schedule();
                }
            }

            private void Schedule() => _debounce.Change(RefreshDebounceMs, Timeout.Infinite);

            public void Dispose()
            {
                Tracker.Changed -= OnTrackerChanged;
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _debounce.Dispose();
            }
        }
    }
}
