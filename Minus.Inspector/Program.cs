using System.Text;
using System.Text.Json;
using Minus.Core;
using Terminal.Gui;
using Terminal.Gui.Graphs;
using Terminal.Gui.Trees;
using Events = Minus.Core.Events;

// minus-view — read-only TUI for inspecting Minus session transcripts.
// Three panes (session list / grouped timeline / event detail) plus a
// status bar. Pass --follow to live-tail the selected session.

bool follow = false;
string? sessionsDir = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--follow":
        case "-f":
            follow = true;
            break;
        default:
            sessionsDir ??= args[i];
            break;
    }
}
sessionsDir ??= Path.Combine(Directory.GetCurrentDirectory(), "sessions");

if (!Directory.Exists(sessionsDir))
{
    Console.Error.WriteLine($"minus-view: not a directory: {sessionsDir}");
    return 1;
}

Application.Init();
try
{
    var top = Application.Top;

    var theme = Theme.Build();
    top.ColorScheme = theme.Base;

    var statusInfo = new StatusItem(Key.Null, "(no session)", null);
    var statusBar = new StatusBar(new[]
    {
        statusInfo,
        new StatusItem(Key.r, "~r Raw/Rendered", null),
        new StatusItem(Key.w, "~w Wrap", null),
        new StatusItem(Key.q, "~q Quit", null),
    })
    {
        ColorScheme = theme.Status,
    };

    var inspector = new InspectorView(sessionsDir, follow, statusInfo, statusBar, theme)
    {
        X = 0, Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill(1),
    };

    top.Add(inspector);
    top.Add(statusBar);
    Application.Run();
}
finally
{
    Application.Shutdown();
}
return 0;

// ---------- theme ----------

sealed record Theme(ColorScheme Base, ColorScheme Focus, ColorScheme Status, ColorScheme Title, ColorScheme Separator)
{
    public static Theme Build()
    {
        Terminal.Gui.Attribute Attr(Color fg, Color bg) => Application.Driver.MakeAttribute(fg, bg);

        var baseScheme = new ColorScheme
        {
            Normal    = Attr(Color.Gray,        Color.Black),
            Focus     = Attr(Color.White,       Color.Black),
            HotNormal = Attr(Color.BrightCyan,  Color.Black),
            HotFocus  = Attr(Color.BrightCyan,  Color.Black),
            Disabled  = Attr(Color.DarkGray,    Color.Black),
        };

        // Focused list/tree row: subtle dark highlight behind text.
        var focusScheme = new ColorScheme
        {
            Normal    = Attr(Color.Gray,        Color.Black),
            Focus     = Attr(Color.BrightYellow,Color.DarkGray),
            HotNormal = Attr(Color.BrightCyan,  Color.Black),
            HotFocus  = Attr(Color.BrightYellow,Color.DarkGray),
            Disabled  = Attr(Color.DarkGray,    Color.Black),
        };

        // Status bar sits on a blue band with bright-yellow shortcut letters.
        var statusScheme = new ColorScheme
        {
            Normal    = Attr(Color.White,       Color.Blue),
            Focus     = Attr(Color.White,       Color.Blue),
            HotNormal = Attr(Color.BrightYellow,Color.Blue),
            HotFocus  = Attr(Color.BrightYellow,Color.Blue),
            Disabled  = Attr(Color.DarkGray,    Color.Blue),
        };

        // Pane-title labels: bold cyan on base background.
        var titleScheme = new ColorScheme
        {
            Normal    = Attr(Color.BrightCyan,  Color.Black),
            Focus     = Attr(Color.BrightCyan,  Color.Black),
            HotNormal = Attr(Color.BrightCyan,  Color.Black),
            HotFocus  = Attr(Color.BrightCyan,  Color.Black),
            Disabled  = Attr(Color.DarkGray,    Color.Black),
        };

        // Separator lines: dim gray, distinct from content.
        var separatorScheme = new ColorScheme
        {
            Normal    = Attr(Color.DarkGray,    Color.Black),
            Focus     = Attr(Color.DarkGray,    Color.Black),
            HotNormal = Attr(Color.DarkGray,    Color.Black),
            HotFocus  = Attr(Color.DarkGray,    Color.Black),
            Disabled  = Attr(Color.DarkGray,    Color.Black),
        };

        return new Theme(baseScheme, focusScheme, statusScheme, titleScheme, separatorScheme);
    }
}

// ---------- timeline node hierarchy ----------

