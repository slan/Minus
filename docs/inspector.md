# The inspector (`minus-view`)

A Terminal.Gui TUI for browsing and live-tailing Minus session transcripts. Read-only; it never modifies files.

## Running it

```powershell
# Browse sessions in ./sessions (default)
dotnet run --project Minus.Inspector

# Live-tail the currently-selected session
dotnet run --project Minus.Inspector -- --follow

# Point at a specific directory
dotnet run --project Minus.Inspector -- C:\path\to\sessions
```

## Layout

```
┌ Sessions ────┬ Timeline ───────────────────────────────────┐
│ 2026-04-24 a │ ▼ Turn abc12345 — 3 events, 412ms, 265 tok │
│ 2026-04-24 b │     14:30:15  user_input      hi           │
│ 2026-04-24 c │     14:30:15  llm_request                  │
│              │     14:30:15  llm_response    455ms  265tk │
│              │   14:30:15    session_meta    persona=...  │
│              │                                             │
│              ├ Event [raw] ────────────────────────────────┤
│              │ {                                           │
│              │   "type": "llm_response",                   │
│              │   "body": { ... syntax-highlighted JSON ... │
│              │   "duration_ms": 455                        │
│              │ }                                           │
└──────────────┴─────────────────────────────────────────────┘
 <session name>  |  N events  |  ctx 1.2k tok (+40 gen)  | [following]
```

- **Left pane (Sessions)** — every `*.jsonl` in the sessions directory, newest first.
- **Top-right pane (Timeline)** — events grouped by `turn_id`. Turn rows are expandable and show per-turn totals (event count, wall duration, total tokens, tool-call count). Events without a turn (session_meta, session_end) sit at the top level.
- **Bottom-right pane (Event detail)** — the currently-selected event as pretty-printed, syntax-highlighted JSON by default. `r` toggles a rendered human-readable view.
- **Status bar** — current session, event count, latest context token usage from the most recent `llm_response`, follow-mode indicator, and key hints.

## Keybindings

| Key | Action |
|---|---|
| `Tab` / `Shift+Tab` | Cycle focus between panes |
| `↑` / `↓` | Navigate the focused pane |
| `←` / `→` | Collapse / expand a turn row (timeline) |
| `Enter` | Expand / collapse (timeline) or no-op (lists) |
| `Page Up` / `Page Down` | Scroll by page (detail pane) |
| `Home` / `End` | Jump to top / bottom (detail pane) |
| `r` | Toggle raw JSON ↔ rendered view in the detail pane |
| `w` | Toggle word-wrap in the detail pane |
| `q` | Quit |

## Detail modes

### Raw (default)

The complete `SessionEvent` as pretty-printed JSON with syntax highlighting:

- **Keys** — bright cyan
- **Strings** — bright green
- **Numbers** — bright magenta
- **Keywords** (`true`, `false`, `null`) — bright yellow
- **Structural** (`{}[],:`) — dim gray

Everything the writer serialized is visible; nothing is hidden. Use this when you want ground truth.

### Rendered

Press `r` to switch. Each event type gets a hand-tuned human-readable layout:

- **session_meta** — key/value block with model, persona, tools, etc.
- **user_input** — quoted content
- **llm_request** — messages as `[N] role:` blocks with content, tool definitions, tool_choice
- **llm_response** — usage, server timings, reasoning, content, tool calls, finish reason — all in separate sections
- **tool_call** — name, call_id, arguments (pretty-printed JSON)
- **tool_result** — content or error with duration
- **error** — phase, status, message, retryable

Useful when you want to quickly scan a session without reading JSON.

## Turn grouping

Events sharing a `turn_id` collapse into one expandable row showing aggregate stats:

```
▼ Turn abc12345 — 5 events, 412ms, 265 tok, 1 tools
```

- `5 events` — how many events belong to the turn
- `412ms` — wall duration from first event to last
- `265 tok` — sum of `total_tokens` across `llm_response`s in the turn
- `1 tools` — number of `tool_call` events

Selecting the turn row itself shows a synthetic summary in the detail pane (stats + list of child events). Selecting a child event shows that specific event.

Events with no `turn_id` (`session_meta`, `session_end`, standalone errors) render at the top level, not nested.

All turns start expanded. Use `←` to collapse, `→` to re-expand.

## Follow mode (`--follow`)

The selected session's file stream is held open; every 500ms the inspector reads any new lines and appends them to the timeline.

```powershell
# Terminal 1
dotnet run --project Minus.Agent

# Terminal 2
dotnet run --project Minus.Inspector -- --follow
```

The status bar shows `[following]` when active. Switching sessions tears down the follower and starts a new one on the newly-selected file; quitting cleans up properly.

**Caveats**

- The session list is a snapshot taken at launch. If the agent creates a *new* session file after the inspector is already running, it won't appear in the list until you relaunch. (Fix pending: `FileSystemWatcher` on the sessions directory.)
- Selection is preserved across tailed appends by matching on event `id`. If the selected event disappears (won't happen with append-only writes, but edge cases exist), selection falls back to the first visible event.

## Handling parse errors gracefully

Transcripts written before the typed-event schema change don't parse. The inspector catches this per-session:

- The timeline shows a single `[parse error] <reason>` row.
- The detail pane explains what happened and suggests deleting the file or starting a new session.
- Other sessions in the list stay accessible — a broken file doesn't crash the inspector.

## Known limitations

- **Character-level word wrap, not word-boundary.** Fine for JSON; less ideal for long reasoning prose in rendered mode.
- **No search.** You can't `/`-find across a session yet.
- **No mouse wheel scrolling in the detail pane.** Use PgUp/PgDn or arrow keys.
- **No selection / copy.** The detail pane is pure display.
- **The running `minus` agent locks `Minus.Core.dll`.** When iterating on the inspector while the agent is live, build `Minus.Inspector` directly (`dotnet build Minus.Inspector`) rather than the full solution.

Any of these are small-to-medium fixes — open for prioritization.
