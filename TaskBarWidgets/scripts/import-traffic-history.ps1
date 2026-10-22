[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceFile,
    [string]$DestinationDirectory
)
$ErrorActionPreference = 'Stop'
$productRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$destination = & (Join-Path $PSScriptRoot 'resolve-app-directory.ps1') -Directory $DestinationDirectory -ParameterName 'DestinationDirectory'
$prefix = (Join-Path $productRoot 'artifacts').TrimEnd('\') + '\'
if (-not $destination.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'History destination must be inside product artifacts.' }
$source = [IO.Path]::GetFullPath($SourceFile)
$target = Join-Path $destination 'Data\history\traffic.dat'
if ($source -eq $target) { throw 'Source and destination history must differ.' }
$executable = Join-Path $destination 'TaskBarWidgets.exe'
if (Test-Path -LiteralPath $executable) {
    try { $guard = [IO.File]::Open($executable, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $guard.Dispose() }
    catch { throw 'Destination application is running or locked; close it before importing history.' }
}

function Add-DailyRecord($Records, $Record) {
    $previous = $Records[$Record.Date]
    # Both files contain daily cumulative snapshots, not disjoint usage intervals.
    # Keep the larger entire record, never add duplicates or combine component maxima.
    if ($null -eq $previous -or $Record.Total -gt $previous.Total -or
        ($Record.Total -eq $previous.Total -and ($previous.Mixed -or -not $Record.Mixed))) {
        $Records[$Record.Date] = $Record
    }
}

function Read-DailyRecords([string]$Path) {
    $records = @{}
    $lines = [IO.File]::ReadAllLines($Path)
    if ($lines.Length -eq 0 -or $lines[0] -notmatch '^lines:\s*"\d+"\s*$') { throw "Invalid traffic history header: $Path" }
    for ($i = 1; $i -lt $lines.Length; $i++) {
        if ([string]::IsNullOrWhiteSpace($lines[$i])) { continue }
        if ($lines[$i] -notmatch '^(?<date>\d{4}/\d{2}/\d{2})\s+(?<first>\d+)(?:/(?<second>\d+))?\s*$') {
            throw "Invalid traffic history record at $Path line $($i + 1). No history was changed."
        }
        $dateText = $Matches.date
        $first = [decimal]::Parse($Matches.first, [Globalization.CultureInfo]::InvariantCulture)
        $mixed = -not $Matches.ContainsKey('second')
        $second = if ($mixed) { [decimal]0 } else { [decimal]::Parse($Matches.second, [Globalization.CultureInfo]::InvariantCulture) }
        $date = [datetime]::MinValue
        if (-not [datetime]::TryParseExact($dateText, 'yyyy/MM/dd', [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None, [ref]$date) -or $date.Year -lt 1900 -or $date.Year -gt 3000 -or
            $first + $second -gt [long]::MaxValue) { throw "Invalid traffic date or amount at $Path line $($i + 1)." }
        Add-DailyRecord $records ([pscustomobject]@{ Date = $dateText; First = $first; Second = $second; Mixed = $mixed; Total = $first + $second })
    }
    return $records
}

$sourceRecords = Read-DailyRecords $source
$targetRecords = if (Test-Path -LiteralPath $target) { Read-DailyRecords $target } else { @{} }
$merged = @{}
foreach ($record in $sourceRecords.Values) { Add-DailyRecord $merged $record }
foreach ($record in $targetRecords.Values) { Add-DailyRecord $merged $record }

$backup = Join-Path $productRoot ('artifacts\backups\history-import-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backup, $destination, (Split-Path -Parent $target) -Force | Out-Null
Copy-Item -LiteralPath $source -Destination (Join-Path $backup 'source-history.dat')
$output = [Collections.Generic.List[string]]::new()
$output.Add(('lines: "{0}"' -f $merged.Count))
foreach ($record in $merged.Values | Sort-Object Date -Descending) {
    $first = $record.First.ToString('0', [Globalization.CultureInfo]::InvariantCulture)
    $amount = if ($record.Mixed) { $first } else { $first + '/' + $record.Second.ToString('0', [Globalization.CultureInfo]::InvariantCulture) }
    $output.Add($record.Date + ' ' + $amount)
}
$temporary = Join-Path $destination ('history-import-' + [Guid]::NewGuid().ToString('N') + '.tmp')
[IO.File]::WriteAllLines($temporary, $output, [Text.UTF8Encoding]::new($false))
if (Test-Path -LiteralPath $target) { [IO.File]::Replace($temporary, $target, (Join-Path $backup 'destination-history.dat')) }
else { [IO.File]::Move($temporary, $target) }
Write-Host "History imported: $($sourceRecords.Count) source days, $($targetRecords.Count) destination days, $($merged.Count) unique days. Duplicate dates use the larger daily total. Backup: $backup"