abstract class TimelineNode
{
    public abstract string Display { get; }
    public virtual IEnumerable<TimelineNode> Children => Array.Empty<TimelineNode>();
    public abstract Events.SessionEvent? BackingEvent { get; }
}

sealed class EventNode : TimelineNode
{
    public Events.SessionEvent Event { get; }
    private readonly string _display;
    public EventNode(Events.SessionEvent ev, string display)
    {
        Event = ev;
        _display = display;
    }
    public override string Display => _display;
    public override Events.SessionEvent BackingEvent => Event;
}

sealed class TurnGroup : TimelineNode
{
    public string TurnId { get; }
    public List<EventNode> ChildNodes { get; } = new();

    public TurnGroup(string turnId) { TurnId = turnId; }

    public override IEnumerable<TimelineNode> Children => ChildNodes;
    public override Events.SessionEvent? BackingEvent => null;

    public override string Display
    {
        get
        {
            if (ChildNodes.Count == 0) return $"Turn {Short(TurnId)}";
            var first = ChildNodes[0].Event.Ts;
            var last = ChildNodes[^1].Event.Ts;
            var dur = (long)(last - first).TotalMilliseconds;
            var tokens = ChildNodes
                .Select(n => n.Event)
                .OfType<Events.LlmResponse>()
                .Select(r => r.Body.Usage?.TotalTokens ?? 0)
                .Sum();
            var toolCalls = ChildNodes
                .Select(n => n.Event)
                .OfType<Events.ToolCall>()
                .Count();
            var parts = new List<string> { $"{ChildNodes.Count} events" };
            if (dur > 0) parts.Add($"{dur}ms");
            if (tokens > 0) parts.Add($"{FormatK(tokens)} tok");
            if (toolCalls > 0) parts.Add($"{toolCalls} tools");
            return $"Turn {Short(TurnId)}  —  {string.Join(", ", parts)}";
        }
    }

    private static string Short(string id) => id[..Math.Min(id.Length, 8)];
    private static string FormatK(int n) => n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();
}

// ---------- main view ----------

sealed class InspectorView : View
{
    private enum DetailMode { Raw, Rendered }

    private const int SessionPaneWidth = 30;

    private readonly ListView _sessionList;
    private readonly TreeView<TimelineNode> _timeline;
    private readonly Label _detailHeader;
    private readonly TextView _detail;
    private readonly string _sessionsDir;
    private readonly bool _follow;
    private readonly StatusItem _statusInfo;
    private readonly StatusBar _statusBar;
    private readonly Theme _theme;

    private List<string> _sessionFiles = new();
    private List<Events.SessionEvent> _currentEvents = new();
    private List<TimelineNode> _timelineNodes = new();

    private DetailMode _detailMode = DetailMode.Raw;
    private bool _wordWrap;

    private FileStream? _followStream;
    private StreamReader? _followReader;
    private object? _followTimerToken;

    public InspectorView(
        string sessionsDir,
        bool follow,
        StatusItem statusInfo,
        StatusBar statusBar,
        Theme theme)
    {
        _sessionsDir = sessionsDir;
        _follow = follow;
        _statusInfo = statusInfo;
        _statusBar = statusBar;
        _theme = theme;
        CanFocus = true;

        // Layout math:
        //   col 0         : margin
        //   cols 1..30    : sessions pane (width 30)
        //   col 31        : margin
        //   col 32        : vertical separator │
        //   col 33        : margin
        //   cols 34..     : right-side panes (timeline on top, detail below)
        const int sessionX = 1;
        const int vSepX = sessionX + SessionPaneWidth + 1;  // 32
        const int rightX = vSepX + 2;                       // 34

        var sessionsHeader = new Label("Sessions")
        {
            X = sessionX, Y = 0,
            Width = SessionPaneWidth, Height = 1,
            ColorScheme = theme.Title,
        };
        _sessionList = new ListView(new List<string>())
        {
            X = sessionX, Y = 1,
            Width = SessionPaneWidth, Height = Dim.Fill(),
            AllowsMarking = false,
            ColorScheme = theme.Focus,
        };

        var vSep = new LineView(Orientation.Vertical)
        {
            X = vSepX, Y = 0,
            Height = Dim.Fill(),
            ColorScheme = theme.Separator,
        };

        var timelineHeader = new Label("Timeline")
        {
            X = rightX, Y = 0,
            Width = Dim.Fill(1), Height = 1,
            ColorScheme = theme.Title,
        };
        _timeline = new TreeView<TimelineNode>
        {
            X = rightX, Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Percent(55),
            AspectGetter = n => n.Display,
            TreeBuilder = new DelegateTreeBuilder<TimelineNode>(n => n.Children),
            ColorScheme = theme.Focus,
        };

        var hSep = new LineView(Orientation.Horizontal)
        {
            X = rightX, Y = Pos.Bottom(_timeline),
            Width = Dim.Fill(1),
            ColorScheme = theme.Separator,
        };

        _detailHeader = new Label("Event [raw]")
        {
            X = rightX, Y = Pos.Bottom(hSep),
            Width = Dim.Fill(1), Height = 1,
            ColorScheme = theme.Title,
        };
        _detail = new TextView
        {
            X = rightX, Y = Pos.Bottom(_detailHeader),
            Width = Dim.Fill(1),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = false,
            ColorScheme = theme.Base,
        };

        Add(sessionsHeader, _sessionList, vSep,
            timelineHeader, _timeline, hSep,
            _detailHeader, _detail);

        _sessionList.SelectedItemChanged += args => LoadSession(args.Item);
        _timeline.SelectionChanged += (_, args) => ShowDetailFor(args.NewValue);

        KeyPress += OnKeyPress;

        LoadSessionList();
    }

