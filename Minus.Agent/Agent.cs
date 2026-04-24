using System.Text.Json;
using Minus.Core;

namespace Minus;

public sealed class Agent
{
    public string SystemPrompt { get; }
    public int MaxIterations { get; init; } = 25;

    private readonly LlamaClient _client;
    private readonly TranscriptWriter _transcript;
    private readonly Dictionary<string, ITool> _tools;
    private readonly List<ToolDefinition> _toolDefs;
    private readonly List<Message> _history = new();

    public Agent(string systemPrompt, LlamaClient client, TranscriptWriter transcript, IEnumerable<ITool> tools)
    {
        SystemPrompt = systemPrompt;
        _client = client;
        _transcript = transcript;
        _tools = tools.ToDictionary(t => t.Name);
        _toolDefs = _tools.Values
            .Select(t => new ToolDefinition(
                "function",
                new FunctionDefinition(t.Name, t.Description, t.ParametersSchema)))
            .ToList();
        _history.Add(new Message("system", SystemPrompt));
    }

    public async Task<string> RunAsync(string userInput, CancellationToken ct)
    {
        _transcript.Log("user_input", new { content = userInput });
        _history.Add(new Message("user", userInput));

        for (int i = 0; i < MaxIterations; i++)
        {
            var assistant = await _client.ChatAsync(_history, _toolDefs, ct);
            _history.Add(assistant);

            if (assistant.ToolCalls is null || assistant.ToolCalls.Count == 0)
                return assistant.Content ?? "";

            foreach (var call in assistant.ToolCalls)
            {
                var result = await ExecuteToolAsync(call, ct);
                _history.Add(new Message(
                    "tool",
                    result,
                    ToolCallId: call.Id,
                    Name: call.Function.Name));
            }
        }

        return "[agent stopped: max iterations reached]";
    }

    private async Task<string> ExecuteToolAsync(ToolCall call, CancellationToken ct)
    {
        _transcript.Log("tool_call", new
        {
            id = call.Id,
            name = call.Function.Name,
            arguments = call.Function.Arguments,
        });

        if (!_tools.TryGetValue(call.Function.Name, out var tool))
        {
            var err = $"unknown tool: {call.Function.Name}";
            _transcript.Log("tool_result", new { id = call.Id, error = err });
            return err;
        }

        try
        {
            var args = string.IsNullOrWhiteSpace(call.Function.Arguments)
                ? JsonDocument.Parse("{}").RootElement
                : JsonDocument.Parse(call.Function.Arguments).RootElement;
            var result = await tool.ExecuteAsync(args, ct);
            _transcript.Log("tool_result", new { id = call.Id, content = result });
            return result;
        }
        catch (Exception ex)
        {
            var err = $"tool error: {ex.Message}";
            _transcript.Log("tool_result", new { id = call.Id, error = err });
            return err;
        }
    }
}
