# 04 Layout Engine

## Purpose

Convert template/preset layout intent into deterministic per-window target rectangles across monitor/DPI variations.

## Layout Model

- `strategy`: `side-by-side | main-secondary | grid | custom`
- `monitorPolicy`: `primary | current | any | explicit`
- `slots[]`: normalized role rectangles (`x`, `y`, `w`, `h`) in `[0,1]`
- `preset`: optional id expanded to slots at creation time
- `minWidth`/`minHeight`: optional per-slot minimums (defaults `480x320`)

## Slot Assignment Rules

1. Match windows to slots by `role`.
2. If multiple windows share same role, assign by stable order (creation order).
3. If no slot exists for a role:
   - `custom` strategy: skip window placement override.
   - preset/structured strategy: place in overflow cascade area on target monitor.
4. If slot exists but no matching window, ignore slot.

## Preset Expansion (v1 canonical slots)

- `horizontal-equal`:
  - `slotA: x=0.00,y=0.00,w=0.50,h=1.00`
  - `slotB: x=0.50,y=0.00,w=0.50,h=1.00`
- `vertical-equal`:
  - `slotA: x=0.00,y=0.00,w=1.00,h=0.50`
  - `slotB: x=0.00,y=0.50,w=1.00,h=0.50`
- `main-left-70`:
  - `main: x=0.00,y=0.00,w=0.70,h=1.00`
  - `secondary: x=0.70,y=0.00,w=0.30,h=1.00`
- `main-right-70`:
  - `secondary: x=0.00,y=0.00,w=0.30,h=1.00`
  - `main: x=0.30,y=0.00,w=0.70,h=1.00`
- `triple-columns`:
  - `col1: x=0.000,y=0.00,w=0.333,h=1.00`
  - `col2: x=0.333,y=0.00,w=0.334,h=1.00`
  - `col3: x=0.667,y=0.00,w=0.333,h=1.00`
- `grid-2x2`:
  - `q1: x=0.00,y=0.00,w=0.50,h=0.50`
  - `q2: x=0.50,y=0.00,w=0.50,h=0.50`
  - `q3: x=0.00,y=0.50,w=0.50,h=0.50`
  - `q4: x=0.50,y=0.50,w=0.50,h=0.50`

Preset IDs resolve to explicit slots at create-time and are stored as slots.

## Position Calculation (required)

Given monitor work area `(monitorLeft, monitorTop, monitorWidth, monitorHeight)`:

- `targetX = monitorLeft + round(slot.x * monitorWidth)`
- `targetY = monitorTop + round(slot.y * monitorHeight)`
- `targetW = max(minWidth, round(slot.w * monitorWidth))`
- `targetH = max(minHeight, round(slot.h * monitorHeight))`

Then:

1. Clamp `targetX/targetY/targetW/targetH` to monitor work area bounds.
2. If right/bottom edge exceeds monitor, shift left/up first; shrink only if needed.
3. Resolve overlaps by strategy priority (`main` > `secondary` > others).
4. Apply placement.
5. Run settle verification with tolerance.

Rounding rule:

- Use banker's rounding-disabled midpoint-away-from-zero for deterministic edge results.

## Monitor Resolution Order

1. Exact monitor from `explicit`.
2. `current` monitor.
3. `primary` monitor.
4. Any available monitor.

Tie-breakers:

- Prefer monitor with largest work area.
- Then lowest monitor index.

## Determinism Rules

- Same inputs must produce same slot rectangles.
- Persist expanded slot geometry in workspace definition.
- Avoid random monitor picks when multiple candidates exist.
- Use stable window ordering when mapping multiple windows to same role.

## Failure And Fallback Behavior

- If all monitor resolution attempts fail, return layout failure and skip destructive window moves.
- If one window placement fails, continue placing others and report partial success.
- If settle verification fails, retry bounded times and keep best-known rectangle.

## Observability

Must log per window:

- selected monitor id
- slot id/role
- computed rectangle
- final applied rectangle
- retries and settle result

## Implementation Checklist

- [ ] Implement preset-to-slots expansion.
- [ ] Implement normalized-to-pixel conversion utility.
- [ ] Implement monitor fallback resolver.
- [ ] Implement stable role-to-slot mapping.
- [ ] Implement overlap resolver.
- [ ] Implement bounded settle retry policy.
- [ ] Add metrics/logging for final rectangle assignment.
