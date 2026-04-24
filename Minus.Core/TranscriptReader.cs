using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Minus.Core;

// Reads a JSONL transcript as a pull stream of LoggedEvent values. Pairs
// with TranscriptWriter on the agent side. The payload is kept as a raw
// JsonElement for now — a typed event hierarchy comes in a later step.
public sealed record LoggedEvent(DateTimeOffset Ts, string Type, JsonElement? Data);

public static class TranscriptReader
{
    public static async IAsyncEnumerable<LoggedEvent> ReadAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            yield return Parse(line);
        }
    }

    private static LoggedEvent Parse(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var ts = root.GetProperty("ts").GetDateTimeOffset();
        var type = root.GetProperty("type").GetString()
            ?? throw new FormatException("transcript entry missing 'type'");
        JsonElement? data = root.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null
            ? d.Clone()
            : null;
        return new LoggedEvent(ts, type, data);
    }
}
