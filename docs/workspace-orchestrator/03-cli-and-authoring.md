# 03 CLI And Authoring

## Command Families

- `ws templates ...` for template lifecycle.
- `ws new ...` for workspace instance creation from template/preset/inline apps.
- `ws switch ...` for runtime switching.
- `ws mcp` for MCP-based template authoring.

## Implemented Commands

- `ws templates [--json]`
- `ws templates show <template>`
- `ws templates delete <template> [--json]`
- `ws templates validate <template> [--json]`
- `ws templates update <template> --ops-file <path> [--json]`
- `ws templates normalize [--dry-run] [--json] [--templates-dir <path>]`
- `ws new --template <template> --name "<name>" [--repo <path>] [--focus <roles>] [--no-launch]`
- `ws new --name "<name>" --repo <path> --preset <preset> [--focus <roles>] [--save-template <templateId>] [--no-launch]`
- `ws new --name "<name>" --layout <layout> --app "<appSpec>" [--app "<appSpec>" ...] [--focus <roles>] [--save-template <templateId>] [--no-launch]`
- `ws switch --id <workspaceId> [--json] [--quiet]`
- `ws switch --template <template> --name "<instanceName>" [--json] [--quiet]`
- `ws mcp [--templates-dir <path>]`

## `ws new` Mode Matrix

- Template mode: `--template` present.
- Preset mode: `--preset` present and `--template` absent.
- Inline mode: one or more `--app` present and `--template` absent.

### Required

- `--name` is required.
- At least one of `--template`, `--preset`, or `--app`.

### Constraints

- `--template` cannot be combined with `--preset` or `--app`.
- `--focus` can be combined with any mode.
- `--no-launch` still persists workspace config.

## App Spec Contract (`--app`)

### Format

- Comma-separated `key=value` pairs.
- Required keys: `role`, `exe`.
- Optional keys: `cwd`, `args`, `init`, `monitor`.

### Rules

- Keys are case-insensitive.
- Whitespace around keys/values is trimmed.
- Unknown keys fail validation.
- Duplicate keys in one spec fail validation.

## Template Update Syntax

`ws templates update` accepts an ops JSON file in either form:

### Array form

```json
[
  { "op": "set", "path": "description", "value": "My description" },
  { "op": "set", "path": "layout.monitorPolicy", "value": "current" },
  { "op": "set", "path": "windows[role=editor].exe", "value": "code.exe" }
]
```

### Document form

```json
{
  "syntax": "workspace-template-update/v1",
  "template": "pt-dev",
  "ops": [
    { "op": "set", "path": "agent.command", "value": "codex" },
    { "op": "remove", "path": "windows[role=logs].init" }
  ]
}
```

Supported ops: `set`, `replace`, `merge`, `upsert`, `remove`.

## Variable Substitution

Supported tokens in template launch fields:

- `{repo}`
- `{instanceName}`
- `{workspaceTitle}`

## Exit Codes

- `0` success
- `2` validation error or bad arguments
- `3` not found
- `4` launch/switch failure
- `5` switch diagnostic contains errors

## Error Messages

- `Workspace name is required.`
- `Template '<name>' was not found.`
- `Template '<name>' requires --repo.`
- `Invalid --app spec '<value>': expected key=value pairs including role and exe. (<detail>)`
- `Either --template, --preset, or --app must be provided.`
- `Conflicting options: --template cannot be combined with --preset or --app.`

## MCP Summary

- Transport: stdio JSON-RPC with `Content-Length` framing.
- Startup: `TopToolbar.exe ws mcp`.
- Core tools: `template.list`, `template.get`, `template.save`, `template.validate`, `template.delete`, `template.update`, `template.normalize_all`, `app.discover_executables`, `template.suggest`.
