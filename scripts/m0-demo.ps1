<#
.SYNOPSIS
    The M0 demo with nothing simulated: edit offline, reconnect, sync.

.DESCRIPTION
    Runs the real GateKeeper.Server as its own process and the real GateKeeper.Desktop window, drives the
    window through Windows UI Automation, and goes offline by killing the server process. A second gate
    then proves the edit reached the server. Windows only; the cross-platform equivalent of this flow is
    tests/GateKeeper.Desktop.Tests (headless), which runs in CI.

    Build first:  dotnet build Leeway.slnx
    Run:          powershell -ExecutionPolicy Bypass -File scripts/m0-demo.ps1
    Add -CaptureDir <dir> to save window screenshots.
    Exits non-zero if any step does not show what it should.
#>
param(
    [string]$CaptureDir,
    [int]$Port = 5180
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class LeewayDemoNative {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
[LeewayDemoNative]::SetProcessDPIAware() | Out-Null  # So screenshots are not cropped on scaled displays.

$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $env:TEMP ("leeway-m0-demo-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$url = "http://127.0.0.1:$Port"
$dotnetHome = Join-Path $env:USERPROFILE '.dotnet'
$dotnet = if (Test-Path (Join-Path $dotnetHome 'dotnet.exe')) { Join-Path $dotnetHome 'dotnet.exe' } else { 'dotnet' }
$serverDll = Join-Path $root 'samples\GateKeeper.Server\bin\Debug\net10.0\GateKeeper.Server.dll'
$desktopDll = Join-Path $root 'samples\GateKeeper.Desktop\bin\Debug\net10.0\GateKeeper.Desktop.dll'
foreach ($dll in @($serverDll, $desktopDll)) {
    if (-not (Test-Path $dll)) { throw "Missing $dll. Run: dotnet build Leeway.slnx" }
}

$AutomationElement = [System.Windows.Automation.AutomationElement]
$script:Processes = @()
$script:StepNumber = 0

function Start-Server {
    $p = Start-Process -FilePath $dotnet -PassThru -WindowStyle Hidden -WorkingDirectory $work `
        -ArgumentList @("`"$serverDll`"", '--urls', $url, '--ConnectionStrings:GateKeeper', "`"Data Source=$work\server.db`"", '--contentRoot', "`"$work`"")
    $script:Processes += $p
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-RestMethod -Uri "$url/tables/zone" -TimeoutSec 2 | Out-Null
            return $p
        }
        catch {
            if ($p.HasExited) { throw "The server exited with code $($p.ExitCode)." }
            Start-Sleep -Milliseconds 250
        }
    }
    throw 'The server did not start.'
}

function Start-Gate([string]$name) {
    $p = Start-Process -FilePath $dotnet -PassThru -WorkingDirectory $work `
        -ArgumentList @("`"$desktopDll`"", "--server=$url/", "`"--db=$work\$name.db`"")
    $script:Processes += $p
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        $p.Refresh()
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
            return [PSCustomObject]@{ Name = $name; Process = $p; Window = $AutomationElement::FromHandle($p.MainWindowHandle) }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "The window for $name did not appear."
}

function Find-Control($gate, [string]$id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::AutomationIdProperty, $id)
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $element = $gate.Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 200
    }
    throw "No control '$id' in $($gate.Name)."
}

function Read-Text($gate, [string]$id) { (Find-Control $gate $id).Current.Name }

function Click($gate, [string]$id) {
    (Find-Control $gate $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Set-Notes($gate, [string]$text) {
    (Find-Control $gate 'NotesBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
}

function Read-Zones($gate) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $texts = (Find-Control $gate 'ZoneList').FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    ($texts | ForEach-Object { $_.Current.Name }) -join ' | '
}

function Wait-Status($gate, [string]$expected) {
    $deadline = (Get-Date).AddSeconds(40)
    do {
        $actual = Read-Text $gate 'StatusText'
        if ($actual -like $expected) { return $actual }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    throw "$($gate.Name): expected status '$expected' but it shows '$actual'."
}

function Assert-Like([string]$actual, [string]$expected, [string]$what) {
    if ($actual -notlike $expected) { throw "$what`: expected '$expected' but got '$actual'." }
}

function Save-Capture($gate, [string]$file) {
    if (-not $CaptureDir) { return }
    New-Item -ItemType Directory -Force -Path $CaptureDir | Out-Null
    $r = $gate.Window.Current.BoundingRectangle
    $bitmap = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    [LeewayDemoNative]::PrintWindow($gate.Process.MainWindowHandle, $hdc, 2) | Out-Null  # PW_RENDERFULLCONTENT
    $graphics.ReleaseHdc($hdc)
    $bitmap.Save((Join-Path $CaptureDir $file), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
}

function Step([string]$text) {
    $script:StepNumber++
    Write-Output ("[{0}] {1}" -f $script:StepNumber, $text)
}

try {
    Step "Start the server process on $url"
    $server = Start-Server
    Write-Output "    server pid $($server.Id)"

    Step 'Open the north gate and sync'
    $north = Start-Gate 'gate-north'
    Write-Output ("    status : " + (Wait-Status $north 'Synced with the server.'))
    Write-Output ("    zones  : " + (Read-Zones $north))
    Save-Capture $north 'm0-live-1-online.png'

    Step "Kill the server process (pid $($server.Id)). The port now refuses connections."
    Stop-Process -Id $server.Id -Force
    $server.WaitForExit()

    Step 'Edit notes with no server, and save locally'
    Set-Notes $north 'Turnstile 3 jammed'
    Click $north 'SaveButton'
    Write-Output ("    status : " + (Wait-Status $north 'Saved locally*'))
    Write-Output ("    pending: " + (Read-Text $north 'PendingText'))
    Write-Output ("    zones  : " + (Read-Zones $north))
    Assert-Like (Read-Text $north 'PendingText') '*: 1' 'Pending changes'

    Step 'Press Sync while offline'
    Click $north 'SyncButton'
    Write-Output ("    status : " + (Wait-Status $north 'Server unreachable*'))
    Write-Output ("    pending: " + (Read-Text $north 'PendingText'))
    Assert-Like (Read-Text $north 'PendingText') '*: 1' 'Pending changes'
    Save-Capture $north 'm0-live-2-offline.png'

    Step 'Restart the server process (same database) and press Sync'
    $server = Start-Server
    Write-Output "    server pid $($server.Id)"
    Click $north 'SyncButton'
    Write-Output ("    status : " + (Wait-Status $north 'Synced with the server.'))
    Write-Output ("    pending: " + (Read-Text $north 'PendingText'))
    Assert-Like (Read-Text $north 'PendingText') '*: 0' 'Pending changes'
    Save-Capture $north 'm0-live-3-reconnected.png'

    Step 'Open a brand-new south gate: does it receive the edit from the server?'
    $south = Start-Gate 'gate-south'
    Write-Output ("    status : " + (Wait-Status $south 'Synced with the server.'))
    $zones = Read-Zones $south
    Write-Output ("    zones  : " + $zones)
    Assert-Like $zones '*North Stand (capacity 500): Turnstile 3 jammed*' 'South gate zones'
    Save-Capture $south 'm0-live-4-second-gate.png'

    Write-Output ''
    Write-Output 'M0 DEMO PASSED: edit offline, reconnect, sync.'
}
finally {
    foreach ($p in $script:Processes) {
        try { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } } catch { }
    }
    Start-Sleep -Milliseconds 300
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
