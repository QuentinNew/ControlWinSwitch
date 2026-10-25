using ControlWinSwitch.Actions;
using ControlWinSwitch.Config;
using ControlWinSwitch.UI;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.App;

/// <summary>What a menu command needs from the app around it: the commands that open another screen or mode.</summary>
internal interface ICommandHost
{
    void OpenSettings();

    void EnterMouse(char glyph);

    void EnterSlider(MenuEntry entry, bool volume);

    void TogglePad(char glyph);

    /// <summary>The PC is going to sleep or off: mouse mode ends and nothing stays pressed.</summary>
    void BeforePowerAction();
}

/// <summary>
/// Runs the command of a menu node once the menu is closed. The slow ones (power, saving a screenshot) run on a worker
/// thread and post their outcome back (<see cref="AppMessages.PowerFailed"/>, <see cref="AppMessages.ScreenshotDone"/>).
/// </summary>
internal sealed class CommandRunner(ICommandHost host, nint hwnd, Toast toast, PadService pads, HeadsetService headset,
    PairingService pairing, Func<bool> shutDown)
{
    private const char ErrorGlyph = '';

    private bool _screenshotBusy;
    private char _screenshotGlyph;
    private volatile string? _screenshotResult; // written by the screenshot thread, read after ScreenshotDone

    public void Execute(MenuEntry entry, MenuCommand command)
    {
        Log.Info($"Menu command: {command.Kind} ({entry.Label})");
        switch (command.Kind)
        {
            case CommandKind.Quit:
                PostMessageW(hwnd, WM_CLOSE, 0, 0);
                break;

            case CommandKind.MouseMode:
                host.EnterMouse(entry.Glyph);
                break;

            case CommandKind.OpenSettings:
                host.OpenSettings();
                break;

            case CommandKind.AddController:
                pairing.Start(only: null);
                break;

            case CommandKind.Launch:
                if (entry.Launch is { } launch) Launch(launch);
                break;

            case CommandKind.CloseMenu:
                break; // the menu is already closed

            case CommandKind.CloseWindow:
                CloseWindow(entry);
                break;

            case CommandKind.Screenshot:
                TakeScreenshot(entry);
                break;

            case CommandKind.ChangeScreen:
                ChangeScreen(entry);
                break;

            case CommandKind.Volume:
            case CommandKind.Brightness:
                host.EnterSlider(entry, volume: command.Kind == CommandKind.Volume);
                break;

            case CommandKind.ToggleVirtualPad:
                host.TogglePad(entry.Glyph);
                break;

            case CommandKind.ToggleHeadset:
                headset.Toggle(entry.Glyph);
                break;

            case CommandKind.Power:
                RunPower(entry.PowerMode, entry);
                break;

            default:
                Log.Error($"Unhandled menu command {command.Kind} ({entry.Label})");
                break;
        }
    }

    /// <summary>Mouse mode is off and the pad state cleared before the PC goes away.</summary>
    private void RunPower(PowerAction action, MenuEntry entry)
    {
        host.BeforePowerAction();
        toast.Show(entry.Glyph, action switch
        {
            PowerAction.Sleep => "Sleeping...",
            PowerAction.Restart => "Restarting...",
            _ => "Shutting down...",
        });

        // Sleep blocks until the PC wakes, so it never runs on the UI thread.
        ErrorBoundary.StartWorker("power", () =>
        {
            var error = PowerActions.Run(action);
            if (error is not null)
            {
                Log.Error($"Power {action}: {error}");
                PostMessageW(hwnd, AppMessages.PowerFailed, 0, 0);
            }
        }, onFailure: () => PostMessageW(hwnd, AppMessages.PowerFailed, 0, 0));
    }

    /// <summary>AppMessages.PowerFailed, on the UI thread.</summary>
    public void OnPowerFailed()
    {
        if (!shutDown()) toast.Show(MenuPages.Power, "Power action failed - see log");
    }

    /// <summary>The menu is already hidden: capture now, encode and write on a worker thread.</summary>
    private void TakeScreenshot(MenuEntry entry)
    {
        if (_screenshotBusy)
        {
            toast.Show(entry.Glyph, "Still saving the last screenshot");
            return;
        }
        toast.Hide(); // a toast still on screen would end up in the picture

        var shot = Screenshot.Capture(out var error);
        if (shot is null)
        {
            Log.Error($"Screenshot: {error}");
            toast.Show(entry.Glyph, error ?? "Screenshot failed");
            return;
        }

        _screenshotBusy = true;
        _screenshotGlyph = entry.Glyph;
        string? folder = entry.SaveFolder;
        bool copy = entry.CopyToClipboard;
        ErrorBoundary.StartWorker("screenshot", () =>
        {
            _screenshotResult = Screenshot.Save(shot, folder, copy);
            PostMessageW(hwnd, AppMessages.ScreenshotDone, 0, 0);
        }, onFailure: () => PostMessageW(hwnd, AppMessages.ScreenshotDone, 0, 0)); // clears the busy flag
    }

    /// <summary>AppMessages.ScreenshotDone, on the UI thread.</summary>
    public void OnScreenshotDone()
    {
        _screenshotBusy = false;
        if (!shutDown() && _screenshotResult is { } text) toast.Show(_screenshotGlyph, text);
        _screenshotResult = null;
    }

    /// <summary>The menu is already closed: close the foreground window, or the app the node names.</summary>
    private void CloseWindow(MenuEntry entry)
    {
        string? error;
        string message;
        if (entry.CloseTarget == CloseTarget.ByName)
            error = WindowActions.CloseByName(entry.ProcessName, entry.ForceQuit, out message);
        else
        {
            error = WindowActions.CloseForeground(out string title);
            message = $"Closing {title}";
        }

        if (error is not null) Log.Info($"Close window: {error}");
        toast.Show(error is null ? entry.Glyph : ErrorGlyph, error ?? message);
    }

    /// <summary>The menu is already closed: apply the node's Win+P mode and say how it went.</summary>
    private void ChangeScreen(MenuEntry entry)
    {
        var error = DisplayTopology.Apply(entry.ScreenMode);
        if (error is not null)
        {
            Log.Error($"Change screen ({entry.ScreenMode}): {error}");
            toast.Show(ErrorGlyph, $"Screen change failed: {error}");
            return;
        }
        toast.Show(entry.Glyph, $"Screen: {ScreenModes.Label(entry.ScreenMode)}");
    }

    private void Launch(ShortcutConfig shortcut)
    {
        toast.Show(MenuPages.Play, $"Launching {shortcut.Name}");
        var error = UnelevatedLauncher.Launch(shortcut.Target, shortcut.Args, shortcut.WorkingDir, shortcut.RunAsAdmin == true);
        if (error is not null)
        {
            Log.Error($"Shortcut '{shortcut.Name}': {error}");
            toast.Show(ErrorGlyph, $"{shortcut.Name}: {error}");
            return;
        }

        pads.WatchSoon(); // the launched program may be a watched one
    }
}
