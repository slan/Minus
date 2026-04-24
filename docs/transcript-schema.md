# Transcript schema

Every Minus session is written as a JSONL file: one JSON object per line, one line per event, UTF-8, LF line endings. Files live in `sessions/<utc-timestamp>.jsonl` relative to the agent's working directory.

## Envelope

Every event shares the same base fields, inherited from `Minus.Core.Events.SessionEvent`:

| Field       | Type   | Always? | Notes |
|-------------|--------|---------|-------|
| `type`      | string | yes     | Discriminator (first field on every line). See event types below. |
| `id`        | string | yes     | UUIDv7, 32 hex chars, no dashes. Sortable by creation time. |
| `ts`        | string | yes     | ISO-8601 UTC timestamp with offset (e.g. `2026-04-24T05:57:10.8781027+00:00`). |
| `parent_id` | string | no      | Optional. Reserved for direct causal chains. Currently unused. |
| `turn_id`   | string | no      | Groups events belonging to one user turn. Absent on `session_meta`, `session_end`, and unattributed errors. |

Serialization uses snake_case naming and omits null fields. Polymorphic dispatch on `type` is via `System.Text.Json`'s `[JsonPolymorphic]`/`[JsonDerivedType]`.

## Event types

### `session_meta`

First line of every session. Fixed fields describing the run.

```json
{
  "type": "session_meta",
  "session_id": "019dbe10460c746d845a9422ddb98e97",
  "cwd": "C:\\Users\\sylva\\src\\Minus",
  "model": "local",
  "endpoint": "http://127.0.0.1:8080",
  "minus_version": "1.0.0.0",
  "persona": "default",
  "tools": ["read_file", "list_directory"],
  "id": "019dbe10465e7358b5182040278a2554",
  "ts": "2026-04-24T05:57:10.8781027+00:00"
}
```

