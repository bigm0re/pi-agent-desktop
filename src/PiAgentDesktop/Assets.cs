using System.Drawing;
using System.Reflection;

namespace PiAgentDesktop;

/// <summary>Embedded resources: application icon and the pre-navigation loading page.</summary>
internal static class Assets
{
    private static readonly Lazy<Icon> ApplicationIconLazy = new(() => LoadIcon(SystemInformation.IconSize));
    private static readonly Lazy<Icon> TrayIconLazy = new(() => LoadIcon(SystemInformation.SmallIconSize));

    public static Icon ApplicationIcon => ApplicationIconLazy.Value;

    public static Icon TrayIcon => TrayIconLazy.Value;

    private static Icon LoadIcon(Size size)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("PiAgentDesktop.app.ico");
            if (stream is not null)
            {
                return new Icon(stream, size);
            }
        }
        catch
        {
            /* fall through to the system icon */
        }

        return SystemIcons.Application;
    }

    /// <summary>
    /// Shown inside the WebView2 host until the pi-web server answers. Exposes
    /// <c>window.piDesktop</c> so the shell can push status text into the page.
    /// </summary>
    public const string LoadingHtml = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Pi Agent Desktop</title>
<style>
  :root { color-scheme: dark; }
  html, body { height: 100%; margin: 0; }
  body {
    background: radial-gradient(1200px 600px at 50% -10%, #1c2434 0%, #0d1017 62%, #0a0c11 100%);
    color: #e6e9f0;
    font: 14px/1.6 "Segoe UI", system-ui, -apple-system, "Microsoft YaHei", sans-serif;
    display: flex; align-items: center; justify-content: center;
    -webkit-user-select: none; user-select: none;
  }
  .card { display: flex; flex-direction: column; align-items: center; gap: 18px; padding: 40px 48px; }
  .logo {
    width: 68px; height: 68px; border-radius: 18px;
    background: linear-gradient(160deg, #262c3e, #11141c);
    display: flex; align-items: center; justify-content: center;
    box-shadow: 0 12px 34px rgba(0,0,0,.5);
  }
  .logo svg { width: 38px; height: 38px; fill: #ffffff; }
  h1 { font-size: 17px; font-weight: 600; margin: 0; letter-spacing: .3px; }
  .bar { width: 240px; height: 3px; border-radius: 2px; background: #1e2430; overflow: hidden; }
  .bar i {
    display: block; height: 100%; width: 40%; border-radius: 2px;
    background: linear-gradient(90deg, #4f7cff, #8a5cff);
    animation: slide 1.25s ease-in-out infinite;
  }
  @keyframes slide { 0% { transform: translateX(-110%); } 100% { transform: translateX(310%); } }
  .status { font-size: 13px; color: #8f9bb3; min-height: 20px; text-align: center; max-width: 460px; }
  .status.err { color: #ff8f8f; }
  .hint { font-size: 12px; color: #5d677d; min-height: 18px; text-align: center; }
  a { color: #6f9bff; text-decoration: none; }
  a:hover { text-decoration: underline; }
</style>
</head>
<body>
  <div class="card">
    <div class="logo">
      <svg viewBox="0 0 100 100" aria-hidden="true">
        <rect x="23.5" y="28.5" width="53" height="11" rx="1.5"/>
        <rect x="31.5" y="35.5" width="11" height="39" rx="1.5"/>
        <rect x="57.5" y="35.5" width="11" height="39" rx="1.5"/>
      </svg>
    </div>
    <h1>Pi Agent Desktop</h1>
    <div class="bar"><i id="bar"></i></div>
    <div class="status" id="status">正在启动 pi agent 服务…</div>
    <div class="hint" id="hint"></div>
  </div>
<script>
  window.piDesktop = {
    setStatus(text, isError) {
      const el = document.getElementById('status');
      el.textContent = text || '';
      el.className = 'status' + (isError ? ' err' : '');
    },
    setHint(html) {
      document.getElementById('hint').innerHTML = html || '';
    },
    setBusy(busy) {
      document.getElementById('bar').style.visibility = busy ? 'visible' : 'hidden';
    }
  };
</script>
</body>
</html>
""";
}
