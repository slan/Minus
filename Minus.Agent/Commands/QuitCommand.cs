namespace Minus.Commands;

public sealed class QuitCommand : ISlashCommand
{
    public string Name => "quit";
    public string Description => "exit the agent";

    public Task ExecuteAsync(string args, SlashCommandContext ctx, CancellationToken ct)
    {
        ctx.RequestExit();
        return Task.CompletedTask;
    }
}
