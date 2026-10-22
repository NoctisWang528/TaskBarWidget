[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$defaults = Join-Path $productRoot 'Native\TrafficMonitor\defaults'
$mediaDefault = Join-Path $productRoot 'Media\Defaults\media-settings.default.json'

function Fail([string]$message) { throw "Factory default check failed: $message" }
function Read-Text([string]$path) { [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8) }

$configPath = Join-Path $defaults 'config.default.ini'
foreach ($path in @($configPath, (Join-Path $defaults 'Battery.dll.default.ini'),
        (Join-Path $defaults 'PowerMonPlugin.dll.default.ini'), (Join-Path $defaults 'WeatherPro.default.ini'), $mediaDefault)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "missing $path" }
}

# Encoding rules: the main config is written by CIniHelper (UTF-8 with BOM, CRLF); the
# built-in modules round-trip their INI through ANSI APIs, so they must stay pure ASCII.
$configBytes = [IO.File]::ReadAllBytes($configPath)
if ($configBytes.Length -lt 4 -or $configBytes[0] -ne 0xEF -or $configBytes[1] -ne 0xBB -or $configBytes[2] -ne 0xBF) {
    Fail 'config.default.ini must start with a UTF-8 BOM'
}
$configText = Read-Text $configPath
if ($configText -notmatch "`r`n") { Fail 'config.default.ini must use CRLF line endings' }
if (($configText -split "`r`n" | Where-Object { $_ -match "`n" }).Count -gt 0) { Fail 'config.default.ini mixes LF line endings' }

foreach ($name in @('Battery.dll.default.ini', 'PowerMonPlugin.dll.default.ini')) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $defaults $name))
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { Fail "$name must not carry a BOM" }
    if (@($bytes | Where-Object { $_ -gt 0x7F }).Count -gt 0) { Fail "$name must stay pure ASCII so the module can rewrite it" }
}

# Machine-specific keys must never ship as defaults.
foreach ($section in @('config', 'skins', 'window_size', 'taskbar_default_style', 'app', 'histroy_traffic')) {
    if ($configText -match "(?m)^\[$([regex]::Escape($section))\]\s*$") { Fail "config.default.ini must not contain the [$section] section" }
}
foreach ($key in @('language', 'connection_name', 'cpu_core_name', 'font_name', 'position_x', 'position_y',
        'task_bar_text_color', 'check_update_when_start', 'taskbar_left_space_win11', 'taskbar_right_space_win11')) {
    if ($configText -match "(?m)^\s*$([regex]::Escape($key))\s*=") { Fail "config.default.ini must not pin the machine-specific key '$key'" }
}

$knownItems = @{}
foreach ($index in 0..12) { $knownItems["native_$index"] = $true }
foreach ($id in @('b5R30ITQ', 'FxOP34Lm', 'SmartPowerMeterMon', 'BatteryPowerMon', 'BatteryCapacityMon',
        'BatteryVoltageMon', 'BatteryTimeMon', 'BatteryPercentageMon', 'CPUPowerMon', 'GPUPowerMon')) {
    $knownItems["builtin_$id"] = $true
}
foreach ($key in @('item_order_ids', 'item_rows')) {
    $line = ($configText -split "`r`n" | Where-Object { $_ -match "^\s*$key\s*=" } | Select-Object -First 1)
    if (-not $line) { Fail "config.default.ini is missing '$key'" }
    $value = ($line -split '=', 2)[1]
    foreach ($entry in ($value -split '[,\s]+' | Where-Object { $_ })) {
        $id = ($entry -split ':')[0]
        if (-not $knownItems.ContainsKey($id)) { Fail "'$key' references the unknown display item '$id'" }
    }
}
$displayLine = ($configText -split "`r`n" | Where-Object { $_ -match '^\s*tbar_display_item\s*=' } | Select-Object -First 1)
if (-not $displayLine) { Fail "config.default.ini is missing 'tbar_display_item'" }
$mask = [int]("$(($displayLine -split '=', 2)[1])".Trim())
if ($mask -le 0 -or ($mask -band 0x1FFF) -ne $mask) { Fail "tbar_display_item '$mask' is outside the known display item bits" }

# Weather credentials must never be packaged; the only allowed city default is the generic Beijing fallback.
$weather = Read-Text (Join-Path $defaults 'WeatherPro.default.ini')
foreach ($key in @('AppKey', 'ApiHost', 'app_key', 'api_host')) {
    if ($weather -match "(?m)^[ \t]*$key[ \t]*=[ \t]*\S") { Fail "WeatherPro.default.ini must leave '$key' empty" }
}
if ($weather -match '(?m)^[ \t]*AppKey[ \t]*=') { Fail 'WeatherPro.default.ini must not declare an INI credential slot' }
if ($weather -notmatch '(?m)^\s*api_type\s*=') { Fail 'WeatherPro.default.ini must pin api_type explicitly' }
if ($weather -notmatch '(?m)^\s*city_name\s*=\s*北京\s*$') { Fail 'WeatherPro.default.ini must pin the generic Beijing city name' }
if ($weather -notmatch '(?m)^\s*city_no\s*=\s*101010100\s*$') { Fail 'WeatherPro.default.ini must pin the generic Beijing city id' }

$media = Get-Content -LiteralPath $mediaDefault -Raw | ConvertFrom-Json
foreach ($name in @('FontFamily', 'CardFontFamily')) {
    if ($media.$name -ne 'Microsoft YaHei UI') { Fail "media-settings.default.json must use the system font for $name" }
}
foreach ($name in @('LocalMusicFolders', 'CustomPlayerSources', 'SelectedDisplayIds')) {
    if (@($media.$name).Count -ne 0) { Fail "media-settings.default.json must leave $name empty" }
}
if ($null -ne $media.LastUpdateCheckUtc) { Fail 'media-settings.default.json must not record an update check time' }
if ($media.SpectrumAudioAccessGranted) { Fail 'media-settings.default.json must not pre-grant audio capture' }

# Strings that would leak one machine's environment into the factory defaults.
$forbidden = @('Meta Tunnel', 'MiSans', 'Misans', '平均温度', '江宁', '南京', 'VMware', 'TAP-Windows',
    'VirtualBox', 'C:\Users', 'C:\Program Files')
foreach ($path in @($configPath, $mediaDefault, (Join-Path $defaults 'Battery.dll.default.ini'),
        (Join-Path $defaults 'PowerMonPlugin.dll.default.ini'), (Join-Path $defaults 'WeatherPro.default.ini'))) {
    $text = Read-Text $path
    foreach ($needle in $forbidden) {
        if ($text.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            Fail "$([IO.Path]::GetFileName($path)) contains the machine-specific text '$needle'"
        }
    }
}

Write-Host 'Factory defaults are generic, credential-free and encoded as each consumer expects.'
