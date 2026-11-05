<#
.SYNOPSIS
    The GateKeeper demo with separate processes and real windows.

.DESCRIPTION
    Runs the real server, the real dashboard, and two real gate windows as separate processes, drives the windows
    through Windows UI Automation, and goes offline by killing the server process. Windows only. The cross-platform
    equivalent is tests/GateKeeper.Desktop.Tests, which runs in CI.

    Build first:  dotnet build Leeway.slnx
    Run:          powershell -ExecutionPolicy Bypass -File scripts/m6-demo.ps1
    Exits non-zero if any step does not show what it should.
#>
param(
    [int]$Port = 5180,
    [int]$DashboardPort = 5181
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $env:TEMP ("leeway-m6-demo-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$url = "http://127.0.0.1:$Port"
$dashboardUrl = "http://127.0.0.1:$DashboardPort"
$dotnetHome = Join-Path $env:USERPROFILE '.dotnet'
$dotnet = if (Test-Path (Join-Path $dotnetHome 'dotnet.exe')) { Join-Path $dotnetHome 'dotnet.exe' } else { 'dotnet' }
$serverDll = Join-Path $root 'samples\GateKeeper.Server\bin\Debug\net10.0\GateKeeper.Server.dll'
$dashboardDll = Join-Path $root 'samples\GateKeeper.Dashboard\bin\Debug\net10.0\GateKeeper.Dashboard.dll'
$desktopDll = Join-Path $root 'samples\GateKeeper.Desktop\bin\Debug\net10.0\GateKeeper.Desktop.dll'
foreach ($dll in @($serverDll, $dashboardDll, $desktopDll)) {
    if (-not (Test-Path $dll)) { throw "Missing $dll. Run: dotnet build Leeway.slnx" }
}

$AutomationElement = [System.Windows.Automation.AutomationElement]
$script:Processes = @()
$script:StepNumber = 0

function Wait-Url([string]$address, $process) {
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-RestMethod -Uri $address -TimeoutSec 2 | Out-Null
            return
        }
        catch {
            if ($process.HasExited) { throw "A process exited with code $($process.ExitCode)." }
            Start-Sleep -Milliseconds 250
        }
    }
    throw "$address did not start."
}

function Start-Server {
    $p = Start-Process -FilePath $dotnet -PassThru -WindowStyle Hidden -WorkingDirectory $work `
        -ArgumentList @("`"$serverDll`"", '--urls', $url, '--ConnectionStrings:GateKeeper', "`"Data Source=$work\server.db`"", '--contentRoot', "`"$work`"")
    $script:Processes += $p
    Wait-Url "$url/admin/state" $p
    return $p
}

function Start-Dashboard {
    $p = Start-Process -FilePath $dotnet -PassThru -WindowStyle Hidden -WorkingDirectory $work `
        -ArgumentList @("`"$dashboardDll`"", '--urls', $dashboardUrl, '--Server', "$url/", '--contentRoot', "`"$work`"")
    $script:Processes += $p
    Wait-Url "$dashboardUrl/api/state" $p
}

function Start-Gate([string]$name) {
    $p = Start-Process -FilePath $dotnet -PassThru -WorkingDirectory $work `
        -ArgumentList @("`"$desktopDll`"", "--server=$url/", "`"--db=$work\$name.db`"", "--gate=$name")
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

function Wait-Text($gate, [string]$id, [string]$expected) {
    $deadline = (Get-Date).AddSeconds(45)
    do {
        $actual = Read-Text $gate $id
        if ($actual -like $expected) { return $actual }
        Start-Sleep -Milliseconds 150
    } while ((Get-Date) -lt $deadline)
    throw "$($gate.Name): expected $id to be '$expected', but it shows '$actual'."
}

function Scan($gate, [string]$ticket, [string]$expected) {
    (Find-Control $gate 'TicketBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($ticket)
    Click $gate 'ScanButton'
    Write-Output ("    {0}: {1}" -f $gate.Name, (Wait-Text $gate 'LastScanText' $expected))
}

function Wait-Enabled($gate, [string]$id) {
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        if ((Find-Control $gate $id).Current.IsEnabled) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "$($gate.Name): $id stayed disabled."
}

# The button is disabled while a sync runs, and the status still shows the previous result until it ends.
function Sync($gate) {
    Wait-Enabled $gate 'SyncButton'
    Click $gate 'SyncButton'
    Start-Sleep -Milliseconds 300
    Wait-Enabled $gate 'SyncButton'
    Write-Output ("    {0}: {1}" -f $gate.Name, (Wait-Text $gate 'StatusText' 'Synced with the server.'))
}

function Step([string]$text) {
    $script:StepNumber++
    Write-Output ("[{0}] {1}" -f $script:StepNumber, $text)
}

try {
    Step "Start the server on $url and the dashboard on $dashboardUrl"
    $server = Start-Server
    Start-Dashboard

    Step 'Open two gates. Each syncs and leases 4 of the 10 places in North Stand.'
    $a = Start-Gate 'gate-a'
    $b = Start-Gate 'gate-b'
    Write-Output ("    gate-a: " + (Wait-Text $a 'WalletText' 'Lease: 4 left'))
    Write-Output ("    gate-b: " + (Wait-Text $b 'WalletText' 'Lease: 4 left'))

    Step "Kill the server process (pid $($server.Id)). The port now refuses connections."
    Stop-Process -Id $server.Id -Force
    $server.WaitForExit()

    Step 'Each gate spends its lease offline, and T-1001 is scanned at both.'
    foreach ($ticket in 'T-1001', 'T-1002', 'T-1003', 'T-1004') { Scan $a $ticket 'ADMIT*' }
    Scan $a 'T-1005' 'PROVISIONAL*'
    foreach ($ticket in 'T-1001', 'T-1006', 'T-1007', 'T-1008') { Scan $b $ticket 'ADMIT*' }
    Scan $b 'T-1009' 'PROVISIONAL*'
    Scan $b 'T-1010' 'PROVISIONAL*'

    Step 'Restart the server (same database), and sync both gates.'
    $server = Start-Server
    Sync $a; Sync $b; Sync $a; Sync $b

    Step 'Check the result on the dashboard, over HTTP.'
    $state = Invoke-RestMethod -Uri "$dashboardUrl/api/state"
    $north = $state.zones | Where-Object { $_.id -eq 'zone-north' }
    Write-Output ("    North Stand: {0} of {1} used, conserved: {2}" -f $north.consumed, $north.capacity, $north.conserved)
    Write-Output ("    Duplicates : " + (($state.duplicates | ForEach-Object { "$($_.ticket) at $($_.gates -join ', ')" }) -join '; '))
    if ($north.consumed -ne 10 -or -not $north.conserved) { throw 'The zone is wrong.' }
    if (@($state.duplicates).Count -ne 1 -or $state.duplicates[0].ticket -ne 'T-1001') { throw 'The duplicate was not flagged.' }

    Write-Output ''
    Write-Output 'M6 DEMO PASSED: the zone never overfilled, and the duplicate was flagged.'
}
finally {
    foreach ($p in $script:Processes) {
        try { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } } catch { }
    }
    Start-Sleep -Milliseconds 400
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
