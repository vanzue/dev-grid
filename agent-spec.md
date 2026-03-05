Spec: Agent Session via Windows Terminal Window (WT-per-Agent)
1. Goals
1.1 What we are building

A workspace-integrated agent launcher where:

Spinning an agent == launching a new Windows Terminal window running the agent (Copilot/Codex/etc.).

All user interaction (chat, prompts, confirmations) stays inside WT.

The Toolbar shows real-time session status (running / waiting user / done / error) and can bring the correct WT window to foreground with one click.

1.2 Non-goals

No custom chat UI in Toolbar.

No requirement to focus a specific WT tab (each agent has its own window).

No deep semantic understanding of agent output (only minimal status detection).

2. User Experience
2.1 Create / Run

User clicks “New Agent” (or Workspace > New Agent).

Toolbar starts an agent session using a template.

A new WT window opens.

In that WT window, the agent starts and user interacts normally.

2.2 Monitor

Toolbar shows an Agent icon with:

badge count of active sessions

per-session indicators:

Running

Waiting for user

Done

Error

2.3 Return to session

When status changes to WaitingUser (or user manually clicks a session):

User clicks the agent icon / session item in toolbar

Toolbar brings the corresponding WT window to front

User continues interaction in WT

3. Architecture Overview
3.1 Components

Toolbar (Orchestrator UI)

Launch sessions from templates

Maintain session list + state

Subscribe to state updates

Focus WT window for a session

AgentWrap (Session Shim)

Runs inside WT window as the primary command

Spawns the actual agent backend process (copilot, codex, etc.) as a child

Transparently forwards stdin/stdout/stderr between WT and the backend

Emits status events to Toolbar via local IPC

Local IPC Channel

Named pipe (recommended) or local HTTP/gRPC

Bi-directional not strictly required; one-way events + simple request API is enough

3.2 Key design choice

WT is only the interaction surface.
AgentWrap is the reliable status source.
Toolbar never parses or renders full conversations.

4. Data Model
4.1 Session Record (Toolbar)

SessionRecord fields:

sessionId: string (GUID)

workspaceId: string (optional)

templateId: string

displayName: string (e.g., fix-45732)

backend: enum { Copilot, Codex, Claude, Custom }

workingDir: string

createdAt: datetime

state: enum { Starting, Running, WaitingUser, Done, Error, Cancelled }

stateMessage: string (short human hint, optional)

wtProcessId: int (optional)

wtWindowHwnd: uint64 (optional)

lastHeartbeatAt: datetime (optional)

exitCode: int? (optional)

4.2 Template (Agent Hub config)

AgentTemplate fields:

templateId

name

backend

commandLine (backend command, e.g. copilot / codex)

args

env (key-value)

defaultWorkingDirStrategy (repo root / workspace dir / custom)

capabilities:

supportsWaitingUserHeuristics: bool

supportsStructuredEvents: bool (future)

wtProfile (optional: profile GUID/name)

wtWindowTitleFormat (default below)

5. Launch & Window Binding
5.1 Launch command (Toolbar -> WT)

Toolbar launches Windows Terminal with:

new window

explicit title including sessionId

runs agentwrap run ...

Example (conceptual):

Window title: [PTA] <displayName> • <sessionId-short>

Command executed in WT: agentwrap run --session <id> --backend copilot --workdir <dir> -- <backend args>

5.2 Window handle acquisition

Goal: Toolbar stores wtWindowHwnd so it can BringToFront.

Mechanism

AgentWrap, after startup, obtains:

its own process id

parent WT process id (or the window HWND directly if possible)

AgentWrap sends an event: session.bound { wtProcessId, wtWindowHwnd }

If AgentWrap can’t reliably find HWND:

It at least reports wtProcessId.

Toolbar resolves HWND by enumerating top-level windows and matching PID (Win32).

6. Status Detection & State Machine
6.1 State machine

Starting

on backend child process started -> Running

on start failure -> Error

Running

