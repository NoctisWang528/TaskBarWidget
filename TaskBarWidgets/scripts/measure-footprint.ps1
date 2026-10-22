[CmdletBinding()]
param(
    [int]$IntervalSeconds = 1,
    [int]$DurationSeconds = 60,
    [string]$Label = 'unspecified',
    [string]$BuildDirectory,
    [string]$OutputDirectory,
    [string[]]$ProcessNames = @('TaskBarWidgets', 'TaskBarWidgets.Media', 'TaskBarWidgets.UI'),
    [int]$CountdownSeconds = 3,
    [switch]$AllowMissing,
    [switch]$AllowAnyProcessPath,
    [switch]$SkipInternalMetrics
)
$ErrorActionPreference = 'Stop'

# Samples the three product processes and the product's own footprint counters.
# Repeated calls with different -Label build the state matrix recorded in
# docs/2026-10-22-资源占用优化方案.md (phase 0 baseline). Results land in the
# ignored artifacts directory, never in the repository.

$productRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $productRoot 'artifacts')).TrimEnd('\') + '\'

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $productRoot 'artifacts\measurements' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $OutputDirectory.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output directory must be inside product artifacts.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

if (-not $BuildDirectory) {
    $BuildDirectory = & (Join-Path $PSScriptRoot 'resolve-app-directory.ps1')
}
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
if (-not (Test-Path -LiteralPath $BuildDirectory -PathType Container)) { throw "Build directory not found: $BuildDirectory" }

# Private working set comes from the process performance object ("Working Set
# - Private"). It is the same attribution the task manager shows, one cheap query
# covers every process, and the English counter names resolve on any locale.
# QueryWorkingSetEx was tried first: it returns success but leaves the buffer
# empty on this host, so it is not used.
if (-not ('Footprint.PrivateWorkingSet' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Footprint
{
    public static class PrivateWorkingSet
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Item
        {
            public IntPtr Name;
            public uint Status;
            public long Value;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);

        private const uint MoreData = 0x800007D2;
        private const uint FormatLarge = 0x00000400;

        private static List<Item> Read(IntPtr counter)
        {
            uint size = 0;
            uint count = 0;
            if (PdhGetFormattedCounterArrayW(counter, FormatLarge, ref size, out count, IntPtr.Zero) != MoreData) return null;
            if (size == 0) return null;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, FormatLarge, ref size, out count, buffer) != 0) return null;
                var items = new List<Item>((int)count);
                var stride = Marshal.SizeOf(typeof(Item));
                for (var i = 0; i < count; i++)
                {
                    items.Add((Item)Marshal.PtrToStructure(new IntPtr(buffer.ToInt64() + (i * stride)), typeof(Item)));
                }
                return items;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // Process id -> private working set in bytes. Instance names repeat for
        // same-named processes ("#1"), so the id counter disambiguates them.
        public static Dictionary<int, long> Snapshot()
        {
            IntPtr query;
            if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) return null;
            try
            {
                IntPtr ids;
                IntPtr sets;
                if (PdhAddEnglishCounterW(query, "\\Process(*)\\ID Process", IntPtr.Zero, out ids) != 0) return null;
                if (PdhAddEnglishCounterW(query, "\\Process(*)\\Working Set - Private", IntPtr.Zero, out sets) != 0) return null;
                if (PdhCollectQueryData(query) != 0) return null;
                var idItems = Read(ids);
                var setItems = Read(sets);
                if (idItems == null || setItems == null || idItems.Count != setItems.Count) return null;
                var map = new Dictionary<int, long>(idItems.Count);
                for (var i = 0; i < idItems.Count; i++)
                {
                    var pid = (int)idItems[i].Value;
                    if (pid <= 0) continue;
                    map[pid] = setItems[i].Value;
                }
                return map;
            }
            finally
            {
                PdhCloseQuery(query);
            }
        }
    }
}
'@
}


