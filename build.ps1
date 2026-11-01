[CmdletBinding()]
param([switch]$NoPackage, [string]$CompatibilityVersion = '12.4.0')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$candidates = @(
    @{ Name = 'py'; Prefix = @('-3') },
    @{ Name = 'python3'; Prefix = @() },
    @{ Name = 'python'; Prefix = @() }
)
foreach ($candidate in $candidates) {
    $command = Get-Command $candidate.Name -ErrorAction SilentlyContinue
    if (-not $command) { continue }
    try {
        $probe = @($candidate.Prefix) + @('-c', 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)')
        & $command.Source @probe
        $valid = ($LASTEXITCODE -eq 0)
    } catch { $valid = $false }
    if (-not $valid) { continue }
    $arguments = @($candidate.Prefix) + @((Join-Path $PSScriptRoot 'tools/build-local.py'), '--compatibility-version', $CompatibilityVersion)
    if ($NoPackage) { $arguments += '--no-package' }
    & $command.Source @arguments
    if ($LASTEXITCODE -ne 0) { throw "Trailer Reel validation failed (exit $LASTEXITCODE)." }
    return
}
throw 'Python 3.10+ was not found. Install Python, reopen PowerShell, then run build.ps1 again.'
