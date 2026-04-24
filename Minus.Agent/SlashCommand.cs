namespace Minus;

// Slash commands are user-level directives (input starting with '/').
// Unlike ITool, which the *model* invokes and whose output is fed back
// into the conversation, slash commands are invoked by the *user* and
// their effect is purely local (print help, change settings, exit).
//
// A command's Name is the identifier without the leading slash:
//     input "/quit"   -> command "quit", args ""
//     input "/help X" -> command "help", args "X"
public interface ISlashCommand
{
    string Name { get; }
    string Description { get; }
    Task ExecuteAsync(string args, SlashCommandContext ctx, CancellationToken ct);
}

public sealed class SlashCommandContext
{
    public SlashCommandContext(IReadOnlyDictionary<string, ISlashCommand> commands)
    {
        Commands = commands;
    }

    public IReadOnlyDictionary<string, ISlashCommand> Commands { get; }
    public bool ExitRequested { get; private set; }

    public void RequestExit() => ExitRequested = true;
}

public sealed class SlashCommandDispatcher
{
    private readonly IReadOnlyDictionary<string, ISlashCommand> _commands;

    public SlashCommandDispatcher(IEnumerable<ISlashCommand> commands)
    {
        _commands = commands.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, ISlashCommand> Commands => _commands;

    // Returns true if the input was dispatched as a slash command (whether
    // or not the command itself succeeded). Returns false if the input
    // wasn't a slash command and should flow to the agent instead.
    public async Task<bool> TryDispatchAsync(string input, SlashCommandContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(input) || input[0] != '/') return false;

        var rest = input[1..];
        var space = rest.IndexOf(' ');
        var name = space < 0 ? rest : rest[..space];
        var args = space < 0 ? "" : rest[(space + 1)..];

        if (!_commands.TryGetValue(name, out var cmd))
        {
            Spectre.Console.AnsiConsole.MarkupLine(
                $"[red]unknown command:[/] [grey]/{Spectre.Console.Markup.Escape(name)}[/]  (try /help)");
            return true;
        }

        await cmd.ExecuteAsync(args, ctx, ct);
        return true;
    }
}
