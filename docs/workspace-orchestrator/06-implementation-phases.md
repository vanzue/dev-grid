# 06 Implementation Phases

## Delivery Principles

- No phase starts without entry criteria met.
- No phase closes without objective acceptance evidence.
- Ship vertical slices when possible; avoid long-lived hidden work.
- Keep feature flags available until Phase 6 completion.

## Phase 0: Contract Freeze

### Entry criteria

- Draft docs exist.

### Key tasks

- Finalize command names/flags.
- Finalize schema field names.
- Finalize built-in preset catalog.
- Freeze required error messages and exit codes.

### Outputs

- Signed contract docs (`01` to `05`).
- Final CLI surface list for implementation.

### Exit criteria

- Signed-off docs `01-05`.

## Phase 1: Schema And Stores

### Entry criteria

- Phase 0 complete.

### Key tasks

- Implement `TemplateDefinition` + `TemplateStore`.
- Extend workspace model with template metadata.
- Add validation primitives.
- Implement atomic write + lock/version guard behavior.
- Implement canonicalization pass.

### Outputs

- Schema models and storage services.
- Validation + normalization pipeline.

### Exit criteria

- Template CRUD works with validation.
- Workspace write includes required template metadata fields.

## Phase 2: CLI Core

### Entry criteria

- Phase 1 complete.

### Key tasks

- Implement `ws templates`, `ws templates show`.
- Implement `ws new` with `--template`.
- Implement `ws switch`.
- Implement stage-1/stage-2 parser architecture.
- Add base help text.

### Outputs

- Usable template-based create/switch CLI.

### Exit criteria

- Template-based create/switch works end-to-end.

## Phase 3: Direct CLI Creation

### Entry criteria

- Phase 2 complete.

### Key tasks

- Implement `--preset`.
- Implement repeatable `--app`.
- Implement `--focus`, `--no-launch`, `--save-template`.
- Implement app-spec parser escaping and key validation.
- Implement option precedence and conflict validation.

### Outputs

- Full settings-free CLI creation path.

### Exit criteria

- User can create runnable workspace fully from CLI without settings.

## Phase 4: Layout Engine

### Entry criteria

- Phase 3 complete.

### Key tasks

- Implement preset expansion.
- Implement normalized rectangle calculation.
- Implement monitor resolution + overlap handling.
- Implement stable slot assignment and overflow behavior.
- Implement settle verification retries.

### Outputs

- Deterministic window placement subsystem.

### Exit criteria

- Rectangles are deterministic and pass layout tests.

## Phase 5: Runtime Focus Integration

### Entry criteria

- Phase 4 complete.

### Key tasks

- Implement role-based focus target resolution.
- Add activation/fallback logic.
- Add switch diagnostics.
- Add structured switch result payload.
- Add stage duration metrics.

### Outputs

- Deterministic switch pipeline with observable outcomes.

### Exit criteria

- Focus behavior is deterministic and matches template policy.

## Phase 6: Hardening

### Entry criteria

- Phase 5 complete.

### Key tasks

- Add command help + samples.
- Add error polish and recovery paths.
- Performance tuning.
- Add regression and stress tests.
- Final docs polish and release notes.

### Outputs

- Release-candidate quality implementation.

### Exit criteria

- Meets latency target and validation gate in test plan.

## Cross-Phase Risks And Mitigations

- Parser ambiguity in `--app`:
  - Mitigation: strict grammar + explicit failing examples in tests.
- Window focus inconsistency on Windows foreground rules:
  - Mitigation: bounded retries + deterministic fallback + telemetry.
- Multi-monitor edge cases:
  - Mitigation: dedicated monitor fallback tests and manual reconnect scenarios.
- Scope creep:
  - Mitigation: enforce Phase 0 contract freeze and out-of-scope list.

## Milestones

- M1: Contract frozen (Phase 0).
- M2: Schema and store foundation complete (Phase 1).
- M3: Template mode usable from CLI (Phase 2).
- M4: Full CLI creation (Phase 3).
- M5: Deterministic layout + focus runtime (Phases 4-5).
- M6: Release candidate (Phase 6).

## Tracking Checklist

- [ ] Phase 0 complete
- [ ] Phase 1 complete
- [ ] Phase 2 complete
- [ ] Phase 3 complete
- [ ] Phase 4 complete
- [ ] Phase 5 complete
- [ ] Phase 6 complete
