[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$productRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$prefix = $productRoot.TrimEnd('\') + '\'
$pending = [Collections.Generic.Stack[string]]::new()
$pending.Push($productRoot)
$count = 0
while ($pending.Count -gt 0) {
    $directory = $pending.Pop()
    foreach ($child in Get-ChildItem -LiteralPath $directory -Directory) {
        if ($child.Name -notin @('.packages', 'artifacts', 'bin', 'obj', 'node_modules', '.vs')) { $pending.Push($child.FullName) }
    }
    foreach ($project in Get-ChildItem -LiteralPath $directory -File | Where-Object Extension -in @('.csproj', '.vcxproj', '.props', '.targets')) {
        $xml = [xml](Get-Content -LiteralPath $project.FullName -Raw)
        foreach ($item in $xml.SelectNodes('//*[@Include] | //*[local-name()="HintPath"]')) {
            $value = if ($item.HasAttribute('Include')) { $item.GetAttribute('Include') } else { $item.InnerText }
            if ($item.LocalName -in @('PackageReference', 'Reference', 'ProjectConfiguration', 'Filter', 'Using')) { continue }
            if ($value -match '\$\(|%\(|\*|;|^https?://' -or [string]::IsNullOrWhiteSpace($value)) { continue }
            $resolved = [IO.Path]::GetFullPath((Join-Path $project.DirectoryName $value))
            if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Project input escapes TaskBarWidgets: $($project.FullName) -> $value"
            }
            $count++
        }
    }
}
Write-Host "Product boundary check passed ($count literal project inputs). Dynamic MSBuild/toolchain paths require build verification."
