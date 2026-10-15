using ControlWin.Actions;
using ControlWin.Input;
using ControlWin.UI;
using static ControlWin.Native.Win32;

namespace ControlWin.App;

/// <summary>
/// The brain: decides for every controller report whether it is consumed by the app (menu open) or
/// forwarded (idle). Forwarding is a no-op until the virtual pad exists (F9).
/// </summary>
internal sealed class AppController : IDisposable
{
    private const nuint RepeatTimerId = 1;
    private const uint RepeatFirstMs = 400, RepeatNextMs = 110;
    private const float StickEngage = 0.6f, StickRelease = 0.4f;

    private enum Mode { Idle, Menu }

    private readonly ControllerState _state;
    private readonly MainWindow _window;
    private readonly MenuModel _menu = new();
    private readonly MenuOverlay _overlay = new();
    private readonly Toast _toast = new();
    private readonly VirtualPad _pad = new();

    private bool _padEnabled = VirtualPadDefaults.Enabled;
    private bool _padFailed;
    private Buttons _ignoreMask; // buttons still held from the menu: hidden from the game until released
    private bool _ignoreSticks;  // sticks used in the menu: hidden until back near center

    private Mode _mode;
    private int _heldDirection; // -1 up, 0 none, +1 down
    private bool _repeatTimerArmed;

    public AppController(ControllerState state, MainWindow window)
    {
        _state = state;
        _window = window;
    }

    /// <summary>Report handler: updates the shared state first, so later subscribers (the debug printer) see it.</summary>
    public void OnReport(ProReport report, ReadOnlyMemory<byte> raw)
    {
        _state.Update(report);

        // Capture is reserved for the menu toggle and is never forwarded.
        if (_state.Pressed(Buttons.Capture))
        {
            if (_mode == Mode.Menu) CloseMenu();
            else OpenMenu();
            return;
        }

        switch (_mode)
        {
            case Mode.Menu:
                HandleMenuInput();
                break;
            default:
                Forward();
                break;
        }
    }

    public void OnControllerRemoved()
    {
        _state.Reset();
        _pad.Detach(); // re-plugged on the next report
        if (_mode == Mode.Menu) CloseMenu();
        _ignoreMask = Buttons.None;
        _ignoreSticks = false;
    }

    /// <summary>WM_TIMER of the main window.</summary>
    public void OnTimer(nuint id)
    {
        if (id != RepeatTimerId) return;
        if (_mode != Mode.Menu || _heldDirection == 0)
        {
            StopRepeat();
            return;
        }

        _menu.Move(_heldDirection);
        _overlay.Refresh();
        SetTimer(_window.Handle, RepeatTimerId, RepeatNextMs, 0); // same id: replaces the timer with the faster interval
    }

    private void OpenMenu()
    {
        _menu.Reset(MenuPages.Root(PadDetail));
        _pad.Submit(default); // neutral pad while the menu owns the input
        _heldDirection = ReadDirection(); // a stick already tilted must be released first, not scroll on open
        _mode = Mode.Menu;
        _overlay.Show(_menu);
        Log.Info("Menu opened");
    }

    private void CloseMenu()
    {
        StopRepeat();
        _heldDirection = 0;
        _mode = Mode.Idle;
        _ignoreMask = _state.Current;
        _ignoreSticks = true;
        _overlay.Hide();
        Log.Info("Menu closed");
    }

    private void HandleMenuInput()
    {
        int direction = ReadDirection();
        if (direction != _heldDirection)
        {
            _heldDirection = direction;
            StopRepeat();
            if (direction != 0)
            {
                _menu.Move(direction);
                _overlay.Refresh();
                _repeatTimerArmed = true;
                SetTimer(_window.Handle, RepeatTimerId, RepeatFirstMs, 0);
            }
        }

        if (_state.Pressed(Buttons.A))
        {
            var entry = _menu.CurrentEntry;
            var command = _menu.Accept();
            if (command is { } c && entry is not null)
            {
                CloseMenu();
                Execute(entry, c);
            }
            else
            {
                _overlay.Refresh();
            }
        }
        else if (_state.Pressed(Buttons.B))
        {
            if (_menu.Back()) _overlay.Refresh();
            else CloseMenu();
        }
    }

    /// <summary>Up/down from the D-pad or the left stick (with hysteresis so a wobbling stick doesn't flicker).</summary>
    private int ReadDirection()
    {
        var held = _state.Current;
        if ((held & Buttons.Up) != 0) return -1;
        if ((held & Buttons.Down) != 0) return 1;

        float threshold = _heldDirection != 0 ? StickRelease : StickEngage;
        float y = _state.Left.Y; // +Y = down
        if (y <= -threshold) return -1;
        if (y >= threshold) return 1;
        return 0;
    }

    private void StopRepeat()
    {
        if (!_repeatTimerArmed) return;
        KillTimer(_window.Handle, RepeatTimerId);
        _repeatTimerArmed = false;
    }

    private void Execute(MenuEntry entry, MenuCommand command)
    {
        Log.Info($"Menu command: {command.Kind} ({entry.Label})");
        switch (command.Kind)
        {
            case CommandKind.Quit:
                PostMessageW(_window.Handle, WM_CLOSE, 0, 0);
                break;

            case CommandKind.ToggleVirtualPad:
                _padEnabled = !_padEnabled;
                _padFailed = false;
                if (!_padEnabled) _pad.Detach();
                else if (!_pad.Attach()) _padFailed = true;
                _toast.Show(entry.Glyph, _padFailed ? "Virtual pad unavailable (ViGEmBus?)"
                    : _padEnabled ? "Virtual pad: on" : "Virtual pad: off");
                break;

            default:
                // Implemented by F3-F7.
                _toast.Show(entry.Glyph, $"{entry.Label}: not implemented yet");
                break;
        }
    }

    private string? PadDetail() => !_padEnabled ? "Off" : _padFailed ? "Unavailable" : "On";

    /// <summary>The state is not consumed: it drives the virtual pad.</summary>
    private void Forward()
    {
        if (!_padEnabled || _padFailed) return;
        if (!_pad.IsAttached && !_pad.Attach())
        {
            _padFailed = true;
            _toast.Show('', "Virtual pad unavailable (ViGEmBus?)");
            return;
        }

        _ignoreMask &= _state.Current;
        var left = _state.Left;
        var right = _state.Right;
        if (_ignoreSticks)
        {
            if (left.Magnitude < 0.3f && right.Magnitude < 0.3f) _ignoreSticks = false;
            else left = right = default;
        }

        var held = _state.Current & ~_ignoreMask;
        _pad.Submit(XInputMapper.Map(held, left, right, VirtualPadDefaults.SwapAB, VirtualPadDefaults.SwapXY));
    }

    public void Dispose() => _pad.Dispose();
}

/// <summary>Code defaults until the config file (F4) provides them.</summary>
internal static class VirtualPadDefaults
{
    public const bool Enabled = true, SwapAB = true, SwapXY = true; // label-based: Switch A = Xbox A, B = B, X = X, Y = Y
}
