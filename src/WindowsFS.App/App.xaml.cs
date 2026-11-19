using System.Windows;

namespace WindowsFS.App;

public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? open;
    private RegisteredWaitHandle? registration;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--history-collector")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (e.Args.Length == 3 && Guid.TryParseExact(e.Args[1], "N", out var nonce) && int.TryParse(e.Args[2], out var pid))
                    await Task.Run(() => History.DetailCollector.RunAsync(nonce, pid));
            }
            catch (Exception) { Shutdown(1); return; }
            Shutdown(); return;
        }
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        string key = @"Local\WindowsFS." + identity.User!.Value;
        open = new(false, EventResetMode.AutoReset, key + ".Open");
        instance = new(false, key + ".Instance", out bool first);
        if (!first) { open.Set(); Shutdown(); return; }
        var window = new MainWindow();
        MainWindow = window;
        registration = ThreadPool.RegisterWaitForSingleObject(open, (_, _) => Dispatcher.BeginInvoke(window.RestoreWindow), null, -1, false);
        window.Show();
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow window) window.EndUserSession();
        base.OnSessionEnding(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        registration?.Unregister(null); open?.Dispose(); instance?.Dispose();
        base.OnExit(e);
    }
}
