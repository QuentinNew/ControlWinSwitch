using ControlWinSwitch.App;
using ControlWinSwitch.Input;
using ControlWinSwitch.Native;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch;

internal static unsafe class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
            return AppMessages.QuitRunningInstance();

        // Single instance: the mutex is held until the process exits. A second launch opens the menu of the first.
        // ACCESS_DENIED means the first instance is elevated and this one is not.
        nint mutex = CreateMutexW(0, 1, "Local\\ControlWinSwitch.SingleInstance");
        int mutexError = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        if (mutex == 0 || mutexError == ERROR_ALREADY_EXISTS || mutexError == ERROR_ACCESS_DENIED)
        {
            AppMessages.PostToRunningInstance(AppMessages.ShowMenu);
            return 0;
        }

        AppController? controller = null;
        try
        {
            using var set = new ControllerSet();
            var window = new MainWindow(set);
            controller = new AppController(set, window);
            window.Controller = controller;
            Log.Info($"Started {typeof(Program).Assembly.GetName().Version?.ToString(3)} (elevated={IsElevated()}, hwnd=0x{window.Handle:X})");

            var app = controller;
            ErrorBoundary.Recover = app.RecoverToIdle;
            // Any thread not started through ErrorBoundary.StartWorker (thread-pool and WinRT callbacks): the process is
            // about to end, so only show the hidden controllers again.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Log.Error($"Fatal on thread '{Thread.CurrentThread.Name ?? Environment.CurrentManagedThreadId.ToString()}'",
                    e.ExceptionObject as Exception);
                try { app.ShowControllersAgain(); }
                catch (Exception ex) { Log.Error("Showing the controllers again failed", ex); }
            };

            set.Report += controller.OnReport;
            set.CalibrationRead += controller.OnCalibration;
            set.ControllerRemoved += controller.OnControllerRemoved;
            set.JoyConsChanged += controller.OnJoyConsChanged;
            set.ControllerIdentified += controller.OnControllerIdentified;
            set.Hub.DevicePaired += controller.OnJoyConPaired;

            set.TryOpen();
            if (!set.AnyOpen) Log.Info("No controller connected yet (waiting for one)");
            window.UpdateTray();

            RunMessageLoop(set, controller);
            controller.Shutdown(); // no-op when WM_CLOSE already did it
            controller.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Fatal", ex);
            try { controller?.Shutdown(); } // shows the controllers again, unplugs the pads
            catch (Exception shutdownError) { Log.Error("Shutdown after the fatal error failed", shutdownError); }
            return 1;
        }
    }

    /// <summary>Event-driven loop: blocks until a message or a controller report arrives (~0% CPU idle).</summary>
    private static void RunMessageLoop(ControllerSet set, AppController controller)
    {
        // Wait set: the HID readers' reports (Pro Controllers, original Joy-Cons) and their writes in flight, the Joy-Con 2
        // queue, the virtual pads' rumble requests, the mouse timer (signalled only while a stick moves the cursor) and the
        // watched process (signalled when it exits). Writes, pads, mouse and process change at run time, so the set is
        // rebuilt on every iteration.
        const int Slots = ControllerSet.Slots, Readers = ControllerSet.Readers, WriteBase = Readers, PadBase = 2 * Readers,
            MouseId = PadBase + Slots, WatchId = MouseId + 1, JoyConId = MouseId + 2;
        nint* handles = stackalloc nint[JoyConId + 1];
        int* ids = stackalloc int[JoyConId + 1]; // reads, writes, pad rumble, then mouse, watch, Joy-Con 2s
        MSG msg;
        while (true)
        {
            uint count = 0;
            for (int i = 0; i < Readers; i++)
            {
                ids[count] = i;
                handles[count++] = set.HidEventHandle(i);
            }
            for (int i = 0; i < Readers; i++)
            {
                nint write = set.HidWriteHandle(i);
                if (write == 0) continue;
                ids[count] = WriteBase + i;
                handles[count++] = write;
            }
            ids[count] = JoyConId;
            handles[count++] = set.Hub.EventHandle;
            for (int i = 0; i < Slots; i++)
            {
                nint pad = controller.PadNotifyHandle(i);
                if (pad == 0) continue;
                ids[count] = PadBase + i;
                handles[count++] = pad;
            }
            if (controller.MouseTimerHandle != 0)
            {
                ids[count] = MouseId;
                handles[count++] = controller.MouseTimerHandle;
            }
            if (controller.WatchHandle != 0)
            {
                ids[count] = WatchId;
                handles[count++] = controller.WatchHandle;
            }

            uint r = MsgWaitForMultipleObjectsEx(count, handles, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (r == uint.MaxValue)
            {
                Log.Error($"Message loop: wait failed (error {System.Runtime.InteropServices.Marshal.GetLastSystemError()}), quitting");
                return;
            }
            if (r < WAIT_OBJECT_0 + count)
            {
                int id = ids[r - WAIT_OBJECT_0];
                try
                {
                    if (id < Readers) set.OnHidSignaled(id);
                    else if (id < PadBase) set.OnHidWriteSignaled(id - WriteBase);
                    else if (id < MouseId) controller.OnPadNotify(id - PadBase);
                    else if (id == MouseId) controller.OnMouseTick();
                    else if (id == JoyConId) set.Hub.OnSignaled();
                    else controller.OnWatchedProcessExited();
                }
                catch (Exception ex)
                {
                    ErrorBoundary.OnUiError(id < Readers ? "Controller report" : id < PadBase ? "Controller output"
                        : id < MouseId ? "Rumble" : id == MouseId ? "Mouse"
                        : id == JoyConId ? "Joy-Con 2" : "Watched process", ex);
                }
            }

            while (PeekMessageW(&msg, 0, 0, 0, PM_REMOVE) != 0)
            {
                if (msg.message == WM_QUIT) return;
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
    }
}
