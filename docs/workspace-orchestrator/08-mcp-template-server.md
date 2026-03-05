# 08 MCP Template Server

## Purpose

`ws mcp` exposes template authoring and executable discovery over MCP (stdio JSON-RPC), so an agent can build and maintain templates without manual JSON editing.

## Start

```powershell
TopToolbar.exe ws mcp
```

Optional templates directory override:

```powershell
TopToolbar.exe ws mcp --templates-dir "C:\path\to\templates"
```

## Protocol Notes

- Framing: `Content-Length: <bytes>\r\n\r\n<json>`.
- JSON-RPC version: `2.0`.
- Supports `initialize`, `tools/list`, `tools/call`, `shutdown`, `exit`.
- After `shutdown`, only `exit` is accepted.

## Available Tools

- `template.list`
- `template.get`
- `template.save`
- `template.validate`
- `template.delete`
- `template.update`
- `template.normalize_all`
- `app.discover_executables`
- `template.suggest`

## Tool Behavior

- `template.save`: standardizes + validates + persists one template.
- `template.validate`: validates either payload (`template`) or stored template (`name`).
- `template.update`: applies `workspace-template-update/v1` ops to a stored template.
- `template.normalize_all`: normalizes all template files in the templates directory.
- `app.discover_executables`: resolves executable paths for known app IDs.
- `template.suggest`: builds a standardized template skeleton from high-level intent.

## Update Syntax

`template.update` expects:

```json
{
  "syntax": "workspace-template-update/v1",
  "template": "pt-dev",
  "ops": [
    { "op": "set", "path": "description", "value": "Updated by MCP" },
    { "op": "set", "path": "agent.command", "value": "codex" }
  ]
}
```

Supported ops: `set`, `replace`, `merge`, `upsert`, `remove`.

## Suggested Agent Flow

1. `tools/list`
2. `app.discover_executables`
3. `template.suggest`
4. `template.save`
5. `template.validate`
6. Optional incremental edits with `template.update`
7. Optional cleanup with `template.delete`