    private void OnKeyPress(KeyEventEventArgs e)
    {
        switch (e.KeyEvent.Key)
        {
            case Key.q or Key.Q:
                StopFollow();
                Application.RequestStop();
                e.Handled = true;
                break;
            case Key.r or Key.R:
                ToggleDetailMode();
                e.Handled = true;
                break;
            case Key.w or Key.W:
                ToggleWordWrap();
                e.Handled = true;
                break;
        }
    }

    private void ToggleDetailMode()
    {
        _detailMode = _detailMode == DetailMode.Raw ? DetailMode.Rendered : DetailMode.Raw;
        UpdateDetailHeader();
        ShowDetailFor(_timeline.SelectedObject);
    }

    private void ToggleWordWrap()
    {
        _wordWrap = !_wordWrap;
        _detail.WordWrap = _wordWrap;
        UpdateDetailHeader();
    }

    private void UpdateDetailHeader()
    {
        var mode = _detailMode == DetailMode.Raw ? "raw" : "rendered";
        var wrap = _wordWrap ? " wrap" : "";
        _detailHeader.Text = $"Event [{mode}{wrap}]";
        _detailHeader.SetNeedsDisplay();
    }

    private void LoadSessionList()
    {
        _sessionFiles = Directory
            .EnumerateFiles(_sessionsDir, "*.jsonl")
            .OrderByDescending(f => f)
            .ToList();
        var labels = _sessionFiles
            .Select(f => Path.GetFileNameWithoutExtension(f)!)
            .ToList();
        _sessionList.SetSource(labels);
        if (_sessionFiles.Count > 0)
        {
            _sessionList.SelectedItem = 0;
            LoadSession(0);
        }
        else
        {
            UpdateStatus();
        }
    }

    private void LoadSession(int idx)
    {
        StopFollow();
        if (idx < 0 || idx >= _sessionFiles.Count) return;
        var path = _sessionFiles[idx];
        FileStream? stream = null;
        StreamReader? reader = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            reader = new StreamReader(stream);
            _currentEvents = new List<Events.SessionEvent>();
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var ev = JsonSerializer.Deserialize<Events.SessionEvent>(line, Json.Options)
                    ?? throw new FormatException("null SessionEvent after deserialization");
                _currentEvents.Add(ev);
            }

            RebuildTimeline(preserveSelection: false);
            if (_timelineNodes.Count == 0) _detail.Text = "(empty session)";

