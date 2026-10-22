[CmdletBinding()]
param([switch]$ResourceCheck, [switch]$Preview, [switch]$HidCapabilities, [switch]$BatteryDetails, [switch]$HidCollections, [switch]$KeyboardProbe)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $projectRoot
$exe = Join-Path $projectRoot 'app/PopBat.exe'
$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $exe
$start.WorkingDirectory = $projectRoot
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$results = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Force -Path $results | Out-Null
$report = Join-Path $results ($(if ($ResourceCheck) { 'resource-' } elseif ($BatteryDetails) { 'battery-details-' } elseif ($HidCapabilities) { 'hid-capabilities-' } else { 'devices-' }) + $stamp + '.json')
$start.Arguments = $(if ($ResourceCheck) { '--resource-check' } elseif ($KeyboardProbe) { '--probe-keyboard' } elseif ($HidCollections) { '--inspect-hid-collections' } elseif ($BatteryDetails) { '--inspect-battery' } elseif ($HidCapabilities) { '--inspect-hid' } elseif ($Preview) { '--preview --output "' + (Join-Path $projectRoot 'app/preview.png') + '"' } else { '--probe-all' })
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $start
try {
    if (!$process.Start()) { throw 'Could not start PopBat.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $timeout = $(if ($ResourceCheck) { 180000 } else { 30000 })
    if (!$process.WaitForExit($timeout)) { $process.Kill(); $process.WaitForExit(); throw 'PopBat diagnostic timed out.' }
    $text = $stdout.GetAwaiter().GetResult()
    $null = $stderr.GetAwaiter().GetResult()
    if ($process.ExitCode -notin @(0,1)) { throw ('PopBat diagnostic failed, exit code ' + $process.ExitCode) }
    $data = $text | ConvertFrom-Json
    if ($data.Version -ne '0.4.4') { throw 'Unexpected diagnostic format.' }
    $data | Add-Member -NotePropertyName ExecutableSha256 -NotePropertyValue (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    # The executable emits only parsed names/status/battery or its own resource counters.
    $data | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $report -Encoding UTF8
    Write-Output ('Report: ' + $report)
    Write-Output $text
} finally { $process.Dispose() }