function Get-ProcessSample {
    param([int]$ProcessId, [long]$PrivateWorkingSet = -1)
    try {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
        $privateBytes = $PrivateWorkingSet
        [pscustomobject]@{
            Name          = $process.ProcessName
            Id            = $process.Id
            StartTime     = $process.StartTime
            WorkingSet    = [int64]$process.WorkingSet64
            PrivateWorkingSet = [int64]$privateBytes
            Commit        = [int64]$process.PrivateMemorySize64
            PagedMemory   = [int64]$process.PagedMemorySize64
            Threads       = [int]$process.Threads.Count
            Handles       = [int]$process.HandleCount
            CpuSeconds    = [double]$process.TotalProcessorTime.TotalSeconds
        }
    }
    catch {
        $null
    }
}

function Invoke-Slope {
    param([double[]]$Values, [double]$SecondsPerSample)
    if ($Values.Count -lt 2 -or $SecondsPerSample -le 0) { return 0 }
    $n = $Values.Count
    $sumX = 0.0; $sumY = 0.0; $sumXY = 0.0; $sumXX = 0.0
    for ($i = 0; $i -lt $n; $i++) {
        $x = $i * $SecondsPerSample
        $y = $Values[$i]
        $sumX += $x; $sumY += $y; $sumXY += $x * $y; $sumXX += $x * $x
    }
    $denominator = ($n * $sumXX) - ($sumX * $sumX)
    if ([math]::Abs($denominator) -lt 1e-9) { return 0 }
    (($n * $sumXY) - ($sumX * $sumY)) / $denominator
}

function Measure-Series {
    param([double[]]$Values, [double]$SecondsPerSample)
    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $sum = 0.0
    foreach ($value in $Values) { $sum += $value }
    [pscustomobject]@{
        First           = [double]$Values[0]
        Last            = [double]$Values[$Values.Count - 1]
        Delta           = [double]($Values[$Values.Count - 1] - $Values[0])
        Min             = [double]$sorted[0]
        Max             = [double]$sorted[$sorted.Count - 1]
        Mean            = [double]($sum / $Values.Count)
        SlopePerSecond  = [double](Invoke-Slope -Values $Values -SecondsPerSample $SecondsPerSample)
    }
}

# --- resolve targets -------------------------------------------------------
$targets = @()
foreach ($name in $ProcessNames) {
    $running = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) {
        if (-not $AllowMissing) { throw "Process '$name' is not running. Start the build under test, or pass -AllowMissing." }
        Write-Warning "Process '$name' is not running."
        continue
    }
    if ($running.Count -gt 1) {
        Write-Warning "Several '$name' processes are running; only the first one is sampled. Close other installations for a clean baseline."
    }
    $process = $running[0]
    $path = $null
    try { $path = $process.Path } catch { }
    if (-not $AllowAnyProcessPath -and $path -and -not [IO.Path]::GetFullPath($path).StartsWith($BuildDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw "'$name' runs from '$path', which is outside $BuildDirectory. Refusing to sample another installation."
    }
    $targets += [pscustomobject]@{ Name = $name; Id = $process.Id; Path = $path }
}

if ($targets.Count -eq 0) { throw 'Nothing to measure.' }

$buildInfo = $null
try {
    $buildInfo = Get-Content -LiteralPath (Join-Path $BuildDirectory 'build-info.json') -Raw | ConvertFrom-Json
} catch { }
$gitCommit = $null
try { $gitCommit = (& git -C $productRoot rev-parse HEAD 2>$null) } catch { }
$gitBranch = $null
try { $gitBranch = (& git -C $productRoot rev-parse --abbrev-ref HEAD 2>$null) } catch { }

Write-Host "Label: $Label"
Write-Host "Build: $BuildDirectory"
if ($buildInfo) { Write-Host ("Version: {0}" -f $buildInfo.ProductVersion) }
Write-Host ("Duration: {0}s at {1}s intervals, {2} process(es)" -f $DurationSeconds, $IntervalSeconds, $targets.Count)
foreach ($target in $targets) { Write-Host ("  {0} pid={1} {2}" -f $target.Name, $target.Id, $target.Path) }

