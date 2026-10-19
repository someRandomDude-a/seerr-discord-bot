param(
    [Parameter(Mandatory)][string]$CorePackage,
    [Parameter(Mandatory)][string]$DiscordPackage,
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version = '0.1.0.0',
    [ValidatePattern('^mediahub-test-[a-z0-9-]{1,35}$')][string]$Name = 'mediahub-test-split',
    [ValidateRange(1024, 65535)][int]$Port = 18596,
    [ValidateRange(10, 300)][int]$StartupTimeoutSeconds = 180,
    [switch]$ResumeAfterCore
)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'Temp/opencode'
$stateFile = Join-Path $root ($name + '.json')
$harness = Join-Path $PSScriptRoot 'Test-Jellyfin.ps1'
$arguments = @{ Name = $name; Port = $port; PackageVersion = $Version; StartupTimeoutSeconds = $StartupTimeoutSeconds }
$dockerHelper = Join-Path $PSScriptRoot 'Invoke-TestDocker.ps1'
$existing = @(& $dockerHelper -Arguments @('ps', '-a', '--format', '{{.Names}}') -TimeoutSeconds 15)
if (!$ResumeAfterCore -and ((Test-Path $stateFile) -or $existing -contains $name)) { throw 'Split acceptance requires a fresh owned fixture; existing state was not changed.' }
if ($ResumeAfterCore) {
    if (!(Test-Path $stateFile) -or $existing -notcontains $name) { throw 'Resume requires the matching retained split fixture.' }
    $retained = Get-Content $stateFile -Raw | ConvertFrom-Json
    if ($retained.Name -ne $name -or $retained.Port -ne $port -or $retained.Version -ne '12.2.0' -or $retained.BasePath) { throw 'Retained split fixture identity differs. Nothing was reset.' }
}
$hashes = @((Get-FileHash $CorePackage -Algorithm SHA256).Hash, (Get-FileHash $DiscordPackage -Algorithm SHA256).Hash)
function Own {
    $owned = @(& $dockerHelper -Arguments @('ps', '-a', '--filter', ('name=^/' + $name + '$'), '--filter', ('label=org.mediahub.acceptance=' + $name), '--format', '{{.Names}}') -TimeoutSeconds 15)
    if ($owned.Count -ne 1 -or $owned[0] -cne $name) { throw 'Split fixture ownership could not be verified.' }
}
function Docker {
    Own
    $flat = @($args | ForEach-Object { foreach ($argument in $_) { [string]$argument } })
    return & $dockerHelper -Arguments $flat -TimeoutSeconds 90
}
function Api($path, $method = 'GET', $viewer = $false) {
    $state = Get-Content $stateFile -Raw | ConvertFrom-Json
    $token = if ($viewer) { $state.ViewerToken } else { $state.AdminToken }
    $device = if ($viewer) { 'mediahub-acceptance-viewer' } else { 'mediahub-acceptance-admin' }
    try { return Invoke-RestMethod -Uri ('http://127.0.0.1:' + $port + '/' + $path) -Method $method -TimeoutSec 15 -Headers @{ Authorization = ('MediaBrowser Client="Media Hub acceptance", Device="Disposable test", DeviceId="' + $device + '", Version="0.1", Token="' + $token + '"') } }
    catch { throw ('Split API verification failed for ' + $path + '. Credentials and response withheld.') }
}
function Ready {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $StartupTimeoutSeconds) {
        try {
            $info = Invoke-RestMethod -Uri ('http://127.0.0.1:' + $port + '/System/Info/Public') -TimeoutSec 2
            $health = Invoke-RestMethod -Uri ('http://127.0.0.1:' + $port + '/health') -TimeoutSec 2
            if ($info.Version -ceq '12.2.0' -and $health -is [string] -and $health.Trim() -ceq 'Healthy') { return }
        } catch { }
        [Threading.Tasks.Task]::Delay(1000).GetAwaiter().GetResult()
    }
    try {
        $diagnostic = & (Join-Path $PSScriptRoot 'Get-TestStartupDiagnostic.ps1') -Name $name -Phase install-restart
        $encoded = $diagnostic | ConvertTo-Json -Depth 4; Write-Host $encoded
        [IO.File]::WriteAllText((Join-Path $root ($name + '.startup-diagnostic.json')), $encoded, (New-Object Text.UTF8Encoding($false)))
    } catch { Write-Warning 'Sanitized split startup diagnostics could not be retained; private content withheld.' }
    throw ('The owned split fixture did not become healthy within ' + $StartupTimeoutSeconds + ' seconds.')
}
function Status($installed) {
    $status = Api 'MediaHub/Status' 'GET' $true
    if ($status.DiscordIntegration.Installed -ne $installed -or $status.DiscordIntegration.Ready -or $status.WatchParty.Available) { throw 'Split dependency status was not fail-closed without a Private SyncPlay provider.' }
    $null = Api 'MediaHub/Me' 'GET' $true
    $null = Api 'MediaHub/Deletions' 'GET' $true
    $monitor = Api 'MediaHub/Admin/Deletions'
    if ($monitor.Total -ne 0 -or $monitor.AutomaticDeletion) { throw 'Unexpected deletion state in the fresh fixture.' }
}
try {
    if ($ResumeAfterCore) { $null = Docker start $name; Ready }
    else {
        & $harness @arguments -Mode Start
        & $harness @arguments -Mode Install -Package $CorePackage
    }
    & $harness @arguments -Mode Restart
    Status $false
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $allowed = @('Jellyfin.Plugin.MediaHub.Discord.dll', 'Jellyfin.Plugin.MediaHub.Discord.deps.json', 'Jellyfin.Plugin.WatchlistDeletion.Api.dll', 'Discord.Net.Core.dll', 'Discord.Net.Dave.dll', 'Discord.Net.Rest.dll', 'Discord.Net.WebSocket.dll', 'Newtonsoft.Json.dll', 'Jellyfin.Plugin.PrivateSyncPlay.Api.dll', 'LICENSE', 'THIRD_PARTY_NOTICES.md')
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $DiscordPackage))
    try {
        $seen = @{}; $size = 0L
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -cnotin $allowed -or $seen.ContainsKey($entry.FullName) -or $entry.Length -gt 33554432) { throw 'Invalid adapter archive entry.' }
            $seen[$entry.FullName] = $true; $size += $entry.Length
        }
        if ($seen.Count -ne $allowed.Count -or $size -gt 67108864) { throw 'Incomplete or oversized adapter package.' }
    } finally { $zip.Dispose() }
    $stage = Join-Path $root ($name + '-discord-package-' + [Guid]::NewGuid().ToString('N'))
    [IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path $DiscordPackage), $stage)
    if ((Get-FileHash (Join-Path $stage 'Jellyfin.Plugin.PrivateSyncPlay.Api.dll') -Algorithm SHA256).Hash.ToLowerInvariant() -ne '0cf21a33c9d44fbb294503e52ad61479501811c2447d3e5dd925b49dd62ca136') { throw 'The retained Private SyncPlay SDK differs from reviewed 1.1.0 bytes.' }
    $null = Docker stop --time 20 $name
    $null = Docker cp $stage ($name + ':/config/plugins/MediaHub.Discord_' + $Version)
    $null = Docker start $name
    Ready
    Status $true
    $plugins = @(Api 'Plugins')
    foreach ($id in @('c70297bf-77bd-4ad4-93c9-f67598519777', '8456d36a-3c89-4b6d-9959-9a74566cc252')) {
        $plugin = @($plugins | Where-Object { [Guid]$_.Id -eq [Guid]$id })
        if ($plugin.Count -ne 1 -or $plugin[0].Status -ne 'Active') { throw 'Both separate plugins must be discovered and active.' }
    }
    & $harness @arguments -Mode Uninstall
    $remaining = @(Api 'Plugins' | Where-Object { [Guid]$_.Id -eq [Guid]'8456d36a-3c89-4b6d-9959-9a74566cc252' })
    if ($remaining.Count -ne 1 -or $remaining[0].Status -ne 'Active') { throw 'The integration could not remain safely installed without the core.' }
    & $harness @arguments -Mode Install -Package $CorePackage
    Status $true
    $null = Api ('Plugins/8456d36a-3c89-4b6d-9959-9a74566cc252/' + $Version) 'DELETE'
    $null = Docker restart --time 20 $name
    Ready
    Status $false
    if ((Get-FileHash $CorePackage -Algorithm SHA256).Hash -ne $hashes[0] -or (Get-FileHash $DiscordPackage -Algorithm SHA256).Hash -ne $hashes[1]) { throw 'Immutable input ZIP changed during acceptance.' }
    [pscustomobject]@{ Fixture = $name; StandaloneCore = $true; SeparatePluginsDiscovered = $true; MissingProviderFailsClosed = $true; CoreUninstallReinstallRetainsState = $true; AdapterNativeUninstallLeavesCore = $true; PrivateSyncPlaySdkRetained = $true; LiveDiscordTested = $false; ProviderAdmissionTested = $false; CredentialsLogged = $false } | ConvertTo-Json
} finally {
    try {
        $running = @(& $dockerHelper -Arguments @('ps', '--filter', ('name=^/' + $name + '$'), '--filter', ('label=org.mediahub.acceptance=' + $name), '--format', '{{.Names}}') -TimeoutSeconds 15)
        if ($running.Count -eq 1 -and $running[0] -ceq $name) { $null = Docker stop --time 20 $name }
    } catch { Write-Warning 'Owned split fixture stop was not confirmed. Retained state was not reset; inspect before any further mutation.' }
}
