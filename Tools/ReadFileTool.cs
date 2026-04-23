using System.Text.Json;

namespace Minus.Tools;

public sealed class ReadFileTool : ITool
{
    public string Name => "read_file";
    public string Description => "Read the contents of a file and return it as a string.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "Path to the file (absolute or relative to the working directory)." }
      },
      "required": ["path"]
    }
    """).RootElement;

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetProperty("path").GetString()
            ?? throw new ArgumentException("path is required");
        return await File.ReadAllTextAsync(path, ct);
    }
}
