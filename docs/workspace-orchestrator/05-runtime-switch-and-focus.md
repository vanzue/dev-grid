# 05 Runtime Switch And Focus

## Purpose

Define deterministic runtime behavior for switching into a workspace lane, including reuse/launch/layout/focus/minimize sequencing and failure semantics.

## Switch State Machine

1. `ResolveWorkspace`:
   - Resolve by `workspaceId` or `template + instanceName`.
   - Validate metadata and window entries.
2. `ClaimExisting`:
   - Attempt role-aware matching against live windows on current virtual desktop.
3. `LaunchMissing`:
   - Launch all unclaimed required roles.
4. `Arrange`:
   - Apply computed layout rectangles.
5. `MinimizeExtraneous`:
   - If enabled, minimize non-workspace windows on current desktop.
6. `Focus`:
   - Apply focus policy.
7. `Finalize`:
   - Persist launch timestamp and emit result payload.

## Focus Policy

Each template defines `focusPriority` roles.

Resolution:

1. Pick first live role in `focusPriority`.
2. Attempt foreground activation for that window.
3. If activation fails, try next live role.
4. If none activate, fallback to first successfully assigned window by stable order.
5. If no assigned windows exist, return launch failure.

## Matching Priority

For each role candidate, match score priority:

1. explicit app identity (AUMID/package/PWA if present)
2. full process path
3. process name
4. title exact match (last resort)

Tie-breakers:

- nearest saved rectangle center distance
- largest visible area
- stable window handle ordering

## Role Matching Inputs

- `role`
- process path/name hints
- app model identity when available
- title hints (last resort)

## Virtual Desktop And Monitor Constraints

- Matching excludes cloaked windows.
- Matching excludes windows not on current virtual desktop.
- Arrange stage uses resolved monitor targets from layout engine.

## Failure Handling

- Partial launch returns structured diagnostics and non-zero status.
- If all required roles fail claim+launch, return failure.
- If focus target role exists but activation fails, continue fallback chain.
- Minimize failures are non-fatal and logged.

## Result Contract

Return payload per switch:

- `ok` (bool)
- `workspaceId`
- `claimedCount`
- `launchedCount`
- `arrangedCount`
- `minimizedCount`
- `focusedRole`
- `focusedHwnd`
- `durationMs`
- `errors[]`

## Timeouts And Retries

- Claim timeout per role: bounded (default 1.5s).
- Launch wait timeout per app: bounded (default 10s).
- Arrange settle retries: bounded (per layout spec).
- Focus activation retries: bounded (default 3 attempts, 150ms interval).

## Telemetry/Logging

Log per switch:

- total latency
- matched count
- launched count
- focused role/window handle
- minimize count
- per-stage duration (`resolve/claim/launch/arrange/minimize/focus`)
- failure reason taxonomy (`not-found`, `launch-timeout`, `focus-failed`, `arrange-failed`)

## Determinism Requirements

- Same workspace + same desktop state must produce same focus target.
- Same candidate set must produce same claim mapping.
- No random ordering in role assignment or fallback.

## Implementation Checklist

- [ ] Add explicit switch state machine orchestration.
- [ ] Add role-aware matcher with deterministic tie-breakers.
- [ ] Add role-aware focus selection utility.
- [ ] Add explicit foreground activation step.
- [ ] Add deterministic fallback ordering.
- [ ] Add structured switch result payload.
- [ ] Add stage timing and failure taxonomy logs.
- [ ] Add runtime logs for focus decisions.
