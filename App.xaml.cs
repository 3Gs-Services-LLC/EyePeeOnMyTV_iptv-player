using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using LibVLCSharp.Shared;

namespace EyePeeOnMyTV;

public partial class App : Application
{
    // Suffixed with a GUID so an unrelated app that happens to share this name can't collide
    // with our mutex.
    private const string SingleInstanceMutexName = "EyePeeOnMyTV-{2E7A6E7C-2C4E-4C4E-9C1B-3F6F6F6F6F6F}";

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SwRestore = 9;

    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // Another instance already holds the mutex. Surface it instead of launching a
            // second process, then exit immediately — this is what makes the app single-instance
            // regardless of how many times something (a test run, a dev workflow, a user
            // double-click) tries to start it.
            BringExistingInstanceToForeground();
            Shutdown();
            return;
        }

        Core.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void BringExistingInstanceToForeground()
    {
        var current = Process.GetCurrentProcess();
        var existing = Process.GetProcessesByName(current.ProcessName)
            .FirstOrDefault(p => p.Id != current.Id);

        if (existing is null || existing.MainWindowHandle == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(existing.MainWindowHandle, SwRestore);
        SetForegroundWindow(existing.MainWindowHandle);
    }
}
