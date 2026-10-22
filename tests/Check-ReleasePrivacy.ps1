[CmdletBinding()]
param([string[]]$Files, [string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$Files) {
    $Files = @('.gitignore', 'README.md', 'build.ps1', 'Export-Release.ps1', 'NuGet.Config', 'docs/USER_GUIDE.md', 'docs/PRIVACY_REVIEW.md', 'docs/GAME_COMPATIBILITY.md', 'docs/THIRD_PARTY_LICENSES.txt', 'docs/licenses/DOTNET-LIBRARY-LICENSE.txt', 'docs/licenses/WINDOWS-SDK-LICENSE.txt', 'tests/TEST_PLAN.md')
    $Files += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src/PopBat') -File | ForEach-Object { 'src/PopBat/' + $_.Name })
    $Files += @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File | ForEach-Object { 'tests/' + $_.Name })
}
# Print locations/categories only; never reproduce the matched value.
$patterns = [ordered]@{
    ProviderCredential = '(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{24,}|(?:AKIA|ASIA)[A-Z0-9]{16}|xox[baprs]-[A-Za-z0-9-]{20,}|AIza[A-Za-z0-9_-]{30,})'
    PrivateKey = '-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----'
    SecretLiteral = '(?i)["'']?(?:api[_-]?key|client[_-]?secret|access[_-]?token|auth[_-]?token|password|passwd|pwd|connectionstring)["'']?\s*[:=]\s*["''][^"''\r\n]{4,}["'']'
    PersonalPath = '(?i)[a-z]:[\\/]+Users[\\/]+(?!Public[\\/])[^\\/\s"''<>]+'
    Email = '(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b'
    RadioAddress = '(?i)(?<![a-f0-9])(?:[a-f0-9]{2}:){5}[a-f0-9]{2}(?![a-f0-9])'
    SerialLiteral = '(?i)(?:\bserial(?:number|_number)?|\bs/n|序號)\s*[:=]\s*["'']?[a-z0-9][a-z0-9 -]{5,}'
    CredentialUrl = '(?i)https?://[^\s/:@]+:[^\s/@]+@'
    Jwt = '\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{16,}\b'
    BearerOrStorageKey = '(?i)(?:Bearer\s+|AccountKey\s*=\s*)[A-Za-z0-9_+/.=-]{24,}'
    Webhook = 'https://hooks\.(?:slack\.com/services|discord\.com/api/webhooks)/[A-Za-z0-9_/-]{20,}'
}
$findings = [Collections.Generic.List[string]]::new()
function Check-Text([string]$Name, [string]$Text) {
    foreach ($category in $patterns.Keys) {
        if ([regex]::IsMatch($Text, $patterns[$category], [Text.RegularExpressions.RegexOptions]::None, [TimeSpan]::FromSeconds(5))) {
            $findings.Add($Name + ': ' + $category)
        }
    }
}
foreach ($relative in ($Files | Sort-Object -Unique)) {
    if ($relative -match '(^|[/\\])(\.git|\.build|research|artifacts|data|results)([/\\]|$)' -or $relative -match '(?i)(^|[/\\])\.env(?:\.|$)|\.(pem|key|pfx|p12|snk|pdb)$') {
        $findings.Add($relative + ': excluded private/development content'); continue
    }
    $path = [IO.Path]::GetFullPath((Join-Path $projectRoot $relative))
    if (!$path.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Privacy input must stay inside the project.' }
    Check-Text $relative ([IO.File]::ReadAllText($path))
}
if ($AssemblyPath) {
    $bytes = [IO.File]::ReadAllBytes($AssemblyPath)
    if ($bytes.Length -gt 8MB) { throw 'Inspect the application assembly, not the bundled runtime.' }
    Check-Text 'compiled application (ASCII)' ([Text.Encoding]::ASCII.GetString($bytes))
    Check-Text 'compiled application (UTF16)' ([Text.Encoding]::Unicode.GetString($bytes))
}
if ($findings.Count) { $findings | Write-Output; throw ('Release privacy check failed: ' + $findings.Count + ' finding(s).') }
Write-Output ('PASS: privacy patterns; {0} publication files{1}; no matched credentials, personal paths, emails, serial literals or radio addresses.' -f @($Files | Sort-Object -Unique).Count, $(if ($AssemblyPath) { ' and compiled application' } else { '' }))
Write-Output 'Pattern checks supplement manual review and cannot prove the absence of every possible secret.'