| Field           | Type     | Notes |
|-----------------|----------|-------|
| `session_id`    | string   | UUIDv7 for the session as a whole (distinct from `id`, which is the event's own UUID). |
| `cwd`           | string   | Agent's working directory at startup. |
| `model`         | string   | Value passed as the `model` field to `/v1/chat/completions`. |
| `endpoint`      | string   | Base URL the agent is talking to. |
| `minus_version` | string? | Agent assembly version. |
| `persona`       | string   | Persona name selected at startup. |
| `tools`         | string[] | Names of registered tools. |

### `user_input`

Raw text the user typed at the `>` prompt.

```json
{ "type": "user_input", "content": "what time is it?", "turn_id": "019…", "id": "019…", "ts": "…" }
```

### `llm_request`

A request body sent to `/v1/chat/completions`. `body` is the full OpenAI-style request we POST.

```json
{
  "type": "llm_request",
  "body": {
    "model": "local",
    "messages": [ {"role": "system", "content": "…"}, … ],
    "tools": [ {"type": "function", "function": {"name": "…", "description": "…", "parameters": {…}}} ],
    "tool_choice": "auto"
  },
  "turn_id": "019…", "id": "019…", "ts": "…"
}
```

### `llm_response`

The server's response to an `llm_request`. Shares the same `turn_id`.

```json
{
  "type": "llm_response",
  "body": {
    "choices": [
      {
        "message": {
          "role": "assistant",
          "content": "4",
          "reasoning_content": "The user is asking a simple arithmetic question…",
          "tool_calls": null
        },
        "finish_reason": "stop"
      }
    ],
    "id": "chatcmpl-Tlq0dTg8O8fNMMovsJrqeafXjzwbEj7v",
    "usage": { "prompt_tokens": 225, "completion_tokens": 40, "total_tokens": 265 },
    "timings": {
      "prompt_ms": 48.416, "prompt_per_second": 1127.09,
      "predicted_ms": 366.545, "predicted_per_second": 109.12
    }
  },
  "duration_ms": 455,
  "turn_id": "019…", "id": "019…", "ts": "…"
}
```

Notable subfields inside `body`:

- `body.id` — the server's chat-completion id (e.g. `chatcmpl-…`). Useful for correlating with server logs.
- `body.usage` — prompt / completion / total tokens. Captured so you can compute context fill from the transcript alone.
- `body.timings` — server-side timing if the backend returns it (llama.cpp does). Split into prompt (prefill) and predicted (decode), each with ms and tok/s.
- `body.choices[].message.reasoning_content` — present when the server parses `<think>…</think>` into a separate field. Minus captures this but strips it before feeding history back.
- `duration_ms` — client-measured round-trip including HTTP overhead. Comparing this against `body.timings.prompt_ms + body.timings.predicted_ms` surfaces transport-layer issues (e.g. the 21s IPv6 stall we once chased).

### `tool_call`

The agent is about to execute a tool the model asked for.

```json
{
  "type": "tool_call",
  "call_id": "vWTlScwkPD8CRBmZxbasinG8AcZe6DS2",
  "name": "list_directory",
  "arguments": "{\"path\":\".\"}",
  "turn_id": "019…", "id": "019…", "ts": "…"
}
```

`arguments` is the raw string the model produced — we don't re-serialize. That means escaped JSON, potential whitespace, and any non-conforming output the model emitted. The inspector's rendered mode pretty-prints it for readability.

### `tool_result`

Pairs with a `tool_call` via `call_id`.

```json
{
  "type": "tool_result",
  "call_id": "vWTlScwkPD8CRBmZxbasinG8AcZe6DS2",
  "content": ".git/\n.gitignore\nAgent.cs\n…",
  "error": null,
  "duration_ms": 10,
  "turn_id": "019…", "id": "019…", "ts": "…"
}
```

| Field         | Type     | Notes |
|---------------|----------|-------|
| `call_id`     | string   | Same as the originating `tool_call.call_id`. |
| `content`     | string?  | Tool output. Null if the tool errored. |
| `error`       | string?  | Error message. Null on success. Exactly one of `content` or `error` is set. |
| `duration_ms` | number   | How long `ITool.ExecuteAsync` took. |

### `error`

Structured error event. Raised when an HTTP request fails, when the REPL loop catches an exception, or when parsing goes wrong.

```json
{
  "type": "error",
  "phase": "request",
  "http_status": 500,
  "code": null,
  "message": "Internal server error",
  "retryable": false,
  "turn_id": "019…", "id": "019…", "ts": "…"
}
```

| Field         | Type    | Notes |
|---------------|---------|-------|
| `phase`       | string  | One of `request`, `parse`, `tool`, `agent`, or other caller-defined values. |
| `http_status` | number? | Set when the error came from an HTTP response. |
| `code`        | string? | Reserved for server-provided error codes. |
| `message`     | string  | Human-readable message. |
| `retryable`   | bool    | Hint for retry logic. Currently always false. |

### `session_end`

Marks clean termination (user typed `exit`, Ctrl+D, etc.). Absent if the process crashed.

```json
{ "type": "session_end", "id": "019…", "ts": "…" }
```

## Linkage

Three fields chain events together:

- **`turn_id`** — groups all events generated by one user turn. A turn is: one `user_input`, zero or more pairs of `(llm_request, llm_response)` interleaved with zero or more pairs of `(tool_call, tool_result)`, ending with an `llm_response` that has no `tool_calls`.
- **`call_id`** — on `tool_call` / `tool_result`, pairs a dispatched tool with its outcome.
- **`id`** / **`parent_id`** — every event has its own `id`; `parent_id` is reserved for future direct-parent chains.

All IDs are UUIDv7 encoded as 32 hex chars with no dashes (`Guid.CreateVersion7().ToString("N")`). UUIDv7 means the string prefix sorts chronologically, which is useful for grep / eyeballing.

## Example: a full minimal session

```jsonl
{"type":"session_meta","session_id":"019dbe10d05b…","cwd":"…","model":"local","endpoint":"http://127.0.0.1:8080","minus_version":"1.0.0.0","persona":"default","tools":["read_file","list_directory"],"id":"019dbe10d0a8…","ts":"2026-04-24T05:57:46.2806293+00:00"}
{"type":"user_input","content":"what is 2+2?","id":"019dbe10d0ce…","ts":"2026-04-24T05:57:46.3180503+00:00","turn_id":"019dbe10d0cd…"}
{"type":"llm_request","body":{"model":"local","messages":[{"role":"system","content":"…"},{"role":"user","content":"what is 2+2?"}],"tools":[…],"tool_choice":"auto"},"id":"019dbe10d0cf…","ts":"2026-04-24T05:57:46.3196402+00:00","turn_id":"019dbe10d0cd…"}
{"type":"llm_response","body":{"choices":[{"message":{"role":"assistant","content":"4","reasoning_content":"…"},"finish_reason":"stop"}],"id":"chatcmpl-…","usage":{"prompt_tokens":225,"completion_tokens":40,"total_tokens":265},"timings":{"prompt_ms":48.416,"predicted_ms":366.545,"predicted_per_second":109.1}},"duration_ms":455,"id":"019dbe10d29d…","ts":"2026-04-24T05:57:46.7816632+00:00","turn_id":"019dbe10d0cd…"}
{"type":"session_end","id":"019dbe10d29f…","ts":"2026-04-24T05:57:46.7838309+00:00"}
```

## Compatibility

No schema versioning. The first major schema change happened during active development; pre-change files don't parse and produce a clean error in the inspector. Future compatibility-preserving additions go through optional fields (non-breaking). Future breaking changes will either add a `schema_version` to `session_meta` or — more likely — be rare and handled with a one-off migrator.
