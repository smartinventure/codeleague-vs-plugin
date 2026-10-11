using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CodeLeague.PersistentChanges.Core;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using Microsoft.VisualStudio.Threading;
using Interlocked = System.Threading.Interlocked;

namespace CodeLeague.PersistentChanges.VisualStudio
{
    public partial class PersistentChangesControl : UserControl
    {
        private const string SettingsCollection = @"CodeLeague\PersistentChanges";
        private const string GroupByFolderSetting = "GroupByFolder";

        private readonly VsTrackerManager _manager;
        private readonly Dictionary<string, ImageMoniker> _iconCache = new Dictionary<string, ImageMoniker>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _collapsed = new HashSet<string>();
        private bool _groupByFolder = true;
        private int _renderQueued;

        public PersistentChangesControl(VsTrackerManager manager)
        {
            _manager = manager;
            InitializeComponent();
            _groupByFolder = ReadGroupSetting();
            GroupToggle.IsChecked = _groupByFolder;
            _manager.Changed += (_, _) => QueueRender();
            ThreadHelper.ThrowIfNotOnUIThread();
            Render();
            _ = _manager.RefreshAllAsync();
        }

        /// <summary>Called from any thread; coalesces bursts of change notifications into one render on the UI thread.</summary>
        private void QueueRender()
        {
            if (Interlocked.Exchange(ref _renderQueued, 1) == 1)
            {
                return;
            }
            _manager.JoinableTaskFactory.RunAsync(async () =>
            {
                await _manager.JoinableTaskFactory.SwitchToMainThreadAsync();
                Interlocked.Exchange(ref _renderQueued, 0);
                Render();
            }).FileAndForget("CodeLeague/PersistentChanges/Render");
        }

        /// <summary>Rebuilds the tree from the trackers, keeping expansion and selection.</summary>
        private void Render()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var selectedKey = (Tree.SelectedItem as NodeViewModel)?.Key;
            foreach (var n in Tree.Items.OfType<NodeViewModel>().SelectMany(n => n.SelfAndDescendants()))
            {
                if (n.IsExpanded) _collapsed.Remove(n.Key); else _collapsed.Add(n.Key);
            }

            var trackers = _manager.Trackers;
            var roots = new List<NodeViewModel>();
            var total = 0;
            foreach (var tracker in trackers)
            {
                var files = tracker.GetFiles();
                total += files.Count;
                var fileNodes = files.Select(f => NodeViewModel.ForFile(tracker, f, !_groupByFolder, IconFor)).ToList();
                var children = _groupByFolder ? NodeViewModel.BuildFolderTree(tracker, fileNodes).ToList() : fileNodes;
                if (trackers.Count == 1)
                {
                    roots.AddRange(children);
                }
                else
                {
                    var repo = NodeViewModel.Repository(tracker, files);
                    children.ForEach(repo.Children.Add);
                    roots.Add(repo);
                }
            }
            foreach (var n in roots.SelectMany(r => r.SelfAndDescendants()))
            {
                n.IsExpanded = !_collapsed.Contains(n.Key);
                n.IsSelected = n.Key == selectedKey;
            }
            Tree.ItemsSource = roots;

            if (trackers.Count == 0)
            {
                HeaderText.Text = "No Git repository";
                EmptyText.Text = "Open a solution or folder that is inside a Git repository. Files you change will be listed here and stay listed after commit and push.";
            }
            else if (trackers.Count == 1)
            {
                var t = trackers[0];
                HeaderText.Text = $"Repository: {t.Name}    Branch: {t.Branch ?? "…"}{(t.Operation != null ? $" ({t.Operation} in progress)" : "")}";
                HeaderText.ToolTip = t.Root;
                EmptyText.Text = t.Error != null
                    ? "⚠ " + t.Error
                    : total == 0 ? "No changes tracked yet on this branch. Modified files will appear here and stay after commit and push until you click Reset Tracking." : "";
            }
            else
            {
                HeaderText.Text = $"{trackers.Count} repositories";
                HeaderText.ToolTip = string.Join("\n", trackers.Select(t => $"{t.Name}: {t.Branch}"));
                EmptyText.Text = total == 0 ? "No changes tracked yet." : "";
            }
            EmptyText.Visibility = string.IsNullOrEmpty(EmptyText.Text) ? Visibility.Collapsed : Visibility.Visible;
            FooterText.Text = NodeViewModel.CountText(total);
            ResetButton.IsEnabled = trackers.Count > 0;
        }

        private ImageMoniker IconFor(string relativePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var ext = Path.GetExtension(relativePath);
            if (_iconCache.TryGetValue(ext, out var moniker))
            {
                return moniker;
            }
            moniker = KnownMonikers.Document;
            if (Package.GetGlobalService(typeof(SVsImageService)) is IVsImageService2 images)
            {
                var m = images.GetImageMonikerForFile(Path.GetFileName(relativePath));
                if (m.Guid != Guid.Empty)
                {
                    moniker = m;
                }
            }
            return _iconCache[ext] = moniker;
        }

        private NodeViewModel? Selected => Tree.SelectedItem as NodeViewModel;

