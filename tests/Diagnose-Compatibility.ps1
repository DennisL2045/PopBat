[CmdletBinding()]
param([switch]$EnvironmentOnly)
$ErrorActionPreference = 'Stop'
# Works in the repository tests/ folder and alongside a redistributed PopBat.exe.
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'PopBat.exe') -PathType Leaf) {
    $root = $PSScriptRoot; $exe = Join-Path $root 'PopBat.exe'; $reportDir = Join-Path $root 'diagnostics'
} else {
    $root = $repository; $exe = Join-Path $root 'app/PopBat.exe'; $reportDir = Join-Path $root 'tests/results'
}
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Keep this script alongside PopBat.exe, or run it from the project tests folder.' }
Set-Location -LiteralPath $root
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
function Invoke-Local([string]$File,[string]$Arguments,[int]$TimeoutSeconds = 35) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $File; $start.Arguments = $Arguments; $start.WorkingDirectory = $root
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.EnvironmentVariables['DOTNET_DISABLE_GUI_ERRORS'] = '1'
    $start.EnvironmentVariables['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.EnvironmentVariables['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $start.EnvironmentVariables['DOTNET_CLI_HOME'] = Join-Path $reportDir '.cli'
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        if (!$process.Start()) { return @{ Code = -2; Text = '' } }
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill(); $process.WaitForExit(); return @{ Code = -1; Text = '' }
        }
        $text = $stdout.GetAwaiter().GetResult(); $null = $stderr.GetAwaiter().GetResult()
        return @{ Code = $process.ExitCode; Text = $text }
    } catch { return @{ Code = -2; Text = '' } }
    finally { $process.Dispose() }
}
$version = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue)
$architecture = [Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITEW6432')
if (!$architecture) { $architecture = [Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE') }
if ($architecture -notin @('AMD64','ARM64','x86')) { $architecture = 'Unknown' }
$result = [ordered]@{
    FormatVersion = 1; CheckedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    ExecutableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    Environment = [ordered]@{ WindowsBuild = [string]$version.CurrentBuildNumber; WindowsRevision = [int]$version.UBR;
        DisplayVersion = [string]$version.DisplayVersion; OSArchitecture = $architecture; SdkVersions = @(); DesktopRuntimeVersions = @() }
    SelfTest = $null; BatteryProbe = $null; Outcome = 'NotStarted'
}
$dotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($dotnet) {
    $sdks = Invoke-Local $dotnet.Source '--list-sdks'
    $result.Environment.SdkVersions = @([regex]::Matches($sdks.Text, '(?m)^([0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?)\s+\[') | ForEach-Object { $_.Groups[1].Value })
    $runtimes = Invoke-Local $dotnet.Source '--list-runtimes'
    $result.Environment.DesktopRuntimeVersions = @([regex]::Matches($runtimes.Text, '(?m)^Microsoft\.WindowsDesktop\.App\s+([0-9]+\.[0-9]+\.[0-9]+)\s+\[') | ForEach-Object { $_.Groups[1].Value })
}
$self = Invoke-Local $exe '--self-test'
$check = [regex]::Match($self.Text.Trim(), '^PASS: (\d+) checks; no hardware was opened and no test history was written\.$')
$result.SelfTest = [ordered]@{ ExitCode = $self.Code; Passed = $self.Code -eq 0 -and $check.Success; Checks = $(if ($check.Success) { [int]$check.Groups[1].Value } else { 0 }) }
$code = 0
if (!$result.SelfTest.Passed) {
    $result.Outcome = 'ExecutableStartupOrSelfTestFailed'
    $code = 1
} elseif ($EnvironmentOnly) {
    $result.Outcome = 'EnvironmentAndSelfTestPassed'
} else {
    $probe = Invoke-Local $exe '--diagnose'
    if ($probe.Code -eq 0) {
        try {
            $report = $probe.Text | ConvertFrom-Json
            if ($report.Scope -ne 'SupportedBatteryProtocols' -or !$report.Environment -or $null -eq $report.Providers) { throw 'Unsupported diagnostic format.' }
            # The app's reviewed diagnostic format excludes identity keys, paths and raw payloads.
            $result.BatteryProbe = $report
            $result.Outcome = $(if ($report.Interrupted) { 'BatteryProbePartiallyCompleted' } else { 'BatteryProbeCompleted' })
        } catch { $result.Outcome = 'InvalidBatteryDiagnostic'; $code = 2 }
    } else {
        $result.Outcome = $(if ($probe.Code -eq 2) { 'AnotherInstanceRunning' } elseif ($probe.Code -eq -1) { 'BatteryProbeTimedOut' } else { 'BatteryProbeFailed' })
        $code = 2
    }
}
$file = Join-Path $reportDir ('compatibility-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '.json')
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $file -Encoding UTF8
Write-Output ('Outcome: ' + $result.Outcome)
Write-Output ('Self-test checks: ' + $result.SelfTest.Checks)
Write-Output ('Report: ' + [IO.Path]::GetFileName($file))
Write-Output 'No account name, machine name, device address, serial, native path or raw HID packet is recorded.'
exit $code