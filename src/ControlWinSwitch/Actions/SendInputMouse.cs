using ControlWinSwitch.App;
using ControlWinSwitch.Native;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Actions;

/// <summary>Real mouse output: SendInput, the on-screen keyboard, and the high-resolution tick timer.</summary>
internal sealed unsafe class SendInputMouse : IMouseOutput, IDisposable
{
    private readonly HighResTimer _timer = new();

    public nint TimerHandle => _timer.Handle;

    public void SetTimer(bool running)
    {
        if (running) _timer.Start(MouseDefaults.TickMs);
        else _timer.Stop();
    }

    public void Move(int dx, int dy) => Send(MOUSEEVENTF_MOVE, 0, dx, dy);

    public void Button(bool right, bool down) => Send(
        right ? (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP)
              : (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP), 0);

    public void Wheel(int amount) => Send(MOUSEEVENTF_WHEEL, (uint)amount);

    public void HWheel(int amount) => Send(MOUSEEVENTF_HWHEEL, (uint)amount);

    private static void Send(uint flags, uint data, int dx = 0, int dy = 0)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags },
        };
        if (SendInput(1, &input, sizeof(INPUT)) == 0)
            Log.Error($"SendInput failed ({System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
    }

    public void Dispose() => _timer.Dispose();
}
