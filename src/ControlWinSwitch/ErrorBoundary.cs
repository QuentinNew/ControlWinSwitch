using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch;

/// <summary>
/// One policy for unexpected exceptions. On the UI thread (message loop, window procedures) the error is logged, the app
/// goes back to idle and keeps running. On a worker thread it is logged and the thread ends.
/// </summary>
internal static class ErrorBoundary
{
    private const int GiveUpCount = 100;
    private const long WindowMs = 10_000;

    private static readonly ErrorBudget s_budget = new(GiveUpCount, WindowMs);
    private static bool s_gaveUp;

    /// <summary>Puts the app back in a safe state; the argument says whether to tell the user. Set by Program.</summary>
    public static Action<bool>? Recover;

    /// <summary>An exception caught on the UI thread.</summary>
    public static void OnUiError(string where, Exception ex)
    {
        if (s_gaveUp) return; // quitting already
        var verdict = s_budget.Record(Environment.TickCount64, out int suppressed);
        if (suppressed > 0) Log.Error($"{suppressed} more errors were not logged");
        if (verdict == ErrorVerdict.GiveUp)
        {
            // The same error over and over (e.g. on every controller report): quit cleanly, which shows the controllers again.
            s_gaveUp = true;
            Log.Error($"{where}: {GiveUpCount} errors in a row, quitting", ex);
            PostQuitMessage(1);
            return;
        }
        bool first = verdict == ErrorVerdict.Log;
        if (first) Log.Error(where, ex);
        try { Recover?.Invoke(first); }
        catch (Exception recoverError) { if (first) Log.Error("Recovering from the error failed", recoverError); }
    }

    /// <summary>
    /// Starts a background thread whose exceptions are logged instead of ending the process. <paramref name="onFailure"/>
    /// runs after the log line, typically to post the "done" message the UI thread waits for.
    /// </summary>
    public static void StartWorker(string name, Action body, Action? onFailure = null) =>
        new Thread(() =>
        {
            try { body(); }
            catch (Exception ex)
            {
                Log.Error($"Thread {name}", ex);
                try { onFailure?.Invoke(); }
                catch (Exception failureError) { Log.Error($"Thread {name}: failure handler", failureError); }
            }
        }) { IsBackground = true, Name = name }.Start();
}

internal enum ErrorVerdict { Log, Quiet, GiveUp }

/// <summary>
/// Rate limit for errors: the first one of a burst is logged in full, the next ones only counted, and too many within
/// one window means the app is stuck in a loop of errors.
/// </summary>
internal sealed class ErrorBudget(int giveUpCount, long windowMs)
{
    private long _windowStart;
    private int _count;

    /// <param name="now">Milliseconds, any monotonic origin.</param>
    /// <param name="suppressed">When a new burst starts: how many errors of the previous one were not logged.</param>
    public ErrorVerdict Record(long now, out int suppressed)
    {
        suppressed = 0;
        if (_count == 0 || now - _windowStart >= windowMs)
        {
            suppressed = Math.Max(0, _count - 1);
            _windowStart = now;
            _count = 1;
            return ErrorVerdict.Log;
        }
        return ++_count >= giveUpCount ? ErrorVerdict.GiveUp : ErrorVerdict.Quiet;
    }
}
