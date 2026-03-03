# Workspace Orchestrator Spec v0.6

## 1. Purpose

Define a template-first workspace system for TopToolbar where:

- A template captures reusable workflow structure.
- A workspace instance is a named task lane created from a template.
- Switching restores both geometry and operator intent (focus target).

This spec defines a greenfield product contract implemented on top of reusable runtime components from prior code.

## 2. Product Goals

- Make parallel work in the same repo explicit and safe.
- Reduce context-switch cost to one click.
- Keep launch/switch deterministic across multi-monitor and virtual desktops.
- Support easy template authoring for non-expert users.

## 3. Out of Scope (v1)

- Cloud/shared template sync.
- Nested visual tree UI in toolbar.
- Arbitrary scripting/conditionals in templates.

## 4. Implementation Baseline

The implementation reuses these runtime components:

- `WorkspaceProvider` for toolbar actions/buttons.
- `WorkspacesRuntimeService` for launch orchestration.
- `WorkspaceLauncher` phases (reuse, launch, arrange, minimize).
- `WorkspaceDefinitionStore` in `%LOCALAPPDATA%\\TopToolbar\\config\\workspaces.json`.

No launch-engine rewrite is required.

## 5. Core UX

### 5.1 Day-to-day flow

1. User chooses a template.
2. User creates a named lane:
   `ws new --template powertoys-coding --name "FancyZones crash fix" --repo D:\src\PowerToys`
3. Toolbar shows:
   `PowerToys · FancyZones crash fix`
4. User switches lanes via toolbar.
5. System restores windows/layout and applies template focus policy.

Direct CLI creation is also first-class:

- `ws new --name "FZ fix" --repo D:\src\PowerToys --preset horizontal-equal --app "role=terminal,exe=wt.exe,cwd={repo}" --app "role=editor,exe=code.exe,args={repo},cwd={repo}" --focus editor,terminal`

### 5.2 Identity model

Workspace identity is:

- `templateName` + `instanceName` (semantic identity)
- `id` (technical identity)

Title format:

- `<Template Display Name> · <Instance Name>`

## 6. Template Authoring UX

Template authoring must support two entry paths and produce the same JSON schema.

### 6.1 Snapshot-assisted authoring (default path)

Command:

- `ws template capture --name <templateId> [--from-current-desktop]`

Flow:

1. Capture current windows (existing snapshot pipeline).
2. Open guided cleanup:
   - Keep/remove captured apps.
   - Assign role for each kept app.
   - Parameterize repo/path values (`D:\src\PowerToys` -> `{repo}`).
3. Choose layout strategy/preset.
4. Set `focusPriority`.
5. Save template.

### 6.2 Guided-create authoring (clean path)

Commands:

- `ws template init --name <templateId>`
- `ws template add-window --template <templateId> --role <role> --path <exe> [--args ...] [--cwd ...]`
- `ws template set-layout --template <templateId> --preset <preset>`
- `ws template set-focus --template <templateId> --roles <r1,r2,...>`

Flow:

1. Start empty template.
2. Add windows and roles explicitly.
3. Configure layout and focus.
4. Validate and save.

### 6.3 Direct CLI workspace creation (no settings required)

Users can create runnable workspaces directly from CLI without opening settings.

Supported patterns:

- Template-based: `ws new --template <template> --name "<name>" [--repo <path>]`
- Preset-based: `ws new --name "<name>" --repo <path> --preset <preset>`
- Inline-app-based: `ws new --name "<name>" --layout <layout> --app "<spec>" --app "<spec>"`

Optional:

- `--focus <role1,role2,...>`
- `--save-template <templateId>` to persist current CLI definition as reusable template
- `--no-launch` to only persist definition

### 6.4 Authoring quality gates

Before save:

- At least one window entry.
- Unique role names per template window definition (role aliases allowed only via explicit duplicate flag).
- `focusPriority` roles must exist in template windows.
- Required variables must be satisfiable (`{repo}` requires repo source).

## 7. Functional Requirements

### FR-1 Template storage

- Root: `%LOCALAPPDATA%\\TopToolbar\\config\\templates\\`
- One file per template: `<template-id>.json`
- UTF-8 JSON
- Hot reload on provider refresh

### FR-2 Workspace metadata

Workspace instance must carry:

- `id`
- `templateName`
- `instanceName`
- `workspaceTitle`
- `repoRoot` (optional)
- `createdAt`
- monitor/application fields from `WorkspaceDefinition`

All template metadata fields above are required for v1-created instances.

### FR-3 Instance creation

Command:

