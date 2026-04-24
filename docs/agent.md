# The agent (`minus`)

`Minus.Agent` is a REPL that talks to an OpenAI-compatible chat-completions endpoint, dispatches tool calls, and writes a structured JSONL transcript of the whole session.

## Running it

```powershell
dotnet run --project Minus.Agent
```

You'll see:

```
Minus — persona: default — endpoint: http://127.0.0.1:8080 — transcript: sessions\2026-04-24T05-15-57.jsonl
Type your message. 'exit' or Ctrl+D to quit.

>
```

Each user message triggers a turn. The agent talks to the model, executes any tool calls the model asks for, feeds results back, and repeats until the model answers without calling a tool (or `MaxIterations = 25` is hit).

`exit` at the prompt or Ctrl+D ends the session cleanly (writes `session_end` to the transcript).

## CLI flags

| Flag         | Env var           | Default                   |
| ------------ | ----------------- | ------------------------- |
| `--endpoint` | `MINUS_ENDPOINT`  | `http://127.0.0.1:8080`   |
| `--model`    | `MINUS_MODEL`     | `local`                   |
| `--persona`  | —                 | `default`                 |

Example:

```powershell
dotnet run --project Minus.Agent -- `
  --endpoint http://127.0.0.1:9090 `
  --model gemma-4-e4b `
  --persona debugger
```

### Why `127.0.0.1` and not `localhost`

Docker Desktop on Windows only binds forwarded ports on IPv4. `localhost` resolves to `::1` (IPv6) first; the TCP SYN has no listener, so Windows waits ~21 seconds before falling back to IPv4. That stall shows up per-request as huge TTFT even though the server is responding in milliseconds.

The default endpoint uses `127.0.0.1` for this reason. Leave it alone unless you're pointing at a remote server with a proper hostname.

## Personas

A persona is a markdown file whose contents become the system prompt. They live in `Minus.Agent/personas/` and are copied to the build output.

```
Minus.Agent/personas/
├── default.md
└── your-persona.md
```

Select one with `--persona your-persona` (no extension). The file's raw contents become the `system` message at the top of `_history`.

**Adding a persona:** drop a new `.md` file into `Minus.Agent/personas/`. It's picked up automatically because `Minus.Agent.csproj` copies `personas/*.md` to the output directory on build.

## Tools

Tools are callable functions the model can invoke. Two ship by default:

- `read_file(path: string)` — read a file's contents as a string
- `list_directory(path: string)` — list files and subdirectories (directories are suffixed with `/`)

### How the agent advertises them

On startup, `Agent.cs` builds a `List<ToolDefinition>` from the registered tools and forwards it to the server on every request as the OpenAI `tools` array. `tool_choice` is set to `"auto"`, letting the model decide when to call one.

### The dispatch loop

When a response comes back with `tool_calls`:

1. Each call is executed in order (`Agent.ExecuteToolAsync`).
2. The result (or error) is wrapped in a `tool` role message and appended to history.
3. Another `llm_request` is made with the updated history.

This repeats up to `MaxIterations = 25`. If the model keeps calling tools past that, the agent returns `[agent stopped: max iterations reached]`.

Every tool call generates one `tool_call` event (with the arguments as stringified JSON) and one `tool_result` event (with the returned content or error, plus `duration_ms`). Both share a `call_id` so the inspector can pair them.

### Adding a tool

1. Create a class in `Minus.Agent/Tools/` implementing `ITool` (see `Minus.Agent/Tool.cs`):

   ```csharp
   public interface ITool
   {
       string Name { get; }
       string Description { get; }
       JsonElement ParametersSchema { get; }  // JSON Schema for the args object
       Task<string> ExecuteAsync(JsonElement args, CancellationToken ct);
   }
   ```

2. Register it in `Minus.Agent/Program.cs`:

   ```csharp
   ITool[] tools = [new ReadFileTool(), new ListDirectoryTool(), new MyTool()];
   ```

The `ParametersSchema` is a JSON Schema object sent verbatim to the model — what the model sees as the function signature. Keep it strict: `required` fields, enums where applicable, no free-form objects unless necessary. The less the model has to guess, the fewer retries you'll see.

### Tool errors

If `ExecuteAsync` throws or the tool name is unknown, the agent logs a `tool_result` with `Error = "tool error: <message>"` and feeds that string back to the model as the tool's output. The conversation doesn't crash — the model sees the error and can adapt.

## Transcripts

Every session writes to `sessions/<timestamp>.jsonl`. One JSON object per line, one line per event. The schema is documented in [transcript-schema.md](transcript-schema.md).

The transcript is flushed after every event (`AutoFlush = true`), so `minus-view --follow` sees events within its 500ms poll interval.

## Reasoning models

If the server is configured with `--reasoning-format deepseek`, responses come back with a populated `reasoning_content` field alongside `content`. Minus:

- **Captures it in the transcript** — the `llm_response` event carries the full `ChatResponse` body including `reasoning_content`.
- **Strips it before replay** — `LlamaClient.ChatAsync` returns the assistant message with `ReasoningContent = null`, so subsequent turns don't re-feed reasoning into history. Reasoning models are trained to re-generate reasoning each turn, not to reuse prior reasoning verbatim.

If your model is non-reasoning, `reasoning_content` is simply absent and nothing changes.

## Session files and working directory

The `sessions/` directory is resolved relative to the process working directory — which is wherever you run `dotnet run` from, not where the binary lives. Running the agent and inspector from the same directory keeps them in sync.

If you run the agent elsewhere (e.g. using Minus as a coding assistant in some other repo), its sessions will land in that repo's `sessions/`. Start the inspector from the same location and point it at that directory if needed:

```powershell
dotnet run --project C:\Users\sylva\src\Minus\Minus.Inspector -- C:\path\to\other-project\sessions
```
