using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PiAgentDesktop;

/// <summary>
/// The native window. It hosts the OS WebView2 runtime (no bundled browser) and
/// loads the local pi agent server; closing it hides to the notification area.
///
/// Named AgentWindow rather than MainForm on purpose: <see cref="ApplicationContext"/>
/// already exposes a <c>Form? MainForm</c> property, so a class of that name is
/// shadowed inside the context and resolves to the property instead of the type.
/// </summary>
internal sealed class AgentWindow : Form
{
    private readonly AppConfig _config;
    private readonly Log _log;
    private readonly WebView2 _webView = new();
    private readonly object _initLock = new();
    private CoreWebView2Environment? _environment;
    private Task<bool>? _initialization;
    private bool _initialized;
    private bool _allowClose;
    private ulong? _agentNavigationId;
    private string? _agentAuthority;
    private string? _currentUrl;

    public AgentWindow(AppConfig config, Log log)
    {
        _config = config;
        _log = log;

        Text = $"Pi Agent Desktop {AppInfo.Version}";
        Icon = Assets.ApplicationIcon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 560);
        Width = config.WindowWidth;
        Height = config.WindowHeight;
        BackColor = Color.FromArgb(13, 16, 23);
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        if (config.WindowMaximized)
        {
            WindowState = FormWindowState.Maximized;
        }

        _webView.Dock = DockStyle.Fill;
        _webView.DefaultBackgroundColor = Color.FromArgb(13, 16, 23);
        Controls.Add(_webView);
    }

    /// <summary>Raised after a navigation to the agent UI finished (true = success).</summary>
    public event EventHandler<bool>? Navigated;

    /// <summary>Raised when the window was hidden into the notification area.</summary>
    public event EventHandler? HiddenToTray;

    public bool IsInitialized => _initialized;

    public bool? LastNavigationSucceeded { get; private set; }

    public string? WebViewVersion => _environment?.BrowserVersionString;

    public string? InitializationError { get; private set; }

    /// <summary>
    /// Single-flight initialization. ShowMainWindow and the server-ready path can both
    /// call this, and calling EnsureCoreWebView2Async twice with different environments
    /// throws "already initialized with a different CoreWebView2Environment".
    /// </summary>
    public async Task<bool> EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return true;
        }

        Task<bool> attempt;
        lock (_initLock)
        {
            attempt = _initialization ??= InitializeAsync();
        }

        var succeeded = await attempt.ConfigureAwait(true);

        if (!succeeded)
        {
            // Allow a later call to retry, e.g. after the WebView2 runtime is repaired.
            lock (_initLock)
            {
                _initialization = null;
            }
        }

        return succeeded;
    }

    private async Task<bool> InitializeAsync()
    {
        try
        {
            // Force the window and control handles so WebView2 can be created while hidden.
            _ = Handle;
            _ = _webView.Handle;

            _environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: AppPaths.WebViewDataDirectory,
                options: null).ConfigureAwait(true);

            await _webView.EnsureCoreWebView2Async(_environment).ConfigureAwait(true);

            Configure();
            _webView.NavigateToString(Assets.LoadingHtml);

            _initialized = true;
            _log.Info($"WebView2 initialized (runtime {WebViewVersion}).");
            return true;
        }
        catch (Exception error)
        {
            InitializationError = error.Message;
            _log.Error("WebView2 initialization failed", error);
            return false;
        }
    }

    private void Configure()
    {
        var core = _webView.CoreWebView2;

        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;

        core.NewWindowRequested += OnNewWindowRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += (_, e) =>
            _log.Error($"WebView2 process failed: {e.ProcessFailedKind} {e.Reason}");

        core.DocumentTitleChanged += (_, _) =>
        {
            var title = core.DocumentTitle;
            if (!string.IsNullOrWhiteSpace(title) && !string.Equals(title, "Pi Agent Desktop", StringComparison.Ordinal))
            {
                Text = $"{title} — Pi Agent Desktop {AppInfo.Version}";
            }
        };
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternal(e.Uri);
    }

    /// <summary>
    /// Records the id of the top-level navigation that targets the agent server.
    /// Navigating to the loading page first means a later <c>NavigationCompleted</c>
    /// can belong to a superseded navigation (which Chromium reports as
    /// ConnectionAborted), so completion must be matched by navigation id.
    /// </summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_agentAuthority is null || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (string.Equals(uri.Authority, _agentAuthority, StringComparison.OrdinalIgnoreCase))
        {
            _agentNavigationId = e.NavigationId;
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_agentNavigationId is null || e.NavigationId != _agentNavigationId)
        {
            return;
        }

        _agentNavigationId = null;
        LastNavigationSucceeded = e.IsSuccess;

        if (e.IsSuccess)
        {
            _log.Info($"Agent UI loaded: {_currentUrl}");
        }
        else
        {
            _log.Warn($"Agent UI navigation failed: {e.WebErrorStatus} ({_currentUrl})");
            _ = SetStatusAsync($"无法加载 pi agent 界面（{e.WebErrorStatus}）。", isError: true);
        }

        Navigated?.Invoke(this, e.IsSuccess);
    }

    /// <summary>Loads the agent UI, reusing the current document when the URL is unchanged.</summary>
    public async Task LoadAgentUiAsync(string url, bool forceReload = false)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (!await EnsureInitializedAsync().ConfigureAwait(true))
        {
            return;
        }

        // Re-check: initialization is asynchronous, so the window may be gone by now.
        if (IsDisposed || Disposing || _webView.IsDisposed || _webView.CoreWebView2 is null)
        {
            return;
        }

        if (!forceReload && string.Equals(_currentUrl, url, StringComparison.OrdinalIgnoreCase) && LastNavigationSucceeded == true)
        {
            return;
        }

        _currentUrl = url;
        _agentAuthority = new Uri(url).Authority;
        Text = $"Pi Agent Desktop {AppInfo.Version} — {_agentAuthority}";
        _webView.CoreWebView2.Navigate(url);
    }

    public async Task SetStatusAsync(string text, bool isError = false, string? hintHtml = null)
    {
        if (!_initialized || IsDisposed || Disposing || _webView.IsDisposed)
        {
            return;
        }

        try
        {
            var script = $"window.piDesktop && window.piDesktop.setStatus({ToJsString(text)}, {(isError ? "true" : "false")});";
            if (hintHtml is not null)
            {
                script += $"window.piDesktop && window.piDesktop.setHint({ToJsString(hintHtml)});";
            }

            await _webView.ExecuteScriptAsync(script).ConfigureAwait(true);
        }
        catch
        {
            /* the loading document may already be gone */
        }
    }

    public static void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            /* best effort */
        }
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        BringToFront();
    }

    public void HideToTray()
    {
        SaveWindowState();
        Hide();
        HiddenToTray?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Permits the window to actually close (used on application exit).</summary>
    public void AllowClose() => _allowClose = true;

    public void SaveWindowState()
    {
        if (WindowState == FormWindowState.Normal)
        {
            _config.WindowWidth = Width;
            _config.WindowHeight = Height;
        }

        _config.WindowMaximized = WindowState == FormWindowState.Maximized;
        _config.Save();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && _config.CloseToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        SaveWindowState();
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        if (WindowState == FormWindowState.Minimized && _config.MinimizeToTray && Visible)
        {
            HideToTray();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _webView.Dispose();
            }
            catch
            {
                /* ignore */
            }
        }

        base.Dispose(disposing);
    }

    private static string ToJsString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
