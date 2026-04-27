using System.Text.Json;

namespace Minus.Core;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement ParametersSchema { get; }
    Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct);
}
