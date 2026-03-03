# Workspaces and Window Management Review

Date: 2026-02-06
Scope: Window management, workspace persistence/management, snapshot flow, launch flow, performance, and correctness.

## Final architecture summary

### 1) Window runtime
- `WindowManager` tracks top-level windows through WinEvent hooks and updates `WindowInfo` snapshots.
- Destroy semantics are strict: only explicit destroy events trigger `WindowDestroyed` unbind behavior.
- Location updates use lightweight bounds/visibility refresh paths.

### 2) Workspace persistence
- Definitions: `%LOCALAPPDATA%\\TopToolbar-Standalone\\config\\workspaces.json`.
- Provider config/buttons: `%LOCALAPPDATA%\\TopToolbar-Standalone\\Providers\\WorkspaceProvider.json`.
- Both stores now use:
  - cross-process sidecar lock files,
  - version-checked compare-and-retry writes,
  - temp-file replace writes.

### 3) Snapshot flow
- `WorkspaceProvider.SnapshotAsync` -> `WorkspacesRuntimeService` -> `WorkspaceSnapshotter`.
- Snapshot filter includes visibility, cloaking, current virtual desktop, excluded classes/titles/tool windows.
- AFH fallback process-path resolution now ignores hidden/cloaked/off-desktop candidates.

### 4) Launch flow
- `WorkspaceLauncher` phases:
  1. Ensure apps alive (existing-window claim pass, then launch pass).
  2. Resize/arrange windows in parallel with post-settle validation.
  3. Minimize extraneous windows on current virtual desktop (when `MoveExistingWindows=true`).
- `LastLaunchedTime` is updated on successful launch.

## Completed fixes

### A) Matching correctness and ambiguity handling
1. Scored matcher implemented (`AUMID > package > PWA > path > process > title` with weighted scoring).
- `Services/Workspaces/WorkspaceWindowMatcher.cs`

2. Existing-window claim now uses candidate ranking by:
- identity score,
- distance to saved app position,
- window area.
- `Services/Workspaces/WorkspaceLauncher.Ensure.cs`

3. Title-only ambiguity hardening:
- unresolved tied title-only candidates are no longer claimed,
- launch falls back to pass-2 instead of risky mis-assignment.
- `Services/Workspaces/WorkspaceLauncher.Ensure.cs`

### B) Performance
4. Indexed candidate lookup for pass-1 existing-window assignment.
- Built once per launch pass and queried per app, reducing broad scans.
- `Services/Workspaces/WorkspaceLauncher.cs`
- `Services/Workspaces/WorkspaceLauncher.Ensure.cs`

### C) Persistence concurrency
5. Added `FileConcurrencyGuard` shared lock/version utilities.
- `Services/Storage/FileConcurrencyGuard.cs`

6. Workspace definition store now uses compare-and-retry writes for all mutations.
- `Services/Workspaces/WorkspaceDefinitionStore.cs`

7. Provider config store now merges on detected concurrent edits (instead of blind overwrite), with retry-safe writes.
- `Services/Providers/WorkspaceProviderConfigStore.cs`

### D) Monitor identity stability
8. Display snapshot now captures stronger monitor `InstanceId` from `EnumDisplayDevices(DeviceID)` when available.
- `Services/Display/DisplayManager.cs`

### E) Prior fixes retained
9. Window-destroy lifecycle unbind bug fix retained.
10. Minimize/maximize restore without saved rect retained.
11. Snapshot virtual desktop/cloak safety retained.
12. Monitor rect fallback mapping retained.

## Residual (non-blocking) improvements
1. Pass-1 candidate indexing can be extended further with precomputed PWA token maps to reduce fallback checks.
2. Optional: add interactive conflict UX for config edits (currently merge-and-retry is automatic and logged).
3. Optional: cache expensive identity fields even more aggressively for non-location WinEvents.

## Validation
- Build:
  - `dotnet build .\\TopToolbar.csproj -c Debug -r win-arm64 -p:Platform=arm64`
- Result:
  - Success, 0 warnings, 0 errors.
