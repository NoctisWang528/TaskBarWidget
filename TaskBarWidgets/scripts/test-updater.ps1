[CmdletBinding()]
param([switch]$SkipBuild, [string]$PackageStage, [string]$PreviousInventory,
    [switch]$BridgeOnly, [string[]]$LegacyValidatorDirectory)
$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $productRoot ('artifacts\updater-tests\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$binaryRoot = Join-Path $productRoot 'Native\Bin\x64\UpdaterTests'
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build-updater.ps1') -OutputDirectory $binaryRoot -TestMode | Out-Null }
$testUpdater = Join-Path $binaryRoot 'TaskBarWidgets.Updater.Test.exe'
$fake = Join-Path $binaryRoot 'TaskBarWidgets.Updater.FakeHost.exe'
$sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$manifestTool = Get-ChildItem -LiteralPath $sdkBin -Directory | Where-Object Name -Match '^\d+\.\d+\.\d+\.\d+$' | Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'x64\mt.exe' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $manifestTool) { throw 'Windows SDK manifest inspection tool is required.' }
foreach ($fixture in @($testUpdater,$fake)) {
    $manifestPath = Join-Path $testRoot ([IO.Path]::GetFileName($fixture) + '.manifest')
    & $manifestTool -nologo "-inputresource:$fixture;#1" "-out:$manifestPath"
    if ($LASTEXITCODE -ne 0) { throw 'Isolated updater fixture has no embedded elevation manifest.' }
    $manifestXml = [xml](Get-Content -LiteralPath $manifestPath -Raw)
    if ($manifestXml.SelectSingleNode("//*[local-name()='requestedExecutionLevel']").level -ne 'asInvoker') { throw 'Isolated test executable must never request elevation.' }
}
function Write-Json($Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false)) }
function Check($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Read-FixtureJson([string]$Path) {
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader=[IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd()|ConvertFrom-Json } finally { $reader.Dispose() }
}
function New-Case([string]$Name) {
    $base = Join-Path $testRoot $Name
    $install = Join-Path $base 'install'; $stage = Join-Path $base 'stage'; $work = Join-Path $base 'jobs\test'; $data = Join-Path $install 'Data'
    New-Item -ItemType Directory -Path $install,$stage,$work,(Join-Path $data 'config'),(Join-Path $data 'history'),(Join-Path $data 'lyrics'),(Join-Path $data 'secrets') -Force | Out-Null
    Copy-Item -LiteralPath $fake -Destination (Join-Path $install 'TaskBarWidgets.exe')
    Copy-Item -LiteralPath $fake -Destination (Join-Path $stage 'TaskBarWidgets.exe')
    Copy-Item -LiteralPath $testUpdater -Destination (Join-Path $work 'TaskBarWidgets.Updater.exe')
    [IO.File]::WriteAllText((Join-Path $install 'OpenHardwareMonitorApi.dll'), 'old program')
    [IO.File]::WriteAllText((Join-Path $stage 'OpenHardwareMonitorApi.dll'), 'new program')
    [IO.File]::WriteAllText((Join-Path $data 'config\app.ini'), 'private config')
    [IO.File]::WriteAllText((Join-Path $data 'config\media.json'), 'private settings')
    [IO.File]::WriteAllText((Join-Path $data 'history\traffic.dat'), "lines:`"0`"`r`n")
    [IO.File]::WriteAllText((Join-Path $data 'lyrics\user_data.db'), 'private db')
    [IO.File]::WriteAllText((Join-Path $data 'secrets\weather-appkey.bin'), 'private key')
    New-Item -ItemType Directory -Path (Join-Path $install 'UI'),(Join-Path $install 'Media') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $install 'UI\my-note.txt'), 'unknown user note')
    [IO.File]::WriteAllText((Join-Path $install 'Media\custom.json'), 'unknown user file')
    $inventory = [ordered]@{ protocolVersion = 1; version = '261018-1700'; architecture = 'win-x64'; files = @() }
    foreach ($name in @('OpenHardwareMonitorApi.dll','TaskBarWidgets.exe')) {
        $file = Get-Item -LiteralPath (Join-Path $stage $name)
        $inventory.files += @{ path=$name; bytes=$file.Length; sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    Write-Json (Join-Path $stage 'app-files.json') $inventory
    $job = [ordered]@{ protocolVersion=2; installRoot=$install;stageRoot=$stage;workRoot=$work;dataRoot=$data;version=$inventory.version; fileManifestSha256=(Get-FileHash -LiteralPath (Join-Path $stage 'app-files.json')).Hash.ToLowerInvariant(); nonce=[guid]::NewGuid().ToString('N');nativePid=0;mediaPid=0;uiPid=0;requirements=@{dotnetDesktop='8.0.0';windowsAppRuntime='2.5.1.0';windowsBuild=22000} }
    $path=Join-Path $work 'job.json'; Write-Json $path $job
    Write-Json (Join-Path $work 'shutdown-approved.json') @{nonce=$job.nonce}
    return @{install=$install;stage=$stage;work=$work;data=$data;job=$job;path=$path}
}
function Invoke-Case($Case,[switch]$Recover,[scriptblock]$OnReady,[switch]$BridgeRecovery) {
    $verb=if($Recover){'--recover'}else{'--job'}
    $helperName=if($BridgeRecovery){'BridgeRecovery.exe'}else{'TaskBarWidgets.Updater.exe'}
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $Case.work $helperName))
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$Case.work
    $start.ArgumentList.Add($verb);$start.ArgumentList.Add($Case.path)
    $process=[Diagnostics.Process]::Start($start)
    if ($OnReady) {
        $deadline=[DateTime]::UtcNow.AddSeconds(10);$ready=$false
        while([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited){
            try{$snapshot=Read-FixtureJson (Join-Path $Case.work 'status.json');if($snapshot.phase -eq 'readyToExit'){$ready=$true;break}}catch{}
            Start-Sleep -Milliseconds 25
        }
        Check $ready 'Updater did not reach isolated pre-exit readiness.'
        & $OnReady $Case
    }
    Check ($process.WaitForExit(15000)) 'Isolated updater test timed out.'
    $statusPath=Join-Path $Case.work 'status.json'
    Check (Test-Path -LiteralPath $statusPath) "Missing status for $($Case.work)"
    return Read-FixtureJson $statusPath
}
function Assert-Original($Case) {
    Check (([IO.File]::ReadAllText((Join-Path $Case.install 'OpenHardwareMonitorApi.dll'))) -eq 'old program') 'Original program was not preserved/restored.'
    Check (([IO.File]::ReadAllText((Join-Path $Case.data 'config\app.ini'))) -eq 'private config') 'Native config changed.'
    Check (([IO.File]::ReadAllText((Join-Path $Case.data 'config\media.json'))) -eq 'private settings') 'Media settings changed.'
    Check (([IO.File]::ReadAllText((Join-Path $Case.data 'lyrics\user_data.db'))) -eq 'private db') 'Lyrics database changed.'
    Check (([IO.File]::ReadAllText((Join-Path $Case.data 'secrets\weather-appkey.bin'))) -eq 'private key') 'Weather credential changed.'
    Check (([IO.File]::ReadAllText((Join-Path $Case.install 'UI\my-note.txt'))) -eq 'unknown user note') 'Uninventoried UI file was deleted.'
    Check (([IO.File]::ReadAllText((Join-Path $Case.install 'Media\custom.json'))) -eq 'unknown user file') 'Uninventoried Media file was deleted.'
}
if (-not $BridgeOnly) {
$case=New-Case 'three-live-processes'
$hosts=@()
$signal=Join-Path $case.work 'fixture-exit.signal'
try {
    foreach($entry in @(@('nativePid','TaskBarWidgets.exe'),@('mediaPid','Media/TaskBarWidgets.Media.exe'),@('uiPid','UI/TaskBarWidgets.UI.exe'))) {
        $target=Join-Path $case.install $entry[1]
        if($entry[0] -ne 'nativePid') { Copy-Item -LiteralPath $fake -Destination $target }
        $hostProcess=Start-Process -FilePath $target -ArgumentList @('--wait-for-exit',('"'+$signal+'"')) -WindowStyle Hidden -PassThru
        $hosts+=$hostProcess
        $case.job[$entry[0]]=$hostProcess.Id
    }
    Write-Json $case.path $case.job
    $status=Invoke-Case $case -OnReady {param($readyCase);[IO.File]::WriteAllText((Join-Path $readyCase.work 'fixture-exit.signal'),'exit')}
    Check ($status.phase -eq 'complete') 'Native/Media/UI process identities must match despite path separator spelling.'
} finally {
    [IO.File]::WriteAllText($signal,'exit')
    foreach($hostProcess in $hosts) { Check ($hostProcess.WaitForExit(3000)) 'Fixture failed to exit normally.'; $hostProcess.Dispose() }
}
Write-Host 'PASS three live Native/Media/UI identities and graceful exit'
$case=New-Case 'foreign-process'
$foreignRoot=Join-Path (Split-Path $case.install) 'other-install/Media'
New-Item -ItemType Directory -Path $foreignRoot -Force|Out-Null
$foreignExe=Join-Path $foreignRoot 'TaskBarWidgets.Media.exe'
Copy-Item -LiteralPath $fake -Destination $foreignExe
$signal=Join-Path $case.work 'foreign-exit.signal'
$foreign=Start-Process -FilePath $foreignExe -ArgumentList @('--wait-for-exit',('"'+$signal+'"')) -WindowStyle Hidden -PassThru
try {
    $case.job.mediaPid=$foreign.Id;Write-Json $case.path $case.job
    $status=Invoke-Case $case
    Check ($status.phase -eq 'error' -and $status.message -match 'Process ID no longer belongs' -and -not $foreign.HasExited) 'Other installation process must still be rejected without closing it.'
    Check (-not (Test-Path (Join-Path $case.work 'journal.json'))) 'Identity mismatch must fail before mutation.'
    Assert-Original $case
} finally { [IO.File]::WriteAllText($signal,'exit');Check ($foreign.WaitForExit(3000)) 'Foreign fixture failed to exit.';$foreign.Dispose() }
Write-Host 'PASS foreign same-name process rejected without mutation'
$case=New-Case 'success'
$previous=Join-Path (Split-Path -Parent $case.work) 'previous';New-Item -ItemType Directory -Path (Join-Path $previous 'backup\program') -Force|Out-Null
$priorJob=@{};foreach($key in $case.job.Keys){$priorJob[$key]=$case.job[$key]};$priorJob.workRoot=$previous;$priorJob.nonce=[guid]::NewGuid().ToString('N');Write-Json (Join-Path $previous 'job.json') $priorJob
$priorBackup=Join-Path $previous 'backup\program\OpenHardwareMonitorApi.dll';[IO.File]::WriteAllText($priorBackup,'older backup')
Write-Json (Join-Path $previous 'journal.json') @{protocolVersion=1;nonce=$priorJob.nonce;phase='complete';launchedPid=0;files=@(@{group='program';path='OpenHardwareMonitorApi.dll';existed=$true;sha256=(Get-FileHash $priorBackup).Hash})}
$status=Invoke-Case $case
Check ($status.phase -eq 'complete') "Success case: $($status | ConvertTo-Json -Compress)"
Check (([IO.File]::ReadAllText((Join-Path $case.install 'OpenHardwareMonitorApi.dll'))) -eq 'new program') 'New program missing.'
Check (Test-Path -LiteralPath (Join-Path $case.work 'backup\program\OpenHardwareMonitorApi.dll')) 'Rollback backup missing.'
Check (-not (Test-Path -LiteralPath (Join-Path $previous 'backup'))) 'Previous completed backup was not rotated after health.'
Check (([IO.File]::ReadAllText((Join-Path $case.data 'config\app.ini'))) -eq 'private config') 'Success overwrote personal data.'
Check (([IO.File]::ReadAllText((Join-Path $case.install 'UI\my-note.txt'))) -eq 'unknown user note') 'Success deleted an unknown UI file.'
Check (([IO.File]::ReadAllText((Join-Path $case.install 'Media\custom.json'))) -eq 'unknown user file') 'Success deleted an unknown Media file.'
Write-Host 'PASS successful transaction and personal data preservation'
$case=New-Case 'exact-settings-program-resources';$inventory=Get-Content (Join-Path $case.stage 'app-files.json') -Raw|ConvertFrom-Json
foreach($path in @('UI/Settings/SettingsThemeResources.xbf','UI/Resources/SettingsNavigationResources.xbf','vccorlib140.dll','release-manifest.json')){
    $target=Join-Path $case.stage $path;New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force|Out-Null;[IO.File]::WriteAllText($target,'program resource');$file=Get-Item $target
    $inventory.files+=@{path=$path;bytes=$file.Length;sha256=(Get-FileHash $target).Hash}
}
Write-Json (Join-Path $case.stage 'app-files.json') $inventory;$case.job.fileManifestSha256=(Get-FileHash (Join-Path $case.stage 'app-files.json')).Hash;Write-Json $case.path $case.job;$status=Invoke-Case $case
Check ($status.phase -eq 'complete') "Exact packaged program resources rejected: $($status.message)"
Write-Host 'PASS exact Settings program resources, vccorlib and release manifest accepted'
$case=New-Case 'unknown-settings-file';$inventory=Get-Content (Join-Path $case.stage 'app-files.json') -Raw|ConvertFrom-Json;$inventory.files[0].path='UI/Settings/my-note.txt';Write-Json (Join-Path $case.stage 'app-files.json') $inventory;$case.job.fileManifestSha256=(Get-FileHash (Join-Path $case.stage 'app-files.json')).Hash;Write-Json $case.path $case.job;$status=Invoke-Case $case
Check ($status.phase -eq 'error') 'Settings resource exceptions must not allow unrelated personal files.';Assert-Original $case
Write-Host 'PASS unrelated Settings subtree file remains protected'
$case=New-Case 'blocked-new-settings-resource';$inventory=Get-Content (Join-Path $case.stage 'app-files.json') -Raw|ConvertFrom-Json;$inventory.files[0].path='UI/Settings/SettingsNavigationResources.xbf';Write-Json (Join-Path $case.stage 'app-files.json') $inventory;$case.job.fileManifestSha256=(Get-FileHash (Join-Path $case.stage 'app-files.json')).Hash;Write-Json $case.path $case.job;$status=Invoke-Case $case
Check ($status.phase -eq 'error' -and $status.message -match 'Protected data directory') 'The old protected Settings path must remain forbidden.';Assert-Original $case
Write-Host 'PASS new resource relocated without widening the old Settings allowlist'
if ($PackageStage) {
    $fullStage = [IO.Path]::GetFullPath($PackageStage)
    $artifactPrefix = [IO.Path]::GetFullPath((Join-Path $productRoot 'artifacts')).TrimEnd('\') + '\'
    Check ($fullStage.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) 'Full package fixture must be inside product artifacts.'
    $case = New-Case 'full-package-preflight'
    if ($PreviousInventory) {
        Copy-Item -LiteralPath ([IO.Path]::GetFullPath($PreviousInventory)) -Destination (Join-Path $case.install 'app-files.json')
    }
    foreach ($file in Get-ChildItem -LiteralPath $fullStage -Recurse -File) {
        Check (-not ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) 'Linked fixture input is forbidden.'
        $destination = Join-Path $case.stage ([IO.Path]::GetRelativePath($fullStage, $file.FullName))
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
    $inventory = Get-Content (Join-Path $case.stage 'app-files.json') -Raw | ConvertFrom-Json
    $case.job.version = $inventory.version
    $case.job.fileManifestSha256 = (Get-FileHash (Join-Path $case.stage 'app-files.json')).Hash
    Write-Json $case.path $case.job
    # No approval means Run must stop before Backup/Apply/Launch. The full real
    # executable is hashed and checked but is NEVER run, even in this test fixture.
    Remove-Item -LiteralPath (Join-Path $case.work 'shutdown-approved.json')
    $status = Invoke-Case $case -OnReady { param($readyCase); Write-Host 'PASS full package reached native readyToExit' }
    Check ($status.phase -eq 'error' -and $status.message -match 'did not approve shutdown') 'Full package did not stop safely before installation.'
    Check (-not (Test-Path (Join-Path $case.work 'journal.json'))) 'Full package fixture unexpectedly began replacement.'
    Assert-Original $case
    if ($PreviousInventory) {
        Check ((Get-FileHash (Join-Path $case.install 'app-files.json')).Hash -eq (Get-FileHash -LiteralPath $PreviousInventory).Hash) 'Previous inventory was changed during preflight.'
    }
    Write-Host "PASS full published inventory ($($inventory.files.Count) files), no product launch or installation"
}
$case=New-Case 'bad-hash'; [IO.File]::AppendAllText((Join-Path $case.stage 'OpenHardwareMonitorApi.dll'),'bad');$status=Invoke-Case $case
Check ($status.phase -eq 'error') 'Bad staged hash accepted.';Assert-Original $case
Write-Host 'PASS staged hash rejected before exit/replacement'
$case=New-Case 'bad-dependency';$case.job.requirements.dotnetDesktop='missing';Write-Json $case.path $case.job;$status=Invoke-Case $case
Check ($status.phase -eq 'error') 'Missing dependency accepted.';Assert-Original $case
Write-Host 'PASS dependency failure before replacement'
$case=New-Case 'protected-path';$inventory=Get-Content (Join-Path $case.stage 'app-files.json') -Raw|ConvertFrom-Json;$inventory.files[0].path='Media/media-settings.json';Write-Json (Join-Path $case.stage 'app-files.json') $inventory;$case.job.fileManifestSha256=(Get-FileHash (Join-Path $case.stage 'app-files.json')).Hash;Write-Json $case.path $case.job;$status=Invoke-Case $case
Check ($status.phase -eq 'error') 'Protected configuration accepted.';Assert-Original $case
Write-Host 'PASS protected personal path rejected'
$case=New-Case 'protected-license-path';$inventory=Get-Content (Join-Path $case.stage 'app-files.json') -Raw|ConvertFrom-Json;$inventory.files[0].path='licenses/private.db';Write-Json (Join-Path $case.stage 'app-files.json') $inventory;$case.job.fileManifestSha256=(Get-FileHash (Join-Path $case.stage 'app-files.json')).Hash;Write-Json $case.path $case.job;$status=Invoke-Case $case
Check ($status.phase -eq 'error') 'Protected database in licenses subtree accepted.';Assert-Original $case
Write-Host 'PASS licenses subtree does not bypass protected personal filenames'
$case=New-Case 'old-inventory';$oldInventory=Get-Content (Join-Path $case.stage 'app-files.json') -Raw|ConvertFrom-Json;$oldInventory.files[0].path='config.ini';Write-Json (Join-Path $case.install 'app-files.json') $oldInventory;$status=Invoke-Case $case
Check ($status.phase -eq 'error' -and -not (Test-Path (Join-Path $case.work 'journal.json'))) 'Malformed old inventory must fail preflight.';Assert-Original $case
Write-Host 'PASS old managed inventory validated before shutdown'
$case=New-Case 'trailing-root-separators';foreach($key in @('installRoot','stageRoot','workRoot','dataRoot')){$case.job[$key]+='\'};Write-Json $case.path $case.job;$status=Invoke-Case $case
Check ($status.phase -eq 'complete') 'Canonical trailing root separators should be supported.'
Write-Host 'PASS root separator normalization'
$case=New-Case 'locked';$locked=[IO.File]::Open((Join-Path $case.install 'OpenHardwareMonitorApi.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{$status=Invoke-Case $case;Check ($status.phase -eq 'error') 'Locked file should preserve original before modification.';Check (Test-Path (Join-Path $case.work 'test-old-restarted')) 'Prepared transaction failure did not restart old host.';Assert-Original $case}finally{$locked.Dispose()}
Write-Host 'PASS locked target preserves original'
$case=New-Case 'stage-changed-after-exit';Remove-Item -LiteralPath (Join-Path $case.work 'shutdown-approved.json')
$status=Invoke-Case $case -OnReady {param($readyCase);[IO.File]::AppendAllText((Join-Path $readyCase.stage 'OpenHardwareMonitorApi.dll'),'corrupt');Write-Json (Join-Path $readyCase.work 'shutdown-approved.json') @{nonce=$readyCase.job.nonce}}
Check ($status.phase -eq 'error' -and (Test-Path (Join-Path $case.work 'test-old-restarted'))) 'Post-exit stage verification failure did not safely restart old host.';Assert-Original $case
Write-Host 'PASS post-exit verification failure restarts intact old host'
$case=New-Case 'backup-failure-after-exit';Remove-Item -LiteralPath (Join-Path $case.work 'shutdown-approved.json')
try{
    $status=Invoke-Case $case -OnReady {param($readyCase);$readyCase.testLock=[IO.File]::Open((Join-Path $readyCase.data 'config\app.ini'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None);Write-Json (Join-Path $readyCase.work 'shutdown-approved.json') @{nonce=$readyCase.job.nonce}}
    Check ($status.phase -eq 'error' -and (Test-Path (Join-Path $case.work 'test-old-restarted'))) 'Post-exit backup failure did not safely restart old host.'
}finally{if($case.testLock){$case.testLock.Dispose()}}
Assert-Original $case
Check ((Get-Content (Join-Path $case.work 'journal.json') -Raw|ConvertFrom-Json).phase -eq 'rolledBack') 'Intact abort journal must be terminal before old restart.'
Write-Host 'PASS post-exit backup failure preserves state and restarts intact old host'
$case=New-Case 'no-exit';$signal=Join-Path $case.work 'fixture-exit.signal'
$old=Start-Process -FilePath (Join-Path $case.install 'TaskBarWidgets.exe') -ArgumentList @('--wait-for-exit',('"'+$signal+'"')) -WindowStyle Hidden -PassThru
try {
    $case.job.nativePid=$old.Id;Write-Json $case.path $case.job;$status=Invoke-Case $case
    Check ($status.phase -eq 'error' -and -not $old.HasExited) 'Old-process timeout did not leave process running.';Assert-Original $case
} finally { [IO.File]::WriteAllText($signal,'exit');Check ($old.WaitForExit(3000)) 'Fixture failed to exit normally.';$old.Dispose() }
Write-Host 'PASS old process exit timeout without termination'
$case=New-Case 'no-approval';Remove-Item -LiteralPath (Join-Path $case.work 'shutdown-approved.json');$status=Invoke-Case $case
Check ($status.phase -eq 'error') 'Exited processes without authenticated shutdown approval should not install.';Assert-Original $case
Write-Host 'PASS shutdown requires explicit nonce approval even after all processes exit'
$case=New-Case 'cancel';[IO.File]::WriteAllText((Join-Path $case.work 'cancel.request'),'1');$status=Invoke-Case $case
Check ($status.phase -eq 'error') 'Cancelled preflight should not install.';Assert-Original $case
Write-Host 'PASS cancellation before mutation'
$case=New-Case 'rollback-data';[IO.File]::WriteAllText((Join-Path $case.work 'test-mode.txt'),'fail-data')
[IO.File]::WriteAllText((Join-Path $case.data 'lyrics\user_data.db-wal.notes'),'unknown user note')
$status=Invoke-Case $case
Check ($status.phase -eq 'rolledBack') "Failed health should roll back: $($status.phase)";Assert-Original $case
Check (-not (Test-Path -LiteralPath (Join-Path $case.data 'lyrics\user_data.db-wal'))) 'New SQLite WAL survived rollback.'
Check (-not (Test-Path -LiteralPath (Join-Path $case.data 'lyrics\user_data.db-shm'))) 'New SQLite SHM survived rollback.'
Check (-not (Test-Path -LiteralPath (Join-Path $case.data 'lyrics\user_data.db-journal'))) 'New SQLite rollback journal survived rollback.'
Check (([IO.File]::ReadAllText((Join-Path $case.data 'lyrics\user_data.db-wal.notes'))) -eq 'unknown user note') 'Unrecognized sidecar-like file was deleted.'
Write-Host 'PASS failed health rolls programs/settings/database/sidecars back'
$case=New-Case 'rollback-existing-sidecars';[IO.File]::WriteAllText((Join-Path $case.work 'test-mode.txt'),'fail-data')
foreach($suffix in @('wal','shm','journal')) {
    [IO.File]::WriteAllText((Join-Path $case.data "lyrics\user_data.db-$suffix"), "private $suffix")
}
$status=Invoke-Case $case
Check ($status.phase -eq 'rolledBack') "Existing-sidecar rollback failed: $($status.phase)";Assert-Original $case
foreach($suffix in @('wal','shm','journal')) {
    Check (([IO.File]::ReadAllText((Join-Path $case.data "lyrics\user_data.db-$suffix"))) -eq "private $suffix") "Original SQLite $suffix was not restored."
    Check (([IO.File]::ReadAllText((Join-Path $case.work "backup\media\lyrics\user_data.db-$suffix"))) -eq "private $suffix") "Original SQLite $suffix was not backed up."
}
Write-Host 'PASS pre-existing SQLite sidecars backed up and restored with the database'
$case=New-Case 'normal-close';[IO.File]::WriteAllText((Join-Path $case.work 'test-mode.txt'),'no-health');$status=Invoke-Case $case
Check ($status.phase -eq 'rolledBack') 'Unhealthy host was not normally closed and rolled back.';Assert-Original $case
Write-Host 'PASS unhealthy host normal close before rollback'
$case=New-Case 'interrupted';[IO.File]::WriteAllText((Join-Path $case.work 'test-interrupt'),'1');$status=Invoke-Case $case
Check ($status.phase -eq 'installing') 'Fault injection did not interrupt transaction.'
Check (([IO.File]::ReadAllText((Join-Path $case.install 'OpenHardwareMonitorApi.dll'))) -eq 'new program') 'Interrupted transaction never changed its first file.'
$locked=[IO.File]::Open((Join-Path $case.install 'OpenHardwareMonitorApi.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{$status=Invoke-Case $case -Recover;Check ($status.phase -eq 'recoveryRequired') 'Locked recovery should retain backup and report recoveryRequired.'}finally{$locked.Dispose()}
$status=Invoke-Case $case -Recover;Check ($status.phase -eq 'rolledBack') 'Interrupted journal recovery failed.';Assert-Original $case
Write-Host 'PASS interrupted durable journal recovery'
}

function Write-Inventory([string]$Directory, [string]$Version) {
    $files = @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Where-Object { $_.FullName -ne (Join-Path $Directory 'app-files.json') } | Sort-Object FullName | ForEach-Object {
        @{ path=[IO.Path]::GetRelativePath($Directory,$_.FullName).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
    })
    Write-Json (Join-Path $Directory 'app-files.json') @{protocolVersion=1;version=$Version;architecture='win-x64';files=$files}
}
function New-BridgeCase([string]$Name, [string]$LegacyDirectory, [string]$Problem) {
    $case=New-Case $Name
    $final=Join-Path (Split-Path $case.install) 'final'
    New-Item -ItemType Directory -Path $final -Force | Out-Null
    foreach($name in @('TaskBarWidgets.exe','OpenHardwareMonitorApi.dll')) { Copy-Item -LiteralPath (Join-Path $case.stage $name) -Destination (Join-Path $final $name) }
    Copy-Item -LiteralPath $testUpdater -Destination (Join-Path $final 'TaskBarWidgets.Updater.exe')
    foreach($name in @('TaskBarWidgets.PeripheralProbe.exe','peripheral-compatibility.csv','peripheral-compatibility.json','外设电量.md','Dependencies/HeadsetControl/headsetcontrol.exe')) {
        $path=Join-Path $final $name;New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
        [IO.File]::WriteAllText($path,'isolated fixture payload')
    }
    if($LegacyDirectory) {
        # Only published program inputs, never personal data from the supplied directory.
        foreach($name in @('Media/TaskBarWidgets.Media.runtimeconfig.json','UI/TaskBarWidgets.UI.runtimeconfig.json','UI/Microsoft.WindowsAppRuntime.Bootstrap.dll')) {
            $path=Join-Path $final $name;New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $LegacyDirectory $name) -Destination $path
        }
    }
    Write-Inventory $final $case.job.version
    $payload=Join-Path $case.stage 'Media/UpgradePayload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    $inner=Get-Content -LiteralPath (Join-Path $final 'app-files.json') -Raw | ConvertFrom-Json
    foreach($file in $inner.files) {
        $relative=if($file.path -in @('TaskBarWidgets.exe','TaskBarWidgets.PeripheralProbe.exe','peripheral-compatibility.csv','peripheral-compatibility.json','外设电量.md') -or $file.path.StartsWith('Dependencies/')) { 'Media/UpgradePayload/'+$file.sha256.Substring(0,16)+'.bin' } else { $file.path }
        $target=Join-Path $case.stage $relative;New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $final $file.path) -Destination $target -Force
    }
    Copy-Item -LiteralPath $testUpdater -Destination (Join-Path $case.stage 'TaskBarWidgets.exe') -Force
    if($Problem -eq 'protected') { $inner.files[0].path='Data/config/private.json' }
    Write-Json (Join-Path $payload 'manifest.json') $inner
    $innerHash=(Get-FileHash -LiteralPath (Join-Path $payload 'manifest.json')).Hash.ToLowerInvariant()
    if($Problem -eq 'inner-hash') { $innerHash='0'*64 }
    Write-Json (Join-Path $payload 'bridge.json') @{format='TBW_BRIDGE_V1';version=$case.job.version;fileManifestSha256=$innerHash}
    Write-Json (Join-Path $payload 'resume.json') @{}
    Write-Inventory $case.stage $case.job.version
    $case.job.fileManifestSha256=(Get-FileHash -LiteralPath (Join-Path $case.stage 'app-files.json')).Hash.ToLowerInvariant()
    Write-Json $case.path $case.job
    $case.final=$final
    return $case
}
function Wait-Bridge($Case, [string]$Expected) {
    $deadline=[DateTime]::UtcNow.AddSeconds(25)
    do {
        try {
            $status=Read-FixtureJson (Join-Path $Case.work 'status.json')
            $journal=Read-FixtureJson (Join-Path $Case.work 'journal.json')
            if($status.phase -eq $Expected -and $journal.phase -eq $Expected -and ($Expected -ne 'complete' -or $journal.bridge)) {
                # The original helper may already have exited; wait for the NEW
                # helper so a later recovery test never races its mutex.
                try { $helper=[Diagnostics.Process]::GetProcessById([int]$status.helperPid);Check ($helper.WaitForExit(5000)) 'Bridge helper did not exit.';$helper.Dispose() } catch [ArgumentException] { }
                return $status
            }
            if($status.phase -in @('rolledBack','recoveryRequired','error') -and $status.phase -ne $Expected) { break }
        } catch [IO.IOException] { }
        Start-Sleep -Milliseconds 50
    } while([DateTime]::UtcNow -lt $deadline)
    $detail=if(Test-Path -LiteralPath (Join-Path $Case.work 'bridge-error.json')) { Get-Content -LiteralPath (Join-Path $Case.work 'bridge-error.json') -Raw } else { $status|ConvertTo-Json -Compress }
    throw "Bridge expected $Expected but did not finish: $detail; evidence $($Case.work)"
}
function Assert-BridgeSuccess($Case) {
    Check ((Get-FileHash -LiteralPath (Join-Path $Case.install 'app-files.json')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $Case.final 'app-files.json')).Hash) 'Installed final inventory differs from the real ROOT payload.'
    Check (Test-Path -LiteralPath (Join-Path $Case.install 'TaskBarWidgets.PeripheralProbe.exe')) 'Peripheral probe is not at installation ROOT.'
    Check (Test-Path -LiteralPath (Join-Path $Case.install 'Dependencies/HeadsetControl/headsetcontrol.exe')) 'Dependencies ROOT layout was not retained.'
    Check (-not @(Get-ChildItem -LiteralPath (Join-Path $Case.install 'Media/UpgradePayload') -File).Count) 'Transport payload files survived final installation.'
    Check (([IO.File]::ReadAllText((Join-Path $Case.work 'backup/program/OpenHardwareMonitorApi.dll'))) -eq 'old program') 'Bridge replaced the ORIGINAL backup with an intermediate version.'
    Check (([IO.File]::ReadAllText((Join-Path $Case.data 'config/app.ini'))) -eq 'private config') 'Bridge changed personal data.'
}
$case=New-BridgeCase 'bridge-success' '' ''
$null=Invoke-Case $case
$null=Wait-Bridge $case 'complete'
Assert-BridgeSuccess $case
Write-Host 'PASS bridge automatically continues to ROOT layout with one original backup'
foreach($problem in @('protected','inner-hash')) {
    $case=New-BridgeCase ('bridge-'+$problem) '' $problem
    $status=Invoke-Case $case
    Check ($status.phase -eq 'rolledBack') 'Invalid inner payload must roll back the outer installation.'
    Assert-Original $case
    Write-Host "PASS bridge rejects $problem despite valid outer hashes"
}
$case=New-BridgeCase 'bridge-final-health-failure' '' ''
[IO.File]::WriteAllText((Join-Path $case.work 'test-mode.txt'),'fail-data')
$null=Invoke-Case $case
$null=Wait-Bridge $case 'rolledBack'
Assert-Original $case
Check (-not (Test-Path -LiteralPath (Join-Path $case.install 'TaskBarWidgets.PeripheralProbe.exe'))) 'Final-only root file survived original-version rollback.'
Write-Host 'PASS final health failure restores PRE-BRIDGE programs and original personal data'
$case=New-BridgeCase 'bridge-interrupted' '' ''
[IO.File]::WriteAllText((Join-Path $case.work 'test-bridge-interrupt'),'1')
$null=Invoke-Case $case
$null=Wait-Bridge $case 'installing'
$status=Invoke-Case $case -Recover
Check ($status.phase -eq 'rolledBack') 'Interrupted bridge did not restore the original transaction.'
Assert-Original $case
Write-Host 'PASS interrupted second stage recovers the original backup'

foreach($action in @('resume','recover')) {
    $case=New-BridgeCase ('bridge-handoff-'+$action) '' ''
    [IO.File]::WriteAllText((Join-Path $case.work 'test-bridge-handoff-stop'),'1')
    $null=Invoke-Case $case
    $journal=Read-FixtureJson (Join-Path $case.work 'journal.json')
    Check ($journal.phase -eq 'complete' -and -not $journal.bridge) 'Fault injection did not stop at the legacy handoff.'
    try { $entry=[Diagnostics.Process]::GetProcessById([int]$journal.launchedPid);Check ($entry.WaitForExit(5000)) 'Bridge fixture failed to stop.';$entry.Dispose() } catch [ArgumentException] { }
    if($action -eq 'resume') {
        $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $case.install 'TaskBarWidgets.exe'))
        $start.UseShellExecute=$false;$start.CreateNoWindow=$true
        $entry=[Diagnostics.Process]::Start($start)
        Check ($entry.WaitForExit(5000)) 'Confirmed bridge resume did not hand off.';$entry.Dispose()
        $null=Wait-Bridge $case 'complete'
        Assert-BridgeSuccess $case
    } else {
        $status=Invoke-Case $case -Recover -BridgeRecovery
        Check ($status.phase -eq 'rolledBack') 'Recovery mistook legacy completion for final product success.'
        Assert-Original $case
    }
    Write-Host "PASS interrupted legacy handoff can $action without losing the original backup"
}
function Get-ProgramSections([string]$Path) {
    $bytes=[IO.File]::ReadAllBytes($Path)
    $pe=[BitConverter]::ToInt32($bytes,60)
    $count=[BitConverter]::ToUInt16($bytes,$pe+6)
    $table=$pe+24+[BitConverter]::ToUInt16($bytes,$pe+20)
    $hashes=@(for($index=0;$index -lt $count;$index++) {
        $at=$table+40*$index
        $name=[Text.Encoding]::ASCII.GetString($bytes,$at,8).TrimEnd([char]0)
        if($name -eq '.rsrc') { continue }
        $length=[BitConverter]::ToInt32($bytes,$at+16)
        $offset=[BitConverter]::ToInt32($bytes,$at+20)
        $section=[byte[]]::new($length);[Array]::Copy($bytes,$offset,$section,0,$length)
        $name+':'+[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($section))
    })
    return ($hashes -join ';')
}
foreach($legacyDirectory in $LegacyValidatorDirectory) {
    $legacyDirectory=[IO.Path]::GetFullPath($legacyDirectory)
    $version=(Get-Content -LiteralPath (Join-Path $legacyDirectory 'build-info.json') -Raw|ConvertFrom-Json).version
    $case=New-BridgeCase ('legacy-'+$version) $legacyDirectory ''
    # Exercise the legacy installed-inventory allowlist as well as its incoming
    # envelope policy. Missing program files are intentional in this FakeHost fixture.
    $previousInventory=Join-Path $legacyDirectory 'app-files.json'
    Check (Test-Path -LiteralPath $previousInventory -PathType Leaf) 'Legacy program inventory is required for this compatibility fixture.'
    Copy-Item -LiteralPath $previousInventory -Destination (Join-Path $case.install 'app-files.json')
    $legacySource=Join-Path $legacyDirectory 'TaskBarWidgets.Updater.exe'
    $legacyCopy=Join-Path $case.work 'TaskBarWidgets.Updater.exe'
    Copy-Item -LiteralPath $legacySource -Destination $legacyCopy -Force
    # Real released code, isolated copy only. Remove the elevation REQUEST from
    # its manifest so this test never elevates; do not patch executable code.
    $manifestPath=Join-Path (Split-Path $case.install) 'legacy-asInvoker.manifest'
    & $manifestTool -nologo "-inputresource:$legacyCopy;#1" "-out:$manifestPath"
    Check ($LASTEXITCODE -eq 0) 'Cannot inspect legacy helper manifest.'
    $manifestXml=[xml](Get-Content -LiteralPath $manifestPath -Raw)
    $manifestXml.SelectSingleNode("//*[local-name()='requestedExecutionLevel']").SetAttribute('level','asInvoker')
    $manifestXml.Save($manifestPath)
    & $manifestTool -nologo -manifest $manifestPath "-outputresource:$legacyCopy;#1"
    Check ($LASTEXITCODE -eq 0) 'Cannot make isolated legacy helper non-elevating.'
    $programSections=Get-ProgramSections $legacySource
    Check ($programSections -ceq (Get-ProgramSections $legacyCopy)) 'Manifest patch unexpectedly changed legacy executable code/data sections.'
    $release=Join-Path (Split-Path $case.install) 'release';New-Item -ItemType Directory -Path $release|Out-Null
    $assetName="TaskBarWidgets-$($case.job.version)-win-x64.zip"
    $zip=Join-Path $release $assetName
    $archive=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($file in Get-ChildItem -LiteralPath $case.stage -Recurse -File) {
            $relative=[IO.Path]::GetRelativePath($case.stage,$file.FullName).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,([IO.Path]::GetFileNameWithoutExtension($assetName)+'/'+$relative))|Out-Null
        }
    } finally { $archive.Dispose() }
    $sha=(Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant()
    Write-Json (Join-Path $release 'update.json') @{protocolVersion=1;version=$case.job.version;architecture='win-x64';assetName=$assetName;size=(Get-Item -LiteralPath $zip).Length;sha256=$sha;fileManifestSha256=$case.job.fileManifestSha256;requirements=$case.job.requirements}
    [IO.File]::WriteAllText((Join-Path $release 'SHA256SUMS.txt'),"$sha  $assetName`n")
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-release-package.ps1') -ReleaseDirectory $release -ValidatorDirectory $legacyDirectory -LegacyJobFixture (Join-Path $case.work 'job.json')
    Check ($LASTEXITCODE -eq 0) 'Actual legacy Media rejected the full bridge ZIP.'
    $originalHash=(Get-FileHash -LiteralPath $legacySource).Hash
    $fixtureHash=(Get-FileHash -LiteralPath $legacyCopy).Hash
    $null=Invoke-Case $case
    $null=Wait-Bridge $case 'complete'
    Assert-BridgeSuccess $case
    Write-Json (Join-Path (Split-Path $case.install) 'legacy-evidence.json') @{version=$version;source=$legacySource;sourceSha256=$originalHash;fixtureSha256=$fixtureHash;manifestOnlyChange='requireAdministrator -> asInvoker';nonResourceSections=$programSections;success=$true}
    Write-Host "PASS actual $version Media ZIP validation + released native helper bridge -> final ROOT layout"
}
# All launched processes are short-lived purpose-built fixtures; never touch real application processes.
Write-Host "Updater isolated checks passed. Evidence: $testRoot"
