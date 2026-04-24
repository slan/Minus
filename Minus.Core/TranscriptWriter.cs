using System.Text.Json;
using Minus.Core.Events;

namespace Minus.Core;

public sealed class TranscriptWriter : IDisposable
{
    private readonly StreamWriter _writer;
    public string Path { get; }

    public TranscriptWriter(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    public void Log(SessionEvent ev)
    {
        // Serialize as the abstract base so polymorphic type-discriminator
        // emission ("type": "...") kicks in.
        _writer.WriteLine(JsonSerializer.Serialize<SessionEvent>(ev, Json.Options));
    }

    public void Dispose() => _writer.Dispose();
}
