# Architecture

Minus is three projects, one solution. Each has a single responsibility.

```
┌──────────────────────┐        writes        ┌────────────────────┐
│     Minus.Agent      │ ───────────────────▶ │  sessions/*.jsonl  │
│   (CLI, REPL, tools) │                      └────────────────────┘
└──────────┬───────────┘                                 │
           │                                             │ reads
           │ depends on                                  ▼
           ▼                                  ┌────────────────────┐
┌──────────────────────┐                      │  Minus.Inspector   │
│      Minus.Core      │ ◀─── depends on ──── │  (Terminal.Gui TUI)│
│  (events, I/O, JSON) │                      └────────────────────┘
└──────────────────────┘
```

## Who owns what

### `Minus.Core`

The shared contract. No UI, no CLI, no HTTP. Just:

- **`Events.cs`** — polymorphic `SessionEvent` hierarchy (`Meta`, `UserInput`, `LlmRequest`, `LlmResponse`, `ToolCall`, `ToolResult`, `Error`, `End`). Serialized flat via `System.Text.Json` polymorphism with a `"type"` discriminator.
- **`Protocol.cs`** — OpenAI-compatible chat records (`Message`, `ChatRequest`, `ChatResponse`, `Usage`, `ServerTimings`, etc.). What we send to and receive from `llama-server`.
- **`TranscriptWriter.cs`** — append-only JSONL writer used by the agent. `Log(SessionEvent ev)` serializes and flushes.
- **`TranscriptReader.cs`** — pull-stream reader used by the inspector. Both sync (`Read`) and async (`ReadAsync`) variants. Sync is correct for the UI thread; async for background streaming.
- **`Json.cs`** — shared `JsonSerializerOptions` (snake_case naming, null omission).

Both binaries depend on this project. Writer and reader round-trip the same typed record graph — it's the single source of truth for what a session file means.

### `Minus.Agent`

The REPL. Builds to `minus`.

- **`Program.cs`** — CLI entry, arg/env parsing, REPL loop, `Meta` + `End` events.
- **`Agent.cs`** — the turn loop. User input → LLM request → LLM response → optional tool calls → LLM request → … up to `MaxIterations`. Generates a `turn_id` per user turn so all derived events link together.
- **`LlamaClient.cs`** — HTTP client around `/v1/chat/completions`. Logs request/response events. Strips `reasoning_content` from the returned message so it isn't replayed into subsequent turns.
- **`Tool.cs` + `Tools/`** — `ITool` interface and built-in implementations (`read_file`, `list_directory`).
- **`personas/`** — markdown files; contents become the system prompt.

### `Minus.Inspector`

The TUI. Builds to `minus-view`.

- **`Program.cs`** — everything: theme, custom views, TUI wiring.
  - `InspectorView` — root layout (session list, timeline tree, detail pane, separators).
  - `ColoredTextView` — minimal custom view for the detail pane, needed because Terminal.Gui v1's `TextView` applies a single attribute to the whole buffer, ruling out syntax highlighting.
  - `JsonColorizer` — small hand-rolled tokenizer that turns pretty-printed JSON into colored runs.
  - `TimelineNode` / `EventNode` / `TurnGroup` — timeline hierarchy that groups events by `turn_id`.
  - `Theme` — cohesive color scheme (cyan titles, green strings, yellow keywords, blue status bar, etc.).

Depends only on `Minus.Core`. Never touches the HTTP layer.

## Why three projects, not one

An earlier version had everything in a single `Minus.csproj`. The split was driven by a future goal: the agent may eventually embed an inspector panel alongside its REPL, so the inspector's rendering code shouldn't be trapped inside a separate binary.

Concrete benefits of the split:

- **Core has zero UI or CLI dependencies.** If we want to write unit tests, a scripted log processor, or a web viewer, they all take the same `SessionEvent` types.
- **Writer/reader symmetry is enforced by the compiler.** A new event type added to `Events.cs` must be handled everywhere the compiler demands, not just where developers remember to look.
- **Build-time breakage surfaces faster.** If a field renames, the agent and inspector both fail to compile — no drift between a producer and a consumer living in separate checkouts.

The cost is a bit of ceremony (three `.csproj` files, explicit project references, a solution file) but it pays for itself the moment a second consumer appears.

## Event flow

A single user turn generates this sequence, all sharing the same `turn_id`:

```
user_input                         ← agent logs as the user hits enter
  llm_request   ─┐
  llm_response   │ (loop up to MaxIterations)
  tool_call      │
  tool_result   ─┘
(final llm_response without tool_calls ends the loop)
```

The `session_meta` event is written once at the top of a session; `session_end` when the REPL exits cleanly. `Error` events can appear at any point — phase-tagged (`request`, `parse`, `tool`, `agent`) so the inspector can group them.

Linkage fields on every event:

- `id` — per-event UUIDv7 (sortable by creation time)
- `ts` — ISO-8601 UTC timestamp
- `turn_id` — groups events belonging to one user turn
- `parent_id` — optional; currently unused but reserved for direct causal chains (e.g. tool_result → tool_call)

## Key design decisions

- **Typed events, not free-form `{type, data}`.** A polymorphic base `SessionEvent` with `[JsonDerivedType]` attributes keeps writer/reader in lockstep and makes the inspector's pattern-matching dispatcher a joy to write.
- **Write a raw line per event.** No batching, no cross-line state. A line is a complete JSON object, so a poison line (malformed JSON) affects exactly one event.
- **`AutoFlush = true` on the writer.** The inspector tailing a live session sees events within 500ms of the agent emitting them. Throughput is not a concern — these are per-turn events, not per-token.
- **Sync I/O for UI consumers.** Terminal.Gui installs a `SynchronizationContext`; sync-over-async there deadlocks. `TranscriptReader.Read` is sync; `ReadAsync` exists for background streaming contexts.
- **Reasoning content is logged but not replayed.** `LlamaClient.ChatAsync` returns the assistant message with `ReasoningContent = null`; the full reasoning lives only in the transcript's `llm_response` body. Reasoning models expect this.
- **No backward compatibility with pre-typed-event transcripts.** V1 files don't parse — the inspector flags them with a clear error message. Simpler than a migrator.
