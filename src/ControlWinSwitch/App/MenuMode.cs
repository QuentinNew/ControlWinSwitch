using ControlWinSwitch.Config;
using ControlWinSwitch.Input;
using ControlWinSwitch.UI;

namespace ControlWinSwitch.App;

/// <summary>
/// The menu opened with Capture, in the layout of the config: a list (D-pad / stick up and down, A runs, B goes back) or a
/// radial menu (a tilt highlights, a quick release or A runs). The mouse works on both.
/// </summary>
internal sealed class MenuMode : IMode
{
    private readonly IModeHost _host;
    private readonly DirectionRepeat _direction;
    private readonly CommandRunner _commands;
    private readonly MenuModel _menu = new();
    private readonly MenuOverlay _overlay = new();
    private readonly RadialModel _radial = new();
    private readonly RadialOverlay _radialOverlay = new();
    private readonly RadialStick _radialStick = new(100);
    private bool _radialShown; // the open menu is the radial one (layout is read when the menu opens)

    public MenuMode(IModeHost host, DirectionRepeat direction, CommandRunner commands)
    {
        _host = host;
        _direction = direction;
        _commands = commands;

        _overlay.RowHovered += OnRowHovered;
        _overlay.RowClicked += OnRowClicked;
        _overlay.BackRequested += () => { if (IsOpen) Back(); };
        _overlay.CloseRequested += () => { if (IsOpen) _host.ToIdle(); };
        _overlay.Scrolled += OnScrolled;
        _overlay.ConfirmAnswered += OnConfirmAnswered;
        _radialOverlay.NodeHovered += OnNodeHovered;
        _radialOverlay.NodeClicked += OnNodeClicked;
        _radialOverlay.BackRequested += () => { if (IsOpen && _radialShown) Back(); };
    }

    private bool IsOpen => _host.IsOpen(this);

    private ControllerState State => _host.State;

    /// <summary>The pad is already neutral. <paramref name="tiles"/>: the controllers shown above the menu (null = none).</summary>
    public void Open(AppConfig config, NodeStatuses statuses, List<SlotTile>? tiles)
    {
        _radialShown = config.MenuLayout == MenuLayouts.Radial;
        if (_radialShown)
        {
            _radial.Reset(MenuTree.ToPage("ControlWinSwitch", config.RadialMenu ?? [], statuses));
            _radialStick.ReleaseMs = config.RadialReleaseMs;
            _radialStick.CardinalBias = config.RadialCardinalBias * MathF.PI / 180f;
            _radialStick.Reset(); // a stick already tilted must come back to the center first
            _direction.Held = Dir.None;
            _radialOverlay.Show(_radial, tiles);
        }
        else
        {
            _menu.Reset(MenuTree.ToPage("ControlWinSwitch", config.ListMenu ?? [], statuses, slotted: false));
            _direction.Held = _direction.Read(State, horizontal: false); // a stick already tilted must be released first, not scroll on open
            _overlay.Show(_menu, tiles);
        }
        Log.Info(_radialShown ? "Menu opened (radial)" : "Menu opened");
    }

    public void Exit(bool announce)
    {
        HideOverlay();
        Log.Info("Menu closed");
    }

    /// <summary>Hides whichever layout is open (also once the settings window opened over it).</summary>
    public void HideOverlay()
    {
        if (_radialShown) _radialOverlay.Hide();
        else _overlay.Hide();
    }

    /// <summary>Hides both layouts (recovery after an error).</summary>
    public void HideAll()
    {
        _overlay.Hide();
        _radialOverlay.Hide();
    }

    /// <summary>Repaints the menu when it is open (a node's status changed).</summary>
    public void Refresh()
    {
        if (!IsOpen) return;
        if (_radialShown) _radialOverlay.Refresh();
        else _overlay.Refresh();
    }

    public void OnReport()
    {
        if (_radialShown)
        {
            OnRadialReport();
            return;
        }

        _direction.Track(_direction.Read(State, horizontal: false), Move, direction =>
        {
            Move(direction);
            return true;
        });

        if (State.Pressed(Buttons.A)) Accept();
        else if (State.Pressed(Buttons.B)) Back();
    }

    private void Move(Dir direction)
    {
        if (direction is not (Dir.Up or Dir.Down)) return;
        _menu.Move(direction == Dir.Up ? -1 : 1);
        _overlay.Refresh();
    }

