$ErrorActionPreference = 'Stop'
$productRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$builds = Join-Path $productRoot 'artifacts\builds'
$script = Join-Path $PSScriptRoot 'retire-previous-build.ps1'
$stamp = [TimeSpan]::FromSeconds((Get-Random -Minimum 0 -Maximum 86400)).ToString('hhmmss')
$old = Join-Path $builds ('19980101-' + $stamp)
$new = Join-Path $builds ('19990101-' + $stamp)
$created = @()
$backupsBefore = @(Get-ChildItem -LiteralPath (Join-Path $productRoot 'artifacts\backups') -Directory | Select-Object -ExpandProperty FullName)
function Assert-Refused([scriptblock]$Action) {
    try { & $Action | Out-Null }
    catch { return }
    throw 'An unsafe operation was unexpectedly accepted.'
}
try {
    foreach ($directory in @($old, $new)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
        $created += $directory
        New-Item -ItemType Directory -Path (Join-Path $directory 'Media') | Out-Null
        foreach ($relative in @('TaskBarWidgets.exe', 'OpenHardwareMonitorApi.dll', 'LibreHardwareMonitorLib.dll', 'Media\TaskBarWidgets.Media.exe')) {
            [IO.File]::WriteAllText((Join-Path $directory $relative), 'Synthetic fixture, never executed.')
        }
        $year = if ($directory -eq $old) { '1998' } else { '1999' }
        [ordered]@{ component='All'; configuration='Release'; buildStartedAt="$year-01-01T00:00:00+08:00"; buildCompletedAt="$year-01-01T00:01:00+08:00" } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'build-info.json') -Encoding UTF8
    }
    New-Item -ItemType Directory -Path (Join-Path $old 'plugins') | Out-Null
    foreach ($relative in @('config.ini','global_cfg.ini','history_traffic.dat','history_traffic.dat.bak','plugins\Battery.dll.ini','plugins\PowerMonPlugin.dll.ini','plugins\WeatherPro.ini')) {
        [IO.File]::WriteAllText((Join-Path $old $relative), "Synthetic user data for $relative`r`ncredential=synthetic-test-only`r`n")
    }
    $plan = (& $script -PreviousBuild $old -NewBuild $new | Out-String | ConvertFrom-Json)
    if ($plan.Mode -ne 'Plan' -or -not (Test-Path -LiteralPath $old) -or (Test-Path -LiteralPath (Join-Path $new 'config.ini'))) { throw 'Plan modified fixture data.' }
    Assert-Refused { & $script -PreviousBuild $productRoot -NewBuild $new -Execute }
    Assert-Refused { & $script -PreviousBuild $new -NewBuild $new -Execute }
    [IO.File]::WriteAllText((Join-Path $new 'config.ini'), 'Conflicting newer settings.')
    Assert-Refused { & $script -PreviousBuild $old -NewBuild $new -Execute }
    Remove-Item -LiteralPath (Join-Path $new 'config.ini')
    $locked = [IO.File]::Open((Join-Path $old 'history_traffic.dat'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { Assert-Refused { & $script -PreviousBuild $old -NewBuild $new -Execute } }
    finally { $locked.Dispose() }
    & $script -PreviousBuild $old -NewBuild $new -Execute
    if (Test-Path -LiteralPath $old) { throw 'Immediate previous fixture build was not retired.' }
    foreach ($record in $plan.Files | Where-Object Migrate) {
        if ((Get-FileHash -LiteralPath (Join-Path $new $record.RelativePath) -Algorithm SHA256).Hash -ne $record.SHA256) { throw 'Migrated user bytes changed.' }
    }
    $saved = @(Get-ChildItem -LiteralPath (Join-Path $productRoot 'artifacts\backups') -Directory | Where-Object FullName -NotIn $backupsBefore)
    if ($saved.Count -ne 1 -or -not (Test-Path -LiteralPath (Join-Path $saved[0].FullName 'retirement.json'))) { throw 'Retirement backup or record is missing.' }
    Write-Host 'Build retirement regression passed: dry plan, path/order/conflict/lock rejection, data backup/migration and single previous-build deletion.'
}
finally {
    foreach ($directory in $created) {
        $full = [IO.Path]::GetFullPath($directory)
        if ((Split-Path -Parent $full) -ine $builds -or (Split-Path -Leaf $full) -notmatch '^(19980101|19990101)-\d{6}$') { throw 'Unsafe fixture cleanup path.' }
        if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    }
    foreach ($backup in Get-ChildItem -LiteralPath (Join-Path $productRoot 'artifacts\backups') -Directory) {
        if ($backup.FullName -in $backupsBefore -or $backup.Name -notlike ('retired-19980101-' + $stamp + '-*')) { continue }
        if ($backup.Parent.FullName -ine (Join-Path $productRoot 'artifacts\backups') -or $backup.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe fixture backup cleanup path.' }
        Remove-Item -LiteralPath $backup.FullName -Recurse -Force
    }
}
