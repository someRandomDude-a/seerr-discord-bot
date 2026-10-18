param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$PlaywrightModule,
    [Parameter(Mandatory)][string]$AxeFile,
    [Parameter(Mandatory)][string]$ProviderZip,
    [ValidateRange(10, 300)][int]$StartupTimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
# CI-only fresh runner; never replace a retained fixture or use production credentials.
$name = 'mediahub-test-settings'
$root = Join-Path $env:LOCALAPPDATA 'Temp/opencode'
$null = New-Item -ItemType Directory -Path $root -Force
$harness = Join-Path $PSScriptRoot 'Test-Jellyfin.ps1'
$arguments = @{ Name = $name; Port = 18296; PackageVersion = $Version; StartupTimeoutSeconds = $StartupTimeoutSeconds }
$digest = (Get-FileHash -Algorithm SHA256 $Package).Hash
$dockerHelper = Join-Path $PSScriptRoot 'Invoke-TestDocker.ps1'
$existing = @(& $dockerHelper -Arguments @('ps', '-a', '--format', '{{.Names}}') -TimeoutSeconds 15)
if ((Test-Path (Join-Path $root ($name + '.json'))) -or $existing -contains $name) { throw 'Fresh release smoke refuses retained fixture state. Nothing was started or stopped.' }
try {
    & $harness @arguments -Mode Start
    & $harness @arguments -Mode Install -Package $Package
    & $harness @arguments -Mode Restart
    & (Join-Path $PSScriptRoot 'Test-WebDependencies.ps1') -Name $name -Port 18296
    & $harness @arguments -Mode Check
    & node (Join-Path $PSScriptRoot 'Test-NativeUi.mjs') (Join-Path $root ($name + '.json')) $PlaywrightModule (Join-Path $root 'release-smoke.png') $AxeFile
    if ($LASTEXITCODE -ne 0) { throw 'Exact-ZIP native browser smoke failed.' }
    & $harness @arguments -Mode Uninstall
    & $harness @arguments -Mode Install -Package $Package
    & (Join-Path $PSScriptRoot 'Test-Completion.ps1') -Package $Package -Version $Version -PlaywrightModule $PlaywrightModule -AxeFile $AxeFile
    & (Join-Path $PSScriptRoot 'Test-LibraryAcceptance.ps1') -Package $Package -PackageVersion $Version -PlaywrightModule $PlaywrightModule
    & (Join-Path $PSScriptRoot 'Test-ProviderAcceptance.ps1') -ProviderZip $ProviderZip -PlaywrightModule $PlaywrightModule
    if ((Get-FileHash -Algorithm SHA256 $Package).Hash -ne $digest) { throw 'Release ZIP changed during smoke tests.' }
    Write-Output ('Exact-ZIP lifecycle/native-browser gate passed. SHA256=' + $digest.ToLowerInvariant())
} finally {
    # Label-guarded stop only. Retain volumes/state for runner-local failure diagnosis.
    try {
        $owned = @(& $dockerHelper -Arguments @('ps', '-a', '--filter', ('name=^/' + $name + '$'), '--filter', ('label=org.mediahub.acceptance=' + $name), '--format', '{{.Names}}') -TimeoutSeconds 10)
        if ($owned.Count -eq 1 -and $owned[0] -ceq $name) { $null = & $dockerHelper -Arguments @('stop', '--time', '20', $name) -TimeoutSeconds 30 }
    } catch { Write-Warning 'Owned smoke-fixture stop was not confirmed. Private state and volumes were retained; no daemon reset or automatic retry was performed.' }
}
