param([switch]$Keep)
$ErrorActionPreference = 'Stop'
trap {
    $msg = 'The build could not start or crashed.' + "`n`n" + $_.Exception.Message + "`n`n" + [string]$_.InvocationInfo.PositionMessage
    try { Add-Content -LiteralPath (Join-Path $env:TEMP 'FortniteUnityConverter_build.log') -Value ("`r`nCRASH: " + $msg) } catch {}
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show($msg + "`n`n" + 'Log: ' + (Join-Path $env:TEMP 'FortniteUnityConverter_build.log'), 'Fortnite Unity Converter') | Out-Null
    } catch {}
    exit 1
}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
Add-Type -Namespace Win32 -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);'
try { [Win32.Shell]::SetCurrentProcessExplicitAppUserModelID('FortniteUnityConverter.Setup') } catch {}
Add-Type -Namespace Win32 -Name Dwm -MemberDefinition '[DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);'
try {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TaskbarProps
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Sequential)]
    public struct PROPVARIANT { public ushort vt; public ushort r1; public ushort r2; public ushort r3; public IntPtr p1; public IntPtr p2; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint c);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY k);
        [PreserveSig] int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
        [PreserveSig] int SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
        [PreserveSig] int Commit();
    }
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore ps);
    [DllImport("propsys.dll", CharSet = CharSet.Unicode)] static extern int InitPropVariantFromString(string s, out PROPVARIANT v);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT v);
    static void Put(IPropertyStore ps, uint pid, string value)
    {
        PROPERTYKEY k = new PROPERTYKEY();
        k.fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        k.pid = pid;
        PROPVARIANT v;
        InitPropVariantFromString(value, out v);
        ps.SetValue(ref k, ref v);
        PropVariantClear(ref v);
    }
    public static void Apply(IntPtr hwnd, string appId, string command, string iconPath, string name)
    {
        Guid iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        IPropertyStore ps;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out ps) != 0) return;
        Put(ps, 5, appId);
        Put(ps, 2, command);
        Put(ps, 3, iconPath);
        Put(ps, 4, name);
        ps.Commit();
        Marshal.ReleaseComObject(ps);
    }
}
'@
} catch {}
try {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WinHelp
{
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}
'@
} catch {}
function Set-DarkTitleBar($handle) {
    try {
        $on = 1
        if ([Win32.Dwm]::DwmSetWindowAttribute($handle, 20, [ref]$on, 4) -ne 0) { [Win32.Dwm]::DwmSetWindowAttribute($handle, 19, [ref]$on, 4) | Out-Null }
        $caption = 0x120A0C
        $text = 0xFFFFFF
        $border = 0x261C2A
        [Win32.Dwm]::DwmSetWindowAttribute($handle, 35, [ref]$caption, 4) | Out-Null
        [Win32.Dwm]::DwmSetWindowAttribute($handle, 36, [ref]$text, 4) | Out-Null
        [Win32.Dwm]::DwmSetWindowAttribute($handle, 34, [ref]$border, 4) | Out-Null
    } catch {}
}

$App  = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $App
$Name = 'Fortnite Unity Converter'
$Log  = Join-Path $env:TEMP 'FortniteUnityConverter_build.log'
Set-Location -LiteralPath $App
Set-Content -LiteralPath $Log -Value ('Build started ' + (Get-Date))
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:PIP_DISABLE_PIP_VERSION_CHECK = '1'
$env:npm_config_audit = 'false'
$env:npm_config_fund = 'false'
$env:npm_config_update_notifier = 'false'

$script:Shown    = 0.0
$script:Target   = 0.0
$script:Abort    = $false
$script:CanClose = $false

