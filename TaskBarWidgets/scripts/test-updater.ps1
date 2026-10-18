[CmdletBinding()]
param([switch]$SkipBuild)
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
function New-Case([string]$Name) {
    $base = Join-Path $testRoot $Name
    $install = Join-Path $base 'install'; $stage = Join-Path $base 'stage'; $work = Join-Path $base 'jobs\test'; $native = Join-Path $base 'native'; $media = Join-Path $base 'media'
    New-Item -ItemType Directory -Path $install,$stage,$work,$native,(Join-Path $media 'database') -Force | Out-Null
    Copy-Item -LiteralPath $fake -Destination (Join-Path $install 'TaskBarWidgets.exe')
    Copy-Item -LiteralPath $fake -Destination (Join-Path $stage 'TaskBarWidgets.exe')
    Copy-Item -LiteralPath $testUpdater -Destination (Join-Path $work 'TaskBarWidgets.Updater.exe')
    [IO.File]::WriteAllText((Join-Path $install 'OpenHardwareMonitorApi.dll'), 'old program')
    [IO.File]::WriteAllText((Join-Path $stage 'OpenHardwareMonitorApi.dll'), 'new program')
    [IO.File]::WriteAllText((Join-Path $native 'config.ini'), 'private config')
    [IO.File]::WriteAllText((Join-Path $install 'global_cfg.ini'), 'global marker')
    [IO.File]::WriteAllText((Join-Path $media 'media-settings.json'), 'private settings')
    [IO.File]::WriteAllText((Join-Path $media 'database\user_data.db'), 'private db')
    New-Item -ItemType Directory -Path (Join-Path $install 'UI'),(Join-Path $install 'Media') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $install 'UI\my-note.txt'), 'unknown user note')
    [IO.File]::WriteAllText((Join-Path $install 'Media\custom.json'), 'unknown user file')
    $inventory = [ordered]@{ protocolVersion = 1; version = '261018-1700'; architecture = 'win-x64'; files = @() }
    foreach ($name in @('OpenHardwareMonitorApi.dll','TaskBarWidgets.exe')) {
        $file = Get-Item -LiteralPath (Join-Path $stage $name)
        $inventory.files += @{ path=$name; bytes=$file.Length; sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    Write-Json (Join-Path $stage 'app-files.json') $inventory
    $job = [ordered]@{ protocolVersion=1; installRoot=$install;stageRoot=$stage;workRoot=$work;nativeDataRoot=$native;mediaDataRoot=$media;version=$inventory.version; fileManifestSha256=(Get-FileHash -LiteralPath (Join-Path $stage 'app-files.json')).Hash.ToLowerInvariant(); nonce=[guid]::NewGuid().ToString('N');nativePid=0;mediaPid=0;uiPid=0;requirements=@{dotnetDesktop='8.0.0';windowsAppRuntime='2.5.1.0';windowsBuild=22000} }
    $path=Join-Path $work 'job.json'; Write-Json $path $job
    Write-Json (Join-Path $work 'shutdown-approved.json') @{nonce=$job.nonce}
    return @{install=$install;stage=$stage;work=$work;native=$native;media=$media;job=$job;path=$path}
}
function Invoke-Case($Case,[switch]$Recover,[scriptblock]$OnReady) {
    $verb=if($Recover){'--recover'}else{'--job'}
    $process=Start-Process -FilePath (Join-Path $Case.work 'TaskBarWidgets.Updater.exe') -ArgumentList @($verb,('"'+$Case.path+'"')) -WindowStyle Hidden -PassThru
    if ($OnReady) {
        $deadline=[DateTime]::UtcNow.AddSeconds(10);$ready=$false
        while([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited){
            try{$snapshot=Get-Content -LiteralPath (Join-Path $Case.work 'status.json') -Raw -ErrorAction Stop|ConvertFrom-Json;if($snapshot.phase -eq 'readyToExit'){$ready=$true;break}}catch{}
            Start-Sleep -Milliseconds 25
        }
        Check $ready 'Updater did not reach isolated pre-exit readiness.'
        & $OnReady $Case
    }
    Check ($process.WaitForExit(15000)) 'Isolated updater test timed out.'
    $statusPath=Join-Path $Case.work 'status.json'
    Check (Test-Path -LiteralPath $statusPath) "Missing status for $($Case.work)"
    return Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
}
function Assert-Original($Case) {
    Check (([IO.File]::ReadAllText((Join-Path $Case.install 'OpenHardwareMonitorApi.dll'))) -eq 'old program') 'Original program was not preserved/restored.'
    Check (([IO.File]::ReadAllText((Join-Path $Case.native 'config.ini'))) -eq 'private config') 'Native config changed.'
    Check (([IO.File]::ReadAllText((Join-Path $Case.media 'media-settings.json'))) -eq 'private settings') 'Media settings changed.'
    Check (([IO.File]::ReadAllText((Join-Path $Case.media 'database\user_data.db'))) -eq 'private db') 'Media database changed.'
    Check (([IO.File]::ReadAllText((Join-Path $Case.install 'global_cfg.ini'))) -eq 'global marker') 'Global marker changed.'
    Check (([IO.File]::ReadAllText((Join-Path $Case.install 'UI\my-note.txt'))) -eq 'unknown user note') 'Uninventoried UI file was deleted.'
    Check (([IO.File]::ReadAllText((Join-Path $Case.install 'Media\custom.json'))) -eq 'unknown user file') 'Uninventoried Media file was deleted.'
}
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
Check (([IO.File]::ReadAllText((Join-Path $case.native 'config.ini'))) -eq 'private config') 'Success overwrote personal data.'
Check (([IO.File]::ReadAllText((Join-Path $case.install 'UI\my-note.txt'))) -eq 'unknown user note') 'Success deleted an unknown UI file.'
Check (([IO.File]::ReadAllText((Join-Path $case.install 'Media\custom.json'))) -eq 'unknown user file') 'Success deleted an unknown Media file.'
Write-Host 'PASS successful transaction and personal data preservation'
$case=New-Case 'exact-settings-program-resources';$inventory=Get-Content (Join-Path $case.stage 'app-files.json') -Raw|ConvertFrom-Json
foreach($path in @('UI/Settings/SettingsThemeResources.xbf','Media/Web/Settings/bridge.js','Media/Web/Settings/hotkeys.js','Media/Web/Settings/integration.js','Media/Web/Settings/settings.css','Media/Web/Settings/settings.html','Media/Web/Settings/settings.js','Media/Web/Settings/state.js','Media/Web/Settings/track-offsets-page.js','vccorlib140.dll','release-manifest.json')){
    $target=Join-Path $case.stage $path;New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force|Out-Null;[IO.File]::WriteAllText($target,'program resource');$file=Get-Item $target
    $inventory.files+=@{path=$path;bytes=$file.Length;sha256=(Get-FileHash $target).Hash}
}
Write-Json (Join-Path $case.stage 'app-files.json') $inventory;$case.job.fileManifestSha256=(Get-FileHash (Join-Path $case.stage 'app-files.json')).Hash;Write-Json $case.path $case.job;$status=Invoke-Case $case
Check ($status.phase -eq 'complete') "Exact packaged program resources rejected: $($status.message)"
Write-Host 'PASS exact Settings program resources, vccorlib and release manifest accepted'
$case=New-Case 'unknown-settings-file';$inventory=Get-Content (Join-Path $case.stage 'app-files.json') -Raw|ConvertFrom-Json;$inventory.files[0].path='UI/Settings/my-note.txt';Write-Json (Join-Path $case.stage 'app-files.json') $inventory;$case.job.fileManifestSha256=(Get-FileHash (Join-Path $case.stage 'app-files.json')).Hash;Write-Json $case.path $case.job;$status=Invoke-Case $case
Check ($status.phase -eq 'error') 'Settings resource exceptions must not allow unrelated personal files.';Assert-Original $case
Write-Host 'PASS unrelated Settings subtree file remains protected'
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
$case=New-Case 'trailing-root-separators';foreach($key in @('installRoot','stageRoot','workRoot','nativeDataRoot','mediaDataRoot')){$case.job[$key]+='\'};Write-Json $case.path $case.job;$status=Invoke-Case $case
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
    $status=Invoke-Case $case -OnReady {param($readyCase);$readyCase.testLock=[IO.File]::Open((Join-Path $readyCase.native 'config.ini'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None);Write-Json (Join-Path $readyCase.work 'shutdown-approved.json') @{nonce=$readyCase.job.nonce}}
    Check ($status.phase -eq 'error' -and (Test-Path (Join-Path $case.work 'test-old-restarted'))) 'Post-exit backup failure did not safely restart old host.'
}finally{if($case.testLock){$case.testLock.Dispose()}}
Assert-Original $case
Check ((Get-Content (Join-Path $case.work 'journal.json') -Raw|ConvertFrom-Json).phase -eq 'rolledBack') 'Intact abort journal must be terminal before old restart.'
Write-Host 'PASS post-exit backup failure preserves state and restarts intact old host'
$case=New-Case 'no-exit';$old=Start-Process -FilePath (Join-Path $case.install 'TaskBarWidgets.exe') -ArgumentList @('--old','2400') -WindowStyle Hidden -PassThru;$case.job.nativePid=$old.Id;Write-Json $case.path $case.job;$status=Invoke-Case $case
Check ($status.phase -eq 'error' -and -not $old.HasExited) 'Old-process timeout did not leave process running.';Assert-Original $case;$old.WaitForExit()
Write-Host 'PASS old process exit timeout without termination'
$case=New-Case 'no-approval';Remove-Item -LiteralPath (Join-Path $case.work 'shutdown-approved.json');$status=Invoke-Case $case
Check ($status.phase -eq 'error') 'Exited processes without authenticated shutdown approval should not install.';Assert-Original $case
Write-Host 'PASS shutdown requires explicit nonce approval even after all processes exit'
$case=New-Case 'cancel';[IO.File]::WriteAllText((Join-Path $case.work 'cancel.request'),'1');$status=Invoke-Case $case
Check ($status.phase -eq 'error') 'Cancelled preflight should not install.';Assert-Original $case
Write-Host 'PASS cancellation before mutation'
$case=New-Case 'rollback-data';[IO.File]::WriteAllText((Join-Path $case.work 'test-mode.txt'),'fail-data');$status=Invoke-Case $case
Check ($status.phase -eq 'rolledBack') "Failed health should roll back: $($status.phase)";Assert-Original $case
Check (-not (Test-Path -LiteralPath (Join-Path $case.media 'database\user_data.db-wal'))) 'New SQLite WAL survived rollback.'
Write-Host 'PASS failed health rolls programs/settings/database/sidecars back'
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
# All launched processes are short-lived purpose-built fixtures; never touch real application processes.
Write-Host "Updater isolated checks passed. Evidence: $testRoot"
