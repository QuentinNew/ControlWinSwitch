using ControlWinSwitch.Config;
using ControlWinSwitch.UI;

namespace ControlWinSwitch.App;

/// <summary>The menu editor window (a mouse window: the controller only closes it, with Capture, without saving).</summary>
internal sealed class EditorMode : IMode
{
    private readonly IModeHost _host;
    private readonly ConfigStore _store;
    private readonly Toast _toast;
    private readonly Action _saveConfig;
    private readonly MenuEditorWindow _window = new();

    /// <param name="saveConfig">Saves the config and applies it.</param>
    public EditorMode(IModeHost host, ConfigStore store, Toast toast, Action saveConfig)
    {
        _host = host;
        _store = store;
        _toast = toast;
        _saveConfig = saveConfig;
        _window.Saved += OnSaved;
        _window.Cancelled += () => { if (_host.IsOpen(this)) _host.ToIdle(); };
    }

    /// <summary>The pad is already neutral.</summary>
    public void Open(string layout)
    {
        var config = _store.Current;
        _window.Show(layout, config.RadialMenu ?? [], config.ListMenu ?? []);
        Log.Info($"Menu editor opened ({layout})");
    }

    public void OnReport() { } // the editor is driven by the mouse

    public void Exit(bool announce)
    {
        _window.Hide();
        Log.Info("Menu editor closed");
    }

    private void OnSaved(string layout, List<MenuNodeConfig> tree)
    {
        if (!_host.IsOpen(this)) return;
        var config = _store.Current;
        if (layout == MenuLayouts.Radial) config.RadialMenu = tree;
        else config.ListMenu = tree;
        config.Sanitize();
        _saveConfig();
        _host.ToIdle();
        _toast.Show(MenuPages.Gear, layout == MenuLayouts.Radial ? "Radial menu saved" : "List menu saved");
    }
}