function Col($hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

$script:FontKeep = @()
$script:SfOk = $false
function Get-AppFont($file, $size, $fallbackStyle) {
    try {
        $pfc = New-Object System.Drawing.Text.PrivateFontCollection
        $pfc.AddFontFile((Join-Path $App ('assets\fonts\' + $file)))
        $script:FontKeep += $pfc
        $script:SfOk = $true
        return New-Object System.Drawing.Font($pfc.Families[0], [single]$size, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)
    } catch {
        return New-Object System.Drawing.Font('Segoe UI', [single]$size, $fallbackStyle)
    }
}
function Get-StyleFont($size, $style) {
    if ($style -eq [System.Drawing.FontStyle]::Bold) { return Get-AppFont 'SF-Pro-Text-Bold.otf' $size $style }
    return Get-AppFont 'SF-Pro-Text-Regular.otf' $size $style
}

function Enable-Drag($control, $window) {
    $control.Add_MouseDown({
        param($s, $e)
        if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) {
            [WinHelp]::ReleaseCapture() | Out-Null
            [WinHelp]::SendMessage($window.Handle, 0xA1, 2, 0) | Out-Null
        }
    }.GetNewClosure())
}

function Set-RoundForm($f, $radius) {
    try { $f.Region = [System.Drawing.Region]::FromHrgn([WinHelp]::CreateRoundRectRgn(0, 0, $f.Width + 1, $f.Height + 1, $radius, $radius)) } catch {}
}

function Add-FormChrome($f, $radius) {
    Set-RoundForm $f $radius
    $f.Add_Paint({
        param($s, $e)
        $e.Graphics.SmoothingMode = 'AntiAlias'
        $pen = New-Object System.Drawing.Pen((Col '#2F2D3C'), 1)
        $r = 16
        $w = $s.Width - 1
        $h = $s.Height - 1
        $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
        $gp.AddArc(0, 0, $r, $r, 180, 90)
        $gp.AddArc($w - $r, 0, $r, $r, 270, 90)
        $gp.AddArc($w - $r, $h - $r, $r, $r, 0, 90)
        $gp.AddArc(0, $h - $r, $r, $r, 90, 90)
        $gp.CloseFigure()
        $e.Graphics.DrawPath($pen, $gp)
        $gp.Dispose()
        $pen.Dispose()
    })
    Enable-Drag $f $f
}

function New-RoundPath($x, $y, $w, $h) {
    $r = $h
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $r, $r, 90, 180)
    $p.AddArc($x + $w - $r, $y, $r, $r, 270, 180)
    $p.CloseFigure()
    return $p
}

function New-Label($text, $x, $y, $w, $h, $size, $style, $color) {
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $text
    $l.Location = New-Object System.Drawing.Point($x, $y)
    $l.Size = New-Object System.Drawing.Size($w, $h)
    $l.Font = Get-StyleFont $size $style
    $l.UseCompatibleTextRendering = $true
    $l.ForeColor = Col $color
    $l.BackColor = [System.Drawing.Color]::Transparent
    return $l
}

function New-DialogButton($text, $tag, $primary) {
    $b = New-Object System.Windows.Forms.Button
    $b.Text = $text
    $b.Tag = $tag
    $b.Size = New-Object System.Drawing.Size(120, 34)
    $b.FlatStyle = 'Flat'
    $b.Cursor = [System.Windows.Forms.Cursors]::Hand
    $b.Font = Get-StyleFont 10 ([System.Drawing.FontStyle]::Bold)
    $b.UseCompatibleTextRendering = $true
    $b.ForeColor = [System.Drawing.Color]::White
    if ($primary) {
        $b.FlatAppearance.BorderSize = 0
        $b.BackColor = Col '#953bf8'
        $b.FlatAppearance.MouseOverBackColor = Col '#a65cf9'
        $b.FlatAppearance.MouseDownBackColor = Col '#7f2fe0'
    } else {
        $b.FlatAppearance.BorderSize = 1
        $b.FlatAppearance.BorderColor = Col '#2F2D3C'
        $b.BackColor = Col '#1D1B24'
        $b.FlatAppearance.MouseOverBackColor = Col '#252332'
        $b.FlatAppearance.MouseDownBackColor = Col '#16141E'
    }
    $b.Add_Click({ param($s, $e) $script:DlgResult = $s.Tag; $s.FindForm().Close() })
    return $b
}

function Show-Dialog($message, $primaryText, $secondaryText) {
    $script:DlgResult = 'no'
    $d = New-Object System.Windows.Forms.Form
    $d.Text = $Name
    $d.FormBorderStyle = 'None'
    $d.ShowInTaskbar = $false
    $d.BackColor = Col '#0D0C12'
    if ($form.Icon) { $d.Icon = $form.Icon }
    $lbl = New-Object System.Windows.Forms.Label
    $lbl.AutoSize = $true
    $lbl.MaximumSize = New-Object System.Drawing.Size(400, 0)
    $lbl.Location = New-Object System.Drawing.Point(28, 30)
    $lbl.Font = Get-StyleFont 10 ([System.Drawing.FontStyle]::Regular)
    $lbl.UseCompatibleTextRendering = $true
    $lbl.ForeColor = Col '#E2DFF0'
    $lbl.BackColor = [System.Drawing.Color]::Transparent
    $lbl.Text = $message
    $d.Controls.Add($lbl)
    $textHeight = $lbl.GetPreferredSize((New-Object System.Drawing.Size(400, 0))).Height
    $d.ClientSize = New-Object System.Drawing.Size(456, ($textHeight + 28 + 84))
    $y = $d.ClientSize.Height - 58
    $primary = New-DialogButton $primaryText 'yes' $true
    $primary.Location = New-Object System.Drawing.Point(308, $y)
    $d.Controls.Add($primary)
    $d.AcceptButton = $primary
    if ($secondaryText) {
        $secondary = New-DialogButton $secondaryText 'no' $false
        $secondary.Location = New-Object System.Drawing.Point(176, $y)
        $d.Controls.Add($secondary)
        $d.CancelButton = $secondary
    } else {
        $d.CancelButton = $primary
    }
    Add-FormChrome $d 16
    Enable-Drag $lbl $d
    $d.Add_Shown({ param($s, $e) Set-DarkTitleBar $s.Handle })
    if ($form.Visible) {
        $d.StartPosition = 'CenterParent'
        $d.ShowDialog($form) | Out-Null
    } else {
        $d.StartPosition = 'CenterScreen'
        $d.ShowDialog() | Out-Null
    }
    $d.Dispose()
    return ($script:DlgResult -eq 'yes')
}

$form = New-Object System.Windows.Forms.Form
$form.Text = $Name
$form.ClientSize = New-Object System.Drawing.Size(560, 252)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'None'
$form.BackColor = Col '#0D0C12'
[System.Windows.Forms.Form].GetProperty('DoubleBuffered', [System.Reflection.BindingFlags]'NonPublic,Instance').SetValue($form, $true, $null)
$logoBmp = $null
try {
    $tmp = [System.Drawing.Image]::FromFile((Join-Path $App 'assets\logo.png'))
    $logoBmp = New-Object System.Drawing.Bitmap($tmp, 128, 128)
    $tmp.Dispose()
    $form.Icon = [System.Drawing.Icon]::FromHandle($logoBmp.GetHicon())
} catch {}
try { $form.Icon = New-Object System.Drawing.Icon((Join-Path $App 'assets\icon.ico')) } catch {}
Add-FormChrome $form 16

$hdrName = New-Label $Name 14 11 360 18 9 ([System.Drawing.FontStyle]::Regular) '#A8A4C0'
$form.Controls.Add($hdrName)
Enable-Drag $hdrName $form
$closeBtn = New-Object System.Windows.Forms.Label
$closeBtn.Size = New-Object System.Drawing.Size(46, 36)
$closeBtn.Location = New-Object System.Drawing.Point(514, 0)
$closeBtn.TextAlign = 'MiddleCenter'
$closeBtn.ForeColor = Col '#E2DFF0'
$closeBtn.BackColor = [System.Drawing.Color]::Transparent
$closeBtn.Cursor = [System.Windows.Forms.Cursors]::Hand
try { $closeBtn.Font = New-Object System.Drawing.Font('Segoe MDL2 Assets', 9); $closeBtn.Text = [string][char]0xE8BB } catch { $closeBtn.Text = 'X' }
$closeBtn.Add_MouseEnter({ param($s, $e) $s.BackColor = Col '#E81123' })
$closeBtn.Add_MouseLeave({ param($s, $e) $s.BackColor = [System.Drawing.Color]::Transparent })
$closeBtn.Add_Click({ $form.Close() })
$form.Controls.Add($closeBtn)

if ($logoBmp) {
    $pic = New-Object System.Windows.Forms.PictureBox
    $pic.Image = $logoBmp
    $pic.SizeMode = 'Zoom'
    $pic.Location = New-Object System.Drawing.Point(28, 62)
    $pic.Size = New-Object System.Drawing.Size(60, 60)
    $pic.BackColor = [System.Drawing.Color]::Transparent
    $form.Controls.Add($pic)
}

$bold = [System.Drawing.FontStyle]::Bold
$reg  = [System.Drawing.FontStyle]::Regular
$form.Controls.Add((New-Label $Name 104 66 430 30 15 $bold '#ffffff'))
$form.Controls.Add((New-Label "Sit tight, we're getting everything ready for you." 104 98 430 22 10 $reg '#A8A4C0'))

$task = New-Label 'Starting...' 28 148 410 22 10 $reg '#E2DFF0'
$pct  = New-Label '0%' 452 146 80 24 12 $bold '#ffffff'
$pct.TextAlign = 'MiddleRight'
$form.Controls.Add($task)
$form.Controls.Add($pct)

$bar = New-Object System.Windows.Forms.Panel
$bar.Location = New-Object System.Drawing.Point(28, 180)
$bar.Size = New-Object System.Drawing.Size(504, 12)
$bar.BackColor = Col '#0D0C12'
[System.Windows.Forms.Panel].GetProperty('DoubleBuffered', [System.Reflection.BindingFlags]'NonPublic,Instance').SetValue($bar, $true, $null)
$bar.Add_Paint({
    param($s, $e)
    $g = $e.Graphics
    $g.SmoothingMode = 'AntiAlias'
    $w = $s.Width - 1
    $h = $s.Height - 1
    $trackPath = New-RoundPath 0 0 $w $h
    $trackBrush = New-Object System.Drawing.SolidBrush((Col '#1D1B24'))
    $g.FillPath($trackBrush, $trackPath)
    $fw = [int]($w * $script:Shown / 100)
    if ($fw -gt 0) {
        if ($fw -lt $h) { $fw = $h }
        $fillPath = New-RoundPath 0 0 $fw $h
        $rect = New-Object System.Drawing.Rectangle(0, 0, $fw, $h)
        $fillBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, (Col '#b27af9'), (Col '#953bf8'), 0.0)
        $g.FillPath($fillBrush, $fillPath)
        $fillBrush.Dispose()
        $fillPath.Dispose()
    }
    $trackBrush.Dispose()
    $trackPath.Dispose()
})
$form.Controls.Add($bar)

$note = New-Label 'Please do not close this window. It is doing the work.' 28 208 504 20 9 $reg '#686490'
$form.Controls.Add($note)

$form.Add_FormClosing({
    param($s, $e)
    if ($script:CanClose) { return }
    $e.Cancel = $true
    if (Show-Dialog 'Stop the build?' 'Stop' 'Keep going') { $script:Abort = $true }
})

function Pump {
    $d = $script:Target - $script:Shown
    if ([math]::Abs($d) -gt 0.05) { $script:Shown += $d * 0.12 } else { $script:Shown = $script:Target }
    $pct.Text = ('{0}%' -f [int][math]::Round($script:Shown))
    $bar.Invalidate()
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 25
}

function Settle($value) {
    $script:Target = $value
    while ($script:Shown -lt ($value - 0.4)) { Pump }
    $script:Shown = $value
    Pump
}

function Start-Hidden($arguments, $workDir) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $env:ComSpec
    $psi.Arguments = $arguments
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    if ($workDir) { $psi.WorkingDirectory = $workDir }
    return [System.Diagnostics.Process]::Start($psi)
}

