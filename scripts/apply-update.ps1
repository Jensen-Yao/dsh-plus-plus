param([Parameter(Mandatory=$true)][string]$Plan)
$ErrorActionPreference = 'Stop'
$updatePlan = Get-Content -LiteralPath $Plan -Raw | ConvertFrom-Json
$destinationRoot = [IO.Path]::GetFullPath($updatePlan.destination)
$stageRoot = [IO.Path]::GetFullPath($updatePlan.staged)
$backupRoot = [IO.Path]::GetFullPath($updatePlan.backup)
$deadline = (Get-Date).AddSeconds(90)
while (Get-Process -Id $updatePlan.pid -ErrorAction SilentlyContinue) {
    if ((Get-Date) -gt $deadline) { throw 'The previous application did not exit.' }
    Start-Sleep -Milliseconds 500
}
$copiedFiles = @()
try {
    foreach ($relative in $updatePlan.files) {
        $target = [IO.Path]::GetFullPath([IO.Path]::Combine($destinationRoot, $relative))
        $source = [IO.Path]::GetFullPath([IO.Path]::Combine($stageRoot, $relative))
        if (-not $target.StartsWith($destinationRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid update path.' }
        if (-not $source.StartsWith($stageRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staged path.' }
        $previous = [IO.Path]::Combine($backupRoot, $relative)
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        if (Test-Path -LiteralPath $target) {
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($previous)) -Force | Out-Null
            Copy-Item -LiteralPath $target -Destination $previous -Force
        }
        $copiedFiles += $relative
        Copy-Item -LiteralPath $source -Destination $target -Force
    }
    Start-Process -FilePath $updatePlan.executable -WorkingDirectory $destinationRoot -WindowStyle Hidden
} catch {
    foreach ($relative in $copiedFiles) {
        $previous = [IO.Path]::Combine($backupRoot, $relative)
        if (Test-Path -LiteralPath $previous) { Copy-Item -LiteralPath $previous -Destination ([IO.Path]::Combine($destinationRoot, $relative)) -Force }
    }
    $_ | Out-String | Set-Content -LiteralPath ([IO.Path]::Combine($backupRoot, 'update-error.txt'))
    Start-Process -FilePath $updatePlan.executable -WorkingDirectory $destinationRoot -WindowStyle Hidden
}
