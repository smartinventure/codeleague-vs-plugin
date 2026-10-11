# Architecture – CodeLeague Persistent Changes

Both extensions implement the same tracking engine and share one state-file format, so they behave identically and a
developer who uses both IDEs sees the same list.

| | VS Code | Visual Studio 2022 / 2026 |
|---|---|---|
| Engine | `vscode/src/core/*.ts` | `visualstudio/src/CodeLeague.PersistentChanges.Core/*.cs` |
| Tests | `vscode/src/test/` (node:test, real git repos) + `vscode/src/integration/` (real VS Code) | `visualstudio/tests/` (xUnit, real git repos) |
| Repo discovery | Built-in Git extension API + workspace folders | Solution directory + all project directories |
| Change triggers | Git extension state events, workspace file watcher, save events, window focus, 30 s timer | `FileSystemWatcher` per repository, 30 s timer |
| UI | `TreeView` in the Source Control side bar | Dockable WPF tool window (tabbed next to Solution Explorer) |

The engines are line-by-line ports of each other. **Any change to the algorithm must be made in both** and covered by
the mirrored tests (`repoTracker.test.ts` ⇔ `RepoTrackerTests.cs`).

## Why not just `git status`?

`git status` only knows uncommitted changes – which is exactly what disappears after a commit. The extension therefore
keeps its own persistent list and only *uses* git to find out which files changed.

## Tracking algorithm

The engine works per repository work tree (`RepoTracker`). A refresh is triggered (debounced, ~0.5 s) by any file-system or
IDE event and does:

1. Read `HEAD` (branch name and commit) and detect an in-progress merge / rebase / cherry-pick / revert
   (`MERGE_HEAD`, `rebase-merge/`, … in the git dir). During a rebase the branch name is read from `rebase-merge/head-name`.
2. Run `git --no-optional-locks status --porcelain=v1 -z --untracked-files=all`. Every entry is a candidate.
   * Untracked files inside IDE-state or build-output folders (`.vs`, `.idea`, `bin`, `obj`, `node_modules`) are
     skipped, so repositories or old branches with an incomplete `.gitignore` don't flood the list.
   * While an operation is in progress, entries that were only *staged by git* (work-tree column clean, not conflicted)
     are skipped – they are other people's changes being merged in. Conflicts and work-tree edits are kept.
3. If `HEAD` moved on the **same branch** and the new commits are a **linear continuation** of the previous `HEAD`
   (every commit has exactly one parent) **made by the local user** (`committer email == git config user.email`), the
   files of those commits are candidates too. This catches "edit + commit" that happened between two refreshes or while
   the IDE was closed. Merges, rebases, resets, amends and pulls of other people's commits never match.
4. For each candidate compute a fingerprint `"<size>:<mtime in ms>"` (or `"deleted"`). If the baseline snapshot has the
   same fingerprint for that path, skip it (it was already dirty at Reset and has not been touched since).
5. Add new paths, update `lastSeen` when the fingerprint changed, and follow renames (staged renames from `git status`,
   IDE rename events) so a renamed file moves instead of being duplicated.

### Branch switches

Lists are stored **per branch**. A checkout rewrites files, but after the checkout those files match the new `HEAD` and
therefore never show up in `git status` – so checkout-induced changes are never tracked. Uncommitted changes that git
carries over to the new branch are real edits and are tracked on the new branch as well. Switching back restores the
old branch's list. Commit detection (step 3) only runs when the branch did not change.

### Reset Tracking

Reset replaces the current branch's state with a new baseline: the list is cleared and the fingerprints of all
currently dirty files are stored as the baseline snapshot. Only the state file is written – files, the index, commits,
branches and history are never touched. Subsequent edits (including edits to files that were already dirty) change the
fingerprint and are tracked again.

### What the status letter means

The file list is persistent; the status shown next to a file is live:

| Letter | Meaning |
|---|---|
| `M` | Modified, not committed |
| `A` | Added (staged new file) |
| `U` | Untracked new file |
| `R` | Renamed |
| `D` | Deleted |
| `!` | Merge conflict |
| `✓` | Committed – no uncommitted changes, but changed since the baseline |

## Non-invasiveness

* All git commands are read-only and run with `--no-optional-locks` / `GIT_OPTIONAL_LOCKS=0`, so a background
  `git status` never takes `index.lock` and never collides with a commit or checkout the developer is running.
* Nothing is written into the repository or the `.git` directory.

## State file

Location (shared by both IDEs):

| OS | Directory |
|---|---|
| Windows | `%LOCALAPPDATA%\CodeLeague\PersistentChanges\` |
| macOS (VS Code) | `~/Library/Application Support/CodeLeague/PersistentChanges/` |
| Linux (VS Code) | `$XDG_DATA_HOME/CodeLeague/PersistentChanges/` (default `~/.local/share/…`) |

File name: `<folder>-<first 16 hex chars of sha1(normalizedRoot)>.json`, where `normalizedRoot` is the work-tree root
with forward slashes, no trailing slash, lower-cased on Windows. Example: `D:\Repos\CodeLeague` →
`codeleague-c61f656b7a2db7bb.json` (asserted in both test suites).

```jsonc
{
  "version": 1,
  "repositoryRoot": "D:/Repos/CodeLeague",
  "branches": {
    "feature/new-dashboard": {
      "baseline": {
        "id": "0b6c…",                         // new id on every Reset
        "createdAt": "2026-10-10T08:00:00.000Z",
        "snapshot": { "src/old.ts": "1234:1760083200123" }   // dirty at Reset
      },
      "files": {
        "src/Dashboard.tsx": {
          "firstSeen": "2026-10-10T08:12:00.000Z",
          "lastSeen":  "2026-10-10T09:40:00.000Z",
          "fingerprint": "5120:1760089200456"
        }
      },
      "lastHead": "3f2a…"                      // HEAD at the last refresh, for commit detection
    }
  }
}
```

Writes are atomic (temp file + rename). Before each write and on each refresh the file is re-read if its timestamp or
size changed, so two IDE instances on the same repository stay in sync (last writer wins for simultaneous writes).

## Performance

* One `git status` per debounced burst of events plus a 30 s safety refresh; build output folders (`bin`, `obj`,
  `node_modules`, `.vs`, `.git/objects`, …) don't trigger refreshes.
* Calls are serialized per repository and coalesced (many triggers ⇒ one scan).
* The state is a small JSON file per repository; fingerprints are a `stat` per changed file.

## Known limitations

* An unstaged rename done outside the IDE appears as "deleted old file + new file" (git cannot pair them until staged).
* If a file is edited and committed by a *different* committer identity between two refreshes, it is not detected
  (by design – that is how other people's commits are excluded).
* Two IDE instances writing the state file in the very same millisecond: the last write wins.