function Test-Tool($cmdline) {
    $p = Start-Hidden ('/c ' + $cmdline + ' >nul 2>&1') $null
    $p.WaitForExit()
    return ($p.ExitCode -eq 0)
}

function Invoke-Step($text, $from, $to, $expectSec, $cmdline) {
    $task.Text = $text
    if ($script:Target -lt $from) { $script:Target = $from }
    Add-Content -LiteralPath $Log -Value ("`r`n=== " + $text + " ===")
    $p = Start-Hidden ('/c ' + $cmdline + ' >> "' + $Log + '" 2>&1') $App
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not $p.HasExited) {
        if ($script:Abort) {
            & taskkill.exe /PID $p.Id /T /F | Out-Null
            throw 'ABORT'
        }
        $f = 1 - [math]::Exp(-$sw.Elapsed.TotalSeconds / $expectSec)
        $script:Target = $from + ($to - $from) * 0.92 * $f
        Pump
    }
    $p.WaitForExit()
    return $p.ExitCode
}

function Invoke-Parallel($text, $from, $to, $expectSec, $jobs) {
    $task.Text = $text
    if ($script:Target -lt $from) { $script:Target = $from }
    $procs = [ordered]@{}
    foreach ($j in $jobs) {
        $jl = $Log + '.' + $j.Name
        Set-Content -LiteralPath $jl -Value ''
        $proc = Start-Hidden ('/c (' + $j.Cmd + ') >> "' + $jl + '" 2>&1') $App
        $procs[$j.Name] = @{ P = $proc; Log = $jl }
    }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $running = @($procs.Values | Where-Object { -not $_.P.HasExited })
        if ($running.Count -eq 0) { break }
        if ($script:Abort) {
            foreach ($r in $running) { & taskkill.exe /PID $r.P.Id /T /F | Out-Null }
            throw 'ABORT'
        }
        $f = 1 - [math]::Exp(-$sw.Elapsed.TotalSeconds / $expectSec)
        $script:Target = $from + ($to - $from) * 0.92 * $f
        Pump
    }
    $codes = @{}
    foreach ($k in @($procs.Keys)) {
        $procs[$k].P.WaitForExit()
        $codes[$k] = $procs[$k].P.ExitCode
        Add-Content -LiteralPath $Log -Value ("`r`n=== " + $k + " ===")
        try { Add-Content -LiteralPath $Log -Value ([string](Get-Content -LiteralPath $procs[$k].Log -Raw)) } catch {}
        try { Remove-Item -LiteralPath $procs[$k].Log -Force } catch {}
    }
    return $codes
}

