using ControlWin.App;
using ControlWin.Input;
using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin;

internal static unsafe class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool debugInput = args.Contains("--debug-input", StringComparer.OrdinalIgnoreCase);

        if (debugInput)
        {
            AllocConsole();
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Log.ConsoleEnabled = true;
            Console.WriteLine("ControlWin --debug-input: press buttons / move sticks on the Pro Controller. Ctrl+C to quit.");
        }

        try
        {
            using var reader = new HidControllerReader();
            var state = new ControllerState();
            var window = new MainWindow(reader);
            var controller = new AppController(state, window);
            window.Controller = controller;
            Log.Info($"Started (elevated={IsElevated()}, hwnd=0x{window.Handle:X})");

            // The controller updates the shared state before any later subscriber (the debug printer) reads it.
            reader.Report += controller.OnReport;
            reader.ControllerRemoved += controller.OnControllerRemoved;

            if (debugInput)
            {
                var printer = new DebugInputPrinter(state);
                reader.Report += printer.OnReport;
                reader.UnknownReport += printer.OnUnknownReport;
            }

            reader.TryOpen();
            if (!reader.IsOpen) Log.Info("Pro Controller not connected yet (waiting for it)");

            RunMessageLoop(reader);
            controller.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Fatal", ex);
            return 1;
        }
    }

    /// <summary>Event-driven loop: blocks until a message or a controller report arrives (~0% CPU idle).</summary>
    private static void RunMessageLoop(HidControllerReader reader)
    {
        nint handle = reader.EventHandle; // the mouse timer handle joins it in F3
        MSG msg;
        while (true)
        {
            uint r = MsgWaitForMultipleObjectsEx(1, &handle, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (r == uint.MaxValue) return; // WAIT_FAILED
            if (r == WAIT_OBJECT_0) reader.OnSignaled();

            while (PeekMessageW(&msg, 0, 0, 0, PM_REMOVE) != 0)
            {
                if (msg.message == WM_QUIT) return;
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
    }
}
