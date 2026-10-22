[CmdletBinding()]
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
Set-Location -LiteralPath $projectRoot
$buildRoot = Join-Path $projectRoot '.build'
$publishRoot = Join-Path $buildRoot 'share-publish'
$assembly = Join-Path $buildRoot 'share-bin/PopBat.dll'
$privacyCheck = Join-Path $projectRoot 'tests/Check-ReleasePrivacy.ps1'
& $privacyCheck
if (!$SkipBuild) {
    $env:DOTNET_CLI_HOME = Join-Path $buildRoot 'dotnet-home'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:NUGET_PACKAGES = Join-Path $buildRoot 'nuget'
    dotnet publish (Join-Path $projectRoot 'src/PopBat/PopBat.csproj') -c Release --self-contained true -r win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=none -p:DebugSymbols=false "-p:PathMap=$projectRoot=/_/PopBat" "-p:BaseIntermediateOutputPath=$buildRoot/share-obj/" "-p:OutputPath=$buildRoot/share-bin/" "-p:RestoreConfigFile=$projectRoot/NuGet.Config" -o $publishRoot
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
}
& $privacyCheck -AssemblyPath $assembly
$config = Get-Content -LiteralPath (Join-Path $buildRoot 'share-bin/PopBat.runtimeconfig.json') -Raw | ConvertFrom-Json
$frameworks = @($config.runtimeOptions.includedFrameworks | ForEach-Object { $_.name })
if ('Microsoft.NETCore.App' -notin $frameworks -or 'Microsoft.WindowsDesktop.App' -notin $frameworks -or $config.runtimeOptions.frameworks) { throw 'Expected bundled .NET and Windows Desktop runtimes.' }
if (@($config.runtimeOptions.includedFrameworks | Where-Object { $_.version -ne '8.0.32' }).Count) { throw 'Bundled runtime security patch does not match the reviewed version.' }
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src/PopBat/PopBat.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
$stamp = [DateTime]::Now.ToString('yyyyMMdd-HHmmss')
$artifactRoot = Join-Path $projectRoot 'artifacts'
$stage = Join-Path $buildRoot ('share-package-' + $stamp)
$verify = Join-Path $buildRoot ('share-verify-' + $stamp)
$archive = Join-Path $artifactRoot ('PopBat-' + $version + '-win-x64-' + $stamp + '.zip')
New-Item -ItemType Directory -Force -Path $artifactRoot, $stage | Out-Null
# Never copy the app directory: only a freshly published executable and reviewed text.
Copy-Item -LiteralPath (Join-Path $publishRoot 'PopBat.exe') -Destination (Join-Path $stage 'PopBat.exe')
Copy-Item -LiteralPath (Join-Path $projectRoot 'tests/Diagnose-Compatibility.ps1') -Destination (Join-Path $stage 'Diagnose-Compatibility.ps1')
Copy-Item -LiteralPath (Join-Path $projectRoot 'tests/Run-Diagnostics.cmd') -Destination (Join-Path $stage 'Run-Diagnostics.cmd')
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $stage 'LICENSE.txt')
$utf8 = [Text.UTF8Encoding]::new($true)
[IO.File]::WriteAllText((Join-Path $stage 'README.txt'), [IO.File]::ReadAllText((Join-Path $projectRoot 'docs/USER_GUIDE.md')), $utf8)
$notices = [Text.StringBuilder]::new()
$noticeCount = 0
$assets = Get-Content -LiteralPath (Join-Path $buildRoot 'share-obj/project.assets.json') -Raw | ConvertFrom-Json
$windowsSdkVersions = @($assets.project.frameworks.PSObject.Properties | ForEach-Object {
    $_.Value.downloadDependencies | Where-Object { $_.name -eq 'Microsoft.Windows.SDK.NET.Ref' } | ForEach-Object {
        if ($_.version -notmatch '^\[([^,\]]+),\s*([^\]]+)\]$' -or $Matches[1] -ne $Matches[2]) { throw 'Expected an exact Windows SDK download version.' }
        $Matches[1]
    }
} | Sort-Object -Unique)
if ($windowsSdkVersions.Count -eq 0) {
    $windowsSdkVersions = @($assets.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'Microsoft.Windows.SDK.NET.Ref/*' } | ForEach-Object { ($_ -split '/', 2)[1] } | Sort-Object -Unique)
}
if ($windowsSdkVersions.Count -ne 1) { throw 'Could not identify the resolved Windows SDK projection license package.' }
$windowsSdkVersion = $windowsSdkVersions[0]
$licenseVersions = @{
    'microsoft.netcore.app.runtime.win-x64' = ($config.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
    'microsoft.windowsdesktop.app.runtime.win-x64' = ($config.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App').version
    'microsoft.windows.sdk.net.ref' = $windowsSdkVersion
}
foreach ($package in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64', 'microsoft.windows.sdk.net.ref')) {
    $directory = Get-Item -LiteralPath (Join-Path $buildRoot ('nuget/' + $package + '/' + $licenseVersions[$package]))
    foreach ($notice in @(Get-ChildItem -LiteralPath $directory.FullName -File | Where-Object { $_.Name -match '(?i)^(LICENSE|THIRD-PARTY-NOTICES|ThirdPartyNotices)(\.(txt|md))?$' })) {
        $null = $notices.AppendLine('===== ' + $package + '/' + $directory.Name + '/' + $notice.Name + ' =====')
        $null = $notices.AppendLine([IO.File]::ReadAllText($notice.FullName)).AppendLine()
        $noticeCount++
    }
}
if ($noticeCount -lt 3) { throw 'Missing runtime license or third-party notices.' }
$null = $notices.AppendLine([IO.File]::ReadAllText((Join-Path $projectRoot 'docs/THIRD_PARTY_LICENSES.txt')))
foreach ($license in @('DOTNET-LIBRARY-LICENSE.txt', 'WINDOWS-SDK-LICENSE.txt')) {
    $null = $notices.AppendLine('===== ' + $license + ' =====')
    $null = $notices.AppendLine([IO.File]::ReadAllText((Join-Path $projectRoot ('docs/licenses/' + $license)))).AppendLine()
}
[IO.File]::WriteAllText((Join-Path $stage 'THIRD-PARTY-NOTICES.txt'), $notices.ToString(), $utf8)
$manifest = @('PopBat.exe', 'README.txt', 'THIRD-PARTY-NOTICES.txt', 'LICENSE.txt', 'Diagnose-Compatibility.ps1', 'Run-Diagnostics.cmd' | ForEach-Object {
    (Get-FileHash -LiteralPath (Join-Path $stage $_) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_
})
[IO.File]::WriteAllLines((Join-Path $stage 'SHA256SUMS.txt'), $manifest, [Text.UTF8Encoding]::new($false))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $names = @($zip.Entries | ForEach-Object { $_.FullName } | Sort-Object)
    $expected = @('Diagnose-Compatibility.ps1', 'LICENSE.txt', 'PopBat.exe', 'README.txt', 'Run-Diagnostics.cmd', 'SHA256SUMS.txt', 'THIRD-PARTY-NOTICES.txt')
    if (@(Compare-Object $names $expected).Count -ne 0 -or $names.Count -ne 7) { throw 'Unexpected share archive contents.' }
} finally { $zip.Dispose() }
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $verify)
foreach ($line in $manifest) {
    $parts = $line -split '  ', 2
    if ((Get-FileHash -LiteralPath (Join-Path $verify $parts[1]) -Algorithm SHA256).Hash -ne $parts[0]) { throw 'Archive checksum mismatch.' }
}
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = Join-Path $verify 'PopBat.exe'
$start.Arguments = '--self-test'
$start.WorkingDirectory = $verify
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $verify 'no-external-runtime'
$start.EnvironmentVariables['DOTNET_ROOT_X64'] = Join-Path $verify 'no-external-runtime'
$start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
try {
    if (!$process.Start()) { throw 'Share package self-test did not start.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Share package self-test timed out.' }
    $output = $stdout.GetAwaiter().GetResult().Trim()
    $null = $stderr.GetAwaiter().GetResult()
    $selfTest = [regex]::Match($output, '^PASS: (\d+) checks; no hardware was opened and no test history was written\.$')
    if ($process.ExitCode -ne 0 -or !$selfTest.Success -or [int]$selfTest.Groups[1].Value -lt 167) { throw 'Share package self-test failed.' }
    $selfTestChecks = [int]$selfTest.Groups[1].Value
} finally { $process.Dispose() }
if (Test-Path -LiteralPath (Join-Path $verify 'data')) { throw 'Share self-test unexpectedly created user data.' }
$report = [ordered]@{
    Version = $version; Archive = 'artifacts/' + [IO.Path]::GetFileName($archive)
    ArchiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    ExecutableSha256 = (Get-FileHash -LiteralPath (Join-Path $verify 'PopBat.exe') -Algorithm SHA256).Hash
    Bytes = (Get-Item -LiteralPath $archive).Length; Files = $names
    SelfContained = $true; IncludedFrameworks = $config.runtimeOptions.includedFrameworks; WindowsSdkProjectionVersion = $windowsSdkVersion
    ChecksumsVerified = $true; SelfTestChecks = $selfTestChecks; UserDataIncluded = $false
}
[IO.File]::WriteAllText((Join-Path $buildRoot 'release-report.json'), ($report | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$report | ConvertTo-Json -Depth 6
