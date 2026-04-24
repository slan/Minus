using System.Diagnostics;
using System.Text.Json;
using Minus.Core;
using Events = Minus.Core.Events;

namespace Minus;

public sealed class Agent
{
    public string SystemPrompt { get; }
    public int MaxIterations { get; init; } = 25;

    private readonly LlamaClient _client;
    private readonly ISessionEventSink _transcript;
    private readonly Dictionary<string, ITool> _tools;
    private readonly List<ToolDefinition> _toolDefs;
    private readonly List<Message> _history = new();

    public Agent(string systemPrompt, LlamaClient client, ISessionEventSink transcript, IEnumerable<ITool> tools)
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

    public IReadOnlyCollection<string> ToolNames => _tools.Keys;

    public async Task<string> RunAsync(string userInput, CancellationToken ct)
    {
        var turnId = Guid.CreateVersion7().ToString("N");

        _transcript.Log(new Events.UserInput(userInput) { TurnId = turnId });
        _history.Add(new Message("user", userInput));

        for (int i = 0; i < MaxIterations; i++)
        {
            var assistant = await _client.ChatAsync(_history, _toolDefs, turnId, ct);
            _history.Add(assistant);

            if (assistant.ToolCalls is null || assistant.ToolCalls.Count == 0)
                return assistant.Content ?? "";

            foreach (var call in assistant.ToolCalls)
            {
                var result = await ExecuteToolAsync(call, turnId, ct);
                _history.Add(new Message(
                    "tool",
                    result,
                    ToolCallId: call.Id,
                    Name: call.Function.Name));
            }
        }

        return "[agent stopped: max iterations reached]";
    }

    private async Task<string> ExecuteToolAsync(ToolCall call, string turnId, CancellationToken ct)
    {
        _transcript.Log(new Events.ToolCall(call.Id, call.Function.Name, call.Function.Arguments)
        {
            TurnId = turnId,
        });

        var sw = Stopwatch.StartNew();

        if (!_tools.TryGetValue(call.Function.Name, out var tool))
        {
            var err = $"unknown tool: {call.Function.Name}";
            sw.Stop();
            _transcript.Log(new Events.ToolResult(call.Id, null, err, sw.ElapsedMilliseconds)
            {
                TurnId = turnId,
            });
            return err;
        }

        try
        {
            var argsJson = string.IsNullOrWhiteSpace(call.Function.Arguments)
                ? JsonDocument.Parse("{}").RootElement
                : JsonDocument.Parse(call.Function.Arguments).RootElement;
            var result = await tool.ExecuteAsync(argsJson, ct);
            sw.Stop();
            _transcript.Log(new Events.ToolResult(call.Id, result, null, sw.ElapsedMilliseconds)
            {
                TurnId = turnId,
            });
            return result;
        }
        catch (Exception ex)
        {
            var err = $"tool error: {ex.Message}";
            sw.Stop();
            _transcript.Log(new Events.ToolResult(call.Id, null, err, sw.ElapsedMilliseconds)
            {
                TurnId = turnId,
            });
            return err;
        }
    }
}
