using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace CodeLeague.PersistentChanges.VisualStudio
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [InstalledProductRegistration("CodeLeague – Persistent Changes", "Persistent, commit-independent list of the files you are working on.", "0.1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Docked as a tab next to Solution Explorer, like Git Changes.
    [ProvideToolWindow(typeof(PersistentChangesToolWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057", Orientation = ToolWindowOrientation.Right)]
    // Tracking must run in the background even while the window is closed.
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class PersistentChangesPackage : AsyncPackage
    {
        public const string PackageGuidString = "dcd2567d-32e1-4166-9db6-c90c164f5928";
        public static readonly Guid CommandSet = new Guid("3266175a-67ff-406d-81ef-21e710ecb8d3");
        public const int ShowPersistentChangesCommandId = 0x0100;

        private VsTrackerManager? _manager;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _manager = new VsTrackerManager(this);
            await _manager.StartAsync();

            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
            {
                commands.AddCommand(new MenuCommand(
                    (_, _) => JoinableTaskFactory.RunAsync(ShowToolWindowAsync),
                    new CommandID(CommandSet, ShowPersistentChangesCommandId)));
            }
        }

        private async Task ShowToolWindowAsync()
        {
            var window = await ShowToolWindowAsync(typeof(PersistentChangesToolWindow), 0, create: true, DisposalToken);
            if (window?.Frame == null)
            {
                throw new NotSupportedException("Cannot create the Persistent Changes tool window.");
            }
        }

        public override IVsAsyncToolWindowFactory? GetAsyncToolWindowFactory(Guid toolWindowType) =>
            toolWindowType == typeof(PersistentChangesToolWindow).GUID ? this : null;

        protected override string GetToolWindowTitle(Type toolWindowType, int id) =>
            toolWindowType == typeof(PersistentChangesToolWindow) ? PersistentChangesToolWindow.Title : base.GetToolWindowTitle(toolWindowType, id);

        protected override async System.Threading.Tasks.Task<object?> InitializeToolWindowAsync(Type toolWindowType, int id, CancellationToken cancellationToken)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return _manager;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _manager?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
