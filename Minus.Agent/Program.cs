using System.Reflection;
using Minus;
using Minus.Core;
using Minus.Tools;
using Spectre.Console;
using Events = Minus.Core.Events;

// 127.0.0.1 (not `localhost`) avoids a ~21s IPv6-fallback stall on Windows
// when the llama.cpp container's port forwarder only binds IPv4.
var endpoint = Environment.GetEnvironmentVariable("MINUS_ENDPOINT") ?? "http://127.0.0.1:8080";
var model = Environment.GetEnvironmentVariable("MINUS_MODEL") ?? "local";
var personaName = "default";

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--persona": personaName = args[++i]; break;
        case "--endpoint": endpoint = args[++i]; break;
        case "--model": model = args[++i]; break;
    }
}

var personaPath = Path.Combine(AppContext.BaseDirectory, "personas", $"{personaName}.md");
if (!File.Exists(personaPath))
{
    Console.Error.WriteLine($"persona not found: {personaPath}");
    return 1;
}
var systemPrompt = await File.ReadAllTextAsync(personaPath);

var sessionId = Guid.CreateVersion7().ToString("N");
var filenameStamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss");
var transcriptPath = Path.Combine("sessions", $"{filenameStamp}.jsonl");
using var transcript = new TranscriptWriter(transcriptPath);
var ui = new ConsoleUi();
var sink = new AggregateEventSink(transcript, ui);

var cwd = Directory.GetCurrentDirectory();
var minusVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();

ITool[] tools = [new ReadFileTool(), new ListDirectoryTool()];
var client = new LlamaClient(endpoint, model, sink);
var agent = new Agent(systemPrompt, client, sink, tools);

sink.Log(new Events.Meta(
    SessionId: sessionId,
    Cwd: cwd,
    Model: model,
    Endpoint: endpoint,
    MinusVersion: minusVersion,
    Persona: personaName,
    Tools: agent.ToolNames.ToList()
));

AnsiConsole.MarkupLine($"[grey]transcript:[/] [dim]{Markup.Escape(transcriptPath)}[/]");
AnsiConsole.MarkupLine("[grey]type 'exit' or Ctrl+D to quit[/]");

while (true)
{
    AnsiConsole.Markup("\n[cyan]>[/] ");
    var input = Console.ReadLine();
    if (input is null) break;
    if (input.Trim() == "exit") break;
    if (string.IsNullOrWhiteSpace(input)) continue;

    try
    {
        await agent.RunAsync(input, CancellationToken.None);
    }
    catch (Exception ex)
    {
        sink.Log(new Events.Error(
            Phase: "agent",
            HttpStatus: null,
            Code: null,
            Message: ex.Message,
            Retryable: false
        ));
    }
}

sink.Log(new Events.End());
return 0;
