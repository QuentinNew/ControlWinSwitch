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

        try
        {
            using var set = new ControllerSet();
            var window = new MainWindow(set);
            var controller = new AppController(set, window);
            window.Controller = controller;
            Log.Info($"Started {typeof(Program).Assembly.GetName().Version?.ToString(3)} (elevated={IsElevated()}, hwnd=0x{window.Handle:X})");

            for (int i = 0; i < ControllerSet.Slots; i++)
            {
                int slot = i;
                set.Readers[slot].Report += (report, raw) => controller.OnReport(slot, report, raw);
                set.Readers[slot].CalibrationRead += cal => controller.OnCalibration(slot, cal);
                set.Readers[slot].ControllerRemoved += () => controller.OnControllerRemoved(slot);
            }

            set.TryOpen();
            if (!set.AnyOpen) Log.Info("Pro Controller not connected yet (waiting for it)");
            window.UpdateTray();

            RunMessageLoop(set, controller);
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
    private static void RunMessageLoop(ControllerSet set, AppController controller)
    {
        // Wait set: the controllers' reports, the virtual pads' rumble requests, the mouse timer (signalled only while
        // a stick moves the cursor) and the watched process (signalled when it exits). Pads, mouse and process
        // change at run time, so the set is rebuilt on every iteration.
        const int Slots = ControllerSet.Slots, MouseId = 2 * Slots, WatchId = 2 * Slots + 1;
        nint* handles = stackalloc nint[2 * Slots + 2];
        int* ids = stackalloc int[2 * Slots + 2]; // 0..3 reader, 4..7 pad rumble, then mouse, watch
        MSG msg;
        while (true)
        {
            uint count = 0;
            for (int i = 0; i < Slots; i++)
            {
                ids[count] = i;
                handles[count++] = set.Readers[i].EventHandle;
            }
            for (int i = 0; i < Slots; i++)
            {
                nint pad = controller.PadNotifyHandle(i);
                if (pad == 0) continue;
                ids[count] = Slots + i;
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
            if (r == uint.MaxValue) return; // WAIT_FAILED
            if (r < WAIT_OBJECT_0 + count)
            {
                int id = ids[r - WAIT_OBJECT_0];
                if (id < Slots) set.Readers[id].OnSignaled();
                else if (id < MouseId) controller.OnPadNotify(id - Slots);
                else if (id == MouseId) controller.OnMouseTick();
                else controller.OnWatchedProcessExited();
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
