# 07 Test And Validation Plan

## Test Strategy

- Unit tests for parsing, schema validation, normalization, layout math, and focus selection.
- Integration tests for CLI-to-runtime end-to-end workflows.
- Manual tests for desktop/window-manager edge cases not reliably covered in CI.

## Core Test Matrix

### CLI Parsing

- valid `--template` flow
- valid `--preset` flow
- valid repeatable `--app` flow
- invalid `--app` format
- missing required options
- conflicting options (`--template` + invalid combo)
- quoted/escaped `--app` values with commas/equal signs

### Data Validation

- missing template fields
- invalid focus role reference
- unresolved `{repo}` substitution
- duplicate template id
- duplicate `template + instance`
- invalid slot bounds (`x+w>1`, `y+h>1`)
- unknown schema version

### Layout

- preset expansion correctness
- normalized-to-pixel conversion
- min-size clamp
- out-of-bounds clamp
- monitor fallback behavior
- overlap resolution determinism
- stable role-to-slot mapping for duplicate-role windows

### Runtime Switch

- existing-window claim success
- missing-window launch success
- partial launch diagnostics
- minimize extraneous windows
- focus policy role hit and fallback
- focus activation retry path
- stage timing/result payload completeness

## Required Automated Test Suites

- `CliParserTests`
- `AppSpecParserTests`
- `TemplateValidationTests`
- `LayoutComputationTests`
- `MonitorResolutionTests`
- `SwitchPipelineTests`
- `FocusSelectionTests`

## Non-Functional Gates

- 2-window warm launch under 2.5s target.
- deterministic focus target in repeated runs.
- correct behavior on multi-monitor + virtual desktop.
- no unhandled exceptions in stress run (100 repeated switches).

## Pass/Fail Criteria

- Unit pass rate: 100%.
- Integration pass rate: 100%.
- No P0/P1 defects open.
- All required error messages match contract text.
- Telemetry fields in switch result payload are present.

## Test Data Fixtures

- fixture template: `powertoys-coding`.
- fixture template: `review-triple`.
- fixture preset-only scenarios for all built-in presets.
- fixture inline-app scenarios including escaped values.

## Manual Scenarios

1. Create coding lane via `--template`.
2. Create review lane via `--preset`.
3. Create custom lane via repeated `--app`.
4. Save custom lane as template and reuse.
5. Disconnect target monitor and switch workspace.
6. Switch across virtual desktops and verify off-desktop windows are not claimed.
7. Trigger focus failure (blocked foreground) and verify deterministic fallback.
8. Run with `--no-launch` and verify persistence without activation.

## Observability Validation

- Verify switch log contains:
  - stage durations
  - claimed/launched/arranged/minimized counts
  - focused role + handle
  - failure taxonomy code when not successful
- Verify no sensitive command-line arguments are logged in plain text.

## Exit Criteria For Release Candidate

- All automated suites passing on target architecture.
- Manual scenarios signed off.
- Performance gate met on reference machine.
- Documentation/help examples validated against actual CLI behavior.

## Release Gate Checklist

- [ ] Unit tests green.
- [ ] Integration tests green.
- [ ] Stress test run completed.
- [ ] Manual scenarios completed.
- [ ] Error messages verified.
- [ ] Logs/telemetry verified.
