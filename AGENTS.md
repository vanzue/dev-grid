# Dev Grid Agent Notes

## Naming
- User-facing product name is **Dev Grid**.
- Code-level identifiers still use `TopToolbar` (`TopToolbar.exe`, namespaces, solution/project names). Do not rename these unless the task explicitly asks for it.

## Logs
- Primary log root: `%LOCALAPPDATA%\TopToolbar-Standalone\Logs` (`AppPaths.Logs`).
- Optional app-data root override: `TOPTOOLBAR_APPDATA_ROOT`.
- Logs are written into versioned subfolders under the root (see `Logging/AppLogger.cs`).
- After finishing coding tasks, **clear the log folder contents** so the next test run starts clean.
- During migration/debugging, also clear `%LOCALAPPDATA%\TopToolbar\Logs` if it exists.

## Workspace and template configuration
- Toolbar config (groups/buttons): `%LOCALAPPDATA%\TopToolbar-Standalone\toolbar.config.json` (`AppPaths.ConfigFile`).
- Workspace definitions: `%LOCALAPPDATA%\TopToolbar-Standalone\config\workspaces.json` (`WorkspaceStoragePaths.GetWorkspaceDefinitionsPath`).
- Templates directory (one JSON file per template): `%LOCALAPPDATA%\TopToolbar-Standalone\config\templates\` (`WorkspaceStoragePaths.GetTemplatesDirectoryPath`).
- Workspace provider config/buttons: `%LOCALAPPDATA%\TopToolbar-Standalone\Providers\WorkspaceProvider.json` (`WorkspaceStoragePaths.GetProviderConfigPath`).
- Legacy migration source (PowerToys): `%LOCALAPPDATA%\Microsoft\PowerToys\Workspaces\workspaces.json` (`WorkspaceStoragePaths.GetLegacyPowerToysPath`).

## Current workspace UX contract
- Toolbar is workspace-centric: runtime workspace buttons are first-class and support right-click removal.
- Quick snapshot is a **camera** action and prompts for a name (default name uses current timestamp).
- New workspace creation is template-driven. If no templates exist, `New workspace` stays disabled and indicates templates must be configured first.
- Template management lives in Settings under the **Templates** section (left nav), with list-first flow (create/configure/delete on one page).
- Toast notifications and input prompts render in a separate top-most `ToastWindow`, not inside the toolbar window; current placement is bottom-right of the active display area.

## Template model essentials
- Physical template files are editable JSON (`<template-id>.json`) in the templates directory above.
- `TemplateDefinition` contains:
  - metadata: `schemaVersion`, `name`, `displayName`, `description`, `requiresRepo`, `defaultRepoRoot`
  - launch ordering: `focusPriority[]`
  - layout: `layout.strategy`, `layout.monitorPolicy`, `layout.slots[]`
  - app definitions: `windows[]` with `role`, `exe`, `cwd`, `args`, `init`, `monitor`, `matchHints`
- Token expansion supported in launch fields: `{repo}`, `{instanceName}`, `{workspaceTitle}`.
- Settings UI currently exposes monitor policy values `primary`, `any`, `current` as a dropdown. Runtime also accepts `explicit:<monitor-id>` in JSON.

## Workspace workflow (snapshot/launch)
- Snapshot flow:
  - UI calls `WorkspaceProvider.SnapshotAsync` -> `WorkspacesRuntimeService` -> `WorkspaceSnapshotter`.
  - Captures current window/monitor state, filters excluded windows, resolves `ApplicationFrameHost` paths, then writes `workspaces.json`.
  - Binds current window handles to app IDs in `ManagedWindowRegistry` for reuse on launch.
- Launch flow:
  - `WorkspaceProvider` invokes `WorkspacesRuntimeService.LaunchWorkspaceAsync` -> `WorkspaceLauncher`.
  - Phase 1: reuse existing windows, then launch missing apps.
  - Phase 2: resize/arrange windows.
  - Phase 3: minimize extraneous windows.
  - Matching uses AUMID/package/PWA/process/title; excludes windows on other virtual desktops during launch assignment.

## Build
- Build using **arm64**. Prefer:
  - `dotnet build .\TopToolbar.slnx -c Debug -p:Platform=arm64`
  - `dotnet build .\TopToolbar.slnx -c Release -p:Platform=arm64`
  - For explicit RID build (project-only): `dotnet build .\TopToolbar.csproj -c Debug -r win-arm64 -p:Platform=arm64`
  - For explicit RID Release (project-only): `dotnet build .\TopToolbar.csproj -c Release -r win-arm64 -p:Platform=arm64`
  - Note: `.slnx` + `-r` is blocked by .NET SDK (`NETSDK1134`), so use `.csproj` when passing `-r`.
- Before building, kill `TopToolbar.exe` if it is running.
- After a successful build, start `TopToolbar.exe` automatically for user testing (arm64 output, e.g. `bin\arm64\Debug\net9.0-windows10.0.19041.0\win-arm64\TopToolbar.exe`).

## Design/behavior considerations
- Always consider **virtual desktops** and **multi-monitor** behavior when making changes.
