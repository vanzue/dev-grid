# Dev Grid Built-in Default Actions - Dev Spec (v1)

Date: 2026-02-16  
Scope: Add built-in "System Controls" group with contextual Media Play/Pause action, plus settings toggles.

## 1) Goals
- Ship a built-in, context-aware "System Controls" group.
- v1 action: Media Play/Pause only.
- Group and action can be enabled/disabled in Settings.
- Changes apply live (no app restart).
- No user extensibility for built-in actions in v1.

## 2) Non-goals (v1)
- No plugin model for default actions.
- No media flyout/device picker.
- No additional built-in actions beyond Media Play/Pause.

## 3) Architecture Decision
- Implement built-in actions as a first-class provider:
  - New provider: `Providers/SystemControlsProvider.cs`
  - Interfaces: `IActionProvider`, `IToolbarGroupProvider`, `IChangeNotifyingActionProvider`, `IDisposable`
- Register it via `BuiltinProvider` next to `WorkspaceProvider`.
- Store user toggles in `toolbar.config.json` (extend `ToolbarConfig`), not a separate file.

Rationale:
- Reuses existing provider + runtime + toolbar group pipelines.
- Keeps config and settings persistence in one place.
- Supports dynamic visibility through provider change events.

## 4) Data Model Changes
Add to `Models/ToolbarConfig.cs`:
- `DefaultActionsConfig DefaultActions { get; set; } = new();`

New models:
- `Models/DefaultActionsConfig.cs`
  - `bool SystemControlsEnabled = true`
  - `DefaultActionItemConfig MediaPlayPause = new()`
- `Models/DefaultActionItemConfig.cs`
  - `bool Enabled = true`

Update defaults:
- `Services/ToolbarConfigService.cs` `EnsureDefaults(...)` initializes `DefaultActions`.
- `Serialization/JsonContexts.cs` includes new model types if required by source-gen context.

Example JSON:
```json
"defaultActions": {
  "systemControlsEnabled": true,
  "mediaPlayPause": {
    "enabled": true
  }
}
```

## 5) Provider Behavior - SystemControlsProvider
### 5.1 Identity
- Provider ID: `"SystemControlsProvider"`
- Group ID: `"system-controls"`
- Group Name: `"System Controls"`

### 5.2 Media Session Source
- Use Windows GSMTC APIs (`Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager`).
- Track session changes using manager/session events.
- Debounce refresh notifications (100-250ms) to avoid churn.

### 5.3 Visibility Rules
Group appears only when all are true:
- `DefaultActions.SystemControlsEnabled == true`
- `DefaultActions.MediaPlayPause.Enabled == true`
- At least one active media session exists and playback status is `Playing`.

Otherwise:
- Provider returns group with zero buttons (or runtime removes group when empty path is chosen).

### 5.4 Button Behavior
- Single button in group:
  - Button ID: `"system-controls::media-play-pause"`
  - Action type: `Provider`
  - `ProviderId = "SystemControlsProvider"`
  - `ProviderActionId = "media.playpause"`
- Click behavior:
  - If current session is `Playing` -> `TryPauseAsync()`
  - Else -> `TryPlayAsync()`
- Icon:
  - Pause glyph when currently playing
  - Play glyph when paused (if shown in edge transitions)

### 5.5 Errors
- Provider catches API exceptions and returns `ActionResult { Ok = false, Message = ... }`.
- Existing `ToolbarActionExecutor` notification path surfaces failures.

## 6) Runtime Integration Changes
## 6.1 Register provider
Update `Providers/BuiltinProvider.cs`:
- Add `LoadSystemControlsProvider()`.
- Register with runtime in `RegisterProvidersTo(...)`.

## 6.2 Generalize provider-refresh handling
Current `ToolbarWindow` dynamic refresh is workspace-specific.
Refactor `TopToolbarXAML/ToolbarWindow.xaml.cs` + `TopToolbarXAML/ToolbarWindow.Store.cs`:
- Replace workspace-only refresh path with generic:
  - `RefreshProviderGroupAsync(string providerId)`
  - `RefreshAllProviderGroupsAsync()`
- On `ProvidersChanged`, refresh the provider indicated by `args.ProviderId` when it is a group provider.
- On config file change, call `RefreshAllProviderGroupsAsync()` after static-group sync.

## 6.3 Ordering
Required order in toolbar:
1. User/static groups
2. Workspace group
3. System Controls group
4. Settings button (existing fixed area)

Implementation note:
- Ensure dynamic refresh calls upsert workspace first, system-controls second on initial/full refresh.

## 7) Settings Integration
Update `ViewModels/SettingsViewModel.*` and `TopToolbarXAML/SettingsWindow.xaml`:
- Add section in General page:
  - Card: `Default actions`
  - Toggle: `Enable System Controls`
  - Toggle: `Media Play/Pause`
- Bindings:
  - Two-way bound to new `SettingsViewModel` properties that map to `ToolbarConfig.DefaultActions`.
- Save behavior:
  - Use existing debounce save.
  - Must trigger toolbar live update via existing config watcher path.

## 8) Implementation Plan (Backbone)
1. Config and model scaffold.
2. Settings bindings and UI toggles.
3. `SystemControlsProvider` with GSMTC session tracking + invoke logic.
4. Register provider in `BuiltinProvider`.
5. Refactor toolbar dynamic refresh to generic provider-group refresh.
6. Apply deterministic dynamic group ordering.
7. Manual verification and regression checks.

## 9) Acceptance Criteria
- When media starts playing, System Controls group appears automatically.
- Media Play/Pause button toggles playback of active session.
- When playback stops and no active playing session remains, button/group disappears.
- Settings toggles hide/show group/action immediately.
- No restart required for any settings toggle.
- No focus steal, no popup UI, no launch of media apps.

## 10) Verification Checklist
- Build:
  - `dotnet build .\TopToolbar.slnx -c Debug -p:Platform=arm64`
- Manual scenarios:
  - Media playing before app start -> group visible at startup.
  - Media starts after app start -> group appears within debounce window.
  - Disable action toggle -> button disappears immediately.
  - Disable group toggle -> group disappears immediately.
  - Re-enable toggles -> behavior restored.
  - No media sessions -> group hidden.
- Regression:
  - Workspace provider still updates correctly.
  - Pinned/static groups unaffected.
  - TopBar and Radial modes both stable.
