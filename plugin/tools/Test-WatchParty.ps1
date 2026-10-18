param(
    [ValidateSet('InstallAndCheck', 'Check')][string]$Mode = 'Check',
    [ValidatePattern('^mediahub-test-[a-z0-9-]{1,35}$')][string]$Name = 'mediahub-test-private-syncplay',
    [ValidateRange(1024, 65535)][int]$Port = 18196,
    [string]$ProviderZip
)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'Temp/opencode'
$stateFile = Join-Path $root ($Name + '.json')
$url = 'http://127.0.0.1:' + $Port
$dockerExe = (Get-Command docker -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
function Docker {
    $output = & $dockerExe @args
    if ($LASTEXITCODE -ne 0) { throw 'Owned watch-party fixture Docker operation failed.' }
    return $output
}
$labels = (Docker inspect --format '{{json .Config.Labels}}' $Name) | ConvertFrom-Json
if ($labels.'org.mediahub.acceptance' -ne $Name -or !(Test-Path $stateFile)) { throw 'Use only an existing labelled Media Hub acceptance fixture with matching private state.' }
$state = Get-Content $stateFile -Raw | ConvertFrom-Json
if ($state.Name -ne $Name -or $state.Port -ne $Port) { throw 'Fixture state/address mismatch.' }
function Api($path, $body = $null, $viewer = $false, $expected = 200) {
    $token = if ($viewer) { $state.ViewerToken } else { $state.AdminToken }
    $device = if ($viewer) { 'mediahub-acceptance-viewer' } else { 'mediahub-acceptance-admin' }
    $headers = @{ Authorization = ('MediaBrowser Client="Media Hub acceptance", Device="Disposable test", DeviceId="' + $device + '", Version="0.1", Token="' + $token + '"') }
    $arguments = @{ Uri = ($url + '/' + $path); Headers = $headers; TimeoutSec = 20; UseBasicParsing = $true }
    if ($null -ne $body) { $arguments.Method = 'POST'; $arguments.ContentType = 'application/json'; $arguments.Body = $body | ConvertTo-Json -Depth 12 -Compress }
    $status = 0; $content = ''
    try { $response = Invoke-WebRequest @arguments; $status = [int]$response.StatusCode; $content = $response.Content }
    catch { if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }; $content = $_.ErrorDetails.Message }
    if ($status -ne $expected) { throw ('Owned watch-party fixture route failed (' + $status + ', expected ' + $expected + '). Request/response and credentials withheld. Route: ' + $path) }
    if ($expected -eq 200) { return $content | ConvertFrom-Json }
}
function Ready {
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { if ((Invoke-RestMethod -Uri ($url + '/health') -TimeoutSec 2) -eq 'Healthy') { return } } catch { }
        $null = [Threading.Tasks.Task]::Delay(1000).GetAwaiter().GetResult()
    }
    throw 'Owned watch-party fixture did not become healthy.'
}
function Snapshot {
    $users = @(Api 'Users' | Sort-Object Id | ForEach-Object { [ordered]@{ Id = $_.Id; Name = $_.Name; Administrator = $_.Policy.IsAdministrator; Disabled = $_.Policy.IsDisabled } })
    $configuration = Api 'System/Configuration'
    return [ordered]@{ Users = $users; Folders = @(Api 'Library/VirtualFolders'); ServerName = $configuration.ServerName; EnableUPnP = $configuration.EnableUPnP; MetadataPath = $configuration.MetadataPath } | ConvertTo-Json -Depth 20 -Compress
}
Ready
if ((Api 'System/Info/Public').Version -ne '12.2.0') { throw 'Only latest reviewed Jellyfin 12.2.0 is supported.' }
$before = Snapshot
if ($Mode -eq 'InstallAndCheck') {
    $null = Api 'MediaHub/WatchParty' $null $false 503
    if (!$ProviderZip -or !(Test-Path $ProviderZip)) { throw 'Supply the reviewed PrivateSyncPlay-0.1.5.zip.' }
    if ((Get-FileHash $ProviderZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'd467ba8f4122dcb015f80afac5c18bad02037200fcf727c3bff88d8ea0d2ea8a') { throw 'Provider release checksum mismatch.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $ProviderZip))
    try {
        $allowed = @('Jellyfin.Plugin.PrivateSyncPlay.dll', 'Jellyfin.Plugin.PrivateSyncPlay.Api.dll', 'LICENSE', 'README.md', 'hls.js.LICENSE')
        if ($archive.Entries.Count -ne $allowed.Count) { throw 'Unexpected provider archive.' }
        $seen = @{}
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -cnotin $allowed -or $seen.ContainsKey($entry.FullName) -or $entry.Length -gt 2097152) { throw 'Invalid provider archive entry.' }
            $seen[$entry.FullName] = $true
        }
    } finally { $archive.Dispose() }
    $stage = Join-Path $root ($Name + '-provider-' + [Guid]::NewGuid().ToString('N'))
    [IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path $ProviderZip), $stage)
    $existing = @(Docker exec $Name find /config/plugins -maxdepth 2 -name Jellyfin.Plugin.PrivateSyncPlay.dll)
    if ($existing.Count -ne 0) { throw 'A provider already exists; no files were overwritten.' }
    $null = Docker stop --time 20 $Name
    $null = Docker cp $stage ($Name + ':/config/plugins/PrivateSyncPlay_0.1.5.0')
    $null = Docker start $Name
    Ready
}
$plugins = @(Api 'Plugins')
if (@($plugins | Where-Object { $_.Name -match 'Private.*SyncPlay' -and $_.Status -eq 'Active' }).Count -ne 1) { throw 'Exactly one active Private SyncPlay provider is required.' }
$initial = Api 'MediaHub/WatchParty'
if (!$initial.Available -or $initial.Active) { throw 'Use a fresh idle player; no existing party was changed.' }
$created = Api 'MediaHub/WatchParty' @{ Name = 'Owned integration fixture'; UserId = [Guid]::NewGuid().ToString(); SessionId = 'forged-session-ignored' }
if (!$created.Party.Owner -or !$created.Party.Active -or !$created.Invite.Url.StartsWith('https://mediahub.invalid/PrivateSyncPlay/Watch#')) { throw 'Creation projection or trusted invitation URL failed.' }
$party = $created.Party.PartyId
if ((Api 'MediaHub/WatchParty').PSObject.Properties['JoinCode']) { throw 'Private join code leaked from the adapter.' }
$null = Api 'MediaHub/WatchParty' @{ Name = 'Must not create twice' } $false 409
$other = Api 'MediaHub/WatchParty' $null $true
if ($other.Active) { throw 'A different native player inherited the owner party.' }
$null = Api ('MediaHub/WatchParty/' + $party + '/End') @{} $true 409
$link = Api ('MediaHub/WatchParty/' + $party + '/Invites') @{}
if (!$link.Url.StartsWith('https://mediahub.invalid/PrivateSyncPlay/Watch#')) { throw 'Invitation URL escaped the trusted public base.' }
$null = Api ('MediaHub/WatchParty/' + $party + '/Invites/' + $link.InviteId + '/Revoke') @{}
$null = Api ('MediaHub/WatchParty/' + $party + '/Invites/' + $created.Invite.InviteId + '/Revoke') @{}
$null = Api ('MediaHub/WatchParty/' + $party + '/End') @{}
if ((Api 'MediaHub/WatchParty').Active) { throw 'Ended party remains active.' }
$created = Api 'MediaHub/WatchParty' @{ Name = 'Owned leave fixture' }
$null = Api ('MediaHub/WatchParty/' + $created.Party.PartyId + '/Leave') @{}
if ((Api 'MediaHub/WatchParty').Active) { throw 'Player leave remains active.' }
$created = Api 'MediaHub/WatchParty' @{ Name = 'Owned restart fixture' }
$null = Docker restart --time 20 $Name
Ready
if ((Api 'MediaHub/WatchParty').Active) { throw 'Provider party unexpectedly survived restart.' }
if ((Snapshot) -ne $before) { throw 'Sampled native fixture changed.' }
[pscustomobject]@{ Jellyfin = '12.2.0'; PrivateSyncPlay = '0.1.5'; Api = '1.1.0'; SeparatePluginLoadContexts = $true; AtomicPrivateCreate = $true; ExactNativeSessionIsolation = $true; DuplicateCreateRejected = $true; RevokeEndLeave = $true; RestartInvalidatesParty = $true; SampledNativeFixtureUnchanged = $true; MediaPlaybackTested = $false; SecretsLogged = $false } | ConvertTo-Json
