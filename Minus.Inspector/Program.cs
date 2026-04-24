using System.Text.Json;
using Minus.Core;
using Terminal.Gui;
using Events = Minus.Core.Events;

// minus-view — read-only TUI for inspecting Minus session transcripts.
// Three panes (session list / timeline / event detail) plus a status bar.
// Pass --follow to live-tail the selected session as the agent appends.

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

    // Dynamic "info" status item; mutable Title is updated by InspectorView.
    var statusInfo = new StatusItem(Key.Null, "(no session)", null);
    var statusBar = new StatusBar(new[]
    {
        statusInfo,
        new StatusItem(Key.q | Key.CtrlMask, "~^Q Quit", () => Application.RequestStop()),
    });

    var inspector = new InspectorView(sessionsDir, follow, statusInfo, statusBar)
    {
        Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill(1), // leave 1 row for the StatusBar
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

sealed class InspectorView : Window
{
    private readonly ListView _sessionList;
    private readonly ListView _timeline;
    private readonly TextView _detail;
    private readonly string _sessionsDir;
    private readonly bool _follow;
    private readonly StatusItem _statusInfo;
    private readonly StatusBar _statusBar;

    private List<string> _sessionFiles = new();
    private List<Events.SessionEvent> _currentEvents = new();

    // Follow-mode state: held open on the selected session, polled via a
    // MainLoop timeout, torn down on session change / quit.
    private FileStream? _followStream;
    private StreamReader? _followReader;
    private object? _followTimerToken;

    public InspectorView(
        string sessionsDir,
        bool follow,
        StatusItem statusInfo,
        StatusBar statusBar)
    {
        _sessionsDir = sessionsDir;
        _follow = follow;
        _statusInfo = statusInfo;
        _statusBar = statusBar;
        var suffix = follow ? "  [follow]" : "";
        Title = $"minus-view — {sessionsDir}{suffix}  (Tab switches panes)";

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
        Closing += (_) => StopFollow();

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

            RefreshTimeline(preserveSelection: false);
            if (_currentEvents.Count > 0) ShowDetail(0);
            else _detail.Text = "(empty session)";

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
            var oneLine = ex.Message.Split('\n', 2)[0];
            _timeline.SetSource(new List<string> { $"[parse error] {Truncate(oneLine, 80)}" });
            _timeline.SelectedItem = 0;
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

    private void ShowDetail(int idx)
    {
        if (idx < 0 || idx >= _currentEvents.Count) return;
        var options = new JsonSerializerOptions(Json.Options) { WriteIndented = true };
        _detail.Text = JsonSerializer.Serialize<Events.SessionEvent>(_currentEvents[idx], options);
    }

    private void RefreshTimeline(bool preserveSelection)
    {
        var prevIdx = _timeline.SelectedItem;
        var wasAtBottom = _currentEvents.Count > 0 && prevIdx >= _currentEvents.Count - 1;
        var rows = _currentEvents.Select(Summary).ToList();
        _timeline.SetSource(rows);
        if (rows.Count == 0) return;
        if (!preserveSelection) _timeline.SelectedItem = 0;
        else if (wasAtBottom) _timeline.SelectedItem = rows.Count - 1;
        else if (prevIdx >= 0 && prevIdx < rows.Count) _timeline.SelectedItem = prevIdx;
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
                catch { /* skip poison line, writer shouldn't produce one */ }
            }
        }
        catch
        {
            StopFollow();
            return;
        }
        if (added > 0)
        {
            RefreshTimeline(preserveSelection: true);
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

        // Token stats from the most recent llm_response with usage.
        var lastUsage = _currentEvents
            .OfType<Events.LlmResponse>()
            .Select(r => r.Body.Usage)
            .LastOrDefault(u => u is not null);

        string tokens;
        if (lastUsage is null)
        {
            tokens = "";
        }
        else
        {
            tokens = $" | ctx {Format(lastUsage.PromptTokens)} tok " +
                     $"(+{Format(lastUsage.CompletionTokens)} gen)";
        }

        var followTag = _follow ? " | [following]" : "";

        _statusInfo.Title = $"{sessionName}  |  {count} events{tokens}{followTag}";
        _statusBar.SetNeedsDisplay();
    }

    private static string Format(int n) =>
        n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();

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
