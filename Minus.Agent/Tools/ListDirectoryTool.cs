using System.Text.Json;

namespace Minus.Tools;

public sealed class ListDirectoryTool : ITool
{
    public string Name => "list_directory";
    public string Description => "List files and subdirectories in a given directory. Directories are suffixed with '/'.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "Directory path to list." }
      },
      "required": ["path"]
    }
    """).RootElement;

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetProperty("path").GetString()
            ?? throw new ArgumentException("path is required");

        var entries = Directory.EnumerateFileSystemEntries(path)
            .Select(e => Directory.Exists(e)
                ? System.IO.Path.GetFileName(e) + "/"
                : System.IO.Path.GetFileName(e))
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult(entries.Count == 0 ? "(empty)" : string.Join("\n", entries));
    }
}
