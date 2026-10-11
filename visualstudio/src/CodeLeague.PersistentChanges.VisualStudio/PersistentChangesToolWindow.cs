using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;

namespace CodeLeague.PersistentChanges.VisualStudio
{
    [Guid("a492a9da-deb5-4ea8-b7a9-2289ca7b8ff4")]
    public sealed class PersistentChangesToolWindow : ToolWindowPane
    {
        public const string Title = "CodeLeague – Persistent Changes";

        /// <param name="state">The <see cref="VsTrackerManager"/> returned by PersistentChangesPackage.InitializeToolWindowAsync.</param>
        public PersistentChangesToolWindow(object state) : base(null)
        {
            Caption = Title;
            BitmapImageMoniker = KnownMonikers.History;
            Content = new PersistentChangesControl((VsTrackerManager)state);
        }
    }
}
