namespace ControlWin.Input;

/// <summary>--debug-input: prints the decoded controller state to the console, only when it changes.</summary>
internal sealed class DebugInputPrinter(ControllerState state)
{
    // Ignore stick jitter below ~1.5% of the axis range so a resting stick doesn't flood the console.
    private const int SimpleStickStep = 512;
    private const int FullStickStep = 32;

    private bool _hasLast;
    private ProReport _last;
    private string? _lastRaw;

    public void OnReport(ProReport report, ReadOnlyMemory<byte> raw)
    {
        state.Update(report);

        // Anything that changes outside the sticks, even bits the parser doesn't know.
        // Simple (0x3F): bytes 0-3 and the tail 12-31. Full (0x30): only 2-5 (battery + buttons);
        // byte 1 is a timer and 12+ is motion data, both change on every report.
        var span = raw.Span;
        string key = report.Format == ReportFormat.Full
            ? Convert.ToHexString(span[2..Math.Min(6, span.Length)])
            : Convert.ToHexString(span[..Math.Min(4, span.Length)])
              + (span.Length > 12 ? "|" + Convert.ToHexString(span[12..Math.Min(32, span.Length)]) : "");
        if (key != _lastRaw)
        {
            if (_lastRaw is not null)
                Log.Info($"  [raw changed] {BitConverter.ToString(span[..Math.Min(16, span.Length)].ToArray())}");
            _lastRaw = key;
        }

        if (!HasChanged(report)) return;
        _last = report;
        _hasLast = true;

        var l = state.Left;
        var r = state.Right;
        var head = raw.Span[..Math.Min(12, raw.Length)].ToArray();
        Log.Info(
            $"[{report.Format,-6}] {(report.Buttons == Buttons.None ? "-" : report.Buttons.ToString())}" +
            $" | raw L({report.LX},{report.LY}) R({report.RX},{report.RY})" +
            $" | norm L({F(l.X)},{F(l.Y)}) R({F(r.X)},{F(r.Y)})" +
            $" | {BitConverter.ToString(head)}");
    }

    private string? _lastUnknown;

    /// <summary>Reports whose ID the parser doesn't handle: show them, deduplicated.</summary>
    public void OnUnknownReport(ReadOnlyMemory<byte> raw)
    {
        var head = raw.Span[..Math.Min(32, raw.Length)].ToArray();
        var text = BitConverter.ToString(head);
        if (text == _lastUnknown) return;
        _lastUnknown = text;
        Log.Info($"  [unknown report] id=0x{head[0]:X2} len={raw.Length} | {text}");
    }

    // "+0.00;-0.00;+0.00" alone prints "-+0.00" for tiny negatives; round first and drop negative zero.
    private static string F(float v) => (MathF.Round(v, 2) + 0f).ToString("+0.00;-0.00;+0.00");

    private bool HasChanged(in ProReport r)
    {
        if (!_hasLast) return true;
        if (r.Format != _last.Format || r.Buttons != _last.Buttons) return true;
        int step = r.Format == ReportFormat.Simple ? SimpleStickStep : FullStickStep;
        return Math.Abs(r.LX - _last.LX) >= step || Math.Abs(r.LY - _last.LY) >= step
            || Math.Abs(r.RX - _last.RX) >= step || Math.Abs(r.RY - _last.RY) >= step;
    }
}
