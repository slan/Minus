# Minus

A minimal C# agent and session inspector for poking at local LLMs over an OpenAI-compatible `/v1/chat/completions` endpoint. Built against [llama.cpp](https://github.com/ggerganov/llama.cpp) but works with anything that speaks the same wire format.

- **`minus`** — a REPL agent with tool-calling support that writes a full typed JSONL transcript of every session.
- **`minus-view`** — a Terminal.Gui TUI for browsing and live-tailing those transcripts. Turn-grouped timeline, syntax-highlighted JSON detail, follow mode.

The goal is deep debuggability: nothing the model sends, receives, or reasons about is hidden.

## Requirements

- .NET 10 SDK
- A running OpenAI-compatible chat-completions endpoint (e.g. [llama.cpp's `llama-server`](https://github.com/ggerganov/llama.cpp/tree/master/examples/server)) that supports tool calling

## Quick start

```powershell
# Terminal 1 — start the agent against a local llama.cpp server
dotnet run --project Minus.Agent

# Terminal 2 — live-tail the newest session
dotnet run --project Minus.Inspector -- --follow
```

Type messages at the agent's `>` prompt. Watch the inspector fill in as the agent calls tools and the model responds. Press `q` in the inspector to quit, `r` to toggle raw-JSON vs rendered detail, `w` to toggle word-wrap.

## Project layout

```
Minus.sln(x)
├── Minus.Core/       — shared event types, transcript I/O, OpenAI chat records
├── Minus.Agent/      — the REPL agent (outputs: minus)
├── Minus.Inspector/  — the TUI viewer (outputs: minus-view)
└── docs/             — detailed documentation (see index below)
```

`Minus.Core` has no UI and no CLI — it's the contract between the two binaries. See [docs/architecture.md](docs/architecture.md) for why the split exists and what each layer owns.

## Documentation

| Doc | What's in it |
|---|---|
| [docs/architecture.md](docs/architecture.md) | Three-project split, event flow, key design decisions |
| [docs/agent.md](docs/agent.md) | Running, configuring, and extending `minus` — personas, tools, CLI flags, the turn loop |
| [docs/inspector.md](docs/inspector.md) | Using `minus-view` — layout, keybindings, detail modes, follow mode |
| [docs/transcript-schema.md](docs/transcript-schema.md) | The JSONL session format, every event type, linkage fields, examples |

## Notes worth knowing up front

- **Use `127.0.0.1`, not `localhost`.** Docker Desktop on Windows only binds the forwarded port on IPv4; resolving `localhost` hits `::1` first and TCP SYN stalls for ~21 seconds before falling back. We default to `127.0.0.1` for this reason. See [docs/agent.md](docs/agent.md).
- **Reasoning models are supported but off-by-default.** If the server is configured with `--reasoning-format deepseek`, Minus captures the `reasoning_content` field in its transcripts and strips it from history before the next turn (so prior reasoning doesn't get replayed).
- **The transcript schema changed during development.** Old JSONL files written before the typed-event refactor won't parse. The inspector surfaces this as a clear error — delete the file or start fresh.

## License

MIT.
