using ControlWinSwitch.Actions;
using ControlWinSwitch.Input;
using ControlWinSwitch.UI;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.App;

/// <summary>
/// Pairing a new controller: Windows' Bluetooth pairing for a Pro Controller or an original Joy-Con (on a worker thread,
/// the result posted back as <see cref="AppMessages.PairDone"/>), and the Joy-Con 2 hub's pairing window meanwhile.
/// </summary>
internal sealed class PairingService(ControllerSet set, ControllerRegistry registry, nint hwnd, Toast toast, Func<bool> shutDown)
{
    private const int PairWindowMs = BluetoothPairing.SearchMs + 20_000; // longer than the Pro Controller search: it ends the window

    private bool _active;
    private int _pairedJoyCons;

    /// <summary>A search ended: the Controller tab shows the result.</summary>
    public event Action? Changed;

    /// <summary>The status shown on the Controller tab.</summary>
    public string Status => _active ? "Searching..." : "";

    /// <summary>
    /// Searches for a controller in sync mode: a Pro Controller or an original Joy-Con (<paramref name="only"/>: that one
    /// only, by MAC) and, when any controller is wanted, a Joy-Con 2.
    /// </summary>
    public void Start(string? only)
    {
        if (_active || shutDown()) return;
        _active = true;
        _pairedJoyCons = 0;
        toast.Show(MenuPages.Game, "Searching... Please hold sync button");
        if (only is null) set.Hub.StartPairing(PairWindowMs); // a Joy-Con 2 in sync mode is claimed meanwhile
        ErrorBoundary.StartWorker("pairing", () =>
        {
            var result = BluetoothPairing.PairController(only);
            PostMessageW(hwnd, AppMessages.PairDone, (nuint)(int)result, 0);
        }, onFailure: () => PostMessageW(hwnd, AppMessages.PairDone, (nuint)(int)PairResult.Failed, 0));
    }

    /// <summary>AppMessages.PairDone, on the UI thread.</summary>
    public void OnDone(PairResult result)
    {
        _active = false;
        set.Hub.StopPairing();
        if (shutDown()) return;
        Log.Info($"Pairing: {result}");
        registry.RefreshRemembered();
        if (result != PairResult.NotFound || _pairedJoyCons == 0) // else the Joy-Con toast said it already
        {
            toast.Show(MenuPages.Game, result switch
            {
                PairResult.Paired => "Controller paired",
                PairResult.NotFound => "No new controller found",
                _ => "Pairing failed - try Windows Bluetooth settings",
            });
        }
        Changed?.Invoke();
    }

    /// <summary>A Joy-Con 2 in sync mode was paired with this PC (UI thread).</summary>
    public void OnJoyConPaired(JoyCon2Device device)
    {
        _pairedJoyCons++;
        if (!shutDown()) toast.Show(MenuPages.Game, $"{device.Name} paired");
    }
}