if ($CountdownSeconds -gt 0) {
    Write-Host ("Starting in {0}s. Leave the desktop in the state labelled '{1}'." -f $CountdownSeconds, $Label)
    Start-Sleep -Seconds $CountdownSeconds
}

# --- sampling --------------------------------------------------------------
$rows = New-Object System.Collections.ArrayList
$warnings = New-Object System.Collections.ArrayList
$samplingStartedAt = Get-Date
$stopwatch = [Diagnostics.Stopwatch]::StartNew()

for ($tick = 0; $tick -lt $DurationSeconds; $tick++) {
    $elapsedBefore = $stopwatch.Elapsed.TotalSeconds
    $privateMap = $null
    try { $privateMap = [Footprint.PrivateWorkingSet]::Snapshot() } catch { }
    foreach ($target in $targets) {
        $privateBytes = -1
        if ($privateMap -and $privateMap.ContainsKey($target.Id)) { $privateBytes = $privateMap[$target.Id] }
        $sample = Get-ProcessSample -ProcessId $target.Id -PrivateWorkingSet $privateBytes
        if (-not $sample) {
            [void]$warnings.Add("$($target.Name) pid=$($target.Id) exited during sampling.")
            continue
        }
        [void]$rows.Add([pscustomobject]@{
            Label           = $Label
            Tick            = $tick
            At              = (Get-Date)
            Process         = $target.Name
            Id              = $sample.Id
            StartTime       = $sample.StartTime
            WorkingSet      = $sample.WorkingSet
            PrivateWorkingSet = $sample.PrivateWorkingSet
            Commit          = $sample.Commit
            PagedMemory     = $sample.PagedMemory
            Threads         = $sample.Threads
            Handles         = $sample.Handles
            CpuSeconds      = $sample.CpuSeconds
        })
    }
    $targetSeconds = ($tick + 1) * $IntervalSeconds
    $remaining = $targetSeconds - $stopwatch.Elapsed.TotalSeconds
    if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int][math]::Round($remaining * 1000)) }
}
$stopwatch.Stop()
$samplingFinishedAt = Get-Date

# --- per process summary ---------------------------------------------------
$summaries = @()
foreach ($target in $targets) {
    $processRows = @($rows | Where-Object { $_.Id -eq $target.Id } | Sort-Object Tick)
    if ($processRows.Count -eq 0) { continue }
    $starts = @($processRows | Select-Object -ExpandProperty StartTime -Unique)
    if ($starts.Count -gt 1) { [void]$warnings.Add("$($target.Name) restarted during sampling; the series is not one instance.") }
    $seconds = @{}
    for ($i = 1; $i -lt $processRows.Count; $i++) {
        $gap = ($processRows[$i].At - $processRows[$i - 1].At).TotalSeconds
        if ($gap -gt 0) { $seconds[$i] = $gap }
    }
    $step = if ($seconds.Count -gt 0) { ($seconds.Values | Measure-Object -Average).Average } else { $IntervalSeconds }
    $values = @{
        WorkingSet        = [double[]]@($processRows | ForEach-Object { [double]$_.WorkingSet })
        PrivateWorkingSet = [double[]]@($processRows | ForEach-Object { [double]$_.PrivateWorkingSet })
        Commit            = [double[]]@($processRows | ForEach-Object { [double]$_.Commit })
        Threads           = [double[]]@($processRows | ForEach-Object { [double]$_.Threads })
        Handles           = [double[]]@($processRows | ForEach-Object { [double]$_.Handles })
        CpuSeconds        = [double[]]@($processRows | ForEach-Object { [double]$_.CpuSeconds })
    }
    $invalidPrivate = @($values['PrivateWorkingSet'] | Where-Object { $_ -lt 0 }).Count
    if ($invalidPrivate -gt 0) {
        [void]$warnings.Add("$($target.Name): private working set unavailable for $invalidPrivate of $($processRows.Count) samples; metric omitted.")
        $values.Remove('PrivateWorkingSet')
    }
    $metrics = @{}
    foreach ($key in $values.Keys) {
        $metrics[$key] = Measure-Series -Values $values[$key] -SecondsPerSample $step
    }
    $summaries += [pscustomobject]@{
        Process = $target.Name
        Id      = $target.Id
        Path    = $target.Path
        Samples = $processRows.Count
        Metrics = $metrics
    }
}

