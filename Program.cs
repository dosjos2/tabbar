namespace Tabbar;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--quota-test")) { QuotaTest.Run(); return; }
        if (args.Length == 2 && args[0] == "--run-script")
        {
            try { Environment.ExitCode = ExecutionHelper.Run(ScriptRunner.DecodeRequest(args[1])); }
            catch (Exception e) { MessageBox.Show(e.Message, "Kunne ikke kjøre script", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            return;
        }
        if (args.Contains("--self-test")) { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException); SelfTest.Run(); return; }
        using var mutex = new Mutex(true, @"Local\Tabbar.DesktopToolbar", out var first);
        if (!first) return;
        Application.ThreadException += (_, e) => { MessageBox.Show(e.Exception.Message, "Tabbar – feil", MessageBoxButtons.OK, MessageBoxIcon.Error); Application.Exit(); };
        try
        {
            if (args.Contains("--install")) Startup.Set(true);
            var store = new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tabbar"));
            Application.Run(new ToolbarForm(store, store.Load()));
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Tabbar kunne ikke starte", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