- `ws new --template <templateName> --name "<instanceName>" [--repo <path>]`

Alternative creation modes:

- `ws new --name "<instanceName>" --repo <path> --preset <presetName>`
- `ws new --name "<instanceName>" --layout <layoutName> --app "<appSpec>" [--app "<appSpec>" ...]`

Rules:

- If `--template` is provided, it must exist.
- `--name` required.
- If `--template` is omitted, either `--preset` or at least one `--app` is required.
- Template+instance uniqueness is case-insensitive.
- If template requires repo and no repo/default is resolvable, fail validation.
- Inline `--app` specs must parse and include at minimum `role` and `exe`.
- Success path:
  - materialize `WorkspaceDefinition`
  - persist
  - ensure toolbar button
  - launch workspace (unless `--no-launch`)

Preset mode behavior:

- Built-in preset expands to explicit layout slots plus default `focusPriority`.
- If user also supplies `--layout`, explicit layout overrides preset layout.
- If user supplies `--focus`, explicit focus overrides preset focus.

### FR-4 Launch/switch behavior

Switch action (toolbar or CLI) must:

1. Claim/reuse existing matching windows.
2. Launch missing windows.
3. Apply layout.
4. Minimize extraneous windows when `MoveExistingWindows=true`.
5. Apply focus policy (FR-5).

Virtual desktop and multi-monitor behavior must remain consistent with current runtime constraints.

### FR-5 Focus policy (required)

Each template defines `focusPriority` as ordered roles.

Resolution:

1. Select first role with a live assigned window.
2. Set foreground/focus to that window.
3. Fallback to first successfully assigned workspace window.

Examples:

- Coding: `["vscode","terminal"]`
- Review: `["browser","terminal","vscode"]`
- Release: `["terminal","browser"]`

### FR-6 Toolbar presentation

- Button text uses `workspaceTitle`.
- Sort order:
  - `templateDisplayName` ASC
  - `lastLaunchedTime` DESC
  - `instanceName` ASC

### FR-7 Validation and errors

Required errors:

- `Workspace name is required.`
- `Template '<name>' was not found.`
- `Template '<name>' requires --repo.`
- `Workspace '<title>' already exists in template '<template>'.`
- `Template '<name>' is invalid: <reason>.`
- `Invalid --app spec '<value>': expected key=value pairs including role and exe.`
- `Either --template, --preset, or --app must be provided.`

### FR-8 Variable substitution

Supported tokens:

- `{repo}`
- `{instanceName}`
- `{workspaceTitle}`

Substitution applies to:

- `path`
- `args`
- `workingDirectory`
- startup/init command fields
- layout labels (optional)

## 8. Data Contracts

### 8.1 TemplateDefinition JSON (v1)

```json
{
  "schemaVersion": 1,
  "name": "powertoys-coding",
  "displayName": "PowerToys",
  "description": "Coding lane for PowerToys tasks",
  "requiresRepo": true,
  "defaultRepoRoot": "D:\\src\\PowerToys",
  "focusPriority": ["vscode", "terminal"],
  "layout": {
    "strategy": "main-secondary",
    "monitorPolicy": "primary",
    "slots": [
      { "role": "vscode", "x": 0.0, "y": 0.0, "w": 0.68, "h": 1.0 },
      { "role": "terminal", "x": 0.68, "y": 0.0, "w": 0.32, "h": 1.0 }
    ]
  },
  "windows": [
    {
      "role": "terminal",
      "path": "C:\\Windows\\System32\\cmd.exe",
      "args": "/k cd /d {repo}",
      "workingDirectory": "{repo}",
      "matchHints": { "processName": "cmd" }
    },
    {
      "role": "vscode",
      "path": "code",
      "args": "\"{repo}\"",
      "workingDirectory": "{repo}",
      "matchHints": { "processName": "Code" }
    }
  ]
}
```

### 8.2 WorkspaceDefinition extensions

Add serialized fields for all v1-created instances:

- `template-name`
- `instance-name`
- `workspace-title`
- `repo-root`

## 9. Layout Specification

### 9.1 Layout model

- `strategy`: `side-by-side | main-secondary | grid | custom`
- `monitorPolicy`: `primary | current | any | explicit`
- `slots`: normalized coordinates (`x`, `y`, `w`, `h` in `[0,1]`)
- `preset`: optional built-in preset ID expanded to `slots` at create time

### 9.2 Layout algorithm

