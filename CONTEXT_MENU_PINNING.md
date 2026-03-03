# Dev Grid Context Menu Pinning (Win11)

## Goals
- Allow pinning from File Explorer without opening settings.
- Support files, `.exe`, and folders.
- Keep pin persistence logic centralized in one place.

## Architecture
- `Program.cs`
  - Handles `--pin <path>` command line and exits after pinning.
  - Uses `ToolbarPinService` for write/dedupe.
  - Uses fallback HKCU registry registration only when running unpackaged.
  - Removes legacy HKCU registration when running packaged to avoid duplicate menu entries.
- `Services/Pinning/ToolbarPinService.cs`
  - Normalizes target path.
  - Rejects non-existent targets.
  - Uses a named mutex for concurrent writes.
  - Dedupes across all groups by normalized command target.
  - Creates/uses `Pinned` group and writes button/action config.
- `ShellExtensions/TopToolbar.ContextMenu`
  - COM `IExplorerCommand` implementation for modern Win11 context menu.
  - Extracts selected shell item file-system paths.
  - Launches `TopToolbar.exe --pin "<path>"` per item.
  - Uses `Assets/Logos/ContextMenuIcon.ico` for the menu icon when available.
- `Package.appxmanifest`
  - Declares COM server (`windows.comServer`) pointing to `TopToolbar.ContextMenu.comhost.dll`.
  - Declares `windows.fileExplorerContextMenus` for `*` (files, including `.exe`) and `Directory`.

## Behavior Notes
- Folder command format: `explorer.exe "<folder-path>"`.
- File/exe command format: `"<file-path>"`.
- Duplicate pin requests are idempotent.

## Risks and Follow-Ups
- Explorer invokes handlers in-process; keep COM code small and defensive.
- Multi-select currently spawns one `TopToolbar.exe` per selected item.
  - Follow-up: batch mode (`--pin-multi`) to reduce process churn.
- If a selection is virtual/non-filesystem, the command remains unavailable for that item.
- Win11 modern menu visibility depends on package install state and Explorer refresh.
  - After install/update, Explorer restart may be required.
