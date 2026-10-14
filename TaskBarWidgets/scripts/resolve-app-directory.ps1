param(
    [string]$Directory,
    [string]$ParameterName = 'OutputDirectory'
)
$productRoot = Split-Path -Parent $PSScriptRoot
if (-not $Directory) {
    $pointer = Join-Path $productRoot 'artifacts\latest-build.txt'
    if (-not (Test-Path -LiteralPath $pointer -PathType Leaf)) {
        throw "No successful full build is recorded. Run scripts/build.ps1 or specify -$ParameterName."
    }
    $Directory = (Get-Content -LiteralPath $pointer -Raw).Trim()
    if (-not $Directory -or -not [IO.Path]::IsPathRooted($Directory) -or -not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "Invalid latest-build.txt. Run scripts/build.ps1 or specify -$ParameterName."
    }
}
$resolved = [IO.Path]::GetFullPath($Directory)
$artifactPrefix = [IO.Path]::GetFullPath((Join-Path $productRoot 'artifacts')).TrimEnd('\') + '\'
if (-not $resolved.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Application directory must be inside product artifacts.'
}
$resolved
