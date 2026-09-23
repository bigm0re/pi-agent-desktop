<#
.SYNOPSIS
    Renders docs/preview.png - an illustration of the window and tray menu.

.DESCRIPTION
    This is a DRAWN ILLUSTRATION, not a screen capture: the shell cannot be
    screenshotted from inside itself (the app cannot run while another instance
    hosts the session). The window chrome, the tray menu contents and the loading
    page are reproduced faithfully; the agent UI itself is the upstream pi-web UI
    and is not depicted.

    Replace this file with a real capture whenever convenient:
    run the app, press Win+Shift+S, save over docs/preview.png.

.EXAMPLE
    powershell -File tools\make-preview.ps1
#>
[CmdletBinding()]
param(
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not usable in a param() default under Windows PowerShell 5.1.
if (-not $OutFile) {
    $OutFile = Join-Path $PSScriptRoot '..\docs\preview.png'
}
Add-Type -AssemblyName System.Drawing

function New-Font {
    param(
        [string[]]$Families,
        [float]$Size,
        [System.Drawing.FontStyle]$Style = [System.Drawing.FontStyle]::Regular
    )
    foreach ($family in $Families) {
        try { return [System.Drawing.Font]::new($family, $Size, $Style) } catch { }
    }
    return [System.Drawing.Font]::new('Segoe UI', $Size, $Style)
}

function New-RoundedPath {
    param(
        [System.Drawing.RectangleF]$Rect,
        [float]$Radius
    )
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $Radius * 2
    $path.AddArc($Rect.X, $Rect.Y, $d, $d, 180, 90)
    $path.AddArc($Rect.Right - $d, $Rect.Y, $d, $d, 270, 90)
    $path.AddArc($Rect.Right - $d, $Rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($Rect.X, $Rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Get-Color {
    param([int]$R, [int]$G, [int]$B)
    return [System.Drawing.Color]::FromArgb(255, $R, $G, $B)
}

function Add-Text {
    param(
        [System.Drawing.Graphics]$Graphics,
        [string]$Text,
        [System.Drawing.Font]$Font,
        [System.Drawing.Color]$Color,
        [float]$X,
        [float]$Y,
        [string]$Align = 'Near'
    )
    $brush = [System.Drawing.SolidBrush]::new($Color)
    $size = $Graphics.MeasureString($Text, $Font)
    $px = $X
    if ($Align -eq 'Center') { $px = $X - ($size.Width / 2) }
    if ($Align -eq 'Far') { $px = $X - $size.Width }
    $Graphics.DrawString($Text, $Font, $brush, $px, $Y)
    $brush.Dispose()
}

$W = 1200
$H = 620

$bmp = [System.Drawing.Bitmap]::new($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$g.Clear((Get-Color 10 12 17))

$fTitle = New-Font -Families @('Segoe UI') -Size 9
$fBody = New-Font -Families @('Microsoft YaHei UI', 'Microsoft YaHei') -Size 9
$fBodyBold = New-Font -Families @('Microsoft YaHei UI', 'Microsoft YaHei') -Size 9 `
    -Style ([System.Drawing.FontStyle]::Bold)
$fHeading = New-Font -Families @('Segoe UI', 'Microsoft YaHei UI') -Size 12 `
    -Style ([System.Drawing.FontStyle]::Bold)
$fSmall = New-Font -Families @('Microsoft YaHei UI', 'Microsoft YaHei') -Size 8

# ---------------------------------------------------------------- window ---
$win = [System.Drawing.RectangleF]::new(40, 56, 680, 470)
$winPath = New-RoundedPath -Rect $win -Radius 10
$g.FillPath([System.Drawing.SolidBrush]::new((Get-Color 13 16 23)), $winPath)
$g.DrawPath([System.Drawing.Pen]::new((Get-Color 38 44 56), 1), $winPath)

# title bar (rounded on top, squared at the bottom)
$barPath = New-RoundedPath -Rect ([System.Drawing.RectangleF]::new(40, 56, 680, 36)) -Radius 10
$barBrush = [System.Drawing.SolidBrush]::new((Get-Color 20 24 33))
$g.FillPath($barBrush, $barPath)
$g.FillRectangle($barBrush, 40, 74, 680, 18)

Add-Text $g 'Pi Agent Desktop 0.1.1 — 127.0.0.1:30141' $fTitle (Get-Color 201 209 224) 56 67
$glyphs = @(@{t = '─'; x = 668}, @{t = '□'; x = 692}, @{t = '✕'; x = 716})
foreach ($glyph in $glyphs) {
    Add-Text $g $glyph.t $fTitle (Get-Color 122 134 153) $glyph.x 66
}

# loading page: pi logo
$logo = [System.Drawing.RectangleF]::new(348, 168, 68, 68)
$g.FillPath([System.Drawing.SolidBrush]::new((Get-Color 32 38 50)), (New-RoundedPath -Rect $logo -Radius 16))
$white = [System.Drawing.SolidBrush]::new((Get-Color 255 255 255))
$g.FillRectangle($white, 363, 190, 38, 8)   # top bar
$g.FillRectangle($white, 369, 196, 8, 26)   # left leg
$g.FillRectangle($white, 387, 196, 8, 26)   # right leg

Add-Text $g 'Pi Agent Desktop' $fHeading (Get-Color 230 233 240) 380 252 -Align 'Center'
Add-Text $g '正在启动 pi agent 服务…' $fBody (Get-Color 143 155 179) 380 282 -Align 'Center'

# progress bar
$g.FillPath([System.Drawing.SolidBrush]::new((Get-Color 30 36 48)), `
    (New-RoundedPath -Rect ([System.Drawing.RectangleF]::new(260, 312, 240, 4)) -Radius 2))
$g.FillPath([System.Drawing.SolidBrush]::new((Get-Color 79 124 255)), `
    (New-RoundedPath -Rect ([System.Drawing.RectangleF]::new(260, 312, 96, 4)) -Radius 2))

Add-Text $g '窗口：WinForms + WebView2 宿主（加载完成后切换为原版 pi-web 界面）' `
    $fSmall (Get-Color 93 103 125) 380 372 -Align 'Center'

# ------------------------------------------------------------ tray menu ---
$menu = [System.Drawing.RectangleF]::new(768, 56, 392, 470)
$menuPath = New-RoundedPath -Rect $menu -Radius 8
$g.FillPath([System.Drawing.SolidBrush]::new((Get-Color 31 36 48)), $menuPath)
$g.DrawPath([System.Drawing.Pen]::new((Get-Color 58 65 82), 1), $menuPath)

$items = @(
    @{t = '状态：运行中 · 端口 30141'; k = 'dim' },
    @{sep = $true },
    @{t = '打开 Pi Agent'; k = 'bold' },
    @{t = '在浏览器中打开' },
    @{t = '复制访问地址' },
    @{t = '重新加载界面' },
    @{sep = $true },
    @{t = '重新启动服务' },
    @{t = '打开日志文件' },
    @{t = '打开配置文件' },
    @{sep = $true },
    @{t = '随 Windows 启动'; k = 'check' },
    @{t = '启动时最小化到托盘' },
    @{t = '关闭窗口时最小化到托盘' },
    @{sep = $true },
    @{t = '检查插件更新环境（npmCommand）' },
    @{t = '安装 / 更新 Pi Web…' },
    @{sep = $true },
    @{t = '版本 0.1.1'; k = 'dim' },
    @{t = '退出' }
)

$rowY = 70
foreach ($item in $items) {
    if ($item.sep) {
        $g.DrawLine([System.Drawing.Pen]::new((Get-Color 58 65 82), 1), 776, $rowY + 6, 1152, $rowY + 6)
        $rowY += 12
        continue
    }

    $colour = Get-Color 226 232 244
    $font = $fBody
    if ($item.k -eq 'dim') { $colour = Get-Color 122 134 153 }
    if ($item.k -eq 'bold') { $font = $fBodyBold }
    if ($item.k -eq 'check') {
        Add-Text $g '✓' $fBody (Get-Color 111 155 255) 778 $rowY
    }

    Add-Text $g $item.t $font $colour 796 $rowY
    $rowY += 21
}

Add-Text $g '托盘右键菜单' $fSmall (Get-Color 93 103 125) 964 534 -Align 'Center'

# ---------------------------------------------------------------- caption ---
Add-Text $g '示意图：外壳窗口（左）与托盘菜单（右）。窗口内的智能体界面为原版 pi-web，此处仅画出启动页。' `
    $fSmall (Get-Color 93 103 125) 600 578 -Align 'Center'

$resolved = [System.IO.Path]::GetFullPath($OutFile)
$dir = Split-Path $resolved -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$bmp.Save($resolved, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()

Write-Host "wrote $resolved ($([math]::Round((Get-Item $resolved).Length / 1KB, 1)) KB)"
