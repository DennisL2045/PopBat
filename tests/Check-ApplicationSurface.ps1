$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src/PopBat') -Filter '*.cs' -File)
$allowedDlls = @('hid.dll','setupapi.dll','kernel32.dll','user32.dll','cfgmgr32.dll')
$disallowed = '(?i)\b(OpenProcess|ReadProcessMemory|WriteProcessMemory|VirtualAllocEx|CreateRemoteThread|SetWindowsHookEx\w*|SendInput|GetAsyncKeyState|CreateService\w*|LoadLibrary\w*|GetProcAddress|WriteValueAsync|WriteValueWithResultAsync|WriteClientCharacteristicConfigurationDescriptorAsync|PairAsync)\s*\(|Process\.(Start|GetProcesses)\s*\(|\b(HttpClient|TcpClient|UdpClient|WebClient|BluetoothLEAdvertisementWatcher)\b|MaintainConnection\s*=\s*true|104EA319-6EE2-4701-BD47-8DDBF425BBE5|System\.Devices\.BatteryLife'
$imports = 0
foreach ($file in $source) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    if ([regex]::IsMatch($text,$disallowed) -or [regex]::IsMatch($text,'\b(HidD_GetInputReport|HidD_SetFeature|HidD_SetOutputReport)\s*\(')) { throw ('Disallowed application capability found in ' + $file.Name) }
    if ($text -match '\bStreamSocket\b') {
        if ($file.Name -ne 'BluetoothClassicReader.cs' -or $text -notmatch 'HostNameType\.Bluetooth' -or
            $text -notmatch 's\.ServiceId\.Uuid == FastPairProtocol\.ServiceUuid' -or
            $text -notmatch 'SocketProtectionLevel\.BluetoothEncryptionWithAuthentication') {
            throw 'StreamSocket is allowed only in the authenticated, service-pinned Bluetooth reader.'
        }
    }
    foreach ($match in [regex]::Matches($text,'DllImport\("([^"]+)"')) {
        if ($match.Groups[1].Value -notin $allowedDlls) { throw 'Unexpected native library.' }
        $imports++
    }
}
[xml]$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'src/PopBat/app.manifest') -Raw
$level = $manifest.SelectSingleNode('//*[local-name()="requestedExecutionLevel"]')
if ($level.level -ne 'asInvoker' -or $level.uiAccess -ne 'false') { throw 'Application privilege boundary changed.' }
Write-Output ('PASS: source capability checks; {0} native declarations, only standard Windows libraries; asInvoker; no known prohibited calls.' -f $imports)
Write-Output 'This source check is not anti-cheat certification.'
