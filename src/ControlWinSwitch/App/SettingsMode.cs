using ControlWinSwitch.Actions;
using ControlWinSwitch.Config;
using ControlWinSwitch.Input;
using ControlWinSwitch.UI;

namespace ControlWinSwitch.App;

/// <summary>
/// The settings window: tabs (L / R), rows (D-pad / stick), A activates, X the alternate action, B goes back. Also the
/// pages of one controller (ZL / ZR switch controller) and the deadzone calibration. What the rows do goes through
/// <see cref="SettingsHost"/>.
/// </summary>
internal sealed class SettingsMode : IMode
{
    private readonly IModeHost _host;
    private readonly DirectionRepeat _direction;
    private readonly SettingsWindow _window = new();
    private readonly SettingsModel _model;
    private readonly SettingsHost _settingsHost;
    private readonly ControllerRegistry _registry;
    private SettingItem? _pageStrip; // the strip row of the open controller page: its Slot is the controller being edited
    private long _lastCalibrationPaint;

    /// <param name="createHost">Builds the host of the settings rows; it gets this mode for the page navigation.</param>
    public SettingsMode(IModeHost host, DirectionRepeat direction, ConfigStore store, ControllerRegistry registry,
        Func<SettingsMode, SettingsHost> createHost)
    {
        _host = host;
        _direction = direction;
        _registry = registry;
        _settingsHost = createHost(this);
        _model = SettingsPages.Create(store, _settingsHost);

        _window.Tile = registry.TileOf;
        _window.PairedTile = registry.PairedTileOf;
        _window.PictureEditRequested += (slot, second) =>
        {
            _settingsHost.ChooseControllerPicture(slot, second);
            _window.Refresh();
        };
        _window.PictureRemoveRequested += (slot, second) =>
        {
            _settingsHost.RemoveControllerPicture(slot, second);
            _window.Refresh();
        };
        _window.PairedPictureEditRequested += key =>
        {
            _settingsHost.ChoosePicture(key);
            _window.Refresh();
        };
        _window.PairedPictureRemoveRequested += key =>
        {
            ControllerPictures.Remove(key);
            _window.Refresh();
        };
        _window.CloseRequested += CloseIfOpen;
    }

    private bool IsOpen => _host.IsOpen(this);

    private ControllerState State => _host.State;

    /// <summary>The owner of the file pickers the settings open.</summary>
    public nint WindowHandle => _window.Handle;

    /// <summary>The pad is already neutral.</summary>
    public void Open()
    {
        _direction.Held = _direction.Read(State, horizontal: true);
        _window.Show(_model);
        Log.Info("Settings opened");
    }

    public void Exit(bool announce)
    {
        _window.Hide();
        Log.Info("Settings closed");
    }

    public void CloseIfOpen()
    {
        if (IsOpen) _host.ToIdle();
    }

    /// <summary>Repaints the window when it is open.</summary>
    public void Refresh()
    {
        if (IsOpen) _window.Refresh();
    }

    /// <summary>Starts inline text editing; <paramref name="commit"/> receives the new text on Enter / A.</summary>
    public void BeginEdit(string current, Action<string> commit) => _window.BeginEdit(current, commit);

    /// <summary>Opens Windows' Bluetooth settings; the settings close first (topmost, they would hide them).</summary>
    public void OpenBluetoothSettings()
    {
        CloseIfOpen();
        BluetoothHeadset.OpenBluetoothSettings();
    }

    /// <summary>A report of any controller: the deadzone calibration follows the sticks of the controller it is for.</summary>
    public void OnAnyReport(int slot)
    {
        if (!IsOpen || _model.Tab.Calibration is not { } calibration || calibration.Slot != slot) return;
        calibration.Feed();
        long now = Environment.TickCount64;
        if (now - _lastCalibrationPaint >= 30) // the sticks report faster than the screen needs
        {
            _lastCalibrationPaint = now;
            _window.Refresh();
        }
    }

