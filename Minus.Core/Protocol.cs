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

public record ChatResponse(List<Choice> Choices);

public record Choice(Message Message, string? FinishReason);
