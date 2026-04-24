namespace Minus;

// Owns the bottom input line and serializes all writes to stdout so the
// spinner, agent output, and the user's typing don't interleave. Think of
// it as a tiny readline: the prompt stays pinned at the end of the scroll,
// content from the agent scrolls above it, and the user's buffer survives
// every redraw.
//
// All public methods take _lock so state is consistent. Writes to stdout
// use ANSI escapes (no Spectre here — the inner render action is where
// Spectre markup gets emitted, but *those* writes also happen under the
// lock via WriteAboveInput).
public sealed class AsyncConsole
{
    // ESC codes. Kept as strings so the call sites read clearly.
    private const string Esc = "\x1b";
    private const string EraseLine = Esc + "[2K";      // clear entire current line
    private const string Cr = "\r";                     // cursor to col 0

    private static readonly string[] SpinnerFrames =
    {
        "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏",
    };

    // ANSI color helpers — matches what Spectre would emit, but we bypass
    // Spectre for the prompt itself because Spectre always terminates with
    // a newline.
    private const string PromptStyle   = Esc + "[36m";  // cyan
    private const string SpinnerStyle  = Esc + "[33m";  // yellow
    private const string Reset         = Esc + "[0m";

    private readonly object _lock = new();
    private string _buffer = "";
    private bool _busy;
    private int _spinnerIdx;
    private CancellationTokenSource? _spinnerCts;

    // Render `action` above the input line, then restore the prompt.
    // The action is free to call AnsiConsole.* — Spectre will append a
    // newline after each line so we end on a fresh row, then we redraw
    // the prompt on that row.
    public void WriteAboveInput(Action action)
    {
        lock (_lock)
        {
            // Clear whatever the prompt wrote on the current line before
            // the caller's output scrolls in, otherwise remnants of the
            // prompt bleed through.
            Console.Write(Cr + EraseLine);
            action();
            RedrawPromptUnsafe();
        }
    }

    // Toggle the busy indicator. When true, a spinner replaces the '>'
    // prefix; a background task redraws the prompt every ~100ms so the
    // frame advances. When false, the static prompt comes back.
    public void SetBusy(bool busy)
    {
        lock (_lock)
        {
            if (_busy == busy) return;
            _busy = busy;

            if (busy)
            {
                _spinnerIdx = 0;
                _spinnerCts = new CancellationTokenSource();
                var token = _spinnerCts.Token;
                _ = Task.Run(() => SpinLoopAsync(token));
            }
            else
            {
                _spinnerCts?.Cancel();
                _spinnerCts = null;
            }

            RedrawPromptUnsafe();
        }
    }

    public void AppendChar(char c)
    {
        lock (_lock)
        {
            _buffer += c;
            // Fast path: the new char appends at the current cursor
            // position; no full redraw needed.
            Console.Write(c);
        }
    }

    public bool Backspace()
    {
        lock (_lock)
        {
            if (_buffer.Length == 0) return false;
            _buffer = _buffer[..^1];
            // "\b \b" visually erases one character at the current cursor.
            Console.Write("\b \b");
            return true;
        }
    }

    public void ClearBuffer()
    {
        lock (_lock)
        {
            if (_buffer.Length == 0) return;
            _buffer = "";
            RedrawPromptUnsafe();
        }
    }

    // Return the current buffer and reset it. Used when Enter is pressed.
    public string TakeBuffer()
    {
        lock (_lock)
        {
            var taken = _buffer;
            _buffer = "";
            RedrawPromptUnsafe();
            return taken;
        }
    }

    // Redraw the prompt + buffer, used after sink renders and state toggles.
    // Cursor ends up at the end of the buffer every time — MVP has no
    // mid-line editing.
    private void RedrawPromptUnsafe()
    {
        Console.Write(Cr + EraseLine);
        if (_busy)
            Console.Write(SpinnerStyle + SpinnerFrames[_spinnerIdx] + Reset + " ");
        else
            Console.Write(PromptStyle + ">" + Reset + " ");
        Console.Write(_buffer);
    }

    private async Task SpinLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                lock (_lock)
                {
                    if (!_busy) break;
                    _spinnerIdx = (_spinnerIdx + 1) % SpinnerFrames.Length;
                    RedrawPromptUnsafe();
                }
            }
        }
        catch (TaskCanceledException) { /* normal shutdown */ }
    }
}