if backend exits with 0 -> Done

if backend exits non-0 -> Error

if heuristics detect prompt requiring user -> WaitingUser

if user cancels -> Cancelled

WaitingUser

on any user input (stdin activity) OR backend output resumes -> Running

backend exit -> Done/Error

Done / Error / Cancelled

terminal remains open (user can review logs)

Toolbar shows final state until user dismisses/archives

6.2 Heuristics for WaitingUser (MVP)

AgentWrap inspects output stream and triggers WaitingUser on a small allowlist of patterns:

"(y/n)", "[y/N]", "Continue?", "Press Enter", "Select", "Choose", "Are you sure"

Backends may add backend-specific patterns by template config

Guardrails

Heuristics must be conservative: false positives are worse than false negatives.

Cooldown: once WaitingUser is emitted, do not emit again for N seconds unless state changed.

6.3 Heartbeat

AgentWrap emits a heartbeat event every 2–5 seconds while backend is alive:

used to mark session as healthy

optional for MVP, but helpful for stale detection

7. IPC Protocol (MVP)
7.1 Transport

Named pipe: \\.\pipe\PowerToys.AgentHub (example)

Messages are newline-delimited JSON (NDJSON) OR length-prefixed JSON.

7.2 Events (AgentWrap -> Toolbar)

All events include:

type

sessionId

timestamp

Event types:

session.created

payload: templateId, backend, workingDir, displayName

session.bound

payload: wtProcessId, wtWindowHwnd?

status.changed

payload: state, message?

heartbeat

payload: state, lastOutputAt?

process.exited

payload: exitCode

error.raised

payload: errorCode, message

7.3 Commands (Toolbar -> AgentWrap) — optional MVP

Not required because interaction happens in WT.
But useful later:

session.archive(sessionId)

session.terminate(sessionId) (sends kill to backend)

For MVP, Toolbar can terminate by OS process kill using tracked PID.

8. Toolbar UI Requirements
8.1 Agent icon

Badge shows:

number of sessions in Running + WaitingUser

Color/indicator:

if any WaitingUser exists: highlight

else if any Running: normal active indicator

else: idle

8.2 Session list (dropdown/panel)

Each row shows:

displayName

state

lastUpdated

quick actions:

Focus (bring WT window front)

Archive (removes from list, does not close WT)

Terminate (optional)

8.3 Focus behavior

On click Focus:

if wtWindowHwnd known: SetForegroundWindow(hwnd)

else:

attempt resolve by PID → hwnd

if still unknown: fall back to launching WT (optional) or show “Window not found”

9. Error Handling & Edge Cases

WT window closed manually

heartbeat stops; process handles invalid

Toolbar marks session as Ended (Window Closed) or Error with message

AgentWrap crashes

heartbeat stops

Toolbar marks session as Error: AgentWrap terminated

Backend exits but window remains

state becomes Done/Error; WT remains open for review

Multiple WT windows with same title

sessionId must be unique; title includes sessionId short to disambiguate

10. Security & Privacy

All IPC is local machine only.

Do not transmit full stdout logs to Toolbar by default.

Only emit:

state

minimal message (short)

PIDs/HWND

Optional future: explicit opt-in to stream log snippets.

11. Milestones
Milestone 0 (MVP)

Agent templates

Launch WT window per agent

AgentWrap I/O passthrough

IPC events: created/bound/status/exited

Toolbar list + focus window

Milestone 1

WaitingUser heuristics + UI highlight

Heartbeat & stale detection

Milestone 2

Structured event channel (future-proof)

richer capabilities per backend

“archive/terminate” via protocol

12. Acceptance Criteria

From Toolbar, user can start a new agent and a WT window opens running it.

Toolbar lists the session within 1s of launch and shows Running.

When backend exits, Toolbar updates to Done or Error.

When WaitingUser triggers, Toolbar highlights and on click focuses the correct WT window.

Closing WT window removes or marks the session as ended within 5–10 seconds.