    private void Accept()
    {
        var entry = _menu.CurrentEntry;
        var command = _menu.Accept();
        if (command is { Kind: CommandKind.OpenSettings } && entry is not null)
        {
            // Straight from the menu to the settings: no close-then-open flicker.
            Log.Info($"Menu command: {CommandKind.OpenSettings} ({entry.Label})");
            _host.OpenSettings();
        }
        else if (command is { } c && entry is not null)
        {
            _host.ToIdle();
            _commands.Execute(entry, c);
        }
        else
        {
            _overlay.Refresh();
        }
    }

    private void Back()
    {
        if (_radialShown)
        {
            if (_radial.Back())
            {
                _radialStick.Reset();
                _radialOverlay.Refresh();
            }
            else _host.ToIdle();
            return;
        }

        if (_menu.Back()) _overlay.Refresh();
        else _host.ToIdle();
    }

    // ---- radial layout -------------------------------------------------------------------------

    private void OnRadialReport()
    {
        // Left stick: tilt highlights, a quick release runs the highlighted node.
        int before = _radialStick.Selected;
        int released = _radialStick.Update(State.Left, Environment.TickCount64, _radial.Ring);
        if (released >= 0)
        {
            _radial.Select(released);
            AcceptRadial();
            return;
        }

        // Only a change made by the stick touches the highlight, so a D-pad selection survives a resting stick.
        bool changed = _radialStick.Selected != before && _radial.Select(_radialStick.Selected);

        if (State.Pressed(Buttons.Left)) { _radial.Step(-1); changed = true; }
        else if (State.Pressed(Buttons.Right)) { _radial.Step(1); changed = true; }

        if (State.Pressed(Buttons.A))
        {
            AcceptRadial();
            return;
        }
        if (State.Pressed(Buttons.B))
        {
            Back();
            return;
        }

        if (changed) _radialOverlay.Refresh();
    }

    private void AcceptRadial()
    {
        var node = _radial.CurrentNode;
        var result = _radial.Activate();
        if (result is { } run)
        {
            if (run.Command.Kind == CommandKind.OpenSettings)
            {
                // Straight from the menu to the settings: no close-then-open flicker.
                Log.Info($"Menu command: {CommandKind.OpenSettings} ({run.Entry.Label})");
                _host.OpenSettings();
            }
            else
            {
                _host.ToIdle();
                _commands.Execute(run.Entry, run.Command);
            }
            return;
        }

        if (node is not null) _radialStick.Reset(); // the ring changed under the stick: start from the center again
        _radialOverlay.Refresh();
    }

    // ---- keyboard --------------------------------------------------------------------------------

    /// <summary>Arrow keys move the highlight, Enter runs it, Escape goes back.</summary>
    public void OnKey(uint key)
    {
        if (!IsOpen) return;
        switch (key)
        {
            case Native.Win32.VK_ESCAPE:
                Back();
                return;
            case Native.Win32.VK_RETURN:
                if (_radialShown)
                {
                    if (_radial.CurrentNode is not null) AcceptRadial();
                }
                else Accept();
                return;
        }

        int delta = key is Native.Win32.VK_UP or Native.Win32.VK_LEFT ? -1 : 1;
        if (_radialShown)
        {
            _radial.Step(delta);
            _radialOverlay.Refresh();
        }
        else if (key is Native.Win32.VK_UP or Native.Win32.VK_DOWN) Move(delta < 0 ? Dir.Up : Dir.Down);
    }

    // ---- mouse -----------------------------------------------------------------------------------

    private void OnRowHovered(int index)
    {
        if (IsOpen && _menu.Select(index)) _overlay.Refresh();
    }

    private void OnRowClicked(int index)
    {
        if (!IsOpen || _menu.IsConfirming) return;
        var entries = _menu.Page.Entries;
        if (index < 0 || index >= entries.Count || !entries[index].Enabled) return;
        _menu.Select(index);
        Accept();
    }

    private void OnScrolled(int direction)
    {
        if (!IsOpen) return;
        _menu.Move(direction);
        _overlay.Refresh();
    }

    private void OnConfirmAnswered(bool yes)
    {
        if (!IsOpen || !_menu.IsConfirming) return;
        if (yes) Accept();
        else Back();
    }

    private void OnNodeHovered(int index)
    {
        if (IsOpen && _radialShown && _radial.Select(index)) _radialOverlay.Refresh();
    }

    private void OnNodeClicked(int index)
    {
        if (!IsOpen || !_radialShown) return;
        _radial.Select(index);
        if (_radial.CurrentNode is not null) AcceptRadial();
    }
}
