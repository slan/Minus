# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Minus is a debug-focused C# REPL agent (`minus`) and TUI session inspector (`minus-view`) for poking at local LLMs over an OpenAI-compatible `/v1/chat/completions` endpoint (primarily llama.cpp). The premise is total visibility: every byte the agent sends, receives, and reasons about is captured in a typed JSONL transcript that the inspector can browse or live-tail.

Target framework is **.NET 10**. Solution file is `Minus.slnx` (the new XML format).

## Common commands

```powershell
# Run the agent (writes sessions/<utc-timestamp>.jsonl in CWD)
dotnet run --project Minus.Agent

# Run the inspector against ./sessions
dotnet run --project Minus.Inspector

# Live-tail the newest session as the agent writes it
dotnet run --project Minus.Inspector -- --follow

# Build a single project (use this when iterating on the inspector while the
# agent is running — a live `minus` process locks Minus.Core.dll, so building
# the whole solution will fail)
dotnet build Minus.Inspector

# Common agent flags / env vars
dotnet run --project Minus.Agent -- --endpoint http://127.0.0.1:9090 --model gemma-4-e4b --persona debugger
# MINUS_ENDPOINT, MINUS_MODEL also work
```

There are no tests in this repo yet.

## Architecture

Three projects, one solution. The split is load-bearing — don't collapse them.

- **`Minus.Core`** — shared contract. Zero UI, zero CLI, zero HTTP. Contains the `SessionEvent` polymorphic hierarchy (`Events.cs`), OpenAI wire records (`Protocol.cs`), `TranscriptWriter` / `TranscriptReader`, `ISessionEventSink`, and shared `JsonSerializerOptions` (snake_case, null-omitting). Both binaries depend on this.
- **`Minus.Agent`** → builds to `minus`. The REPL, turn loop (`Agent.cs`), HTTP client (`LlamaClient.cs`), tool dispatch (`Tool.cs` + `Tools/`), slash commands (`SlashCommand.cs` + `Commands/`), Spectre.Console UI (`ConsoleUi.cs`, `AsyncConsole.cs`), and personas (markdown copied to output as content files).
- **`Minus.Inspector`** → builds to `minus-view`. Terminal.Gui v1 TUI; everything lives in `Program.cs`. Depends only on `Minus.Core`.

### Event flow & linkage

A user turn produces this sequence, all sharing one `turn_id`:

```
user_input → llm_request → llm_response [→ tool_call → tool_result → llm_request → llm_response]* (terminating response has no tool_calls)
```

`session_meta` is written once at startup; `session_end` on clean exit; `error` events can appear at any point (phase-tagged: `request` / `parse` / `tool` / `agent`). All events have a UUIDv7 `id` and ISO-8601 `ts`. `tool_call` ↔ `tool_result` are paired by `call_id`.

### Sinks

The agent doesn't write to disk directly. Events flow through `ISessionEventSink`. `Program.cs` composes an `AggregateEventSink(transcript, ui)` so every event hits both the JSONL file and the live terminal UI. **When adding a new event-emitting code path, log via the sink, never directly to the writer.**

### Slash commands vs. tools — keep them straight

- **`ITool`** (in `Minus.Agent/Tool.cs`) — invoked by the *model*; output is fed back into the conversation as a `tool` role message. Register in `Program.cs` (`ITool[] tools = [...]`). Each needs `Name`, `Description`, a strict `ParametersSchema` (`JsonElement` JSON Schema), and `ExecuteAsync`.
- **`ISlashCommand`** (in `Minus.Agent/SlashCommand.cs`) — invoked by the *user* with `/name`; effect is purely local (print, change settings, exit). Register in `Program.cs` (`ISlashCommand[] slashCommands = [...]`).

### Personas

Markdown files in `Minus.Agent/personas/`. The `.csproj` copies `personas/*.md` to the output dir, and `Program.cs` resolves them from `AppContext.BaseDirectory`. Selected with `--persona <name>` (no extension). The file's raw contents become the `system` message at index 0 of `_history`. Adding a persona = drop a new `.md` file.

## Things that will bite you

- **Use `127.0.0.1`, never `localhost`.** Docker Desktop on Windows binds forwarded ports IPv4-only; resolving `localhost` hits `::1` first and the SYN stalls ~21s before falling back. The default endpoint is hardcoded to `127.0.0.1` for this reason. Don't "fix" it.
- **Reasoning content is captured but not replayed.** `LlamaClient.ChatAsync` returns the assistant message with `ReasoningContent = null`; the full reasoning lives in the transcript's `llm_response.body` only. Reasoning models are trained to regenerate reasoning each turn — re-feeding it breaks them. Don't change this without understanding why.
- **No transcript schema versioning, no backwards compatibility.** The schema changed once during early development; pre-change files don't parse. If you make a breaking change, the inspector should surface it as a clear error, not a silent crash. Either add `schema_version` to `session_meta` or write a one-off migrator — but don't pretend old files still work.
- **`sessions/` is resolved from CWD, not the binary location.** Running `dotnet run` from a different directory writes the transcript there. The inspector takes an optional path argument for this case.
- **`AutoFlush = true` on the writer.** Don't disable it — the inspector's 500ms tail relies on each line being on disk by the time it polls. Throughput isn't a concern; these are per-turn events, not per-token.
- **Terminal.Gui v1 needs sync I/O on the UI thread.** `TranscriptReader.Read` is sync; `ReadAsync` exists for background streaming. Don't sync-over-async on the UI thread — it deadlocks under Terminal.Gui's `SynchronizationContext`.
- **Iterating on the inspector while `minus` is running:** build `Minus.Inspector` directly (`dotnet build Minus.Inspector`). The full-solution build will fail because the live agent process locks `Minus.Core.dll`.
- **JSON conventions are centralized.** Use `Minus.Core.Json.Options` for any `System.Text.Json` (de)serialization that touches transcript data — snake_case + null-omission. Don't roll your own options.
- **Tool argument strings are stored verbatim.** The `tool_call.arguments` field is the raw string the model produced (potentially with escapes/whitespace). Don't normalize it on the way in; the inspector pretty-prints for display.

## Adding things

- **A new tool** — implement `ITool` in `Minus.Agent/Tools/`, register in `Program.cs`. Keep `ParametersSchema` strict (required fields, enums, no free-form objects); the model behaves better with tight schemas.
- **A new slash command** — implement `ISlashCommand` in `Minus.Agent/Commands/`, register in `Program.cs`.
- **A new event type** — add a record to `Minus.Core/Events.cs` with `[JsonDerivedType]`. The compiler will demand the inspector handle it (both raw and rendered detail modes in `Minus.Inspector/Program.cs`). That symmetry is the whole reason `Minus.Core` exists — don't subvert it by stuffing untyped data into an existing event.

## Docs

The `docs/` directory has more depth — read these when relevant:

- `docs/architecture.md` — three-project split rationale, event flow diagrams, design decisions.
- `docs/agent.md` — running/configuring/extending the REPL, the turn loop, reasoning-model handling.
- `docs/inspector.md` — TUI layout, keybindings, follow mode, known limitations.
- `docs/transcript-schema.md` — every event type, every field, examples of full sessions.
