using Minus;
using Minus.Tools;

var endpoint = Environment.GetEnvironmentVariable("MINUS_ENDPOINT") ?? "http://localhost:8080";
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

var sessionId = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss");
var transcriptPath = Path.Combine("sessions", $"{sessionId}.jsonl");
using var transcript = new Transcript(transcriptPath);
transcript.Log("session_start", new { persona = personaName, endpoint, model });

var client = new LlamaClient(endpoint, model, transcript);
ITool[] tools = [new ReadFileTool(), new ListDirectoryTool()];
var agent = new Agent(systemPrompt, client, transcript, tools);

Console.WriteLine($"Minus — persona: {personaName} — endpoint: {endpoint} — transcript: {transcriptPath}");
Console.WriteLine("Type your message. 'exit' or Ctrl+D to quit.\n");

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (input is null) break;
    if (input.Trim() == "exit") break;
    if (string.IsNullOrWhiteSpace(input)) continue;

    try
    {
        var reply = await agent.RunAsync(input, CancellationToken.None);
        Console.WriteLine($"\n{reply}\n");
    }
    catch (Exception ex)
    {
        transcript.Log("error", new { message = ex.Message });
        Console.WriteLine($"\n[error] {ex.Message}\n");
    }
}

transcript.Log("session_end");
return 0;
