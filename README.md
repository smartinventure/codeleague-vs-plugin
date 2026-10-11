# CodeLeague – Persistent Changes

IDE extensions for **Visual Studio 2022 / 2026** and **Visual Studio Code** that keep a clickable list of the files you
are working on, and **keep it after you commit and push**.

The built-in *Git Changes* / *Source Control* view empties as soon as you commit. That's a good reason to commit
rarely, and a bad thing for backups and for CodeLeague's statistics. With *Persistent Changes* you can commit and push
every day and still see every file you touched on your feature branch, until you click **Reset Tracking**.

```text
CodeLeague – Persistent Changes          [🗑 Reset Tracking] [📁] [⟳]
Repository: CodeLeague    Branch: feature/new-dashboard

▼ src/
    Dashboard.tsx              ✓   (committed)
    DashboardService.ts        M   (modified)
▼ src/components/
    UserCard.tsx               U   (new)
    StatisticsChart.tsx        ✓
▼ tests/
    DashboardTests.ts          ✓

5 tracked files
```

## Features

* **Automatic tracking:** modified, new, staged, untracked, deleted and renamed files are added as soon as they change, whether the change comes from the IDE, an external editor or a code generator.
* **Persistent:** the list survives commits, pushes and IDE restarts.
* **Per repository and branch:** every branch has its own list, and switching back restores it. Files changed only by the checkout are ignored, and so are colleagues' changes merged in from `main`.
* **Navigation:** double-click (or Enter) opens a file. Files can be grouped by folder or shown as a flat list, each with a live status letter. The context menu offers *Reveal*, *Copy Path* and *Remove from List*.
* **Reset Tracking:** clears the list and starts a new baseline. It never touches files, the index, commits or branches.
* **Non-invasive:** read-only git commands with `--no-optional-locks`, and no files written to the repository. Works offline, with no server.
* **Shared between IDEs:** both extensions use the same local state file, so VS and VS Code show the same list.

## Repository layout

```text
vscode/          VS Code extension (TypeScript)
visualstudio/    Visual Studio extension (C#, VSIX) + xUnit tests
docs/            Architecture and shared functional specification
```

How tracking works (baseline, branch switches, merges, state-file format): [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Visual Studio 2022 / 2026

**Install:** double-click `CodeLeague.PersistentChanges.VisualStudio.vsix` (from the CI artifacts or your own build),
restart Visual Studio, then open **View → Other Windows → CodeLeague – Persistent Changes**. The window docks next to
Solution Explorer and can be moved anywhere. Tracking runs in the background as soon as a solution or folder is open,
even while the window is closed.

**Build:** requires Visual Studio 2022 or 2026 (MSBuild). The *Visual Studio extension development* workload is only
needed for F5 debugging; the VSSDK build tools come from NuGet.

```powershell
cd visualstudio
# tests (any .NET 8+ SDK, needs git on PATH)
dotnet test tests/CodeLeague.PersistentChanges.Core.Tests
# VSIX -> src/CodeLeague.PersistentChanges.VisualStudio/bin/Release/net472/*.vsix
& "$(& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -find MSBuild\**\Bin\MSBuild.exe)" `
  src/CodeLeague.PersistentChanges.VisualStudio/CodeLeague.PersistentChanges.VisualStudio.csproj -restore -p:Configuration=Release
```

To debug, open `visualstudio/CodeLeague.PersistentChanges.sln`, set the VSIX project as the startup project and press
F5. The experimental instance starts with the extension loaded.

## Visual Studio Code

**Install:** *Extensions → … → Install from VSIX…* and pick `codeleague-persistent-changes.vsix`, or run
`code --install-extension codeleague-persistent-changes.vsix`. The view appears in the **Source Control** side bar
(*CodeLeague – Persistent Changes*) and can be dragged anywhere.

**Build:** requires Node.js 20+ and git.

```bash
cd vscode
npm ci
npm test                  # unit tests against real temporary git repositories
npm run test:integration  # launches a real VS Code instance (downloads it on first run)
npm run package           # -> codeleague-persistent-changes.vsix
```

To debug, open the `vscode` folder in VS Code and press F5.

**Settings:**

| Setting | Default | |
|---|---|---|
| `codeleague.persistentChanges.viewMode` | `tree` | `tree` groups by folder, `list` is flat |
| `codeleague.persistentChanges.openMode` | `singleClick` | `singleClick`: click previews and double-click pins, like the Explorer. `doubleClick`: only a double-click opens |
| `codeleague.persistentChanges.confirmReset` | `true` | Ask before Reset Tracking |

## Acceptance criteria → tests

| # | Criterion | VS Code test | Visual Studio test |
|---|---|---|---|
| 1 | Modifying a file adds it | `1. modifying a file…` | `C01_…` |
| 2 | No duplicates | `2. …` | `C02_…` |
| 3 | Commit keeps files | `3. …` | `C03_…` |
| 4 | Push keeps files | `4. …` | `C04_…` |
| 5 | Additional files are appended | `5. …` | `C05_…` |
| 6 | Clicking opens the file | integration test (`suite.ts`) | manual (see below) |
| 7 | Restart restores the list | `7. …` | `C07_…` |
| 8 | Reset clears without touching git/files | `8. …` | `C08_…` |
| 9 | New modifications after reset | `9. …` | `C09_…` |
| 10 | Branch switch restores the list | `10. …` | `C10_…` |

Both suites also cover: edit and commit between refreshes, commits made while the IDE was closed, merges from `main`,
conflicted merges, fast-forward pulls of colleagues' commits, deletes, reverts, renames, sharing between two IDE
instances, and repositories without commits.

**Manual check in Visual Studio:** open a solution in a git repo and open the window. Edit and save a file (it appears),
commit it in Git Changes (it stays, with ✓), double-click it (it opens), switch branch (the list switches), then
Reset Tracking (the list empties and Git Changes is unchanged).

## License

MIT, see [LICENSE](LICENSE).
