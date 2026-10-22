param([string]$BuildRoot = (Join-Path $PSScriptRoot '.build'))
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
if (!$BuildRoot.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'BuildRoot must stay inside the PopBat project directory.'
}
$publishRoot = Join-Path $projectRoot 'app'
$env:DOTNET_CLI_HOME = Join-Path $BuildRoot 'dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:NUGET_PACKAGES = Join-Path $BuildRoot 'nuget'
$project = Join-Path $PSScriptRoot 'src/PopBat/PopBat.csproj'
dotnet publish $project -c Release --self-contained false -r win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:BaseIntermediateOutputPath=$BuildRoot/obj/" "-p:OutputPath=$BuildRoot/bin/" "-p:RestoreConfigFile=$PSScriptRoot/NuGet.Config" -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'PopBat build failed.' }
Write-Output "Built: $publishRoot/PopBat.exe"