function Run-Step($text, $from, $to, $expectSec, $cmdline) {
    $code = Invoke-Step $text $from $to $expectSec $cmdline
    if ($code -ne 0) { throw ('FAIL: ' + $text) }
    Settle $to
}

$form.Show()
Set-DarkTitleBar $form.Handle
try {
    [TaskbarProps]::Apply($form.Handle, 'FortniteUnityConverter.Setup', ('"' + (Join-Path $Root 'Build.bat') + '"'), ((Join-Path $App 'assets\icon.ico') + ',0'), $Name)
} catch {}
$form.Activate()
[System.Windows.Forms.Application]::DoEvents()

$exitCode = 0
try {
    $task.Text = 'Checking your computer...'
    Settle 3
    if (-not (Test-Tool 'python --version')) { throw ('MSG:Python was not found on this computer.' + "`n`n" + 'Install it from python.org (tick "Add Python to PATH"), then run Build.bat again. Nothing was changed.') }
    if (-not (Test-Tool 'npm --version'))    { throw ('MSG:Node.js was not found on this computer.' + "`n`n" + 'Install it from nodejs.org, then run Build.bat again. Nothing was changed.') }

    if (Test-Path -LiteralPath (Join-Path $App 'engine\bin')) { Remove-Item -LiteralPath (Join-Path $App 'engine\bin') -Recurse -Force }

    # The engine, the file browser helper and the npm packages do not depend on each other,
    # so they are built at the same time instead of one after another.
    $pyCmd = 'python -m pip install -q numpy && (python -m PyInstaller --version >nul 2>&1 || python -m pip install -q pyinstaller) && python -m PyInstaller --noconfirm --clean --log-level WARN --onefile --console --name FortniteUnityPorter --distpath engine\bin --workpath build_py --specpath build_py --exclude-module tkinter --exclude-module unittest --exclude-module pydoc --exclude-module doctest --exclude-module sqlite3 --exclude-module xmlrpc --exclude-module lib2to3 --exclude-module curses --exclude-module turtle --exclude-module idlelib engine\FortniteUnityPorter.py'
    $codes = Invoke-Parallel 'Building the engine, file browser and app parts... this can take a few minutes' 3 88 150 @(
        @{ Name = 'engine'; Cmd = $pyCmd },
        @{ Name = 'helper'; Cmd = 'powershell -NoProfile -ExecutionPolicy Bypass -File helper\build-helper.ps1' },
        @{ Name = 'npm';    Cmd = 'npm install --no-audit --no-fund --prefer-offline --loglevel=error' }
    )
    if ($codes['engine'] -ne 0) { throw 'FAIL: packaging the engine' }
    if ($codes['npm'] -ne 0)    { throw 'FAIL: downloading app components' }
    $helperCode = $codes['helper']
    Settle 88

    if (Test-Path -LiteralPath (Join-Path $App 'dist')) { Remove-Item -LiteralPath (Join-Path $App 'dist') -Recurse -Force }
    Run-Step 'Building the app... almost there' 88 98 60 'npm run pack'

    $task.Text = 'Putting the finishing touches on...'
    $built = Join-Path $App ('dist\' + $Name + '.exe')
    if (-not (Test-Path -LiteralPath $built)) { throw 'FAIL: the finished app was not found' }
    if ($Keep) {
        $outDir = Join-Path $Root 'release'
        if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
        New-Item -ItemType Directory -Path $outDir | Out-Null
    } else {
        $outDir = $Root
    }
    $exePath = Join-Path $outDir ($Name + '.exe')
    Copy-Item -LiteralPath $built -Destination $exePath -Force
    if (-not (Test-Path -LiteralPath $exePath)) { throw 'FAIL: could not copy the finished app' }
    if ($Keep) {
        foreach ($d in 'build_py', 'dist', 'engine\bin') {
            $path = Join-Path $App $d
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        }
    }
    Settle 100

    if ($helperCode -ne 0) {
        if ($helperCode -eq 10) {
            $why = "the .NET 10 SDK was not found. Install it from dotnet.microsoft.com, then extract the zip again and run Build.bat to add it."
        } else {
            $first = ''
            try { $first = [string](Select-String -LiteralPath $Log -Pattern ' error ' -SimpleMatch | Select-Object -First 1).Line } catch {}
            if ($first.Length -gt 300) { $first = $first.Substring(0, 300) }
            $why = 'it could not be built. Everything else works.'
            if ($first) { $why += ' First error: ' + $first.Trim() + '.' }
            $why += ' The details are in ' + $Log
        }
        Show-Dialog ('The app is ready, but the Files tab was left out because ' + $why) 'OK' $null | Out-Null
    }

    $task.Text = 'All done! Opening the app...'
    $note.Text = 'Your app is ready: ' + $Name + '.exe'
    $script:CanClose = $true
    $wait = [System.Diagnostics.Stopwatch]::StartNew()
    while ($wait.ElapsedMilliseconds -lt 1500) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 40
    }
    try {
        Start-Process -FilePath $exePath -WorkingDirectory $outDir
    } catch {
        Show-Dialog ('The app is built but could not be opened automatically. Run "' + $Name + '.exe" yourself.') 'OK' $null | Out-Null
    }
}
catch {
    $m = $_.Exception.Message
    $script:CanClose = $true
    if ($m -eq 'ABORT') {
        $exitCode = 2
    } elseif ($m.StartsWith('MSG:')) {
        $exitCode = 1
        Show-Dialog ($m.Substring(4)) 'OK' $null | Out-Null
    } else {
        $exitCode = 1
        $tail = ''
        try { $tail = (Get-Content -LiteralPath $Log -Tail 8) -join "`n" } catch {}
        $text = 'Something went wrong and the build stopped. Nothing was deleted.' + "`n`n" + $m + "`n`n" + 'Last lines of the log:' + "`n" + $tail + "`n`n" + 'Full log: ' + $Log
        Show-Dialog $text 'OK' $null | Out-Null
    }
}
try { $form.Close() } catch {}
if ($exitCode -eq 0 -and -not $Keep) {
    Set-Location -LiteralPath $Root
    $bat = Join-Path $Root 'Build.bat'
    $cmd = '/c ping -n 4 127.0.0.1 >nul & ie4uinit.exe -show >nul 2>&1 & rmdir /s /q "' + $App + '" & del /f /q "' + $bat + '"'
    Start-Hidden $cmd $Root | Out-Null
}
exit $exitCode