# --- internal counters -----------------------------------------------------
$internal = @{}
$rawMetricLines = New-Object System.Collections.ArrayList
$windowStart = $samplingStartedAt.AddSeconds(-1)
$windowEnd = $samplingFinishedAt.AddSeconds(1)
$counterKeys = @('allocBytes','gen0','gen1','gen2','fft','frames','pipeWrites','pipeBytes','getStateCount',
    'getStateTotalMs','getStateTextBytes','getStateCoverBytes','getStateIconBytes','opens','firstFrames',
    'timeouts','mainTicks','monitorTicks','taskbarTicks','seconds','deviceLost','invalidCall',
    'failedOps','suppressedOps','silentOps','invalidates','adjustRectChanged')

function ConvertTo-MetricTable {
    param([string[]]$Lines, [string]$Source)
    $entries = @()
    foreach ($line in $Lines) {
        if ($line -notmatch 'METRICS (?<payload>.+)$') { continue }
        $payload = $Matches['payload'].Trim()
        $pairs = @{}
        foreach ($token in ($payload -split '\s+')) {
            if ($token -match '^(?<key>[A-Za-z][A-Za-z0-9]*)=(?<value>-?[0-9.]+)$') {
                $pairs[$Matches['key']] = [double]$Matches['value']
            }
        }
        if ($pairs.Count -gt 0) { $entries += , $pairs }
    }
    if ($entries.Count -eq 0) { return $null }
    $keys = @($entries[0].Keys | Sort-Object)
    $summary = @{}
    foreach ($key in $keys) {
        $series = @($entries | ForEach-Object { if ($_.ContainsKey($key)) { [double]$_[$key] } })
        if ($series.Count -eq 0) { continue }
        $summary[$key] = [pscustomobject]@{
            First          = $series[0]
            Last           = $series[$series.Count - 1]
            Delta          = $series[$series.Count - 1] - $series[0]
            Min            = ($series | Measure-Object -Minimum).Minimum
            Max            = ($series | Measure-Object -Maximum).Maximum
            Samples        = $series.Count
            IsCounter      = ($counterKeys -contains $key)
        }
    }
    [pscustomobject]@{ Source = $Source; Lines = $entries.Count; Metrics = $summary }
}

