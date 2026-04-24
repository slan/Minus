using System.Text.Json;

namespace Minus.Core;

// OpenAI-compatible chat completion shapes (llama.cpp server speaks this).
// Serialized via Json.Options (snake_case, nulls omitted).

public record Message(
    string Role,
    string? Content = null,
    List<ToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null,
    string? ReasoningContent = null
);

public record ToolCall(string Id, string Type, FunctionCall Function);

public record FunctionCall(string Name, string Arguments);

public record ChatRequest(
    string Model,
    List<Message> Messages,
    List<ToolDefinition>? Tools = null,
    string? ToolChoice = null
);

public record ToolDefinition(string Type, FunctionDefinition Function);

public record FunctionDefinition(string Name, string Description, JsonElement Parameters);

public record ChatResponse(
    List<Choice> Choices,
    string? Id = null,
    Usage? Usage = null,
    ServerTimings? Timings = null
);

public record Choice(Message Message, string? FinishReason);

public record Usage(int PromptTokens, int CompletionTokens, int TotalTokens);

public record ServerTimings(
    double? PromptMs = null,
    double? PromptPerSecond = null,
    double? PredictedMs = null,
    double? PredictedPerSecond = null
);
