# 03 CLI And Authoring

## Command Families

- `ws templates` and `ws template ...` for template lifecycle.
- `ws new ...` for workspace instance creation.
- `ws switch ...` for lane activation.

## Primary Commands (v1)

- `ws templates`
- `ws templates show <template>`
- `ws new --template <template> --name "<name>" [--repo <path>] [--focus <roles>] [--no-launch]`
- `ws new --name "<name>" --repo <path> --preset <preset> [--focus <roles>] [--save-template <templateId>] [--no-launch]`
- `ws new --name "<name>" --layout <layout> --app "<appSpec>" [--app "<appSpec>" ...] [--focus <roles>] [--save-template <templateId>] [--no-launch]`
- `ws switch --id <workspaceId>`
- `ws switch --template <template> --name "<instanceName>"`

## Template Authoring Commands

- `ws template capture --name <templateId>`
- `ws template init --name <templateId>`
- `ws template add-window --template <templateId> --role <role> --path <exe> [--args ...] [--cwd ...]`
- `ws template set-layout --template <templateId> --preset <preset>`
- `ws template set-focus --template <templateId> --roles <r1,r2,...>`
- `ws template validate --name <templateId>`
- `ws template delete --name <templateId>`

## `ws new` Mode Matrix

- Template mode: `--template` present.
- Preset mode: `--preset` present, `--template` absent.
- Inline mode: one or more `--app` present, `--template` absent.

Required:

- `--name` is always required.
- At least one of `--template`, `--preset`, or `--app`.

Mutual constraints:

- `--template` may be combined with `--focus` and `--no-launch`.
- `--preset` may be combined with `--app` (app entries augment preset windows).
- If both `--preset` and `--layout` are provided, `--layout` overrides preset layout strategy.

## App Spec Contract (`--app`)

### Format

- Comma-separated `key=value` pairs.
- Required keys: `role`, `exe`.
- Optional keys: `cwd`, `args`, `init`, `monitor`.

### Key rules

- Keys are case-insensitive.
- Whitespace around keys/values is trimmed.
- Unknown keys fail validation.
- Duplicate keys in one spec fail validation.

### Escaping and quoting

- If a value contains `,` or `=`, wrap value in double quotes.
- Inside quoted values, `\"` is treated as a literal quote.
- Backslash escaping is supported only inside quoted values.

### Examples

- `--app "role=terminal,exe=wt.exe,cwd={repo},init=pwsh -NoExit -Command .\\tools\\dev.ps1"`
- `--app "role=editor,exe=code.exe,args={repo},cwd={repo}"`
- `--app "role=review,exe=msedge.exe,args=\"https://github.com/microsoft/PowerToys/pulls\""`

## Resolution Precedence

For final runtime config, highest to lowest priority:

1. Explicit CLI flags on current command.
2. Inline `--app` entries.
3. Preset defaults.
4. Template defaults.

Applied examples:

- `--focus` overrides template/preset `focusPriority`.
- `--layout` overrides preset/template layout strategy.
- Per-app `cwd/args/init` from inline `--app` overrides preset-provided app defaults for same role.

## Variable Substitution

Supported tokens:

- `{repo}`
- `{instanceName}`
- `{workspaceTitle}`

Applies to:

- executable path
- args
- working directory
- startup/init command
- monitor selector values when templated

## Required Error Messages

- `Workspace name is required.`
- `Template '<name>' was not found.`
- `Template '<name>' requires --repo.`
- `Invalid --app spec '<value>': expected key=value pairs including role and exe.`
- `Either --template, --preset, or --app must be provided.`
- `Conflicting options: <detail>.`
- `Workspace '<title>' already exists.`

## Exit Codes

- `0` success
- `2` validation error
- `3` template not found
- `4` launch/switch failure
- `5` storage failure

## Help Examples (must ship)

- `ws new --template powertoys-coding --name "FZ crash fix" --repo D:\src\PowerToys`
- `ws new --name "PR review #45773" --preset horizontal-equal --repo D:\src\PowerToys`
- `ws new --name "CmdPal refactor" --layout main-left-70 --app "role=editor,exe=code.exe,args={repo},cwd={repo}" --app "role=terminal,exe=wt.exe,cwd={repo}" --focus editor,terminal`
- `ws new --name "Release prep" --preset vertical-equal --repo D:\src\PowerToys --save-template powertoys-release`
- `ws switch --template powertoys-coding --name "FZ crash fix"`

## Parser Notes

- Parse command line in two stages:
  - Stage 1: verb/subcommand routing (`templates`, `template`, `new`, `switch`).
  - Stage 2: option parsing with mode-specific validation.
- Validation must complete before any persistence side effects.
- `--no-launch` still writes workspace definition and button config.

## Implementation Checklist

- [ ] Add `ws` root command parser.
- [ ] Implement `--app` parser with escaping/quote support.
- [ ] Implement mode matrix and option conflict checks.
- [ ] Implement precedence rules (`--focus` overrides preset/template defaults).
- [ ] Implement `--save-template` flow from inline config.
- [ ] Add command help text and examples.