    public void OnReport()
    {
        if (_window.IsEditing)
        {
            // Typing happens on the keyboard; the pad only confirms or cancels.
            if (State.Pressed(Buttons.A)) _window.EndEdit(commit: true);
            else if (State.Pressed(Buttons.B)) _window.EndEdit(commit: false);
            return;
        }

        // The sticks are being looked at during the calibration, not used.
        _direction.Track(_direction.Read(State, horizontal: true, useStick: _model.Tab.Calibration is null), Move, Repeat);

        // On a controller's page ZL / ZR edit the previous / next connected controller.
        if (_model.InSubPage && _pageStrip is { } controllers)
        {
            int step = State.Pressed(Buttons.ZR) ? 1 : State.Pressed(Buttons.ZL) ? -1 : 0;
            if (step != 0)
            {
                controllers.Adjust?.Invoke(step);
                _window.Refresh();
            }
        }

        if (State.Pressed(Buttons.L))
        {
            _model.MoveTab(-1);
            _window.Refresh();
        }
        if (State.Pressed(Buttons.R))
        {
            _model.MoveTab(1);
            _window.Refresh();
        }

        if (_model.Tab.Calibration is { } calibration)
        {
            if (State.Pressed(Buttons.X)) calibration.StartAuto();
            else if (State.Pressed(Buttons.A) || State.Pressed(Buttons.B)) _model.Back();
            _window.Refresh();
            return;
        }

        if (State.Pressed(Buttons.A))
        {
            _model.Activate();
            Refresh();
        }
        else if (State.Pressed(Buttons.X))
        {
            _model.Alternate();
            Refresh();
        }
        else if (State.Pressed(Buttons.B))
        {
            if (_model.InSubPage) CloseControllerPage();
            else _host.ToIdle();
        }
    }

    /// <summary>Holding left / right must not flip a toggle over and over: only numbers repeat.</summary>
    private bool Repeat(Dir direction)
    {
        if (direction is Dir.Left or Dir.Right && _model.CurrentItem?.Kind != SettingKind.Number) return false;
        Move(direction);
        return true;
    }

    private void Move(Dir direction)
    {
        if (_model.Tab.Calibration is { } calibration)
        {
            switch (direction)
            {
                case Dir.Up: calibration.Adjust(1); break;
                case Dir.Down: calibration.Adjust(-1); break;
                case Dir.Left: calibration.MoveStick(-1); break;
                case Dir.Right: calibration.MoveStick(1); break;
            }
            _window.Refresh();
            return;
        }
        switch (direction)
        {
            case Dir.Up: _model.Move(-1); break;
            case Dir.Down: _model.Move(1); break;
            case Dir.Left: _model.Adjust(-1); break;
            case Dir.Right: _model.Adjust(1); break;
        }
        Refresh();
    }

    // ---- controller pages ------------------------------------------------------------------------

    /// <summary>Opens the page of the controller in <paramref name="slot"/> (name, picture, rumble test).</summary>
    public void OpenControllerPage(int slot)
    {
        if (!_settingsHost.ControllerConnected(slot)) return;
        _model.OpenSub(SettingsPages.ControllerPage(_settingsHost, slot, out _pageStrip));
    }

    /// <summary>Opens the page of a paired (not connected) controller.</summary>
    public void OpenPairedPage(string key)
    {
        if (!_settingsHost.PairedControllers.Contains(key)) return;
        _model.OpenSub(SettingsPages.PairedPage(_settingsHost, key, out _pageStrip));
    }

    /// <summary>Opens the deadzone calibration of the controller page being edited.</summary>
    public void OpenDeadzoneCalibration()
    {
        if (_pageStrip is { } strip) _model.OpenSub(SettingsPages.DeadzonePage(_settingsHost, strip));
    }

    public void CloseControllerPage()
    {
        _pageStrip = null;
        _model.Back();
        Refresh();
    }

    /// <summary>The controller in <paramref name="slot"/> went away: its page closes.</summary>
    public void OnControllerRemoved(int slot)
    {
        if (_model.InSubPage && _pageStrip is { Kind: SettingKind.Strip } strip && strip.Slot == slot) CloseControllerPage();
    }

    /// <summary>A controller was forgotten: closes its own page if it is open.</summary>
    public void OnForgotten(string key)
    {
        if (_model.InSubPage && _pageStrip is { Kind: SettingKind.Paired } page && page.Key == key) CloseControllerPage();
        Refresh();
    }

    /// <summary>Controllers connected or went away (<paramref name="joined"/>: the MACs that connected).</summary>
    public void OnKnownChanged(List<string> joined)
    {
        // The paired controller being edited connected: edit it as a connected controller.
        if (_model.InSubPage && _pageStrip is { Kind: SettingKind.Paired } page && page.Key is { } key && joined.Contains(key))
        {
            int slot = _registry.SlotOf(key);
            CloseControllerPage();
            if (slot >= 0) OpenControllerPage(slot);
        }
        else ClosePairedPageIfEmpty();
        Refresh();
    }

    /// <summary>The paired page has nothing left to show: back to the Controller tab.</summary>
    private void ClosePairedPageIfEmpty()
    {
        if (_model.InSubPage && _pageStrip is { Kind: SettingKind.Paired } && _settingsHost.PairedControllers.Count == 0)
            CloseControllerPage();
    }
}
