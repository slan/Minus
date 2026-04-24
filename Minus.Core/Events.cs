using System.Text.Json.Serialization;

namespace Minus.Core.Events;

// Typed session-event hierarchy. Serialized as one JSON object per line
// via TranscriptWriter, parsed back via TranscriptReader. The "type"
// discriminator is emitted first by System.Text.Json's polymorphism.
//
// Linkage fields on SessionEvent:
//   Id        — unique per-event identifier (UUIDv7)
//   Ts        — event timestamp (UTC)
//   ParentId  — optional; chains an event to a causing event
//   TurnId    — groups events belonging to one user turn
//                 (user_input → request → response → tool_call → tool_result → ...)

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Meta), "session_meta")]
[JsonDerivedType(typeof(UserInput), "user_input")]
[JsonDerivedType(typeof(LlmRequest), "llm_request")]
[JsonDerivedType(typeof(LlmResponse), "llm_response")]
[JsonDerivedType(typeof(ToolCall), "tool_call")]
[JsonDerivedType(typeof(ToolResult), "tool_result")]
[JsonDerivedType(typeof(Error), "error")]
[JsonDerivedType(typeof(End), "session_end")]
public abstract record SessionEvent
{
    public string Id { get; init; } = Guid.CreateVersion7().ToString("N");
    public DateTimeOffset Ts { get; init; } = DateTimeOffset.UtcNow;
    public string? ParentId { get; init; }
    public string? TurnId { get; init; }
}

public sealed record Meta(
    string SessionId,
    string Cwd,
    string Model,
    string Endpoint,
    string? MinusVersion,
    string Persona,
    List<string> Tools
) : SessionEvent;

public sealed record UserInput(string Content) : SessionEvent;

public sealed record LlmRequest(ChatRequest Body) : SessionEvent;

public sealed record LlmResponse(ChatResponse Body, long DurationMs) : SessionEvent;

public sealed record ToolCall(string CallId, string Name, string Arguments) : SessionEvent;

public sealed record ToolResult(
    string CallId,
    string? Content,
    string? Error,
    long DurationMs
) : SessionEvent;

public sealed record Error(
    string Phase,        // "request" | "parse" | "tool" | "agent" | ...
    int? HttpStatus,
    string? Code,
    string Message,
    bool Retryable
) : SessionEvent;

public sealed record End() : SessionEvent;
