using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Minus.Core;
using Events = Minus.Core.Events;

namespace Minus;

public sealed class LlamaClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly TranscriptWriter _transcript;

    public LlamaClient(string endpoint, string model, TranscriptWriter transcript)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(endpoint),
            Timeout = TimeSpan.FromMinutes(5),
        };
        _model = model;
        _transcript = transcript;
    }

    public async Task<Message> ChatAsync(
        List<Message> messages,
        List<ToolDefinition>? tools,
        string turnId,
        CancellationToken ct)
    {
        var req = new ChatRequest(
            _model,
            messages,
            tools is { Count: > 0 } ? tools : null,
            tools is { Count: > 0 } ? "auto" : null);

        _transcript.Log(new Events.LlmRequest(req) { TurnId = turnId });

        var sw = Stopwatch.StartNew();
        var http = await _http.PostAsJsonAsync("/v1/chat/completions", req, Json.Options, ct);
        var body = await http.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!http.IsSuccessStatusCode)
        {
            _transcript.Log(new Events.Error(
                Phase: "request",
                HttpStatus: (int)http.StatusCode,
                Code: null,
                Message: body,
                Retryable: false
            ) { TurnId = turnId });
            throw new Exception($"llama.cpp returned {http.StatusCode}: {body}");
        }

        var resp = JsonSerializer.Deserialize<ChatResponse>(body, Json.Options)
            ?? throw new Exception("null response from llama.cpp");

        _transcript.Log(new Events.LlmResponse(resp, sw.ElapsedMilliseconds) { TurnId = turnId });

        // Reasoning content is logged above for visibility but MUST NOT be
        // echoed back in subsequent turns — reasoning models expect prior
        // turns' history to contain only the final answer.
        return resp.Choices[0].Message with { ReasoningContent = null };
    }
}
