using ControlWinSwitch.Actions;
using ControlWinSwitch.Config;
using ControlWinSwitch.UI;

namespace ControlWinSwitch.App;

/// <summary>Mouse mode: the owner's sticks move the cursor and scroll, A / B click (see <see cref="MouseMode"/>).</summary>
internal sealed class MouseControlMode : IMode, IDisposable
{
    private readonly IModeHost _host;
    private readonly Toast _toast;
    private readonly SendInputMouse _output = new();
    private readonly MouseMode _mouse;
    private char _glyph;

    public MouseControlMode(IModeHost host, Toast toast, Func<MouseConfig> config)
    {
        _host = host;
        _toast = toast;
        _mouse = new MouseMode(() => host.State, _output, config);
    }

    /// <summary>Waitable timer handle for the message loop; signalled only while a stick moves the mouse.</summary>
    public nint TimerHandle => _output.TimerHandle;

    /// <summary>The mouse timer fired.</summary>
    public void OnTick()
    {
        if (_host.IsOpen(this)) _mouse.Tick();
        else _mouse.Deactivate(); // stray tick: make sure the timer is stopped
    }

    /// <summary>The pad is already neutral.</summary>
    public void Enter(char glyph)
    {
        _glyph = glyph;
        _mouse.Activate(_host.State.Current);
        _toast.Show(glyph, "Mouse mode - Capture to exit");
        Log.Info("Mouse mode on");
    }

    public void OnReport() => _mouse.OnReport();

    public void Exit(bool announce)
    {
        _mouse.Deactivate(); // releases left / right buttons
        if (announce) _toast.Show(_glyph, "Mouse mode: off");
        Log.Info("Mouse mode off");
    }

    /// <summary>Releases held mouse buttons and stops the timer (shutdown, recovery).</summary>
    public void Deactivate() => _mouse.Deactivate();

    public void Dispose()
    {
        _mouse.Deactivate();
        _output.Dispose();
    }
}
