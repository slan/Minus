using System.Reflection;
using System.Threading.Channels;
using Minus;
using Minus.Commands;
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

var asyncConsole = new AsyncConsole();
var ui = new ConsoleUi(asyncConsole);
var sink = new AggregateEventSink(transcript, ui);

var cwd = Directory.GetCurrentDirectory();
var minusVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();

ITool[] tools = [new ReadFileTool(), new ListDirectoryTool()];
var client = new LlamaClient(endpoint, model, sink);
var agent = new Agent(systemPrompt, client, sink, tools);

ISlashCommand[] slashCommands = [new QuitCommand(), new HelpCommand()];
var dispatcher = new SlashCommandDispatcher(slashCommands);
var commandContext = new SlashCommandContext(dispatcher.Commands);

sink.Log(new Events.Meta(
    SessionId: sessionId,
    Cwd: cwd,
    Model: model,
    Endpoint: endpoint,
    MinusVersion: minusVersion,
    Persona: personaName,
    Tools: agent.ToolNames.ToList()));

AnsiConsole.MarkupLine($"[grey]transcript:[/] [dim]{Markup.Escape(transcriptPath)}[/]");
AnsiConsole.MarkupLine("[grey]/help for commands · Esc clears, Esc-Esc / Ctrl+C interrupts · /quit to exit[/]");

// ────────── orchestration state ──────────

// Queue of accepted user submissions. Enter pushes, agent task drains.
var submissions = Channel.CreateUnbounded<string>();

// Shared handle on the current agent turn's CancellationTokenSource.
// Escape-Escape and Ctrl+C both cancel it; the agent task replaces it
// between turns. Access is guarded by its own lock object because the
// key handler on the main thread and the agent task race on it.
var agentCtsLock = new object();
CancellationTokenSource? currentAgentCts = null;

// App-level shutdown signal. Fires on /quit and on Ctrl+C while idle.
var appCts = new CancellationTokenSource();

// ────────── Ctrl+C handler ──────────

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // don't terminate the process; we handle it
    lock (agentCtsLock)
    {
        if (currentAgentCts is { } cts)
        {
            cts.Cancel(); // mid-turn: interrupt like double-Escape
        }
        else
        {
            // idle: exit cleanly
            commandContext.RequestExit();
            appCts.Cancel();
            submissions.Writer.TryComplete();
        }
    }
};

// ────────── agent processing task ──────────

var processTask = Task.Run(async () =>
{
    try
    {
        await foreach (var input in submissions.Reader.ReadAllAsync(appCts.Token))
        {
            if (commandContext.ExitRequested) break;

            // Echo the user's submission into the scrollback as a styled
            // block, above whatever the agent is about to print.
            ui.ShowSubmitted(input);

            if (await dispatcher.TryDispatchAsync(input, commandContext, appCts.Token))
            {
                if (commandContext.ExitRequested)
                {
                    appCts.Cancel();
                    submissions.Writer.TryComplete();
                    break;
                }
                continue;
            }

            var turnCts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token);
            lock (agentCtsLock) currentAgentCts = turnCts;
            asyncConsole.SetBusy(true);
            try
            {
                await agent.RunAsync(input, turnCts.Token);
            }
            catch (OperationCanceledException)
            {
                ui.ShowInterrupt();
            }
            catch (Exception ex)
            {
                sink.Log(new Events.Error(
                    Phase: "agent",
                    HttpStatus: null,
                    Code: null,
                    Message: ex.Message,
                    Retryable: false));
            }
            finally
            {
                asyncConsole.SetBusy(false);
                lock (agentCtsLock)
                {
                    currentAgentCts = null;
                }
                turnCts.Dispose();
            }
        }
    }
    catch (OperationCanceledException)
    {
        // appCts fired; shutdown path
    }
});

// ────────── input loop (main thread) ──────────

DateTime lastEscape = DateTime.MinValue;
var doubleEscapeWindow = TimeSpan.FromMilliseconds(500);

// Kick off with the prompt visible.
asyncConsole.WriteAboveInput(() => { /* no-op, forces a prompt draw */ });

while (!appCts.IsCancellationRequested && !commandContext.ExitRequested)
{
    if (!Console.KeyAvailable)
    {
        try { await Task.Delay(15, appCts.Token); }
        catch (OperationCanceledException) { break; }
        continue;
    }

    var key = Console.ReadKey(intercept: true);

    switch (key.Key)
    {
        case ConsoleKey.Enter:
        {
            var text = asyncConsole.TakeBuffer();
            if (!string.IsNullOrWhiteSpace(text))
                submissions.Writer.TryWrite(text);
            break;
        }

        case ConsoleKey.Backspace:
            asyncConsole.Backspace();
            break;

        case ConsoleKey.Escape:
        {
            var now = DateTime.UtcNow;
            if (now - lastEscape < doubleEscapeWindow)
            {
                // Double-Escape: interrupt current turn if one's running.
                lock (agentCtsLock) currentAgentCts?.Cancel();
                lastEscape = DateTime.MinValue;
            }
            else
            {
                // Single Escape: clear the current input buffer.
                asyncConsole.ClearBuffer();
                lastEscape = now;
            }
            break;
        }

        default:
            // Plain printable character goes into the buffer.
            if (!char.IsControl(key.KeyChar))
                asyncConsole.AppendChar(key.KeyChar);
            break;
    }
}

submissions.Writer.TryComplete();
try { await processTask; }
catch { /* swallow; we're shutting down */ }

sink.Log(new Events.End());
AnsiConsole.WriteLine();
return 0;
