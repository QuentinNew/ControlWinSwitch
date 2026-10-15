using ControlWin.Input;
using ControlWin.UI;
using static ControlWin.Native.Win32;

namespace ControlWin.App;

/// <summary>
/// The brain: decides for every controller report whether it is consumed by the app (menu open) or
/// forwarded (idle). Forwarding is a no-op until the virtual pad exists (F9).
/// </summary>
internal sealed class AppController
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
        if (_mode == Mode.Menu) CloseMenu();
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
        _menu.Reset(MenuPages.Root());
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

            default:
                // Implemented by F3-F7.
                _toast.Show(entry.Glyph, $"{entry.Label}: not implemented yet");
                break;
        }
    }

    /// <summary>The state is not consumed: this is where the F9 virtual pad gets its input.</summary>
    private void Forward()
    {
    }
}