if (-not $SkipInternalMetrics) {
    # Media helper: per-build log directory, METRICS lines carry a wall clock.
    $mediaLogs = @(Get-ChildItem -LiteralPath (Join-Path $BuildDirectory 'Media\Logs') -Filter 'app_debug*.log' -ErrorAction SilentlyContinue)
    $mediaLines = New-Object System.Collections.ArrayList
    foreach ($file in $mediaLogs) {
        $content = @(Get-Content -LiteralPath $file.FullName -ErrorAction SilentlyContinue)
        foreach ($line in $content) {
            if ($line -match '^\[(?<ts>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)[^\]]*\] \[METRICS\]') {
                $stamp = [datetime]::ParseExact($Matches['ts'], 'yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
                if ($stamp -ge $windowStart -and $stamp -le $windowEnd) {
                    # Rebuild as "<timestamp> METRICS <payload>" so one parser
                    # handles all three products.
                    $payload = $line.Substring($line.IndexOf('[METRICS]') + 9).TrimStart()
                    [void]$mediaLines.Add($line.Substring(0, $line.IndexOf(']') + 1) + ' METRICS ' + $payload)
                }
            }
        }
    }
    if ($mediaLines.Count -gt 0) {
        $internal['Media'] = ConvertTo-MetricTable -Lines $mediaLines -Source 'Media/Logs/app_debug.log'
        foreach ($line in $mediaLines) { [void]$rawMetricLines.Add("MEDIA $line") }
    }

    # Native host: shared appdata directory, so the window filter is mandatory.
    $nativePath = Join-Path $env:APPDATA 'TaskBarWidgets\native-metrics.log'
    if (Test-Path -LiteralPath $nativePath -PathType Leaf) {
        $nativeLines = New-Object System.Collections.ArrayList
        foreach ($line in @(Get-Content -LiteralPath $nativePath -ErrorAction SilentlyContinue)) {
            if ($line -match '^(?<ts>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) METRICS ') {
                $stamp = [datetime]::ParseExact($Matches['ts'], 'yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
                if ($stamp -ge $windowStart -and $stamp -le $windowEnd) { [void]$nativeLines.Add($line) }
            }
        }
        if ($nativeLines.Count -gt 0) {
            $internal['Native'] = ConvertTo-MetricTable -Lines $nativeLines -Source 'Native native-metrics.log'
            foreach ($line in $nativeLines) { [void]$rawMetricLines.Add("NATIVE $line") }
        }
    }

    # UI process: filter by pid so logs from earlier runs of the same build are ignored.
    $uiLog = Join-Path $BuildDirectory 'UI\ui-metrics.log'
    if (Test-Path -LiteralPath $uiLog -PathType Leaf) {
        $uiTarget = $targets | Where-Object { $_.Name -eq 'TaskBarWidgets.UI' } | Select-Object -First 1
        $uiLines = New-Object System.Collections.ArrayList
        foreach ($line in @(Get-Content -LiteralPath $uiLog -ErrorAction SilentlyContinue)) {
            if ($line -match '^(?<ts>\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[^\s]*) pid=(?<pid>\d+) METRICS ') {
                if ($uiTarget -and [int]$Matches['pid'] -ne $uiTarget.Id) { continue }
                $stamp = [datetimeoffset]::Parse($Matches['ts'], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None)
                if ($stamp.DateTime -ge $windowStart -and $stamp.DateTime -le $windowEnd) { [void]$uiLines.Add($line) }
            }
        }
        if ($uiLines.Count -gt 0) {
            $internal['Ui'] = ConvertTo-MetricTable -Lines $uiLines -Source 'UI/ui-metrics.log'
            foreach ($line in $uiLines) { [void]$rawMetricLines.Add("UI $line") }
        }
    }
}

# --- persist ---------------------------------------------------------------
$stampName = Get-Date -Format 'yyyyMMdd-HHmmss'
$baseName = "$stampName-$($Label -replace '[^A-Za-z0-9._-]', '_')"
$csvPath = Join-Path $OutputDirectory "$baseName.csv"
$jsonPath = Join-Path $OutputDirectory "$baseName.json"
$rawPath = Join-Path $OutputDirectory "$baseName.raw-metrics.log"

$rows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8
if ($rawMetricLines.Count -gt 0) {
    Set-Content -LiteralPath $rawPath -Value $rawMetricLines -Encoding UTF8
}

$result = [pscustomobject]@{
    Label           = $Label
    GeneratedAt     = (Get-Date).ToString('o')
    BuildDirectory  = $BuildDirectory
    BuildVersion    = $(if ($buildInfo) { $buildInfo.ProductVersion } else { $null })
    GitCommit       = $gitCommit
    GitBranch       = $gitBranch
    IntervalSeconds = $IntervalSeconds
    DurationSeconds = $DurationSeconds
    SamplingStarted = $samplingStartedAt.ToString('o')
    SamplingFinished= $samplingFinishedAt.ToString('o')
    Processes       = $summaries
    Internal        = $internal
    Warnings        = @($warnings)
    CsvPath         = $csvPath
}
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

# --- report ----------------------------------------------------------------
function Format-Bytes([double]$value) {
    if ($value -ge 1MB) { return ('{0:N1} MB' -f ($value / 1MB)) }
    if ($value -ge 1KB) { return ('{0:N1} KB' -f ($value / 1KB)) }
    ('{0:N0} B' -f $value)
}

Write-Host ''
Write-Host ("Footprint [{0}] {1} s, {2} samples per process" -f $Label, $DurationSeconds, $DurationSeconds)
$primary = foreach ($summary in $summaries) {
    $ws = $summary.Metrics['WorkingSet']
    $pws = $summary.Metrics['PrivateWorkingSet']
    $commit = $summary.Metrics['Commit']
    $threads = $summary.Metrics['Threads']
    $handles = $summary.Metrics['Handles']
    $cpu = $summary.Metrics['CpuSeconds']
    [pscustomobject]@{
        Process        = $summary.Process
        Pid            = $summary.Id
        'WS start'     = Format-Bytes $ws.First
        'WS end'       = Format-Bytes $ws.Last
        'dWS'          = ('{0:N2} MB' -f ($ws.Delta / 1MB))
        'WS MB/slope'  = ('{0:N3}' -f ($ws.SlopePerSecond / 1MB))
        'PWS start'    = Format-Bytes $pws.First
        'PWS end'      = Format-Bytes $pws.Last
        'dPWS'         = ('{0:N2} MB' -f ($pws.Delta / 1MB))
        'PWS MB/slope' = ('{0:N3}' -f ($pws.SlopePerSecond / 1MB))
        'Commit end'   = Format-Bytes $commit.Last
        'dCommit'      = ('{0:N2} MB' -f ($commit.Delta / 1MB))
        'Commit MB/slope' = ('{0:N3}' -f ($commit.SlopePerSecond / 1MB))
        'dThreads'     = ('{0:N0}' -f $threads.Delta)
        'dHandles'     = ('{0:N0}' -f $handles.Delta)
        'CPU ms/s'     = ('{0:N1}' -f ($cpu.SlopePerSecond * 1000))
    }
}
$primary | Format-Table -AutoSize | Out-String -Width 260 | Write-Host

if ($internal.Count -gt 0) {
    Write-Host 'Internal counters (window delta; counters show rate per second)'
    $internalRows = foreach ($name in ($internal.Keys | Sort-Object)) {
        $table = $internal[$name]
        if (-not $table) { continue }
        foreach ($key in ($table.Metrics.Keys | Sort-Object)) {
            $metric = $table.Metrics[$key]
            $rate = $null
            if ($metric.IsCounter -and $metric.Samples -gt 1) {
                $interval = 5.0
                if ($table.Metrics.ContainsKey('seconds')) { $interval = $table.Metrics['seconds'].Last }
                if ($interval -le 0) { $interval = 5.0 }
                $rate = ('{0:N3}/s' -f ($metric.Delta / [math]::Max(0.001, ($metric.Samples - 1) * $interval)))
            }
            [pscustomobject]@{
                Source  = $name
                Metric  = $key
                First   = ('{0:N1}' -f $metric.First)
                Last    = ('{0:N1}' -f $metric.Last)
                Delta   = ('{0:N1}' -f $metric.Delta)
                'Min/Max' = ('{0:N1}/{1:N1}' -f $metric.Min, $metric.Max)
                Rate    = $(if ($rate) { $rate } else { '' })
            }
        }
    }
    $internalRows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
}
elseif (-not $SkipInternalMetrics) {
    Write-Host 'Internal counters: no METRICS lines inside the sampling window (counters may not be running in this build).'
}

foreach ($warning in $warnings) { Write-Warning $warning }
Write-Host ("Results: {0}" -f $jsonPath)
Write-Host ("Raw per-second samples: {0}" -f $csvPath)
if ($rawMetricLines.Count -gt 0) { Write-Host ("Raw counter lines: {0}" -f $rawPath) }