1. Resolve monitor target via `monitorPolicy`.
2. Read monitor work area (`left`, `top`, `width`, `height`) in DPI-aware coordinates.
3. Calculate each target rectangle from normalized slot values:
   - `targetX = monitorLeft + round(slot.x * monitorWidth)`
   - `targetY = monitorTop + round(slot.y * monitorHeight)`
   - `targetW = round(slot.w * monitorWidth)`
   - `targetH = round(slot.h * monitorHeight)`
4. Apply min-size clamps per window (default 480x320).
5. Resolve overlaps by strategy priority (`main` wins).
6. Arrange windows via existing placement APIs.
7. Run settle pass and verify placement tolerance.

### 9.3 Built-in layout presets

- `horizontal-equal`: 2 columns, 50/50 split
- `vertical-equal`: 2 rows, 50/50 split
- `main-left-70`: primary role left 70%, secondary right 30%
- `main-right-70`: primary role right 70%, secondary left 30%
- `triple-columns`: 3 equal columns
- `grid-2x2`: 4 equal quadrants

Presets are expanded to explicit normalized slots at creation time and persisted as slots.

## 10. CLI Contract

### 10.1 Instance commands

- `ws new --template <template> --name "<name>" [--repo <path>]`
- `ws new --name "<name>" --repo <path> --preset <preset>`
- `ws new --name "<name>" --layout <layout> --app "<appSpec>" [--app "<appSpec>" ...] [--focus <roles>] [--save-template <templateId>] [--no-launch]`
- `ws switch --id <workspaceId>`
- `ws switch --template <template> --name "<instanceName>"`

### 10.2 Template commands

- `ws templates`
- `ws templates show <template>`
- `ws template capture --name <templateId>`
- `ws template init --name <templateId>`
- `ws template validate --name <templateId>`
- `ws template delete --name <templateId>`

### 10.3 Exit codes

- `0` success
- `2` validation error
- `3` template not found
- `4` launch/switch failure
- `5` storage/read-write failure

### 10.4 App spec format for `--app`

`--app` is repeatable and uses comma-separated key-value pairs.

Required keys:

- `role`
- `exe`

Optional keys:

- `cwd`
- `args`
- `init`
- `monitor`

Example:

- `--app "role=terminal,exe=wt.exe,cwd={repo},init=pwsh -NoExit -Command .\\tools\\dev.ps1"`
- `--app "role=editor,exe=code.exe,args={repo},cwd={repo}"`

## 11. Data Initialization

- First run creates empty workspace/template stores if missing.
- No legacy import/migration is performed.
- Only template-native workspace instances are in scope for this product.

## 12. Non-Functional Requirements

- Launch latency target: under 2.5s for 2-window template on warm system.
- Deterministic switch behavior: same input state yields same focus target.
- Virtual desktop filtering is required.
- Multi-monitor placement fallback is required when a target monitor is unavailable.

## 13. Acceptance Criteria

- Can create two instances from same template/repo with distinct names.
- Can create same `instanceName` under different templates.
- Toolbar labels are semantic and unambiguous.
- Template capture flow produces valid template after guided cleanup.
- Guided-create flow can create a valid template without snapshot.
- User can create a runnable workspace entirely from CLI with `--preset` and/or `--app` without visiting settings.
- `focusPriority` is honored; deterministic behavior works when preferred role is missing.
- Layout remains stable across app restart and monitor reconnect.

## 14. Implementation Plan

### Phase 1: Schema and stores

- Add `TemplateDefinition` model and `TemplateStore`.
- Extend `WorkspaceDefinition` with template metadata fields.
- Add validation + variable substitution helpers.

### Phase 2: Orchestrator service

- Implement `WorkspaceTemplateOrchestrator`:
  - build instance from template + inputs
  - enforce uniqueness rules
  - persist workspace and button
  - call launch runtime service

### Phase 3: CLI surface

- Extend `Program` command parser with `ws` verbs.
- Add template and instance command handlers.
- Add `--preset`, repeated `--app`, `--focus`, `--save-template`, and `--no-launch` handling.
- Add parser/validator for `--app` key-value syntax.

### Phase 4: Toolbar and sorting

- Use `workspaceTitle` for display.
- Implement template-aware ordering and lookup helpers.

### Phase 5: Authoring UX

- Implement capture wizard (cleanup, role assignment, parameterization, layout, focus).
- Implement guided-create commands and validation command.

## 15. Default Decisions (Locked for v1)

- `ws new` auto-launches on success.
- Duplicate `instanceName` is allowed across different templates.
- `defaultRepoRoot` supports environment variable expansion.
- Snapshot-assisted authoring is the default recommendation.
- Built-in presets are available out of box for direct CLI creation.