        private void TreeItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // The event bubbles through all ancestor TreeViewItems; only the clicked (selected) one counts.
            if (sender is TreeViewItem item && item.IsSelected && item.DataContext is NodeViewModel node && node.Kind == NodeKind.File)
            {
                Open(node);
                e.Handled = true;
            }
        }

        private void Tree_KeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Selected is not NodeViewModel node)
            {
                return;
            }
            if (e.Key == Key.Enter && node.Kind == NodeKind.File)
            {
                Open(node);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && node.Kind != NodeKind.Repository)
            {
                node.Tracker.Remove(new[] { node.RelativePath });
                e.Handled = true;
            }
        }

        private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Select the item under the mouse so the context menu acts on it.
            var element = e.OriginalSource as DependencyObject;
            while (element != null && !(element is TreeViewItem))
            {
                element = VisualTreeHelper.GetParent(element);
            }
            if (element is TreeViewItem item)
            {
                item.IsSelected = true;
                item.Focus();
            }
        }

        private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var node = Selected;
            var menu = Tree.ContextMenu!;
            menu.Items.Clear();
            if (node == null)
            {
                e.Handled = true;
                return;
            }
            void Add(string header, Action action, bool enabled = true)
            {
                var item = new MenuItem { Header = header, IsEnabled = enabled };
                item.Click += (_, _) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    action();
                };
                menu.Items.Add(item);
            }
            if (node.Kind == NodeKind.File)
            {
                Add("_Open", () => Open(node), node.File!.Status != FileStatus.Deleted);
                menu.Items.Add(new Separator());
            }
            if (node.Kind != NodeKind.Repository || Directory.Exists(node.FullPath))
            {
                Add("Open Containing _Folder", () => RevealInExplorer(node));
                Add("Copy Full _Path", () => Clipboard.SetText(node.FullPath));
            }
            if (node.Kind != NodeKind.Repository)
            {
                Add("Copy _Relative Path", () => Clipboard.SetText(node.RelativePath));
                menu.Items.Add(new Separator());
                Add("Remove from _List", () => node.Tracker.Remove(new[] { node.RelativePath }));
            }
            else
            {
                menu.Items.Add(new Separator());
                Add("Reset _Tracking…", () => ConfirmAndReset(new[] { node.Tracker }));
            }
        }

        private void Open(NodeViewModel node)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var file = node.File!;
            if (!System.IO.File.Exists(file.AbsolutePath))
            {
                VsShellUtilities.ShowMessageBox(_manager.ServiceProvider, $"{file.Path} has been deleted.", PersistentChangesToolWindow.Title,
                    OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
            try
            {
                VsShellUtilities.OpenDocument(_manager.ServiceProvider, file.AbsolutePath);
            }
            catch (Exception ex) when (!ErrorHandler.IsCriticalException(ex))
            {
                VsShellUtilities.ShowMessageBox(_manager.ServiceProvider, ex.Message, PersistentChangesToolWindow.Title,
                    OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }

        private static void RevealInExplorer(NodeViewModel node)
        {
            var path = node.FullPath;
            if (System.IO.File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else
            {
                var dir = path;
                while (!Directory.Exists(dir) && Path.GetDirectoryName(dir) is string parent && parent != dir)
                {
                    dir = parent;
                }
                Process.Start("explorer.exe", $"\"{dir}\"");
            }
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var trackers = _manager.Trackers;
            if (trackers.Count > 1 && Selected is NodeViewModel node)
            {
                trackers = new[] { node.Tracker };
            }
            ConfirmAndReset(trackers);
        }

        private void ConfirmAndReset(IReadOnlyList<RepoTracker> trackers)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (trackers.Count == 0)
            {
                return;
            }
            var what = trackers.Count == 1 ? $"'{trackers[0].Name}' (branch {trackers[0].Branch})" : $"all {trackers.Count} repositories";
            var answer = VsShellUtilities.ShowMessageBox(
                _manager.ServiceProvider,
                $"Clear the Persistent Changes list for {what}?\n\nThis only clears the tracked file list. It does not affect your files, staged changes, commits or branches. Files changed from now on will be tracked again.",
                "Reset Tracking",
                OLEMSGICON.OLEMSGICON_QUERY,
                OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            if (answer == (int)VSConstants.MessageBoxResult.IDOK)
            {
                _ = System.Threading.Tasks.Task.WhenAll(trackers.Select(t => t.ResetAsync()));
            }
        }

        private void GroupToggle_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _groupByFolder = GroupToggle.IsChecked == true;
            WriteGroupSetting(_groupByFolder);
            Render();
        }

        private void CollapseAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var n in Tree.Items.OfType<NodeViewModel>().SelectMany(n => n.SelfAndDescendants()))
            {
                n.IsExpanded = false;
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => _ = _manager.RefreshAllAsync();

        private bool ReadGroupSetting()
        {
            try
            {
                var store = new ShellSettingsManager(_manager.ServiceProvider).GetReadOnlySettingsStore(SettingsScope.UserSettings);
                return store.GetBoolean(SettingsCollection, GroupByFolderSetting, true);
            }
            catch (Exception ex) when (!ErrorHandler.IsCriticalException(ex))
            {
                return true;
            }
        }

        private void WriteGroupSetting(bool value)
        {
            try
            {
                var store = new ShellSettingsManager(_manager.ServiceProvider).GetWritableSettingsStore(SettingsScope.UserSettings);
                if (!store.CollectionExists(SettingsCollection))
                {
                    store.CreateCollection(SettingsCollection);
                }
                store.SetBoolean(SettingsCollection, GroupByFolderSetting, value);
            }
            catch (Exception ex) when (!ErrorHandler.IsCriticalException(ex))
            {
                // Not critical: the setting is a convenience.
            }
        }
    }
}
