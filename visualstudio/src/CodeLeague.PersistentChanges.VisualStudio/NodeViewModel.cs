using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CodeLeague.PersistentChanges.Core;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;

namespace CodeLeague.PersistentChanges.VisualStudio
{
    public enum NodeKind
    {
        Repository,
        Folder,
        File,
    }

    /// <summary>One row of the Persistent Changes tree: a repository, a folder or a tracked file.</summary>
    public sealed class NodeViewModel : INotifyPropertyChanged
    {
        private static readonly Dictionary<FileStatus, (string Letter, string Text, Brush? Brush)> StatusInfo = new Dictionary<FileStatus, (string, string, Brush?)>
        {
            [FileStatus.Modified] = ("M", "Modified (uncommitted)", Frozen(0xD1, 0x9A, 0x2A)),
            [FileStatus.Added] = ("A", "Added (staged)", Frozen(0x4C, 0xA6, 0x4C)),
            [FileStatus.Deleted] = ("D", "Deleted", Frozen(0xE0, 0x4F, 0x4F)),
            [FileStatus.Renamed] = ("R", "Renamed", Frozen(0x4F, 0x8F, 0xE0)),
            [FileStatus.Untracked] = ("U", "Untracked (new file)", Frozen(0x4C, 0xA6, 0x4C)),
            [FileStatus.Conflicted] = ("!", "Conflicted", Frozen(0xE0, 0x4F, 0x4F)),
            [FileStatus.Committed] = ("✓", "Committed – no uncommitted changes", null),
        };

        private bool _isExpanded = true;
        private bool _isSelected;

        private NodeViewModel(NodeKind kind, RepoTracker tracker, string key, string name)
        {
            Kind = kind;
            Tracker = tracker;
            Key = key;
            Name = name;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public NodeKind Kind { get; }
        public RepoTracker Tracker { get; }

        /// <summary>Stable identity used to keep expansion/selection across refreshes.</summary>
        public string Key { get; }
        public string Name { get; }
        public string Description { get; private set; } = "";
        public string ToolTip { get; private set; } = "";
        public ImageMoniker Moniker { get; private set; }
        public string StatusLetter { get; private set; } = "";
        public Brush? StatusBrush { get; private set; }
        public TextDecorationCollection? NameDecorations { get; private set; }
        public ObservableCollection<NodeViewModel> Children { get; } = new ObservableCollection<NodeViewModel>();

        /// <summary>Repository-relative path of a folder or file.</summary>
        public string RelativePath { get; private set; } = "";
        public TrackedFileView? File { get; private set; }
        public string FullPath => File?.AbsolutePath ?? (Kind == NodeKind.Repository ? Tracker.Root : Tracker.ToAbsolute(RelativePath));

        public bool IsExpanded
        {
            get => _isExpanded;
            set => Set(ref _isExpanded, value, nameof(IsExpanded));
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value, nameof(IsSelected));
        }

        public static NodeViewModel Repository(RepoTracker tracker, IReadOnlyList<TrackedFileView> files)
        {
            var node = new NodeViewModel(NodeKind.Repository, tracker, "repo:" + tracker.Root, tracker.Name)
            {
                Moniker = KnownMonikers.Repository,
                Description = $"{tracker.Branch}{(tracker.Operation != null ? $" ({tracker.Operation})" : "")} · {CountText(files.Count)}",
                ToolTip = $"{tracker.Root}\nBranch: {tracker.Branch}{(tracker.Error != null ? "\n\n" + tracker.Error : "")}",
            };
            return node;
        }

        public static NodeViewModel Folder(RepoTracker tracker, string relativePath, string label) =>
            new NodeViewModel(NodeKind.Folder, tracker, $"folder:{tracker.Root}:{relativePath}", label)
            {
                RelativePath = relativePath,
                Moniker = KnownMonikers.FolderClosed,
                ToolTip = relativePath,
            };

        public static NodeViewModel ForFile(RepoTracker tracker, TrackedFileView file, bool showDirectory, Func<string, ImageMoniker> iconFor)
        {
            var info = StatusInfo[file.Status];
            var slash = file.Path.LastIndexOf('/');
            return new NodeViewModel(NodeKind.File, tracker, $"file:{tracker.Root}:{file.Path}", slash >= 0 ? file.Path.Substring(slash + 1) : file.Path)
            {
                File = file,
                RelativePath = file.Path,
                Moniker = iconFor(file.Path),
                Description = showDirectory && slash > 0 ? file.Path.Substring(0, slash) : "",
                StatusLetter = info.Letter,
                StatusBrush = info.Brush,
                NameDecorations = file.Status == FileStatus.Deleted ? TextDecorations.Strikethrough : null,
                ToolTip = $"{file.Path}\n{info.Text}\nTracked since {FormatDate(file.FirstSeen)} · last change seen {FormatDate(file.LastSeen)}",
            };
        }

        /// <summary>Groups files by folder; chains of single-child folders are compacted ("src/components") like VS Code's SCM view.</summary>
        public static IEnumerable<NodeViewModel> BuildFolderTree(RepoTracker tracker, IReadOnlyList<NodeViewModel> files)
        {
            var root = new Dir();
            foreach (var f in files)
            {
                var segments = f.RelativePath.Split('/');
                var d = root;
                foreach (var s in segments.Take(segments.Length - 1))
                {
                    if (!d.Dirs.TryGetValue(s, out var next))
                    {
                        d.Dirs[s] = next = new Dir();
                    }
                    d = next;
                }
                d.Files.Add(f);
            }
            return ToNodes(tracker, root, "");
        }

        private static IEnumerable<NodeViewModel> ToNodes(RepoTracker tracker, Dir d, string prefix)
        {
            foreach (var kv in d.Dirs.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var label = kv.Key;
                var path = prefix.Length > 0 ? prefix + "/" + kv.Key : kv.Key;
                var child = kv.Value;
                while (child.Files.Count == 0 && child.Dirs.Count == 1)
                {
                    var only = child.Dirs.First();
                    label += "/" + only.Key;
                    path += "/" + only.Key;
                    child = only.Value;
                }
                var folder = Folder(tracker, path, label);
                foreach (var c in ToNodes(tracker, child, path))
                {
                    folder.Children.Add(c);
                }
                yield return folder;
            }
            foreach (var f in d.Files)
            {
                yield return f;
            }
        }

        public IEnumerable<NodeViewModel> SelfAndDescendants() => new[] { this }.Concat(Children.SelectMany(c => c.SelfAndDescendants()));

        public static string CountText(int n) => n == 1 ? "1 tracked file" : $"{n} tracked files";

        private static string FormatDate(string iso) =>
            DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime().ToString("g") : iso;

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private void Set<T>(ref T field, T value, string name)
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }

        private sealed class Dir
        {
            public SortedDictionary<string, Dir> Dirs { get; } = new SortedDictionary<string, Dir>(StringComparer.OrdinalIgnoreCase);
            public List<NodeViewModel> Files { get; } = new List<NodeViewModel>();
        }
    }
}
