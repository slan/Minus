# Minus

A minimal C# CLI agent that talks to a [llama.cpp](https://github.com/ggerganov/llama.cpp) server (or any OpenAI-compatible `/v1/chat/completions` endpoint) with tool-calling support.

Minus keeps the loop small and readable: one chat history, one tool registry, one persona prompt, and a JSONL transcript of everything that happens.

## Requirements

- .NET 10 SDK
- A running llama.cpp server (or any OpenAI-compatible endpoint) that supports function/tool calling

## Build and run

```sh
dotnet run
```

By default Minus connects to `http://localhost:8080` and uses the `default` persona.

Override via flags or environment variables:

```sh
dotnet run -- --endpoint http://localhost:8080 --model local --persona default
```

| Flag         | Env var           | Default                  |
| ------------ | ----------------- | ------------------------ |
| `--endpoint` | `MINUS_ENDPOINT`  | `http://localhost:8080`  |
| `--model`    | `MINUS_MODEL`     | `local`                  |
| `--persona`  | —                 | `default`                |

Type messages at the `>` prompt. `exit` or Ctrl+D quits.

## Personas

A persona is a Markdown file in `personas/` whose contents become the system prompt. To add one, drop `personas/my-persona.md` into the directory and pass `--persona my-persona`. Persona files are copied to the build output automatically.

## Tools

The agent is wired with two tools out of the box:

- `read_file` — read a file's contents as a string
- `list_directory` — list files and subdirectories (directories get a trailing `/`)

Adding a tool means implementing `ITool` (see `Tool.cs`) and registering it in `Program.cs`:

```csharp
ITool[] tools = [new ReadFileTool(), new ListDirectoryTool(), new MyTool()];
```

Each tool declares a name, a description, and a JSON Schema for its parameters. These are forwarded to the model as function definitions.

## Transcripts

Every session writes a JSONL transcript to `sessions/<timestamp>.jsonl` containing session metadata, user input, LLM requests and responses, tool calls, tool results, and errors. Useful for debugging model behavior or replaying a conversation.

## Project layout

```
Program.cs        — CLI entry point, arg parsing, REPL loop
Agent.cs          — chat loop, tool dispatch, max-iteration guard
LlamaClient.cs    — HTTP client for /v1/chat/completions
Protocol.cs       — OpenAI-compatible chat completion records
Tool.cs           — ITool interface
Tools/            — built-in tool implementations
Transcript.cs     — JSONL session logger
Json.cs           — shared JsonSerializerOptions
personas/         — system prompts, one per file
```
