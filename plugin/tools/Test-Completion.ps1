param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$PlaywrightModule,
    [Parameter(Mandatory)][string]$AxeFile,
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version = '0.1.0.0',
    [switch]$RetainedFixture
)
$ErrorActionPreference = 'Stop'
# A distinct owned fixture; never mutate the root-path fixture or production.
$name = 'mediahub-test-completion'; $port = 18396
$root = Join-Path $env:LOCALAPPDATA 'Temp/opencode'
$stateFile = Join-Path $root ($name + '.json')
$harness = Join-Path $PSScriptRoot 'Test-Jellyfin.ps1'
if (!(Test-Path $Package -PathType Leaf) -or !(Test-Path $AxeFile -PathType Leaf)) { throw 'Exact package and local test-only axe-core source are required.' }
$digest = (Get-FileHash $Package -Algorithm SHA256).Hash
$argsFixture = @{ Name=$name; Port=$port; PackageVersion=$Version }
if ($RetainedFixture) {
    $labels = docker inspect --format '{{json .Config.Labels}}' $name | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $labels.'org.mediahub.acceptance' -ne $name -or !(Test-Path $stateFile)) { throw 'No matching owned retained completion fixture.' }
    $state = Get-Content $stateFile -Raw | ConvertFrom-Json
    if ($state.Name -ne $name -or $state.Port -ne $port -or $state.BasePath -ne '/mediahub') { throw 'Retained fixture identity/base path mismatch.' }
} elseif (Test-Path $stateFile) { throw 'Fresh completion smoke refuses retained state. Nothing was changed.' }
try {
    if ($RetainedFixture) {
        $null = docker start $name
        if ($LASTEXITCODE -ne 0) { throw 'Owned completion fixture could not start.' }
        & $harness @argsFixture -Mode Check
        & $harness @argsFixture -Mode Uninstall
    } else { & $harness @argsFixture -Mode Start -BasePath '/mediahub' }
    & $harness @argsFixture -Mode Install -Package $Package
    & (Join-Path $PSScriptRoot 'Test-WebDependencies.ps1') -Name $name -Port $port
    & $harness @argsFixture -Mode Restart
    & node (Join-Path $PSScriptRoot 'Test-NativeUi.mjs') $stateFile $PlaywrightModule (Join-Path $root 'mediahub-completion-native.png') $AxeFile
    if ($LASTEXITCODE -ne 0) { throw 'Non-root/deep-link/accessibility browser acceptance failed. Private call logs suppressed.' }
    & $harness @argsFixture -Mode Uninstall
    & $harness @argsFixture -Mode Install -Package $Package
    & $harness @argsFixture -Mode Check
    if ((Get-FileHash $Package -Algorithm SHA256).Hash -ne $digest) { throw 'The exact ZIP changed during acceptance.' }
    Write-Output ('Completion fixture exact-ZIP gate passed. SHA256=' + $digest.ToLowerInvariant())
} finally {
    $labels = docker inspect --format '{{json .Config.Labels}}' $name 2>$null
    if ($LASTEXITCODE -eq 0 -and ($labels | ConvertFrom-Json).'org.mediahub.acceptance' -eq $name) { $null = docker stop --time 20 $name }
}
