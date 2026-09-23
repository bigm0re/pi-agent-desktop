using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PiAgentDesktop;

/// <summary>
/// Lives in the notification area. Owns the pi agent server, the native window
/// and the tray menu; the window is created on demand so the app can sit in the
/// background from the moment Windows starts.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly LaunchOptions _options;
    private readonly Log _log;
    private readonly JobObject? _job;
    private readonly PiWebServer _server;
    private readonly Control _marshaler = new();

    private readonly ToolStripMenuItem _statusItem = new("状态：正在启动…") { Enabled = false };
    private readonly ToolStripMenuItem _openItem = new("打开 Pi Agent");
    private readonly ToolStripMenuItem _browserItem = new("在浏览器中打开");
    private readonly ToolStripMenuItem _copyItem = new("复制访问地址");
    private readonly ToolStripMenuItem _reloadItem = new("重新加载界面");
    private readonly ToolStripMenuItem _restartItem = new("重新启动服务");
    private readonly ToolStripMenuItem _logItem = new("打开日志文件");
    private readonly ToolStripMenuItem _configItem = new("打开配置文件");
    private readonly ToolStripMenuItem _autoStartItem = new("随 Windows 启动");
    private readonly ToolStripMenuItem _startHiddenItem = new("启动时最小化到托盘");
    private readonly ToolStripMenuItem _closeToTrayItem = new("关闭窗口时最小化到托盘");
    private readonly ToolStripMenuItem _versionItem = new($"版本 {AppInfo.Version}") { Enabled = false };
    private readonly ToolStripMenuItem _npmItem = new("检查插件更新环境（npmCommand）");
    private readonly ToolStripMenuItem _installItem = new("安装 / 更新 Pi Web…");
    private readonly ToolStripMenuItem _exitItem = new("退出");

    private NotifyIcon? _tray;
    private ContextMenuStrip? _menu;
    private AgentWindow? _form;
    private NpmCommandStatus? _npmStatus;
    private bool _balloonShown;
    private bool _exiting;
    private bool _disposed;
    private bool _failureReported;
    private bool _smokeSettled;
    private bool _systemEnding;
    private System.Windows.Forms.Timer? _smokeTimer;

    public TrayContext(AppConfig config, LaunchOptions options, Log log, JobObject? job)
    {
        _config = config;
        _options = options;
        _log = log;
        _job = job;

        _ = _marshaler.Handle; // forces a handle so we can marshal onto the UI thread

        _server = new PiWebServer(config, options, log, job);
        _server.Changed += (_, _) => Post(RefreshUi);

        // Windows suspending or ending the session must not look like a server crash.
        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        if (!options.NoTray)
        {
            CreateTrayIcon();
        }

        RefreshUi();

        // Startup runs off the UI thread: it touches the registry and can spawn
        // `cmd /c npm --version`, which must not stall the message loop nor delay
        // StartListening. A second launch during that window would find no listener
        // and exit silently, which is indistinguishable from a crash.
        _ = Task.Run(BootstrapAsync);
    }

    public void ShowMainWindow()
    {
        Post(() =>
        {
            var form = EnsureForm();
            if (form is null)
            {
                return;
            }

            form.ShowAndActivate();

            if (_server.IsRunning && _server.Url is not null)
            {
                _ = form.LoadAgentUiAsync(_server.Url);
            }
            else
            {
                _ = form.SetStatusAsync("pi agent 服务尚未就绪，正在尝试启动…");
                _ = StartServerFromUiAsync();
            }

            RefreshUi();
        });
    }

    // ---------------------------------------------------------------- startup

    private async Task BootstrapAsync()
    {
        _log.Info($"=== Pi Agent Desktop {AppInfo.Version} starting ===");
        _log.Info($"exe={Environment.ProcessPath}");
        _log.Info($"args={string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");
        _log.Info($"config={_config.FilePath}");

        AutoStart.Apply(_config.AutoStart, _log);

        // Repair pi's npmCommand before the server starts, so the very first plugin
        // update check the UI performs already works.
        CheckNpmEnvironment();

        var started = await _server.StartAsync().ConfigureAwait(false);
        if (!started)
        {
            Post(ReportStartupFailure);
            return;
        }

        Post(() => _ = OnServerReadyAsync());
    }

    private async Task OnServerReadyAsync()
    {
        var url = _server.Url;
        if (url is null)
        {
            return;
        }

        var wantsWindow = !_options.SmokeTest && ShouldShowWindow();

        // Preload keeps the first tray click instant; the smoke test always loads
        // the UI so navigation can be verified without showing a window.
        if (wantsWindow || _config.PreloadWindow || _options.SmokeTest)
        {
            var form = EnsureForm();
            if (form is not null)
            {
                if (!await form.EnsureInitializedAsync().ConfigureAwait(true))
                {
                    ReportWebViewFailure(form);
                    return;
                }

                await form.LoadAgentUiAsync(url).ConfigureAwait(true);
            }
        }

        if (_options.SmokeTest)
        {
            StartSmokeWatchdog();
            return;
        }

        if (wantsWindow)
        {
            ShowMainWindow();
        }
        else
        {
            RefreshUi();
            ShowBalloonOnce();
        }
    }

    private async Task StartServerFromUiAsync()
    {
        var started = await _server.EnsureStartedAsync().ConfigureAwait(true);
        if (!started)
        {
            ReportStartupFailure();
            return;
        }

        if (_form is not null && _server.Url is not null)
        {
            await _form.LoadAgentUiAsync(_server.Url).ConfigureAwait(true);
        }

        RefreshUi();
    }

    private bool ShouldShowWindow()
    {
        if (_options.ForceHidden)
        {
            return false;
        }

        // Without a tray icon the window is the only way to reach the app.
        if (_options.ForceShow || _options.NoTray)
        {
            return true;
        }

        return !_config.StartHidden;
    }

    // ------------------------------------------------------------------- tray

    private void CreateTrayIcon()
    {
        _menu = new ContextMenuStrip();

        _openItem.Font = new Font(_openItem.Font, FontStyle.Bold);

        _openItem.Click += (_, _) => ShowMainWindow();
        _browserItem.Click += (_, _) => OpenInBrowser();
        _copyItem.Click += (_, _) => CopyUrl();
        _reloadItem.Click += (_, _) => ReloadUi();
        _restartItem.Click += async (_, _) => await RestartServerAsync().ConfigureAwait(true);
        _logItem.Click += (_, _) => AppPaths.OpenWithShell(_log.FilePath);
        _configItem.Click += (_, _) => AppPaths.OpenWithShell(_config.FilePath);
        _autoStartItem.Click += (_, _) => ToggleAutoStart();
        _startHiddenItem.Click += (_, _) => ToggleSetting(() => _config.StartHidden = !_config.StartHidden);
        _closeToTrayItem.Click += (_, _) => ToggleSetting(() => _config.CloseToTray = !_config.CloseToTray);
        _npmItem.Click += (_, _) => RepairNpmCommand(announce: true);
        _installItem.Click += async (_, _) => await InstallOrUpdatePiWebAsync().ConfigureAwait(true);
        _exitItem.Click += (_, _) => ExitApp();

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            _openItem,
            _browserItem,
            _copyItem,
            _reloadItem,
            new ToolStripSeparator(),
            _restartItem,
            _logItem,
            _configItem,
            new ToolStripSeparator(),
            _autoStartItem,
            _startHiddenItem,
            _closeToTrayItem,
            new ToolStripSeparator(),
            _npmItem,
            _installItem,
            new ToolStripSeparator(),
            _versionItem,
            _exitItem,
        });

        _tray = new NotifyIcon
        {
            Icon = Assets.TrayIcon,
            Text = "Pi Agent Desktop",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleWindow();
            }
        };

        _tray.DoubleClick += (_, _) => ShowMainWindow();
        _tray.BalloonTipClicked += (_, _) => ShowMainWindow();

        _tray.ContextMenuStrip.Opening += (_, _) => RefreshUi();
    }

    private void ToggleWindow()
    {
        if (_form is { Visible: true })
        {
            _form.HideToTray();
            RefreshUi();
            return;
        }

        ShowMainWindow();
    }

    private void RefreshUi()
    {
        if (_disposed)
        {
            return;
        }

        var detail = _server.State switch
        {
            PiWebServerState.Running => $"运行中 · 端口 {_server.Port}",
            PiWebServerState.Reused => $"复用已有服务 · 端口 {_server.Port}",
            PiWebServerState.Starting => "正在启动…",
            PiWebServerState.Restarting => "正在重启…",
            PiWebServerState.Stopped => "已停止",
            PiWebServerState.Failed => "启动失败",
            _ => "未启动",
        };

        _statusItem.Text = $"状态：{detail}";

        var running = _server.IsRunning;
        _browserItem.Enabled = running;
        _copyItem.Enabled = running;
        _reloadItem.Enabled = running;
        _restartItem.Enabled = _server.State != PiWebServerState.Starting;

        _autoStartItem.Checked = _config.AutoStart;
        _startHiddenItem.Checked = _config.StartHidden;
        _closeToTrayItem.Checked = _config.CloseToTray;

        RefreshNpmItem();

        if (_tray is not null)
        {
            var tooltip = _server.State switch
            {
                PiWebServerState.Running or PiWebServerState.Reused when _server.Url is not null => $"Pi Agent Desktop · {_server.Url}",
                PiWebServerState.Failed => "Pi Agent Desktop · 服务启动失败",
                PiWebServerState.Starting => "Pi Agent Desktop · 正在启动",
                _ => "Pi Agent Desktop",
            };

            _tray.Text = tooltip.Length > 62 ? tooltip[..62] : tooltip;
        }
    }

    private void OpenInBrowser()
    {
        if (_server.Url is not null)
        {
            AgentWindow.OpenExternal(_server.Url);
        }
    }

    private void CopyUrl()
    {
        if (_server.Url is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_server.Url);
        }
        catch
        {
            /* clipboard may be locked by another process */
        }
    }

    private void ReloadUi()
    {
        if (_form is null || _server.Url is null)
        {
            return;
        }

        _ = _form.LoadAgentUiAsync(_server.Url, forceReload: true);
    }

    private async Task RestartServerAsync()
    {
        await _server.RestartAsync().ConfigureAwait(true);

        if (_form is not null && _server.Url is not null)
        {
            await _form.LoadAgentUiAsync(_server.Url, forceReload: true).ConfigureAwait(true);
        }

        RefreshUi();
    }

    private void ToggleAutoStart()
    {
        _config.AutoStart = !_config.AutoStart;
        _config.Save();
        AutoStart.Apply(_config.AutoStart, _log);
        RefreshUi();
    }

    private void ToggleSetting(Action mutate)
    {
        mutate();
        _config.Save();
        RefreshUi();
    }

    /// <summary>
    /// Updating pi-web while our own server has the package directory open fails with
    /// EBUSY (npm renames the old directory away), so stop the server first and bring
    /// it back afterwards.
    /// </summary>
    private async Task InstallOrUpdatePiWebAsync()
    {
        if (_server.IsRunning)
        {
            _log.Info("Stopping the agent server before updating pi-web (the package directory is locked while it runs).");
            await _server.StopAsync("更新 pi-web 前先停止服务").ConfigureAwait(true);
            RefreshUi();
        }

        var succeeded = PiWebInstaller.InstallOrUpdate(_config, _log, _form);

        _failureReported = false;
        _server.Installation = null; // re-locate the possibly upgraded package

        var started = await _server.StartAsync().ConfigureAwait(true);
        if (started && _form is not null && _server.Url is not null)
        {
            await _form.LoadAgentUiAsync(_server.Url, forceReload: true).ConfigureAwait(true);
        }

        if (!succeeded)
        {
            _log.Warn("pi-web install/update reported a failure; see the installer output in the log above.");
        }

        RefreshUi();
    }

    // ---------------------------------------------------------- npm / plugins

    /// <summary>
    /// PI's plugin update check runs <c>execFile("npm", ...)</c>, which always fails on
    /// Windows because npm ships as a .cmd shim (ENOENT) and naming the shim is refused
    /// (EINVAL). <see cref="NpmEnvironment"/> repairs pi's npmCommand setting.
    /// </summary>
    private void CheckNpmEnvironment()
    {
        try
        {
            _npmStatus = NpmEnvironment.EnsureNpmCommand(_config, null, _log);
        }
        catch (Exception error)
        {
            _log.Error("npm environment check failed", error);
            _npmStatus = null;
        }

        // Bootstrap runs off the UI thread, so the menu text must be marshalled.
        Post(RefreshNpmItem);
    }

    private void RefreshNpmItem()
    {
        var status = _npmStatus;

        if (status is null)
        {
            _npmItem.Text = "检查插件更新环境（npmCommand）";
            return;
        }

        if (!status.SettingsExists)
        {
            _npmItem.Text = "检查插件更新环境（未找到 settings.json）";
            return;
        }

        if (status.SettingUsable)
        {
            _npmItem.Text = status.Modified
                ? $"插件更新环境已修复（{status.SettingValue}）"
                : $"插件更新环境正常（{status.SettingValue}）";
            return;
        }

        _npmItem.Text = "修复插件更新环境（npmCommand）";
    }

    private void RepairNpmCommand(bool announce)
    {
        CheckNpmEnvironment();

        if (!announce || _tray is null)
        {
            return;
        }

        var status = _npmStatus;
        string message;
        ToolTipIcon icon;

        if (status is null)
        {
            message = "无法读取 pi 设置，请查看日志。";
            icon = ToolTipIcon.Error;
        }
        else if (!status.SettingsExists)
        {
            message = $"未找到 {status.SettingsPath}，请先运行一次 pi 或 pi-web。";
            icon = ToolTipIcon.Warning;
        }
        else if (status.SettingUsable)
        {
            message = status.Modified
                ? $"已写入 npmCommand = {status.SettingValue}。\n自检：{status.Probe}"
                : $"npmCommand = {status.SettingValue} 已可用。\n自检：{status.Probe}";
            icon = ToolTipIcon.Info;
        }
        else
        {
            message = $"修复失败，npmCommand 仍不可用。请查看日志。";
            icon = ToolTipIcon.Error;
        }

        _tray.ShowBalloonTip(8000, "Pi Agent Desktop · 插件更新", message, icon);
    }

    // ------------------------------------------------- system power / session

    /// <summary>
    /// Windows is logging off or shutting down. The node child will be terminated by
    /// the system; that must not be reported as a crash, and the shell must not try to
    /// restart it while the machine is going down.
    /// </summary>
    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        _systemEnding = true;
        _log.Info($"Windows session is ending ({e.Reason}); not restarting the agent server.");
        _server.MarkExpectedTermination("session ending");

        // Best effort: tear down cleanly if the message loop is still running.
        Post(ExitApp);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _log.Info("System is suspending.");
            _server.MarkExpectedTermination("system suspend");
            return;
        }

        if (e.Mode == PowerModes.Resume)
        {
            _log.Info("System resumed.");
            Post(() => _ = ResumeAsync());
        }
    }

    private async Task ResumeAsync()
    {
        if (_systemEnding || _disposed)
        {
            return;
        }

        if (_server.IsRunning)
        {
            // The child survived the suspend; re-enable crash reporting.
            _server.ClearExpectedTermination();
            return;
        }

        _log.Info("The agent server did not survive the suspend; starting a new one.");
        var started = await _server.StartAsync().ConfigureAwait(true);
        if (started && _form is not null && _server.Url is not null)
        {
            await _form.LoadAgentUiAsync(_server.Url, forceReload: true).ConfigureAwait(true);
        }

        RefreshUi();
    }

    // ---------------------------------------------------------- failure paths

    private void ReportStartupFailure()
    {
        if (_options.SmokeTest)
        {
            SettleSmoke(false, "server-start-failed");
            return;
        }

        if (_failureReported)
        {
            return;
        }

        _failureReported = true;

        if (_tray is not null)
        {
            _tray.ShowBalloonTip(
                8000,
                "Pi Agent Desktop",
                _server.LastError ?? "pi agent 服务启动失败，请检查日志。",
                ToolTipIcon.Error);
        }

        var installButton = new TaskDialogButton("自动安装 Pi Web");
        var logButton = new TaskDialogButton("打开日志");
        var page = new TaskDialogPage
        {
            Caption = "Pi Agent Desktop",
            Heading = "无法启动 pi agent 服务",
            Text = _server.LastError ?? "未知错误，请查看日志。",
            Icon = TaskDialogIcon.Error,
            AllowCancel = true,
            Buttons = { installButton, logButton, TaskDialogButton.Close },
        };

        var result = TaskDialog.ShowDialog(page);
        if (result == installButton)
        {
            _ = InstallOrUpdatePiWebAsync();
        }
        else if (result == logButton)
        {
            AppPaths.OpenWithShell(_log.FilePath);
        }
    }

    private void ReportWebViewFailure(AgentWindow form)
    {
        // Never block an unattended run on a modal dialog.
        if (_options.SmokeTest)
        {
            SettleSmoke(false, "webview2-init-failed");
            return;
        }

        var message =
            "无法初始化 WebView2 运行时。\n\n" +
            "请安装 Microsoft Edge WebView2 Runtime（系统通常已自带，可从 Microsoft 官网下载 Evergreen Runtime）。\n\n" +
            $"错误：{form.InitializationError}";

        _log.Error(message);

        var browserButton = new TaskDialogButton("在浏览器中打开");
        var page = new TaskDialogPage
        {
            Caption = "Pi Agent Desktop",
            Heading = "无法创建内置窗口",
            Text = message,
            Icon = TaskDialogIcon.Warning,
            AllowCancel = true,
            Buttons = { browserButton, TaskDialogButton.Close },
        };

        if (TaskDialog.ShowDialog(page) == browserButton)
        {
            OpenInBrowser();
        }
    }

    private void ShowBalloonOnce()
    {
        if (_balloonShown || _tray is null || !_config.NotifyOnHide)
        {
            return;
        }

        _balloonShown = true;
        _tray.ShowBalloonTip(
            4000,
            "Pi Agent Desktop 已在后台运行",
            $"点击托盘图标打开 Pi Agent（{_server.Url}）",
            ToolTipIcon.Info);
    }

    // ---------------------------------------------------------------- window

    private AgentWindow? EnsureForm()
    {
        if (_form is { IsDisposed: false })
        {
            return _form;
        }

        try
        {
            var form = new AgentWindow(_config, _log);
            form.HiddenToTray += (_, _) =>
            {
                RefreshUi();
                ShowBalloonOnce();
            };

            _form = form;
            return form;
        }
        catch (Exception error)
        {
            _log.Error("Failed to create the main window", error);
            return null;
        }
    }

    // ------------------------------------------------------------ smoke test

    private void StartSmokeWatchdog()
    {
        var form = _form;
        if (form is null)
        {
            SettleSmoke(false, "window-not-created");
            return;
        }

        if (form.LastNavigationSucceeded.HasValue)
        {
            SettleSmoke(form.LastNavigationSucceeded.Value, "already-navigated");
            return;
        }

        form.Navigated += (_, success) => SettleSmoke(success, null);

        _smokeTimer = new System.Windows.Forms.Timer { Interval = 90_000 };
        _smokeTimer.Tick += (_, _) =>
        {
            _smokeTimer?.Stop();
            SettleSmoke(false, "navigation-timeout");
        };
        _smokeTimer.Start();
    }

    private void SettleSmoke(bool success, string? reason)
    {
        if (_smokeSettled)
        {
            return;
        }

        _smokeSettled = true;
        _smokeTimer?.Stop();

        var payload = new
        {
            ok = success,
            reason,
            version = AppInfo.Version,
            timestamp = DateTimeOffset.Now.ToString("o"),
            os = RuntimeInformation.OSDescription,
            dotnet = RuntimeInformation.FrameworkDescription,
            webView2Version = _form?.WebViewVersion,
            windowCreated = _form is not null,
            webView2Initialized = _form?.IsInitialized ?? false,
            navigationSucceeded = _form?.LastNavigationSucceeded,
            navigationError = _form?.InitializationError,
            trayCreated = _tray is not null,
            serverState = _server.State.ToString(),
            serverMessage = _server.Message,
            serverProcessId = _server.ProcessId,
            nodePath = _server.NodePath,
            piWebDirectory = _server.Installation?.Directory,
            piWebVersion = _server.Installation?.Version,
            piWebHasProductionBuild = _server.Installation?.HasProductionBuild,
            port = _server.Port,
            url = _server.Url,
            readyMilliseconds = Math.Round(_server.ReadyMilliseconds, 1),
            npmSettings = _npmStatus?.SettingsPath,
            npmCommand = _npmStatus?.SettingValue,
            npmUsable = _npmStatus?.SettingUsable,
            npmModified = _npmStatus?.Modified,
            npmProbe = _npmStatus?.Probe,
            configFile = _config.FilePath,
            logFile = _log.FilePath,
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

        foreach (var target in new[]
                 {
                     Path.Combine(Environment.CurrentDirectory, "smoke-result.json"),
                     Path.Combine(AppPaths.RoamingRoot, "smoke-result.json"),
                 })
        {
            try
            {
                File.WriteAllText(target, json);
            }
            catch
            {
                /* best effort */
            }
        }

        _log.Info($"Smoke test result: ok={success} reason={reason ?? "navigated"}");
        Console.WriteLine(json);
        Console.Out.Flush();

        Environment.ExitCode = success ? 0 : 1;
        ExitApp();
    }

    // ---------------------------------------------------------------- shutdown

    public void ExitApp()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _log.Info("Shutdown requested.");

        DisposeResources();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeResources();
        }

        base.Dispose(disposing);
    }

    private void DisposeResources()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            SystemEvents.SessionEnding -= OnSessionEnding;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch
        {
            /* ignore */
        }

        try
        {
            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
        }
        catch
        {
            /* ignore */
        }

        _menu?.Dispose();

        try
        {
            if (!_server.StopAsync().Wait(TimeSpan.FromSeconds(8)))
            {
                _log.Warn("Server shutdown timed out.");
            }
        }
        catch (Exception error)
        {
            _log.Warn($"Server shutdown error: {error.Message}");
        }

        _job?.Dispose();

        try
        {
            if (_form is { IsDisposed: false })
            {
                _form.AllowClose();
                _form.Close();
            }
        }
        catch
        {
            /* ignore */
        }

        _form?.Dispose();

        try
        {
            _marshaler.Dispose();
        }
        catch
        {
            /* ignore */
        }
    }

    private void Post(Action action)
    {
        // The context is torn down on exit and Windows can end the session at any
        // moment, so every marshalled callback is guarded against disposed state.
        if (_disposed || _marshaler.IsDisposed)
        {
            return;
        }

        try
        {
            if (_marshaler.InvokeRequired)
            {
                _marshaler.BeginInvoke(new Action(() => InvokeGuarded(action)));
            }
            else
            {
                InvokeGuarded(action);
            }
        }
        catch (ObjectDisposedException)
        {
            /* the handle disappeared between the check and the dispatch */
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Post failed: {error.Message}");
        }
    }

    private static void InvokeGuarded(Action action)
    {
        try
        {
            action();
        }
        catch (ObjectDisposedException)
        {
            /* an object was disposed while the callback was running */
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Marshalled callback failed: {error.Message}");
        }
    }
}
