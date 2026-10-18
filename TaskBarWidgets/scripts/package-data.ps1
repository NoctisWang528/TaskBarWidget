# Shared snapshot helpers. Source files are opened for reading only and are never overwritten.
function Copy-StableDataFile([string]$Source, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { throw "Snapshot destination already exists: $Destination" }
    $item = Get-Item -LiteralPath $Source
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked personal data is not supported.' }
    $bytes = $null
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try {
            # Deny write/delete while reading; an in-progress writer causes a bounded retry.
            $stream = [IO.File]::Open($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                $memory = [IO.MemoryStream]::new()
                try { $stream.CopyTo($memory); $bytes = $memory.ToArray() } finally { $memory.Dispose() }
            } finally { $stream.Dispose() }
            break
        } catch [IO.IOException] {
            if ($attempt -eq 4) { throw }
            Start-Sleep -Milliseconds 150
        }
    }
    if ([IO.Path]::GetExtension($Source) -eq '.json') {
        try { [Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xfeff) | ConvertFrom-Json -Depth 100 | Out-Null }
        catch { throw "Invalid JSON snapshot: $Source" }
    }
    if ([IO.Path]::GetFileName($Source) -match '^history_traffic\.dat') {
        $lines = [Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xfeff) -split '\r?\n'
        if ($lines[0] -notmatch '^lines:\s*"(?<count>\d+)"\s*$') { throw 'Invalid traffic snapshot header.' }
        $expected = [int]$Matches.count
        $records = @($lines | Select-Object -Skip 1 | Where-Object { $_.Trim().Length })
        if ($records.Count -ne $expected) { throw 'Traffic snapshot record count does not match its header.' }
        foreach ($line in $records) {
            if ($line -notmatch '^\d{4}/\d{2}/\d{2}\s+\d+(?:/\d+)?\s*$') { throw 'Invalid traffic snapshot record.' }
            $date = [datetime]::MinValue
            if (-not [datetime]::TryParseExact($line.Substring(0, 10), 'yyyy/MM/dd', [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::None, [ref]$date)) { throw 'Invalid traffic snapshot date.' }
        }
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
    [IO.File]::WriteAllBytes($Destination, $bytes)
    $expectedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Snapshot hash verification failed.' }
    [ordered]@{ name = [IO.Path]::GetFileName($Destination); bytes = $bytes.Length; sha256 = $expectedHash.ToLowerInvariant(); method = 'read-locked-validated-copy' }
}

function Assert-UnlinkedPath([string]$Path) {
    $walk = Get-Item -LiteralPath $Path
    while ($null -ne $walk) {
        if ($walk.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package paths must not contain links or junctions.' }
        $walk = if ($walk -is [IO.FileInfo]) { $walk.Directory } else { $walk.Parent }
    }
}

function Test-PortableDataMode([string]$GlobalConfig) {
    $inConfig = $false
    $portable = $false
    foreach ($raw in [IO.File]::ReadAllLines($GlobalConfig)) {
        $line = $raw.Trim()
        if ($line.StartsWith('[')) { $inConfig = $line -ieq '[config]'; continue }
        if ($inConfig -and $line -match '^portable_mode\s*=\s*(?<value>.+?)\s*$') {
            $number = 0
            $portable = $Matches.value -ieq 'true' -or ([int]::TryParse($Matches.value, [ref]$number) -and $number -ne 0)
        }
    }
    $portable
}
