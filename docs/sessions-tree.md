# Tree-structured sessions

Design doc for promoting Minus session transcripts from append-only linear logs to a parent-linked tree, enabling resume and branching without giving up forensic completeness. Inspired by [pi-mono's coding-agent](https://github.com/badlogic/pi-mono/blob/main/packages/coding-agent/docs/session.md) but adapted to keep Minus's "every byte the model sends and receives" property intact.

## Goals (v1 scope)

1. **Resume.** Pick up an existing session and keep going — the agent rebuilds in-memory history from the file.
2. **Branching.** From any prior tree event in a session, start a new conversational thread. Both branches coexist in the same file; the inspector can navigate either.
3. **No loss of forensic data.** `llm_request` / `llm_response` / `tool_call` / `tool_result` continue to capture exactly what they capture today. The tree is overlaid, not substituted.
4. **Single file per session.** No sidecar files, no two-file consistency problems. Atomicity stays at "one JSONL line."

## Non-goals (deferred)

- **LLM-generated branch summaries** (pi's `branch_summary`). v1 leaves the abandoned branch's context out of the new branch's history; users branch at the granularity they want context to start.
- **Compaction** (pi's `compaction`). Out of scope until session lengths actually hurt.
- **Labels / named sessions / model-change events.** Useful, not load-bearing for the core capability.
- **Tree visualization in the inspector beyond a branches list.** v1 picks one branch and renders its timeline; full graph rendering is later.
- **`FileSystemWatcher` on `sessions/`.** Already a known gap; not part of this change.

## Schema changes

### `parent_id` becomes mandatory and meaningful

Today every `SessionEvent` already carries an optional `parent_id` field, currently always null and documented as "reserved for direct causal chains." This change makes it the spine of the tree.

**Definition.** `parent_id` on event `E` is the `id` of the event that immediately precedes `E` in the conversation order. For tree events (see below), this defines the conversation tree. For satellite events, it links them to the tree event they annotate.

`parent_id` is `null` only on `session_meta` (the file header) and on the first user message of the root branch.

### Tree events vs. satellite events

Two-tier semantics, **one file**, no new "Message" type:

**Tree events** — define the conversation tree. The leaf walks these.

| Event          | `parent_id` points to                                           |
|----------------|-----------------------------------------------------------------|
| `user_input`   | last tree event before it (last `llm_response` of prior turn) — or `null` for the very first user message |
| `llm_response` | the `user_input` or `tool_result` that triggered the request    |
| `tool_result`  | either the `llm_response` that emitted the tool call (if first result of that response) or the previous `tool_result` (linearized order for multi-tool turns) |

**Satellite events** — annotations. Not part of the leaf walk; used by the inspector to fold context under a tree event.

| Event         | `parent_id` points to                                       |
|---------------|-------------------------------------------------------------|
| `llm_request` | the tree event whose handling triggered this request (`user_input` or `tool_result`) |
| `tool_call`   | the `llm_response` that emitted this tool call              |
| `error`       | whatever it relates to, if known; otherwise the current leaf |
| `session_end` | last event in the file                                       |

Multi-tool-call turns linearize: if `llm_response R` emits tool calls C1, C2, C3, then `tool_call_1.parent = tool_call_2.parent = tool_call_3.parent = R`, but the **tree** chain is `R → tool_result_1 → tool_result_2 → tool_result_3 → next llm_response`. The order matches what we feed the model on the next turn.

### `session_meta` gains two fields

```json
{
  "type": "session_meta",
  "schema_version": 2,
  "system_prompt": "...full text of the persona system prompt...",
  ...existing fields...
}
```

- **`schema_version`** — explicit version. Absence means v1 (pre-tree). v2 = this design.
- **`system_prompt`** — the resolved persona content at session start. Stored so resume is faithful even if the persona file changed since.

### `turn_id` stays

`turn_id` becomes redundant (you can recover it by walking parent pointers to the nearest `user_input`), but it's cheap and the inspector uses it for grouping. Keep it. Document it as "the cached turn-grouping key; same as walking ancestors to the nearest `user_input.id`."

## Leaf semantics

**The leaf is implicit.** It's not stored in any event. At any point, the leaf is "the `id` that the next appended tree event will use as its `parent_id`." Two ways the leaf is set:

1. **On load** — leaf = the last tree event in the file. (For a file that ends mid-turn — say, the process crashed after `tool_result` but before the next `llm_response` — the leaf is that `tool_result`, and the agent resumes by sending the next `llm_request` to close the turn.)
2. **On `/branch <id>`** — leaf = the given event id. Runtime-only until the next append, at which point the new event's `parent_id` makes the branch durable.

A "branch" is not its own event type. It's just a parent_id pointing somewhere other than the previous tail of the file. Pi has a `branch_summary` event for context preservation when switching branches — we defer that (see non-goals).

## Agent changes

### Resume — `/resume [<file>]`

- No arg → most recent `*.jsonl` in `sessions/` for the current cwd.
- Arg → that file path.

Resume flow:

1. Load the file. If `schema_version` is missing or 1, run the in-memory v1→v2 migration (see Migration below).
2. Read `system_prompt` from `session_meta` and seed `_history` with it. (Don't re-read the persona file — the session is the source of truth.)
3. Walk leaf → root collecting tree events. Reverse to chronological order.
4. Convert each tree event to a `Message`:
   - `user_input` → `Message("user", content)`
   - `llm_response` → `Message("assistant", content, ToolCalls=body.choices[0].message.tool_calls)` (without `reasoning_content`, same stripping as today)
   - `tool_result` → `Message("tool", content ?? error, ToolCallId=call_id, Name=...)`
5. Append to `_history`. Set the agent's runtime leaf to the file's last tree event.
6. Continue: subsequent events written by the agent use the resumed leaf as their starting `parent_id`.

The session file is **opened in append mode** — resume continues writing to the same file, so the tree grows in place rather than spawning a new file per resume. The `session_meta` event is *not* re-emitted.

### Branch — `/branch <id>` and `/branches`

- `/branches` — list all events in the current session that have more than one direct child. These are the branch points already in the file.
- `/branch <id>` — set the runtime leaf to `<id>`. Validate that `<id>` exists and is a tree event. The agent does *not* clear `_history` immediately; it rebuilds from the new leaf the same way `/resume` does, then accepts the next user input.

If the user runs `/branch` and then `/quit` without sending a message, nothing is written and the session file is unchanged. The branch only becomes durable when the next event is appended.

### Mid-turn resume

If the file ends with a `tool_result` (i.e., the process died after dispatching a tool but before getting the next assistant response), the resumed agent's `_history` ends with a tool message. The next thing it does is the same thing it would have done in-process: send another `llm_request` with the current history, expecting either a final assistant response or more tool calls. No special-case code path — the turn loop just continues.

### `LlamaClient` and `Agent` plumbing

- `Agent` gains an `_currentLeafId` field (nullable string). All events it emits — `user_input`, `tool_call`, `tool_result` — set `parent_id` from this and update it after each tree event.
- `LlamaClient` is told the current leaf by `Agent` on each call (passed as a parameter alongside `turn_id`) so its `llm_request` and `llm_response` events get the right `parent_id`.
- `TranscriptWriter` doesn't need to change. It just serializes whatever it's given.

## Inspector changes

### Branches pane

When a session contains branch points, a small "Branches" pane appears (top of the timeline column or as a collapsible section). Each entry: branch tip event id, the user input that started the branch, timestamp, event count.

Selecting a branch sets the timeline view to "this branch only" — events on the path from that tip to the root. Other branches are hidden in this view.

A session with no branches is a single-branch session and the pane is hidden.

### Timeline grouping

Unchanged: still groups by `turn_id`. The change is *which* turns are visible — only those on the selected branch's leaf-to-root path.

### Detail view

Unchanged. `parent_id` is rendered as a field in the raw view but not specially highlighted in v1.

### Resume hint

When the agent is running in `--follow` mode against a session, the inspector status bar can show the current leaf id (last tree event seen). Nice-to-have, not required.

## Migration: v1 → v2

Old sessions (no `schema_version` in `session_meta`, no `parent_id` set) are auto-upgraded **in memory by the reader**, not by rewriting the file:

1. Group events by `turn_id` (existing field).
2. Within a turn, sort by `id` (UUIDv7 sorts chronologically) and chain `parent_id` linearly.
3. Between turns, point the first `user_input` of turn N at the last event of turn N-1.
4. Events with no `turn_id` (`session_meta`, `session_end`, unattributed errors) are linked in file order.

The inspector handles v1 files transparently. The agent **refuses to resume v1 files** — too many edge cases (no `system_prompt` field, ambiguous tree structure when the legacy log has multiple `llm_response`s per turn). Resume is v2-only. Browse is v1+v2.

New sessions always write v2. No automatic file rewriting; old files stay v1 forever unless explicitly re-saved.

## Open questions (decide during implementation)

1. **Mid-turn resume safety.** If the leaf is a `tool_result` whose corresponding `tool_call` has different in-flight semantics on the model side (e.g., the model expected exactly one assistant response per tool result and got partial state), we may want to validate before resuming and warn. Probably fine for v1 to just try.
2. **`session_end` on resume.** When you resume a file that already has a `session_end` event, do we treat it as immutable history and disallow append? Current proposal: warn and refuse. The user can `/branch` to the event before `session_end` if they want to continue.
3. **Branching to a satellite event.** `/branch <id>` where `<id>` is a `tool_call` or `llm_request` should be rejected with a message saying "branch points must be tree events; did you mean `<nearest tree event id>`?"
4. **Empty branches.** If a user `/branch`es and then `/quit`s without writing, no event records the branch attempt. That's the design choice (branches without appends aren't real). Worth a one-line note in `--help`.

## Implementation stages

Roughly:

1. **Schema + writer.** Add `schema_version` and `system_prompt` to `session_meta`. Set `parent_id` everywhere. Document in `transcript-schema.md`. *No new behavior yet.*
2. **Reader migration.** v1→v2 in-memory upgrade in `TranscriptReader`. Inspector keeps working on old files.
3. **Resume.** `/resume` slash command. History rebuild from leaf-to-root walk. Append to existing file.
4. **Branching.** `/branch`, `/branches`. Runtime leaf state in `Agent`.
5. **Inspector branches pane.** Branch detection (any tree event with >1 tree-event children) and per-branch filtering of the timeline.

Stages 1–3 are the minimum viable shipment (resume works, no branching). 4–5 add the branching capability.

## Why not pi's exact schema

Pi uses one entry type per logical message (`user`, `assistant`, `toolResult`, `bashExecution`, `custom`, ...) and folds the operational layer into per-message metadata (e.g., `usage`, `provider`, `model` on the assistant message). Minus keeps `llm_request` / `llm_response` / `tool_call` / `tool_result` as separate events on purpose: the request body and response body are different things at different times, and observability (round-trip duration, server timings, the precise bytes we POSTed) lives at the event grain, not the message grain. The two-tier overlay above gets us the tree benefits without collapsing that distinction.
