using System.Text.Json;
using Minus.Core;
using Terminal.Gui;
using Events = Minus.Core.Events;

// minus-view — read-only TUI for inspecting Minus session transcripts.
// Three panes: session list (left), event timeline (top-right), event
// detail as pretty JSON (bottom-right). Follow mode comes in a later step.

string sessionsDir = args.Length > 0
    ? args[0]
    : Path.Combine(Directory.GetCurrentDirectory(), "sessions");

if (!Directory.Exists(sessionsDir))
{
    Console.Error.WriteLine($"minus-view: not a directory: {sessionsDir}");
    return 1;
}

Application.Init();
try
{
    Application.Run(new InspectorView(sessionsDir));
}
finally
{
    Application.Shutdown();
}
return 0;

sealed class InspectorView : Window
{
    private readonly ListView _sessionList;
    private readonly ListView _timeline;
    private readonly TextView _detail;
    private readonly string _sessionsDir;
    private List<string> _sessionFiles = new();
    private List<Events.SessionEvent> _currentEvents = new();

    public InspectorView(string sessionsDir)
    {
        _sessionsDir = sessionsDir;
        Title = $"minus-view — {sessionsDir}  (q to quit, Tab to switch panes)";

        var sessionPane = new FrameView("Sessions")
        {
            X = 0,
            Y = 0,
            Width = 34,
            Height = Dim.Fill(),
        };
        _sessionList = new ListView(new List<string>())
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false,
        };
        sessionPane.Add(_sessionList);

        var timelinePane = new FrameView("Timeline")
        {
            X = Pos.Right(sessionPane),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(55),
        };
        _timeline = new ListView(new List<string>())
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false,
        };
        timelinePane.Add(_timeline);

        var detailPane = new FrameView("Event")
        {
            X = Pos.Right(sessionPane),
            Y = Pos.Bottom(timelinePane),
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        _detail = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = false,
        };
        detailPane.Add(_detail);

        Add(sessionPane, timelinePane, detailPane);

        _sessionList.SelectedItemChanged += args => LoadSession(args.Item);
        _timeline.SelectedItemChanged += args => ShowDetail(args.Item);

        KeyPress += OnKeyPress;

        LoadSessionList();
    }

    private void OnKeyPress(KeyEventEventArgs e)
    {
        if (e.KeyEvent.Key is Key.q or Key.Q)
        {
            Application.RequestStop();
            e.Handled = true;
        }
    }

    private void LoadSessionList()
    {
        _sessionFiles = Directory
            .EnumerateFiles(_sessionsDir, "*.jsonl")
            .OrderByDescending(f => f)
            .ToList();
        var labels = _sessionFiles
            .Select(Path.GetFileNameWithoutExtension)
            .Cast<string>()
            .ToList();
        _sessionList.SetSource(labels);
        if (_sessionFiles.Count > 0)
        {
            _sessionList.SelectedItem = 0;
            LoadSession(0);
        }
    }

    private void LoadSession(int idx)
    {
        if (idx < 0 || idx >= _sessionFiles.Count) return;
        var path = _sessionFiles[idx];
        try
        {
            _currentEvents = TranscriptReader.Read(path).ToList();
            var rows = _currentEvents.Select(Summary).ToList();
            _timeline.SetSource(rows);
            if (_currentEvents.Count > 0)
            {
                _timeline.SelectedItem = 0;
                ShowDetail(0);
            }
            else
            {
                _detail.Text = "(empty session)";
            }
        }
        catch (Exception ex)
        {
            _currentEvents = new List<Events.SessionEvent>();
            var oneLine = ex.Message.Split('\n', 2)[0];
            _timeline.SetSource(new List<string> { $"[parse error] {Truncate(oneLine, 80)}" });
            _timeline.SelectedItem = 0;
            _detail.Text =
                $"Could not parse {Path.GetFileName(path)}\n\n" +
                $"{ex.GetType().Name}: {ex.Message}\n\n" +
                $"This is expected for transcripts written before the typed-event\n" +
                $"schema change. Delete the file or start a new session.";
        }
    }

    private void ShowDetail(int idx)
    {
        if (idx < 0 || idx >= _currentEvents.Count) return;
        var options = new JsonSerializerOptions(Json.Options) { WriteIndented = true };
        _detail.Text = JsonSerializer.Serialize<Events.SessionEvent>(_currentEvents[idx], options);
    }

    private static string Summary(Events.SessionEvent ev) => ev switch
    {
        Events.Meta m        => $"{Time(ev.Ts)}  session_meta    persona={m.Persona} model={m.Model}",
        Events.UserInput u   => $"{Time(ev.Ts)}  user_input      {Truncate(u.Content, 50)}",
        Events.LlmRequest    => $"{Time(ev.Ts)}  llm_request",
        Events.LlmResponse r => $"{Time(ev.Ts)}  llm_response    {r.DurationMs}ms",
        Events.ToolCall c    => $"{Time(ev.Ts)}  tool_call       {c.Name}",
        Events.ToolResult t  => $"{Time(ev.Ts)}  tool_result     {t.DurationMs}ms{(t.Error is null ? "" : " [error]")}",
        Events.Error e       => $"{Time(ev.Ts)}  error           [{e.Phase}] {Truncate(e.Message, 40)}",
        Events.End           => $"{Time(ev.Ts)}  session_end",
        _                    => $"{Time(ev.Ts)}  {ev.GetType().Name}",
    };

    private static string Time(DateTimeOffset ts) => ts.LocalDateTime.ToString("HH:mm:ss");

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";
}
