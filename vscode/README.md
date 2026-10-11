# CodeLeague – Persistent Changes

A **Source Control** side-bar view with a clickable list of the files you are working on. Unlike the Git changes list,
it **keeps the files after you commit and push**, until you click **Reset Tracking**.

So you can commit and push every day and still keep the overview of your feature branch.

## Features

* Files are added automatically when they are modified, created, staged, deleted or renamed, whether by VS Code, an external editor or a generator.
* The list survives commits, pushes and restarts.
* There is one list per repository and branch. Switching branches switches the list, and checkouts and merges from `main` don't add files.
* Click a file to preview it, double-click to open it. A live status letter shows the state: `M` modified, `U` new, `A` added, `D` deleted, `R` renamed, `!` conflict, `✓` committed.
* Files can be grouped by folder or shown as a flat list. The context menu has *Open Changes*, *Reveal in Explorer*, *Copy Path* and *Remove from List*.
* **Reset Tracking** (trash icon) clears the list and starts a new baseline. It never changes files or git.
* Everything stays local, and git is only read. The state is shared with the Visual Studio extension.

## Commands

* `CodeLeague: Reset Tracking`
* `CodeLeague: Refresh`
* `CodeLeague: View as Tree` / `View as List`

## Settings

* `codeleague.persistentChanges.viewMode`: `tree` (default) or `list`.
* `codeleague.persistentChanges.openMode`: `singleClick` (default; click previews, double-click pins) or `doubleClick`.
* `codeleague.persistentChanges.confirmReset`: ask before resetting (default `true`).

More details: https://github.com/smartinventure/codeleague-vs-plugin
