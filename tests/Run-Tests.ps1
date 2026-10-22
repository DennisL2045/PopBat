[CmdletBinding()]
param([switch]$Build, [switch]$IncludeDeviceProbe)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $projectRoot
if ($Build) { & (Join-Path $projectRoot 'build.ps1') }
$exe = Join-Path $projectRoot 'app/PopBat.exe'
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Missing app/PopBat.exe. Run build.ps1 first.' }

function Invoke-PopBat([string]$Arguments) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $exe
    $start.Arguments = $Arguments
    $start.WorkingDirectory = $projectRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (!$process.Start()) { throw 'Could not start PopBat.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) {
            $process.Kill()
            $process.WaitForExit()
            return @{ ExitCode = -1; Output = '' }
        }
        # Keep stderr in memory only: runtime errors may contain machine-specific paths.
        $text = $stdout.GetAwaiter().GetResult()
        $null = $stderr.GetAwaiter().GetResult()
        return @{ ExitCode = $process.ExitCode; Output = $text }
    } finally { $process.Dispose() }
}

$self = Invoke-PopBat '--self-test'
$match = [regex]::Match($self.Output.Trim(), '^PASS: (\d+) checks; no hardware was opened and no test history was written\.$')
$passed = $self.ExitCode -eq 0 -and $match.Success
$result = [ordered]@{
    FormatVersion = 1
    TestedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    ExecutableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    SelfTest = [ordered]@{ Passed = $passed; Checks = $(if ($match.Success) { [int]$match.Groups[1].Value } else { 0 }); ExitCode = $self.ExitCode }
    DeviceProbe = [ordered]@{ Requested = [bool]$IncludeDeviceProbe; Completed = $false; Connected = $false; Outcome = 'NotRequested' }
}
$exitCode = $(if ($passed) { 0 } else { 1 })
if ($IncludeDeviceProbe -and $passed) {
    $probe = Invoke-PopBat '--probe'
    $result.DeviceProbe.ExitCode = $probe.ExitCode
    $states = @('Connected','ReceiverMissing','HeadsetOffline','NotReady','NoReply','UnsupportedInterface','AccessDenied','ReadError')
    try {
        $data = $probe.Output | ConvertFrom-Json
        $safe = $data.Device -eq 'ROG Delta II' -and $data.VendorId -eq '0B05' -and $data.ProductId -eq '1AFA' -and $data.State -in $states
        $safe = $safe -and ($null -eq $data.Percent -or ($data.Percent -is [int] -and $data.Percent -ge 0 -and $data.Percent -le 100))
        $safe = $safe -and ($null -eq $data.Charging -or $data.Charging -is [bool])
        $safe = $safe -and $data.TargetInterfaces -is [int] -and $data.TargetInterfaces -ge 0 -and $data.TargetInterfaces -le 64
        $safe = $safe -and $data.VendorInterfaces -is [int] -and $data.VendorInterfaces -ge 0 -and $data.VendorInterfaces -le 64
        if (!$safe -or $probe.ExitCode -notin @(0,1)) { throw 'Unexpected probe result.' }
        $result.DeviceProbe.Completed = $true
        $result.DeviceProbe.Connected = $probe.ExitCode -eq 0 -and $data.State -eq 'Connected'
        $result.DeviceProbe.Outcome = $data.State
        $result.DeviceProbe.Percent = $data.Percent
        $result.DeviceProbe.Charging = $data.Charging
        $result.DeviceProbe.TargetInterfaces = $data.TargetInterfaces
        $result.DeviceProbe.VendorInterfaces = $data.VendorInterfaces
        if (!$result.DeviceProbe.Connected) { $exitCode = 2 }
    } catch {
        $result.DeviceProbe.Outcome = $(if ($probe.ExitCode -eq 2) { 'AnotherInstanceRunning' } elseif ($probe.ExitCode -eq -1) { 'TimedOut' } else { 'ProbeFailed' })
        $exitCode = 2
    }
} elseif ($IncludeDeviceProbe) {
    $result.DeviceProbe.Outcome = 'SkippedBecauseSelfTestFailed'
}
$resultsDir = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null
$file = Join-Path $resultsDir ('test-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '.json')
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $file -Encoding UTF8
Write-Output ('Self-test: {0}; checks: {1}' -f $result.SelfTest.Passed, $result.SelfTest.Checks)
Write-Output ('Device probe: {0}' -f $result.DeviceProbe.Outcome)
Write-Output ('Report: tests/results/' + [IO.Path]::GetFileName($file))
exit $exitCode