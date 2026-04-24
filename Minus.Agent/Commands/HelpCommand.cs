using Spectre.Console;

namespace Minus.Commands;

public sealed class HelpCommand : ISlashCommand
{
    public string Name => "help";
    public string Description => "list available slash commands";

    public Task ExecuteAsync(string args, SlashCommandContext ctx, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[grey]available commands:[/]");
        foreach (var cmd in ctx.Commands.Values.OrderBy(c => c.Name))
            AnsiConsole.MarkupLine($"  [cyan]/{Markup.Escape(cmd.Name)}[/]  [grey]{Markup.Escape(cmd.Description)}[/]");
        return Task.CompletedTask;
    }
}
