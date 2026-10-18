function Get-SystemDotnetRoot {
    $key = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $installed = $key.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64')
        try { $root = if ($installed) { $installed.GetValue('InstallLocation') } else { $null } } finally { if ($installed) { $installed.Dispose() } }
    } finally { $key.Dispose() }
    if (-not $root) { $root = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet' }
    [IO.Path]::GetFullPath($root)
}
function New-SharedRuntimeProcess([string]$Directory, [string]$Executable, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $Directory $Executable))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = Split-Path -Parent $start.FileName
    $start.Environment['DOTNET_ROOT'] = $start.Environment['DOTNET_ROOT_X64'] = Get-SystemDotnetRoot
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    foreach ($key in @($start.Environment.Keys | Where-Object { $_ -like 'DOTNET_ROLL_FORWARD*' })) { $start.Environment.Remove($key) | Out-Null }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start
}
