using System.Runtime.CompilerServices;
using System.Text.Json;
using Minus.Core.Events;

namespace Minus.Core;

// Reads a JSONL transcript as a pull stream of typed SessionEvent values.
// Pairs with TranscriptWriter on the agent side: both round-trip the same
// polymorphic SessionEvent contract (see Events.cs).
//
// Use Read for sync callers (TUI on a UI thread, tests, scripts). Use
// ReadAsync only when you actually need async streaming — e.g. tailing a
// live-growing file. Sync-over-async on a UI thread with a
// SynchronizationContext deadlocks; the TUI already hit that.
public static class TranscriptReader
{
    public static IEnumerable<SessionEvent> Read(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            yield return Parse(line);
        }
    }

    public static async IAsyncEnumerable<SessionEvent> ReadAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) yield break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            yield return Parse(line);
        }
    }

    private static SessionEvent Parse(string line) =>
        JsonSerializer.Deserialize<SessionEvent>(line, Json.Options)
            ?? throw new FormatException($"failed to parse transcript line: {line}");
}
