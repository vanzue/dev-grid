# 02 Data Model And Storage

## Storage Paths (v1)

- Templates: `%LOCALAPPDATA%\\TopToolbar-Standalone\\config\\templates\\`
- Workspace instances: `%LOCALAPPDATA%\\TopToolbar-Standalone\\config\\workspaces.json`
- Provider buttons/config: `%LOCALAPPDATA%\\TopToolbar-Standalone\\Providers\\WorkspaceProvider.json`
- Root override (optional): `TOPTOOLBAR_APPDATA_ROOT`

## TemplateDefinition Schema

### Required fields

- `schemaVersion`
- `name`
- `displayName`
- `windows[]`
- `layout`
- `focusPriority[]`

### Optional fields

- `description`
- `requiresRepo`
- `defaultRepoRoot`

### Field constraints

- `schemaVersion`: integer, must be `1`.
- `name`: lowercase slug `[a-z0-9-]{3,64}`, unique case-insensitive.
- `displayName`: `1..80` chars.
- `windows`: `1..20` entries.
- `focusPriority`: `1..20` role names, each role must exist in `windows.role`.
- `defaultRepoRoot`: expanded with environment variables at runtime.

## Window Entry Schema (`windows[]`)

### Required fields

- `role`
- `exe`

### Optional fields

- `cwd`
- `args`
- `init`
- `monitor`
- `matchHints`

### Field constraints

- `role`: `[a-z0-9-]{2,32}`.
- `exe`: command name or absolute path, non-empty.
- `monitor`: one of `primary|current|any|explicit:<id>`.
- `matchHints`: optional identity hints only, no behavior flags.

## Layout Schema (`layout`)

### Required fields

- `strategy`
- `monitorPolicy`
- `slots[]`

### Optional fields

- `preset`

### Slot constraints

- `monitorPolicy`: currently `primary|current|any` in Settings UI; runtime also supports `explicit:<id>` when loaded from JSON.
- `role` must exist in `windows.role`.
- `x`, `y`, `w`, `h` are floats in `[0,1]`.
- `w > 0`, `h > 0`.
- `x + w <= 1`, `y + h <= 1`.

## Workspace Instance Schema (v1-created required fields)

- `id`
- `template-name`
- `instance-name`
- `workspace-title`
- `repo-root` (optional)
- `createdAt`

Plus runtime monitor/application placement fields used by launcher.

### Field constraints

- `id`: GUID string.
- `instance-name`: `1..120` chars.
- `workspace-title`: computed `<displayName> · <instance-name>`.
- uniqueness key: `template-name + instance-name` case-insensitive.

## Validation Rules

- Template `name` is unique, case-insensitive.
- `focusPriority` roles must exist in `windows.role`.
- `windows.role` + `windows.exe` required per entry.
- If any field contains `{repo}`, repo resolution must succeed.
- Unknown required fields or invalid value ranges fail validation.
- `ws new` validation runs before any file write.

## Versioning

- Start at `schemaVersion: 1`.
- Reject unknown major versions.
- Allow additive fields without breaking parse.

## Persistence Semantics

- Writes are atomic: temp file then replace.
- Use cross-process lock/version guard for concurrent writes.
- Retry transient IO failures with bounded retries.
- Never partially write JSON files.

## Canonicalization Rules

- Trim whitespace in ids, names, and role values.
- Normalize template `name` to lowercase.
- Preserve user-visible casing in `displayName` and `instance-name`.
- Expand environment variables at execution time, not at save time.

## Minimal Template Example

```json
{
  "schemaVersion": 1,
  "name": "powertoys-coding",
  "displayName": "PowerToys",
  "focusPriority": ["editor", "terminal"],
  "layout": {
    "strategy": "side-by-side",
    "monitorPolicy": "primary",
    "slots": [
      { "role": "editor", "x": 0.0, "y": 0.0, "w": 0.7, "h": 1.0 },
      { "role": "terminal", "x": 0.7, "y": 0.0, "w": 0.3, "h": 1.0 }
    ]
  },
  "windows": [
    { "role": "terminal", "exe": "wt.exe", "cwd": "{repo}" },
    { "role": "editor", "exe": "code.exe", "args": "{repo}", "cwd": "{repo}" }
  ]
}
```

## Implementation Checklist

- [ ] Add `TemplateDefinition` model.
- [ ] Add `TemplateStore` with atomic writes + retry.
- [ ] Extend workspace definition model with template metadata fields.
- [ ] Add strict validator with field-level errors.
- [ ] Add normalization/canonicalization pass before save.
