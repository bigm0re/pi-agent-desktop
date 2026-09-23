using System.Windows.Forms;

namespace PiAgentDesktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = LaunchOptions.Parse(args);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // A second launch only asks the running instance to show its window. If that
        // instance does not answer, say so: exiting silently is indistinguishable from
        // a crash, which is exactly how a flash-exit gets reported.
        using var single = new SingleInstance();
        if (!single.TryAcquire())
        {
            if (!SingleInstance.SignalRunningInstance())
            {
                MessageBox.Show(
                    $"Pi Agent Desktop {AppInfo.Version} 已经在运行，但另一个实例没有响应。\n\n" +
                    "请在任务栏通知区域（托盘）找到 Pi Agent 图标并点击打开。\n" +
                    "如果找不到图标，请先在任务管理器中结束 PiAgentDesktop.exe，然后重新启动。",
                    $"Pi Agent Desktop {AppInfo.Version}",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return 0;
        }

        AppPaths.EnsureCreated();

        var log = new Log(AppPaths.LogFile, options.EchoLog);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Error("Unhandled exception", e.ExceptionObject as Exception);

        Application.ThreadException += (_, e) => log.Error("UI thread exception", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        var config = AppConfig.Load(AppPaths.ConfigFile, log);

        // The job object guarantees the node server dies with this app.
        var job = JobObject.Create(log);

        using var context = new TrayContext(config, options, log, job);
        single.StartListening(context.ShowMainWindow);

        Application.Run(context);

        log.Info($"=== Pi Agent Desktop {AppInfo.Version} stopped ===");
        return Environment.ExitCode;
    }
}
