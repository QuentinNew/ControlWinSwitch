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
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
            return AppMessages.QuitRunningInstance();

        // Single instance: the mutex is held until the process exits. A second launch opens the menu of the first.
        // ACCESS_DENIED means the first instance is elevated and this one is not.
        nint mutex = CreateMutexW(0, 1, "Local\\ControlWin.SingleInstance");
        int mutexError = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        if (mutex == 0 || mutexError == ERROR_ALREADY_EXISTS || mutexError == ERROR_ACCESS_DENIED)
        {
            AppMessages.PostToRunningInstance(AppMessages.ShowMenu);
            return 0;
        }

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
            window.UpdateTray();

            RunMessageLoop(reader, controller);
            controller.Shutdown(); // no-op when WM_CLOSE already did it
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
    private static void RunMessageLoop(HidControllerReader reader, AppController controller)
    {
        // Wait set: controller reports, the mouse timer (signalled only while a stick moves the cursor), and
        // the watched process (signalled when it exits). The last one changes at run time, so it is re-read.
        nint* handles = stackalloc nint[3];
        MSG msg;
        while (true)
        {
            uint count = 0;
            handles[count++] = reader.EventHandle;
            uint mouseIndex = uint.MaxValue, watchIndex = uint.MaxValue;
            if (controller.MouseTimerHandle != 0)
            {
                mouseIndex = count;
                handles[count++] = controller.MouseTimerHandle;
            }
            if (controller.WatchHandle != 0)
            {
                watchIndex = count;
                handles[count++] = controller.WatchHandle;
            }

            uint r = MsgWaitForMultipleObjectsEx(count, handles, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (r == uint.MaxValue) return; // WAIT_FAILED
            if (r == WAIT_OBJECT_0) reader.OnSignaled();
            else if (r == WAIT_OBJECT_0 + mouseIndex) controller.OnMouseTick();
            else if (r == WAIT_OBJECT_0 + watchIndex) controller.OnWatchedProcessExited();

            while (PeekMessageW(&msg, 0, 0, 0, PM_REMOVE) != 0)
            {
                if (msg.message == WM_QUIT) return;
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
    }
}
