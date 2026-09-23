using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace PiAgentDesktop;

internal enum PiWebServerState
{
    Idle,
    Starting,
    Running,
    Reused,
    Restarting,
    Stopped,
    Failed,
}

/// <summary>
/// Supervises the pi agent server (the original pi-web process) that the desktop
/// shell hosts: spawn, readiness probing, crash restart and clean shutdown.
/// </summary>
internal sealed class PiWebServer : IDisposable
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = TimeSpan.FromSeconds(4),
    };

    private readonly AppConfig _config;
    private readonly Log _log;
    private readonly JobObject? _job;
    private readonly object _gate = new();
    private readonly int? _portOverride;

    private Process? _process;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _reuseWatch;
    private volatile bool _stopping;
    private int _restartCount;
    private Task? _restartTask;

    public PiWebServer(AppConfig config, LaunchOptions options, Log log, JobObject? job)
    {
        _config = config;
        _log = log;
        _job = job;

        if (!string.IsNullOrWhiteSpace(options.NodePath))
        {
            config.NodePath = options.NodePath;
        }

        if (!string.IsNullOrWhiteSpace(options.PiWebDir))
        {
            config.PiWebDir = options.PiWebDir;
        }

        // A command line port override stays transient: it must never be written
        // back into config.json (which is saved whenever the window state changes).
        if (options.Port is { } port)
        {
            log.Info($"Port override from the command line: {port}");
            _portOverride = port;
        }
    }

    public event EventHandler? Changed;

    public PiWebServerState State { get; private set; } = PiWebServerState.Idle;

    public string? Message { get; private set; }

    public string? Url { get; private set; }

    public int Port { get; private set; }

    /// <summary>Port to use: command line override wins over the saved setting.</summary>
    public int EffectivePort => _portOverride ?? _config.Port;

    public string? LastError { get; private set; }

    public PiWebInstallation? Installation { get; set; }

    public string? NodePath { get; private set; }

    public int ProcessId { get; private set; }

    public double ReadyMilliseconds { get; private set; }

    public bool IsRunning => State is PiWebServerState.Running or PiWebServerState.Reused;

    public async Task<bool> StartAsync(int? forcedPort = null)
    {
        lock (_gate)
        {
            _stopping = false;
            _reuseWatch?.Cancel();
            _lifetime?.Dispose();
            _lifetime = new CancellationTokenSource();
        }

        var token = _lifetime!.Token;
        LastError = null;
        SetState(PiWebServerState.Starting, "正在查找 pi agent 运行环境…");

        Installation ??= PiWebLocator.FindPiWeb(_config, _log);
        if (Installation is null)
        {
            return Fail("未找到 pi-web（pi agent 运行环境）。请先安装：npm install -g @agegr/pi-web");
        }

        NodePath ??= PiWebLocator.FindNodeExecutable(_config, _log);
        if (NodePath is null)
        {
            return Fail("未找到 Node.js 运行时。请安装 Node.js 22.19+ 或设置 config.nodePath。");
        }

        var host = _config.HostName;

        // Opt-in: attach to a pi-web that is already listening (e.g. one you started
        // yourself). Off by default because such an instance can disappear at any time.
        if (_config.ReuseRunningServer)
        {
            var defaultPort = forcedPort ?? EffectivePort;
            if (await IsPiWebListeningAsync(host, defaultPort, token).ConfigureAwait(false))
            {
                Port = defaultPort;
                Url = BuildUrl(host, defaultPort);
                SetState(PiWebServerState.Reused, $"已发现运行中的服务，直接复用（端口 {defaultPort}）");
                _log.Info($"Reusing the pi-web instance already listening on {Url}");
                StartReuseWatchdog(host, defaultPort);
                return true;
            }
        }

        int port;
        try
        {
            port = await FindFreePortAsync(forcedPort ?? EffectivePort, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return Fail(error.Message);
        }

        var started = Stopwatch.StartNew();

        try
        {
            var process = Spawn(port, host);
            if (process is null)
            {
                return Fail("无法启动 pi agent 服务进程。");
            }

            lock (_gate)
            {
                _process = process;
                ProcessId = process.Id;
            }
        }
        catch (Exception error)
        {
            _log.Error("Failed to spawn the pi-web server", error);
            return Fail($"启动失败：{error.Message}");
        }

        SetState(PiWebServerState.Starting, $"正在等待服务就绪（端口 {port}）…");

        var timeout = TimeSpan.FromMilliseconds(_config.ReadyTimeoutMs);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (token.IsCancellationRequested)
            {
                return false;
            }

            Process? current;
            lock (_gate)
            {
                current = _process;
            }

            if (current is null || current.HasExited)
            {
                var code = current?.ExitCode;
                return Fail($"pi agent 服务提前退出（退出码 {code}）。请查看日志了解详情。");
            }

            if (await IsPiWebListeningAsync(host, port, token).ConfigureAwait(false))
            {
                Port = port;
                Url = BuildUrl(host, port);
                ReadyMilliseconds = started.Elapsed.TotalMilliseconds;
                _restartCount = 0;
                SetState(PiWebServerState.Running, $"服务已就绪（{started.ElapsedMilliseconds} ms）");
                return true;
            }

            try
            {
                await Task.Delay(350, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return Fail($"等待服务就绪超时（{timeout.TotalSeconds:0} 秒）。");
    }

    public async Task StopAsync(string message = "服务已停止")
    {
        Process? process;
        lock (_gate)
        {
            _stopping = true;
            _reuseWatch?.Cancel();
            process = _process;
            _process = null;
            ProcessId = 0;
        }

        try
        {
            _lifetime?.Cancel();
        }
        catch
        {
            /* ignore */
        }

        if (process is not null)
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
        }

        Url = null;
        SetState(PiWebServerState.Stopped, message);
    }

    public async Task<bool> RestartAsync()
    {
        SetState(PiWebServerState.Restarting, "正在重启服务…");
        await StopAsync("正在重启…").ConfigureAwait(false);
        await Task.Delay(250).ConfigureAwait(false);
        _restartCount = 0;
        return await StartAsync().ConfigureAwait(false);
    }

    public async Task<bool> EnsureStartedAsync()
    {
        if (IsRunning)
        {
            return true;
        }

        return await StartAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Marks the server as intentionally going away because Windows is suspending or
    /// ending the session. The child's exit must then not be reported as a crash and
    /// must not start the restart backoff - the machine is going down, not failing.
    /// </summary>
    public void MarkExpectedTermination(string reason)
    {
        _stopping = true;
        _log.Info($"Expecting the pi-web process to be terminated by the system ({reason}).");

        try
        {
            _reuseWatch?.Cancel();
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>Undoes <see cref="MarkExpectedTermination"/> when the child actually survived.</summary>
    public void ClearExpectedTermination()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
        }

        if (process is not null && !process.HasExited)
        {
            _stopping = false;
            _log.Info("pi-web survived the suspend; crash reporting re-enabled.");
        }
    }

    private Process? Spawn(int port, string host)
    {
        var installation = Installation!;
        var nodePath = NodePath!;

        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            WorkingDirectory = installation.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        startInfo.ArgumentList.Add(installation.EntryScript);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--hostname");
        startInfo.ArgumentList.Add(host);
        startInfo.ArgumentList.Add("--no-open");

        startInfo.Environment["PI_WEB_NO_OPEN"] = "1";
        startInfo.Environment["PI_WEB_HOSTNAME"] = host;

        // The npm repair relies on npm being discoverable by the shell-mediated
        // command the agent runs, so make sure the child can resolve it.
        startInfo.Environment["PATH"] = NpmEnvironment.BuildAugmentedPath(_config, nodePath, _log);

        if (!string.IsNullOrWhiteSpace(_config.PiAgentDir))
        {
            startInfo.Environment["PI_CODING_AGENT_DIR"] = _config.PiAgentDir;
        }

        foreach (var (key, value) in _config.Environment)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                startInfo.Environment[key] = value;
            }
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => _log.Child("pi-web:", e.Data);
        process.ErrorDataReceived += (_, e) => _log.Child("pi-web!", e.Data, isError: true);
        process.Exited += OnProcessExited;

        _log.Info($"Spawning: \"{nodePath}\" \"{installation.EntryScript}\" --port {port} --hostname {host} --no-open");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _job?.Attach(process, _log);
        return process;
    }

    /// <summary>
    /// An opted-in reused server can vanish at any time (its owning terminal closes).
    /// Probe it periodically and, once it is clearly gone, start a private instance
    /// so the window never silently goes dead.
    /// </summary>
    private void StartReuseWatchdog(string host, int port)
    {
        _reuseWatch?.Dispose();
        _reuseWatch = new CancellationTokenSource();
        var token = _reuseWatch.Token;

        _ = Task.Run(async () =>
        {
            var failures = 0;

            while (!token.IsCancellationRequested && State == PiWebServerState.Reused)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (await IsPiWebListeningAsync(host, port, token).ConfigureAwait(false))
                {
                    failures = 0;
                    continue;
                }

                failures++;
                _log.Warn($"Reused pi-web did not answer the health probe ({failures}/3).");

                if (failures < 3)
                {
                    continue;
                }

                _log.Warn("The reused pi-web instance is gone; starting a private one instead.");
                await StopAsync("复用的服务已失联，改为自建服务").ConfigureAwait(false);
                await StartAsync().ConfigureAwait(false);
                return;
            }
        }, token);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        Process? process = sender as Process;

        if (_stopping)
        {
            return;
        }

        int exitCode;
        try
        {
            exitCode = process?.ExitCode ?? -1;
        }
        catch
        {
            exitCode = -1;
        }

        _log.Warn($"pi-web process exited unexpectedly (code {exitCode}).");

        if (!_config.RestartOnCrash)
        {
            SetState(PiWebServerState.Failed, $"服务已退出（退出码 {exitCode}）");
            return;
        }

        if (_restartCount >= _config.MaxRestarts)
        {
            SetState(PiWebServerState.Failed, $"服务反复退出（已重试 {_restartCount} 次），请查看日志。");
            return;
        }

        _restartCount++;
        var delay = TimeSpan.FromSeconds(Math.Min(30, 2 * _restartCount));
        SetState(PiWebServerState.Restarting, $"服务意外退出，{delay.TotalSeconds:0} 秒后自动重启（第 {_restartCount} 次）…");

        lock (_gate)
        {
            _restartTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    if (_stopping)
                    {
                        return;
                    }

                    await StartAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    _log.Error("Automatic restart failed", error);
                }
            });
        }
    }

    private bool Fail(string message)
    {
        LastError = message;
        _log.Error(message);
        SetState(PiWebServerState.Failed, message);
        return false;
    }

    private void SetState(PiWebServerState state, string message)
    {
        State = state;
        Message = message;
        _log.Info($"server state -> {state}: {message}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string BuildUrl(string host, int port)
    {
        var displayHost = host is "0.0.0.0" or "::" or "[::]" ? "127.0.0.1" : host;
        return $"http://{displayHost}:{port}/";
    }

    private static Task<bool> IsPortFreeAsync(int port, CancellationToken token)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private async Task<int> FindFreePortAsync(int startPort, CancellationToken token)
    {
        for (var offset = 0; offset < _config.PortScanLimit; offset++)
        {
            var port = startPort + offset;
            if (port > 65535)
            {
                break;
            }

            if (await IsPortFreeAsync(port, token).ConfigureAwait(false))
            {
                if (offset > 0)
                {
                    _log.Info($"Port {startPort} is busy; using {port} instead.");
                }

                return port;
            }
        }

        throw new InvalidOperationException(
            $"端口 {startPort}–{startPort + _config.PortScanLimit - 1} 均被占用，请修改 config.json 中的 port。");
    }

    /// <summary>
    /// Readiness probe. /api/app-update is a stable pi-web JSON endpoint, so a
    /// matching response proves we are talking to pi-web and not to some other
    /// process that happens to own the port.
    /// </summary>
    public static async Task<bool> IsPiWebListeningAsync(string host, int port, CancellationToken token)
    {
        var probeHost = host is "0.0.0.0" or "::" or "[::]" ? "127.0.0.1" : host;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            using var response = await Http
                .GetAsync($"http://{probeHost}:{port}/api/app-update", timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return body.Contains("currentVersion", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private async Task KillProcessTreeAsync(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            _log.Info($"Stopping pi-web process tree (pid {process.Id}).");
            process.Kill(entireProcessTree: true);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.Warn("pi-web did not exit in time; forcing taskkill.");
                try
                {
                    Process.Start(new ProcessStartInfo("taskkill", $"/PID {process.Id} /T /F")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    })?.WaitForExit(5000);
                }
                catch
                {
                    /* ignore */
                }
            }
        }
        catch (Exception error)
        {
            _log.Warn($"Failed to stop the pi-web process: {error.Message}");
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch
            {
                /* ignore */
            }
        }
    }

    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            /* ignore */
        }

        _lifetime?.Dispose();
        _reuseWatch?.Dispose();
    }
}
