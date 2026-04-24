using System.Diagnostics;
using System.Reflection;
using Minus;
using Minus.Core;
using Minus.Tools;
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

var cwd = Directory.GetCurrentDirectory();
var (gitCommit, gitBranch) = ReadGitMeta(cwd);
var minusVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();

ITool[] tools = [new ReadFileTool(), new ListDirectoryTool()];
var client = new LlamaClient(endpoint, model, transcript);
var agent = new Agent(systemPrompt, client, transcript, tools);

transcript.Log(new Events.Meta(
    SessionId: sessionId,
    Cwd: cwd,
    GitCommit: gitCommit,
    GitBranch: gitBranch,
    Model: model,
    Endpoint: endpoint,
    MinusVersion: minusVersion,
    Persona: personaName,
    Tools: agent.ToolNames.ToList()
));

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
        transcript.Log(new Events.Error(
            Phase: "agent",
            HttpStatus: null,
            Code: null,
            Message: ex.Message,
            Retryable: false
        ));
        Console.WriteLine($"\n[error] {ex.Message}\n");
    }
}

transcript.Log(new Events.End());
return 0;

static (string? commit, string? branch) ReadGitMeta(string cwd)
{
    return (Run("rev-parse HEAD"), Run("rev-parse --abbrev-ref HEAD"));

    string? Run(string gitArgs)
    {
        try
        {
            var psi = new ProcessStartInfo("git", gitArgs)
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(1000);
            return p.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
