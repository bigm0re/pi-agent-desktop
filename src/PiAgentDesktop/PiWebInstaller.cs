using System.Diagnostics;
using System.Text;

namespace PiAgentDesktop;

/// <summary>Small modal window that streams npm output while pi-web is installed.</summary>
internal sealed class InstallForm : Form
{
    private readonly TextBox _output;
    private readonly Label _status;
    private readonly Button _closeButton;

    public InstallForm(string title)
    {
        Text = title;
        Width = 760;
        Height = 480;
        MinimumSize = new Size(560, 320);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Assets.ApplicationIcon;
        ShowInTaskbar = true;

        _status = new Label
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(12, 10, 12, 0),
            Text = "正在安装，请稍候…",
        };

        _output = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = Color.FromArgb(16, 18, 24),
            ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 9f),
            BorderStyle = BorderStyle.None,
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(12) };
        _closeButton = new Button
        {
            Text = "关闭",
            Dock = DockStyle.Right,
            Width = 110,
            Enabled = false,
        };
        _closeButton.Click += (_, _) => Close();
        bottom.Controls.Add(_closeButton);

        Controls.Add(_output);
        Controls.Add(bottom);
        Controls.Add(_status);
    }

    public bool Succeeded { get; private set; }

    public void Append(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => Append(line));
            }
            catch
            {
                /* window already gone */
            }

            return;
        }

        _output.AppendText(line + Environment.NewLine);
    }

    public void SetStatus(string text)
    {
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => SetStatus(text));
            }
            catch
            {
                /* ignore */
            }

            return;
        }

        _status.Text = text;
    }

    public void Complete(bool success, string summary)
    {
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => Complete(success, summary));
            }
            catch
            {
                /* ignore */
            }

            return;
        }

        Succeeded = success;
        SetStatus(summary);
        _closeButton.Enabled = true;
        _closeButton.Select();
    }
}

internal static class PiWebInstaller
{
    public const string PackageSpec = "@agegr/pi-web@latest";

    /// <summary>Runs <c>npm install -g @agegr/pi-web@latest</c> in a visible progress window.</summary>
    public static bool InstallOrUpdate(AppConfig config, Log log, IWin32Window? owner)
    {
        var nodePath = PiWebLocator.FindNodeExecutable(config, log);
        if (nodePath is null)
        {
            MessageBox.Show(
                owner,
                "未找到 Node.js 运行时。\n\n请先安装 Node.js 22.19 或更高版本（https://nodejs.org），然后重试。",
                "Pi Agent Desktop",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        var npmCli = PiWebLocator.FindNpmCommandScript(nodePath);
        if (npmCli is null)
        {
            MessageBox.Show(
                owner,
                "未找到 npm。\n\n请确认 Node.js 安装完整（包含 npm），或手动执行：\nnpm install -g " + PackageSpec,
                "Pi Agent Desktop",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        using var form = new InstallForm("安装 / 更新 Pi Web");
        form.Shown += async (_, _) =>
        {
            // A pi-web that is still listening (for example one started by hand in a
            // terminal, or a reused instance we never owned) keeps the package
            // directory busy no matter what this process does.
            try
            {
                using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var listening = await PiWebServer
                    .IsPiWebListeningAsync(config.HostName, config.Port, probe.Token)
                    .ConfigureAwait(true);

                if (listening)
                {
                    form.Append($"注意：端口 {config.Port} 上仍有 pi-web 在运行。");
                    form.Append("它占用的正是 npm 要替换的目录，这通常就是 EBUSY 的原因。");
                    form.Append(string.Empty);
                }
            }
            catch
            {
                /* the probe is best effort */
            }

            // npm replaces a global package by renaming the old directory aside. That
            // fails with EBUSY while anything still has files open inside it - which
            // includes the seconds right after our own server is terminated, while the
            // antivirus is still scanning it. Give that a moment, then retry instead of
            // treating the first collision as fatal.
            const int attempts = 3;
            var exitCode = -1;
            var locked = false;

            form.SetStatus("等待文件句柄释放…");
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                if (attempt > 1)
                {
                    form.Append(string.Empty);
                    form.Append($"--- 目录仍被占用，5 秒后重试（{attempt}/{attempts}）---");
                    await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                }

                var result = await RunNpmAsync(form, config, nodePath, npmCli, log).ConfigureAwait(true);
                exitCode = result.ExitCode;
                if (exitCode == 0)
                {
                    break;
                }

                locked = LooksLikeDirectoryLocked(result.Output);
                if (!locked)
                {
                    break; // a genuine failure - retrying would only repeat it
                }
            }

            string summary;
            if (exitCode == 0)
            {
                summary = "安装完成。正在重新启动服务…";
            }
            else if (locked)
            {
                summary = "安装失败：pi-web 目录一直被占用（EBUSY）。";
                form.Append(string.Empty);
                form.Append("请按以下步骤处理：");
                form.Append("  1) 完全退出本程序：托盘图标 → 退出");
                form.Append("  2) 关闭其它正在运行的 pi-web / pi 窗口");
                form.Append($"  3) 在终端执行：npm install -g {PackageSpec}");
                form.Append("  4) 重新启动本程序");
            }
            else
            {
                summary = $"安装失败（退出码 {exitCode}）。请查看上方输出或日志文件。";
            }

            form.Complete(exitCode == 0, summary);
        };

        form.ShowDialog(owner);
        return form.Succeeded;
    }

    /// <summary>True when npm reported the Windows "resource busy or locked" failure.</summary>
    private static bool LooksLikeDirectoryLocked(string output) =>
        output.Contains("EBUSY", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("resource busy or locked", StringComparison.OrdinalIgnoreCase);

    private static async Task<(int ExitCode, string Output)> RunNpmAsync(
        InstallForm form,
        AppConfig config,
        string nodePath,
        string npmCli,
        Log log)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add(npmCli);
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("-g");
        startInfo.ArgumentList.Add("--no-fund");
        startInfo.ArgumentList.Add("--no-audit");
        startInfo.ArgumentList.Add(PackageSpec);
        startInfo.Environment["PATH"] = NpmEnvironment.BuildAugmentedPath(config, nodePath, log);

        log.Info($"Running: \"{nodePath}\" \"{npmCli}\" install -g --no-fund --no-audit {PackageSpec}");
        form.Append($"$ npm install -g {PackageSpec}");

        // npm reports the lock on stderr; keep a copy so the caller can tell a lock
        // apart from a real failure and decide whether a retry is worth it.
        var captured = new StringBuilder();

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                captured.AppendLine(e.Data);
                form.Append(e.Data);
                log.Child("npm:", e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                captured.AppendLine(e.Data);
                form.Append(e.Data);
                log.Child("npm!", e.Data, isError: true);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync().ConfigureAwait(true);
            return (process.ExitCode, captured.ToString());
        }
        catch (Exception error)
        {
            log.Error("npm install failed", error);
            form.Append($"错误：{error.Message}");
            return (-1, error.Message);
        }
    }
}
