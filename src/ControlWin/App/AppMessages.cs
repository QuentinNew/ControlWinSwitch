using static ControlWin.Native.Win32;

namespace ControlWin.App;

/// <summary>Registered window messages shared between instances (the sender may be non-elevated, the receiver elevated).</summary>
internal static class AppMessages
{
    public static readonly uint ShowMenu = RegisterWindowMessageW("ControlWin.ShowMenu");
    public static readonly uint Quit = RegisterWindowMessageW("ControlWin.Quit");
    public static readonly uint TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

    /// <summary>Posted by the headset worker thread: wParam = <c>HeadsetState</c>, lParam = 1 when it was a connect/disconnect.</summary>
    public const uint HeadsetDone = WM_APP + 2;

    /// <summary>Posted by the power worker thread when sleep / restart / shut down was refused.</summary>
    public const uint PowerFailed = WM_APP + 3;

    /// <summary>Posted by the pairing worker thread: wParam = <c>PairResult</c>.</summary>
    public const uint PairDone = WM_APP + 4;

    /// <summary>Posts <paramref name="message"/> to the running instance. False when none is running.</summary>
    public static bool PostToRunningInstance(uint message)
    {
        var hwnd = FindWindowW(MainWindow.ClassName, null);
        return hwnd != 0 && PostMessageW(hwnd, message, 0, 0) != 0;
    }

    /// <summary>--quit: asks the running instance to exit and waits for its window to go away. Returns an exit code.</summary>
    public static int QuitRunningInstance(int waitMs = 5000)
    {
        if (!PostToRunningInstance(Quit)) return 0; // nothing running: nothing to quit
        for (int waited = 0; waited < waitMs; waited += 50)
        {
            if (FindWindowW(MainWindow.ClassName, null) == 0) return 0;
            Thread.Sleep(50);
        }
        return 1;
    }
}
