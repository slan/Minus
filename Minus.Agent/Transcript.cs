using System.Text.Json;

namespace Minus;

public sealed class Transcript : IDisposable
{
    private readonly StreamWriter _writer;
    public string Path { get; }

    public Transcript(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    public void Log(string type, object? data = null)
    {
        var entry = new
        {
            ts = DateTimeOffset.UtcNow.ToString("o"),
            type,
            data,
        };
        _writer.WriteLine(JsonSerializer.Serialize(entry, Json.Options));
    }

    public void Dispose() => _writer.Dispose();
}
