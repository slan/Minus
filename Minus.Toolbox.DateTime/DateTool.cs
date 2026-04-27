using System.Text.Json;
using Minus.Core;

namespace Minus.Toolbox.DateTimeTools;

public sealed class DateTool : ITool
{
    public string Name => "date";

    public string Description =>
        "Get the current date and time. Returns both UTC and a local time. " +
        "Pass an IANA timezone (e.g. 'Europe/Paris', 'America/New_York') to get " +
        "local time in that zone; otherwise the system local timezone is used.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
    {
      "type": "object",
      "properties": {
        "timezone": {
          "type": "string",
          "description": "Optional IANA timezone name. Defaults to the system local timezone."
        }
      }
    }
    """).RootElement;

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        string? tzName = null;
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("timezone", out var tzProp) &&
            tzProp.ValueKind == JsonValueKind.String)
        {
            tzName = tzProp.GetString();
        }

        TimeZoneInfo tz;
        if (string.IsNullOrWhiteSpace(tzName))
        {
            tz = TimeZoneInfo.Local;
        }
        else
        {
            try
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(tzName);
            }
            catch (TimeZoneNotFoundException)
            {
                return Task.FromResult($"error: unknown timezone '{tzName}'");
            }
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, tz);

        var result = new
        {
            utc = nowUtc.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            local = nowLocal.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            timezone = tz.Id,
        };

        return Task.FromResult(JsonSerializer.Serialize(result, Json.Options));
    }
}
