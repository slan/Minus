using System.Runtime.CompilerServices;
using System.Text.Json;
using Minus.Core.Events;

namespace Minus.Core;

// Reads a JSONL transcript as a pull stream of typed SessionEvent values.
// Pairs with TranscriptWriter on the agent side: both round-trip the same
// polymorphic SessionEvent contract (see Events.cs).
public static class TranscriptReader
{
    public static async IAsyncEnumerable<SessionEvent> ReadAsync(
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

            var ev = JsonSerializer.Deserialize<SessionEvent>(line, Json.Options)
                ?? throw new FormatException($"failed to parse transcript line: {line}");
            yield return ev;
        }
    }
}
