# 01 Scope And Decisions

## Vision

Workspace Orchestrator lets engineers run multiple parallel task lanes with predictable switching, using templates or direct CLI creation.

## Product Scope (v1)

- Template-first workspace orchestration.
- Named workspace instances (`template + instanceName`).
- Toolbar-based switching with deterministic focus target.
- Full CLI creation path without opening settings.
- Built-in layout presets with deterministic window rectangle calculation.
- Template authoring via capture flow and guided/init flow.

## Out Of Scope (v1)

- Cloud sync and shared templates.
- Nested visual tree grouping in toolbar.
- Arbitrary script/condition DSL.
- Team policy/permission system for template publishing.
- Linux/macOS support.

## Locked Decisions

- `ws new` auto-launches by default.
- Duplicate `instanceName` is allowed across different templates.
- Built-in layout presets are available out of box.
- No legacy import/migration.
- Only template-native instances are in scope.
- `--template`, `--preset`, and repeated `--app` are all valid creation modes.

## User Contract

- One command should produce one runnable engineering lane.
- Users can create lanes in 3 ways: template-based, preset-based, inline-app-based.
- Users can switch lane by toolbar click or CLI command.
- Layout restore and focus target are deterministic.

## Success Metrics (v1)

- Time-to-first-lane (preset path) under 60 seconds for new user.
- 2-window warm switch median under 2.5 seconds.
- Focus policy hit rate above 95% in validation scenarios.
- 100% of v1-created instances include required template metadata fields.

## Definition Of Done (Scope Sign-off)

- All command names/flags frozen.
- All out-of-scope items explicitly deferred.
- Preset catalog frozen for v1.
- Required errors and exit codes frozen.
- Acceptance gates in `07-test-and-validation-plan.md` approved.
