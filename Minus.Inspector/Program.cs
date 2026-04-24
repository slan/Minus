using System.Text;
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

    var statusInfo = new StatusItem(Key.Null, "(no session)", null);
    var statusBar = new StatusBar(new[]
    {
        statusInfo,
        new StatusItem(Key.r, "~r Raw/Rendered", null),
        new StatusItem(Key.w, "~w Wrap", null),
        new StatusItem(Key.q, "~q Quit", null),
    });

    var inspector = new InspectorView(sessionsDir, follow, statusInfo, statusBar)
    {
        Y = 0,
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

sealed class InspectorView : Window
{
    private enum DetailMode { Raw, Rendered }

    private readonly ListView _sessionList;
    private readonly ListView _timeline;
    private readonly FrameView _detailPane;
    private readonly TextView _detail;
    private readonly string _sessionsDir;
    private readonly bool _follow;
    private readonly StatusItem _statusInfo;
    private readonly StatusBar _statusBar;

    private List<string> _sessionFiles = new();
    private List<Events.SessionEvent> _currentEvents = new();

    private DetailMode _detailMode = DetailMode.Raw;
    private bool _wordWrap;

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
        var followTag = follow ? "  [follow]" : "";
        Title = $"minus-view — {sessionsDir}{followTag}  (Tab switches panes)";

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

        _detailPane = new FrameView("Event [raw]")
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
        _detailPane.Add(_detail);

        Add(sessionPane, timelinePane, _detailPane);

        _sessionList.SelectedItemChanged += args => LoadSession(args.Item);
        _timeline.SelectedItemChanged += args => ShowDetail(args.Item);

        KeyPress += OnKeyPress;
        Closing += (_) => StopFollow();

        LoadSessionList();
    }

    private void OnKeyPress(KeyEventEventArgs e)
    {
        switch (e.KeyEvent.Key)
        {
            case Key.q or Key.Q:
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
        UpdateDetailPaneTitle();
        ShowDetail(_timeline.SelectedItem);
    }

    private void ToggleWordWrap()
    {
        _wordWrap = !_wordWrap;
        _detail.WordWrap = _wordWrap;
        UpdateDetailPaneTitle();
    }

    private void UpdateDetailPaneTitle()
    {
        var mode = _detailMode == DetailMode.Raw ? "raw" : "rendered";
        var wrap = _wordWrap ? " wrap" : "";
        _detailPane.Title = $"Event [{mode}{wrap}]";
        _detailPane.SetNeedsDisplay();
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
        var ev = _currentEvents[idx];
        _detail.Text = _detailMode == DetailMode.Raw ? RenderRaw(ev) : RenderEvent(ev);
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
                catch { /* skip poison line; writer shouldn't produce one */ }
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

    // ---------- raw-mode rendering ----------

    private static string RenderRaw(Events.SessionEvent ev)
    {
        var options = new JsonSerializerOptions(Json.Options) { WriteIndented = true };
        return JsonSerializer.Serialize<Events.SessionEvent>(ev, options);
    }

    // ---------- rendered-mode ("human-friendly") rendering ----------

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