            if (_follow)
            {
                _followStream = stream;
                _followReader = reader;
                stream = null;
                reader = null;
                _followTimerToken = Application.MainLoop.AddTimeout(
                    TimeSpan.FromMilliseconds(500),
                    _ => { PollFollow(); return true; });
            }
        }
        catch (Exception ex)
        {
            _currentEvents = new List<Events.SessionEvent>();
            _timelineNodes = new List<TimelineNode>();
            _timeline.ClearObjects();
            _detail.Text =
                $"Could not parse {Path.GetFileName(path)}\n\n" +
                $"{ex.GetType().Name}: {ex.Message}\n\n" +
                $"This is expected for transcripts written before the typed-event\n" +
                $"schema change. Delete the file or start a new session.";
        }
        finally
        {
            reader?.Dispose();
            stream?.Dispose();
        }
        UpdateStatus();
    }

    private void RebuildTimeline(bool preserveSelection)
    {
        var prevId = _timeline.SelectedObject?.BackingEvent?.Id;

        _timelineNodes = BuildTimelineNodes(_currentEvents);
        _timeline.ClearObjects();
        _timeline.AddObjects(_timelineNodes);
        _timeline.ExpandAll();

        TimelineNode? target = null;
        if (preserveSelection && prevId is not null)
            target = FindNodeByEventId(prevId);
        target ??= FirstDisplayableNode();

        if (target is not null)
        {
            _timeline.SelectedObject = target;
            ShowDetailFor(target);
        }
    }

    private static List<TimelineNode> BuildTimelineNodes(List<Events.SessionEvent> events)
    {
        var result = new List<TimelineNode>();
        var groups = new Dictionary<string, TurnGroup>();
        foreach (var ev in events)
        {
            var line = EventLine(ev);
            if (ev.TurnId is { } turnId)
            {
                if (!groups.TryGetValue(turnId, out var group))
                {
                    group = new TurnGroup(turnId);
                    groups[turnId] = group;
                    result.Add(group);
                }
                group.ChildNodes.Add(new EventNode(ev, line));
            }
            else
            {
                result.Add(new EventNode(ev, line));
            }
        }
        return result;
    }

    private TimelineNode? FindNodeByEventId(string id)
    {
        foreach (var n in _timelineNodes)
        {
            if (n.BackingEvent?.Id == id) return n;
            if (n is TurnGroup tg)
                foreach (var child in tg.ChildNodes)
                    if (child.BackingEvent?.Id == id) return child;
        }
        return null;
    }

    private TimelineNode? FirstDisplayableNode()
    {
        foreach (var n in _timelineNodes)
        {
            if (n is EventNode) return n;
            if (n is TurnGroup tg && tg.ChildNodes.Count > 0) return tg.ChildNodes[0];
        }
        return _timelineNodes.FirstOrDefault();
    }

    private void ShowDetailFor(TimelineNode? node)
    {
        if (node is null) { _detail.Text = ""; return; }

        if (node is TurnGroup tg)
        {
            _detail.Text = _detailMode == DetailMode.Raw
                ? $"(turn grouping row — select a child event for raw JSON)\n\n{RenderTurnSummary(tg)}"
                : RenderTurnSummary(tg);
            return;
        }

        if (node.BackingEvent is { } ev)
            _detail.Text = _detailMode == DetailMode.Raw ? RenderRaw(ev) : RenderEvent(ev);
    }

    private void PollFollow()
    {
        if (_followReader is null) return;
        var added = 0;
        try
        {
            while (_followReader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var ev = JsonSerializer.Deserialize<Events.SessionEvent>(line, Json.Options);
                    if (ev is not null)
                    {
                        _currentEvents.Add(ev);
                        added++;
                    }
                }
                catch { /* skip poison line */ }
            }
        }
        catch
        {
            StopFollow();
            return;
        }
        if (added > 0)
        {
            RebuildTimeline(preserveSelection: true);
            UpdateStatus();
        }
    }

    private void StopFollow()
    {
        if (_followTimerToken is not null)
        {
            Application.MainLoop.RemoveTimeout(_followTimerToken);
            _followTimerToken = null;
        }
        _followReader?.Dispose();
        _followStream?.Dispose();
        _followReader = null;
        _followStream = null;
    }

    private void UpdateStatus()
    {
        var sessionName = _sessionFiles.Count > 0
            && _sessionList.SelectedItem >= 0
            && _sessionList.SelectedItem < _sessionFiles.Count
                ? Path.GetFileNameWithoutExtension(_sessionFiles[_sessionList.SelectedItem])
                : "(no session)";

        var count = _currentEvents.Count;

        var lastUsage = _currentEvents
            .OfType<Events.LlmResponse>()
            .Select(r => r.Body.Usage)
            .LastOrDefault(u => u is not null);

        var tokens = lastUsage is null
            ? ""
            : $" | ctx {FormatK(lastUsage.PromptTokens)} tok (+{FormatK(lastUsage.CompletionTokens)} gen)";

        var followTag = _follow ? " | [following]" : "";

        _statusInfo.Title = $"{sessionName}  |  {count} events{tokens}{followTag}";
        _statusBar.SetNeedsDisplay();
    }

    // ---------- timeline formatting ----------

    private static string EventLine(Events.SessionEvent ev)
    {
        var type = TypeLabel(ev).PadRight(14);
        var summary = EventSummary(ev);
        return string.IsNullOrEmpty(summary)
            ? $"{Time(ev.Ts)}  {type}"
            : $"{Time(ev.Ts)}  {type}  {summary}";
    }

    private static string TypeLabel(Events.SessionEvent ev) => ev switch
    {
        Events.Meta         => "session_meta",
        Events.UserInput    => "user_input",
        Events.LlmRequest   => "llm_request",
        Events.LlmResponse  => "llm_response",
        Events.ToolCall     => "tool_call",
        Events.ToolResult   => "tool_result",
        Events.Error        => "error",
        Events.End          => "session_end",
        _                   => ev.GetType().Name.ToLowerInvariant(),
    };

    private static string EventSummary(Events.SessionEvent ev) => ev switch
    {
        Events.Meta m        => $"persona={m.Persona} model={m.Model}",
        Events.UserInput u   => Truncate(u.Content, 60),
        Events.LlmRequest    => "",
        Events.LlmResponse r => $"{r.DurationMs}ms" +
                                (r.Body.Usage is { } u ? $"  {u.TotalTokens} tok" : ""),
        Events.ToolCall c    => c.Name,
        Events.ToolResult t  => $"{t.DurationMs}ms" + (t.Error is null ? "" : " [error]"),
        Events.Error e       => $"[{e.Phase}] {Truncate(e.Message, 50)}",
        Events.End           => "",
        _                    => "",
    };

    // ---------- raw & rendered event bodies ----------

    private static string RenderRaw(Events.SessionEvent ev)
    {
        var options = new JsonSerializerOptions(Json.Options) { WriteIndented = true };
        return JsonSerializer.Serialize<Events.SessionEvent>(ev, options);
    }

    private static string RenderEvent(Events.SessionEvent ev) => ev switch
    {
        Events.Meta m        => RenderMeta(m),
        Events.UserInput u   => RenderUserInput(u),
        Events.LlmRequest r  => RenderLlmRequest(r),
        Events.LlmResponse r => RenderLlmResponse(r),
        Events.ToolCall c    => RenderToolCall(c),
        Events.ToolResult t  => RenderToolResult(t),
        Events.Error e       => RenderError(e),
        Events.End           => "session ended",
        _                    => $"({ev.GetType().Name})",
    };

    private static string RenderMeta(Events.Meta m) =>
        "SESSION STARTED\n" +
        $"  session_id: {m.SessionId}\n" +
        $"  persona:    {m.Persona}\n" +
        $"  model:      {m.Model} @ {m.Endpoint}\n" +
        $"  cwd:        {m.Cwd}\n" +
        $"  minus:      {m.MinusVersion ?? "(unknown)"}\n" +
        $"  tools:      {string.Join(", ", m.Tools)}\n";

    private static string RenderUserInput(Events.UserInput u) =>
        "USER\n\n" + Indent(u.Content, "  ");

    private static string RenderLlmRequest(Events.LlmRequest r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"REQUEST  (turn {Short(r.TurnId)})");
        sb.AppendLine($"model: {r.Body.Model}");
        sb.AppendLine("messages:");
        for (int i = 0; i < r.Body.Messages.Count; i++)
        {
            var msg = r.Body.Messages[i];
            sb.AppendLine($"  [{i + 1}] {msg.Role}");
            if (!string.IsNullOrEmpty(msg.Content))
                sb.Append(Indent(msg.Content, "      ")).AppendLine();
            if (msg.ToolCalls is { Count: > 0 })
                foreach (var tc in msg.ToolCalls)
                    sb.AppendLine($"      → {tc.Function.Name}({tc.Function.Arguments})");
        }
        if (r.Body.Tools is { Count: > 0 })
            sb.AppendLine($"tools: {string.Join(", ", r.Body.Tools.Select(t => t.Function.Name))}");
        if (!string.IsNullOrEmpty(r.Body.ToolChoice))
            sb.AppendLine($"tool_choice: {r.Body.ToolChoice}");
        return sb.ToString();
    }

    private static string RenderLlmResponse(Events.LlmResponse r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"RESPONSE  (turn {Short(r.TurnId)}, {r.DurationMs}ms)");

        if (r.Body.Usage is { } u)
            sb.AppendLine($"tokens:  prompt={u.PromptTokens}  completion={u.CompletionTokens}  total={u.TotalTokens}");

        if (r.Body.Timings is { } t)
        {
            var parts = new List<string>();
            if (t.PromptMs.HasValue && t.PromptPerSecond.HasValue)
                parts.Add($"prompt {t.PromptMs:0}ms @ {t.PromptPerSecond:0} t/s");
            if (t.PredictedMs.HasValue && t.PredictedPerSecond.HasValue)
                parts.Add($"predict {t.PredictedMs:0}ms @ {t.PredictedPerSecond:0} t/s");
            if (parts.Count > 0)
                sb.AppendLine($"server:  {string.Join("   ", parts)}");
        }

        foreach (var choice in r.Body.Choices)
        {
            sb.AppendLine();
            var msg = choice.Message;
            if (!string.IsNullOrEmpty(msg.ReasoningContent))
            {
                sb.AppendLine("reasoning:");
                sb.Append(Indent(msg.ReasoningContent, "  ")).AppendLine();
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(msg.Content))
            {
                sb.AppendLine("content:");
                sb.Append(Indent(msg.Content, "  ")).AppendLine();
            }
            if (msg.ToolCalls is { Count: > 0 })
            {
                sb.AppendLine("tool_calls:");
                foreach (var tc in msg.ToolCalls)
                    sb.AppendLine($"  → {tc.Function.Name}({tc.Function.Arguments})");
            }
            if (!string.IsNullOrEmpty(choice.FinishReason))
                sb.AppendLine($"finish: {choice.FinishReason}");
        }
        return sb.ToString();
    }

    private static string RenderToolCall(Events.ToolCall c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TOOL CALL  (turn {Short(c.TurnId)})");
        sb.AppendLine($"name:    {c.Name}");
        sb.AppendLine($"call_id: {c.CallId}");
        sb.AppendLine("arguments:");
        sb.Append(Indent(PrettyJsonOrRaw(c.Arguments), "  ")).AppendLine();
        return sb.ToString();
    }

    private static string RenderToolResult(Events.ToolResult t)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TOOL RESULT  (call {Short(t.CallId)}, {t.DurationMs}ms)");
        if (!string.IsNullOrEmpty(t.Error))
        {
            sb.AppendLine("ERROR:");
            sb.Append(Indent(t.Error, "  ")).AppendLine();
        }
        else if (!string.IsNullOrEmpty(t.Content))
        {
            sb.AppendLine("content:");
            sb.Append(Indent(t.Content, "  ")).AppendLine();
        }
        else
        {
            sb.AppendLine("(no content)");
        }
        return sb.ToString();
    }

    private static string RenderError(Events.Error e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ERROR  [phase: {e.Phase}]");
        if (e.HttpStatus.HasValue) sb.AppendLine($"http_status: {e.HttpStatus}");
        if (!string.IsNullOrEmpty(e.Code)) sb.AppendLine($"code: {e.Code}");
        sb.AppendLine("message:");
        sb.Append(Indent(e.Message, "  ")).AppendLine();
        sb.AppendLine($"retryable: {e.Retryable}");
        return sb.ToString();
    }

    private static string RenderTurnSummary(TurnGroup tg)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TURN {tg.TurnId}");
        sb.AppendLine($"  events: {tg.ChildNodes.Count}");
        if (tg.ChildNodes.Count > 0)
        {
            var first = tg.ChildNodes[0].Event.Ts;
            var last = tg.ChildNodes[^1].Event.Ts;
            var dur = (long)(last - first).TotalMilliseconds;
            sb.AppendLine($"  duration: {dur}ms  ({Time(first)} → {Time(last)})");
        }
        var tokens = tg.ChildNodes
            .Select(n => n.Event)
            .OfType<Events.LlmResponse>()
            .Select(r => r.Body.Usage?.TotalTokens ?? 0)
            .Sum();
        if (tokens > 0) sb.AppendLine($"  tokens: {tokens}");
        sb.AppendLine();
        sb.AppendLine("  contains:");
        foreach (var child in tg.ChildNodes)
            sb.AppendLine($"    {child.Display}");
        return sb.ToString();
    }

    // ---------- helpers ----------

    private static string Indent(string text, string prefix) =>
        string.Join("\n",
            text.Replace("\r\n", "\n").Split('\n').Select(line => prefix + line));

    private static string PrettyJsonOrRaw(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    private static string Short(string? id) =>
        id is null ? "(none)" : id[..Math.Min(id.Length, 8)];

    private static string FormatK(int n) =>
        n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();

    private static string Time(DateTimeOffset ts) => ts.LocalDateTime.ToString("HH:mm:ss");

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";
}
