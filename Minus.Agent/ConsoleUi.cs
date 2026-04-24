using Minus.Core;
using Spectre.Console;
using Events = Minus.Core.Events;

namespace Minus;

// Renders session events to the console with Spectre.Console styling.
// Acts as a sink alongside TranscriptWriter — both observe the same event
// stream. The UI skips events that would duplicate the live terminal echo
// (UserInput) or would just be noise (LlmRequest, SessionEnd).
public sealed class ConsoleUi : ISessionEventSink
{
    private readonly AsyncConsole _console;

    public ConsoleUi(AsyncConsole console)
    {
        _console = console;
    }

    public void Log(Events.SessionEvent ev)
    {
        _console.WriteAboveInput(() =>
        {
            switch (ev)
            {
                case Events.Meta m:        RenderMeta(m); break;
                case Events.LlmResponse r: RenderAssistant(r); break;
                case Events.ToolCall c:    RenderToolCall(c); break;
                case Events.ToolResult t:  RenderToolResult(t); break;
                case Events.Error e:       RenderError(e); break;
                // UserInput: we render it ourselves at submit time so it
                // shows up as a styled block above the content (below).
                // LlmRequest: internal plumbing, not worth surfacing.
                // End: handled by the REPL loop.
            }
        });
    }

    // Call this from the input loop when a user submission is accepted,
    // so the submitted text renders as a styled block in the scrollback.
    // Must go through the same lock the sink uses.
    public void ShowSubmitted(string content)
    {
        _console.WriteAboveInput(() =>
        {
            AnsiConsole.MarkupLine($"[cyan]> {Markup.Escape(content)}[/]");
        });
    }

    // Call this when a turn is interrupted by Escape/Ctrl+C.
    public void ShowInterrupt()
    {
        _console.WriteAboveInput(() =>
        {
            AnsiConsole.MarkupLine("[yellow]⚠ interrupted[/]");
        });
    }

    private static void RenderMeta(Events.Meta m)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(
            new Rule($"[cyan]minus[/] [grey]· {Markup.Escape(m.Persona)} · {Markup.Escape(m.Model)}[/]")
                .LeftJustified()
                .RuleStyle("grey"));
        AnsiConsole.WriteLine();
    }

    private static void RenderAssistant(Events.LlmResponse r)
    {
        var choice = r.Body.Choices.FirstOrDefault();
        if (choice is null) return;
        var msg = choice.Message;

        AnsiConsole.WriteLine();

        if (!string.IsNullOrEmpty(msg.ReasoningContent))
            RenderReasoningBlock(msg.ReasoningContent);

        if (!string.IsNullOrEmpty(msg.Content))
            AnsiConsole.WriteLine(msg.Content);

        // If there's no visible content but tool calls are coming, say so;
        // individual ToolCall events will render below.
        if (string.IsNullOrEmpty(msg.Content) && msg.ToolCalls is { Count: > 0 })
        {
            var names = string.Join(", ", msg.ToolCalls.Select(tc => tc.Function.Name));
            AnsiConsole.MarkupLine($"[grey dim]calling: {Markup.Escape(names)}[/]");
        }

        var tokens = r.Body.Usage?.TotalTokens ?? 0;
        AnsiConsole.MarkupLine($"[grey dim]{r.DurationMs}ms · {tokens} tok[/]");
    }

    private static void RenderReasoningBlock(string reasoning)
    {
        foreach (var line in reasoning.ReplaceLineEndings("\n").Split('\n'))
            AnsiConsole.MarkupLine($"[grey italic]│ {Markup.Escape(line)}[/]");
        AnsiConsole.WriteLine();
    }

    private static void RenderToolCall(Events.ToolCall c)
    {
        AnsiConsole.MarkupLine(
            $"[yellow]⏵[/] [yellow]{Markup.Escape(c.Name)}[/] [grey]{Markup.Escape(c.Arguments)}[/]");
    }

    private static void RenderToolResult(Events.ToolResult t)
    {
        if (!string.IsNullOrEmpty(t.Error))
            AnsiConsole.MarkupLine($"[red]⏹ {Markup.Escape(t.Error)}[/] [grey dim]({t.DurationMs}ms)[/]");
        else
            AnsiConsole.MarkupLine($"[green]⏹[/] [grey dim]({t.DurationMs}ms)[/]");
    }

    private static void RenderError(Events.Error e)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[red bold]error[/] [grey]· {Markup.Escape(e.Phase)}[/]");
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.Message)}[/]");
        AnsiConsole.WriteLine();
    }
}
