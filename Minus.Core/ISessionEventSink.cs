using Minus.Core.Events;

namespace Minus.Core;

// Contract for anything that observes session events. TranscriptWriter
// is one implementation (persistence); the agent's UI is another
// (display). AggregateEventSink fans out to multiple at once.
public interface ISessionEventSink
{
    void Log(SessionEvent ev);
}

public sealed class AggregateEventSink : ISessionEventSink
{
    private readonly IReadOnlyList<ISessionEventSink> _sinks;

    public AggregateEventSink(params ISessionEventSink[] sinks)
    {
        _sinks = sinks;
    }

    public void Log(SessionEvent ev)
    {
        foreach (var sink in _sinks) sink.Log(ev);
    }
}
