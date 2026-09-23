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
            var exitCode = await RunNpmAsync(form, config, nodePath, npmCli, log).ConfigureAwait(true);
            var success = exitCode == 0;
            form.Complete(
                success,
                success
                    ? "安装完成。重新启动服务后即可使用。"
                    : $"安装失败（退出码 {exitCode}）。请查看上方输出或日志文件。");
        };

        form.ShowDialog(owner);
        return form.Succeeded;
    }

    private static async Task<int> RunNpmAsync(InstallForm form, AppConfig config, string nodePath, string npmCli, Log log)
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

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    form.Append(e.Data);
                    log.Child("npm:", e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    form.Append(e.Data);
                    log.Child("npm!", e.Data, isError: true);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync().ConfigureAwait(true);
            return process.ExitCode;
        }
        catch (Exception error)
        {
            log.Error("npm install failed", error);
            form.Append($"错误：{error.Message}");
            return -1;
        }
    }
}
