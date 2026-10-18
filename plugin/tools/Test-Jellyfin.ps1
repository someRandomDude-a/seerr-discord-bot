param(
    [ValidateSet('Start', 'Configure', 'Install', 'Check', 'Restart', 'Uninstall', 'Stop')][string]$Mode = 'Check',
    [ValidatePattern('^mediahub-test-[a-z0-9-]{1,35}$')][string]$Name = 'mediahub-test-brave-otter',
    [ValidateRange(1024, 65535)][int]$Port = 18096,
    [string]$Package,
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$PackageVersion = '0.1.0.0',
    [ValidatePattern('^(|/[a-z0-9-]{1,32})$')][string]$BasePath = '',
    [string]$Image = 'jellyfin/jellyfin@sha256:357724bf0ae27a672c7cbaa899db2d9abeb13dbd8657ccce750258a4c059d037'
)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'Temp/opencode'
$stateFile = Join-Path $root ($Name + '.json')
$url = 'http://127.0.0.1:' + $Port
$dockerExe = (Get-Command docker -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
function Docker {
    $result = & $dockerExe @args
    if ($LASTEXITCODE -ne 0) { throw ('Docker acceptance operation failed: ' + $args[0]) }
    return $result
}
function OwnContainer {
    $labels = (Docker @('inspect', '--format', '{{json .Config.Labels}}', $Name)) | ConvertFrom-Json
    $owner = $labels.'org.mediahub.acceptance'
    if ($owner -ne $Name) { throw 'Refusing to touch a container not owned by this acceptance test.' }
}
function Secret {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes)
}
function Header($token, $device) {
    $authorization = 'MediaBrowser Client="Media Hub acceptance", Device="Disposable test", DeviceId="' + $device + '", Version="0.1"'
    if ($token) { $authorization += ', Token="' + $token + '"' }
    $h = @{ Authorization = $authorization }
    return $h
}
function Api($path, $body = $null, $token = $null, $device = 'mediahub-acceptance-admin', $timeout = 15, $method = $null) {
    $arguments = @{ Uri = ($url + '/' + $path); Headers = (Header $token $device); TimeoutSec = $timeout }
    if ($null -ne $body) { $arguments['Method'] = 'POST'; $arguments['ContentType'] = 'application/json'; $arguments['Body'] = ($body | ConvertTo-Json -Depth 20 -Compress) }
    if ($method) { $arguments['Method'] = $method }
    try { $result = Invoke-RestMethod @arguments; return $result }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 'unreachable' }
        throw ('Disposable Jellyfin API failed: ' + $path + ' (' + $status + '). Credentials and response content withheld.')
    }
}
function Ready {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $info = Api 'System/Info/Public' $null $null 'mediahub-acceptance-admin' 2
            # Jellyfin 12 exposes public info on a temporary setup listener before
            # the real application takes over the port. Do not mistake that for ready.
            $health = Api 'health' $null $null 'mediahub-acceptance-admin' 2
            if ($info.Version -and $health -eq 'Healthy') { return $info }
        } catch { }
        [Threading.Tasks.Task]::Delay(1000).GetAwaiter().GetResult()
    }
    throw 'The disposable Jellyfin did not become ready within 60 seconds.'
}
function Expect-Status($path, $expected, $token = $null, $device = 'mediahub-acceptance-viewer') {
    try { $result = Invoke-WebRequest -UseBasicParsing -Uri ($url + '/' + $path) -Headers (Header $token $device) -TimeoutSec 15; $actual = [int]$result.StatusCode }
    catch { $actual = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 } }
    if ($actual -ne $expected) { throw ('Unexpected access result for ' + $path + ': ' + $actual + ', expected ' + $expected + '. Response content withheld.') }
}
function Reject-MalformedJson($state) {
    $marker = 'mediahub-private-binding-marker'
    $cases = @('{"Revision":0,"revision":0,"Preferences":{}}', '{', ('{"Revision":"' + $marker + '","Preferences":{}}'), '{"Revision":0,"Preferences":null}', ('{"x":' + ('[' * 40) + '0' + (']' * 40) + '}'), ('{"x":"' + ('a' * 65537) + '"}'))
    foreach ($body in $cases) {
        $actual = 0; $content = ''
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Method POST -Uri ($url + '/MediaHub/Me') -Headers (Header $state.ViewerToken 'mediahub-acceptance-viewer') -ContentType 'application/json' -Body $body -TimeoutSec 15
            $actual = [int]$response.StatusCode; $content = $response.Content
        } catch {
            if ($_.Exception.Response) { $actual = [int]$_.Exception.Response.StatusCode }
            $content = $_.ErrorDetails.Message
        }
        if ($actual -notin @(400, 413) -or ($content -and $content.Contains($marker))) { throw 'Malformed plugin JSON was not rejected safely. Request and response content withheld.' }
    }
    $null = Ready # The host remains usable after all rejected plugin requests.
    return $cases.Count
}
function Save($state) {
    # Credentials belong only to this disposable server and stay outside the repository/log output.
    [IO.File]::WriteAllText($stateFile, ($state | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
}
function Snapshot($state) {
    $users = @(Api 'Users' $null $state.AdminToken | Sort-Object Id | ForEach-Object { [ordered]@{ Id = $_.Id; Name = $_.Name; Administrator = $_.Policy.IsAdministrator; Disabled = $_.Policy.IsDisabled } })
    $folders = @(Api 'Library/VirtualFolders' $null $state.AdminToken)
    $config = Api 'System/Configuration' $null $state.AdminToken
    $snapshot = [ordered]@{ Users = $users; Folders = $folders; ServerName = $config.ServerName; EnableUPnP = $config.EnableUPnP; MetadataPath = $config.MetadataPath }
    if ($state.PSObject.Properties['BasePath']) { $snapshot.NetworkBasePath = (Api 'System/Configuration/network' $null $state.AdminToken).BaseUrl }
    return $snapshot
}
function Normalize-Snapshot($snapshot) {
    # Early Windows PowerShell runs wrapped REST arrays. Preserve and normalize the
    # recorded values, never replace them with a new baseline from the live host.
    if ($snapshot.Users.Count -eq 1 -and $snapshot.Users[0].Id -is [array]) {
        $old = $snapshot.Users[0]
        if (@($old.Name).Count -ne $old.Id.Count -or @($old.Administrator).Count -ne $old.Id.Count -or @($old.Disabled).Count -ne $old.Id.Count) { throw 'Unrecognized old snapshot; no baseline was replaced.' }
        $snapshot.Users = @(for ($i = 0; $i -lt $old.Id.Count; $i++) { [pscustomobject][ordered]@{ Id = $old.Id[$i]; Name = $old.Name[$i]; Administrator = $old.Administrator[$i]; Disabled = $old.Disabled[$i] } })
    }
    $snapshot.Users = @($snapshot.Users | Sort-Object Id)
    if ($snapshot.Folders.Count -eq 1 -and $snapshot.Folders[0].PSObject.Properties['value']) { $snapshot.Folders = @($snapshot.Folders[0].value) }
    return $snapshot
}
function Plugin-StateDigest {
    # Do not hash a live WAL-backed database; check the stable settings/key and reinstallation state separately.
    return (Docker @('exec', $Name, 'find', '/config/data/media-hub', '-maxdepth', '1', '-type', 'f', '(', '-name', 'secrets.key', '-o', '-name', 'settings.json', '-o', '-name', 'state-auth.json', '-o', '-name', 'state-auth.previous.json', ')', '-exec', 'sha256sum', '{}', '+') | Sort-Object) -join '|'
}
function Configure-Fixture($info) {
    if ($info.Version -ne '12.2.0') { throw 'This test image is not the latest reviewed Jellyfin 12.2.0. No plugin was installed.' }
    if (Test-Path $stateFile) { $state = Get-Content $stateFile -Raw | ConvertFrom-Json }
    else {
        if ($info.StartupWizardCompleted) { throw 'A configured test server has no matching private credentials. Nothing was reset.' }
        $state = [pscustomobject]@{ Name = $Name; Port = $Port; BasePath = $BasePath; Version = $info.Version; AdminPassword = (Secret); ViewerPassword = (Secret); AdminToken = ''; ViewerToken = ''; Snapshot = $null }
        Save $state
    }
    if ($state.Name -ne $Name -or $state.Port -ne $Port) { throw 'Private test state belongs to another container/address.' }
    if (!$info.StartupWizardCompleted) {
        $null = Api 'Startup/Configuration' @{ UICulture = 'en-US'; MetadataCountryCode = 'US'; PreferredMetadataLanguage = 'en' }
        $null = Api 'Startup/User'
        $null = Api 'Startup/User' @{ Name = 'mediahub-admin'; Password = $state.AdminPassword }
        $null = Api 'Startup/RemoteAccess' @{ EnableRemoteAccess = $false; EnableAutomaticPortMapping = $false }
        $null = Api 'Startup/Complete' @{}
    }
    $auth = Api 'Users/AuthenticateByName' @{ Username = 'mediahub-admin'; Pw = $state.AdminPassword }
    $state.AdminToken = $auth.AccessToken
    $users = @(Api 'Users' $null $state.AdminToken)
    $viewer = @($users | Where-Object { $_.Name -eq 'mediahub-viewer' }) | Select-Object -First 1
    if (!$viewer) {
        $viewer = Api 'Users/New' @{ Name = 'mediahub-viewer' } $state.AdminToken
        $null = Api ('Users/' + $viewer.Id + '/Password') @{ NewPw = $state.ViewerPassword } $state.AdminToken
    }
    $auth = Api 'Users/AuthenticateByName' @{ Username = 'mediahub-viewer'; Pw = $state.ViewerPassword } $null 'mediahub-acceptance-viewer'
    $state.ViewerToken = $auth.AccessToken
    if (!$state.Snapshot -and $state.BasePath) {
        # Fresh disposable fixture only, before its native baseline. Never change a
        # retained/production host's network configuration to make a test pass.
        $networkConfig = Api 'System/Configuration/network' $null $state.AdminToken
        if ($networkConfig.BaseUrl) { throw 'Unexpected pre-existing base path; nothing was replaced.' }
        $networkConfig.BaseUrl = $state.BasePath
        $null = Api 'System/Configuration/network' $networkConfig $state.AdminToken
        $script:url = 'http://127.0.0.1:' + $Port + $state.BasePath
        $null = Docker @('restart', '--time', '20', $Name)
        $null = Ready
    }
    if (!$state.Snapshot) { $state.Snapshot = Snapshot $state } else { $state.Snapshot = Normalize-Snapshot $state.Snapshot }
    Save $state
    [pscustomobject]@{ Step = 'Test fixture configured'; Version = $state.Version; Address = $url; MediaFolders = @($state.Snapshot.Folders).Count; CredentialsLogged = $false } | ConvertTo-Json
}
if ($Mode -eq 'Start') {
    if (Test-Path $stateFile) { throw 'Acceptance state already exists. Use another test name; retained state was not overwritten.' }
    if (@(Docker @('ps', '-a', '--format', '{{.Names}}')) -contains $Name) { throw 'Container name already exists. Nothing was changed.' }
    foreach ($volume in @($Name + '-config', $Name + '-cache')) {
        if (@(Docker @('volume', 'ls', '--format', '{{.Name}}')) -contains $volume) { throw 'Test volume name already exists. Nothing was removed.' }
    }
    $network = $Name + '-network'
    if (@(Docker @('network', 'ls', '--format', '{{.Name}}')) -contains $network) { throw 'Test network name already exists. Nothing was changed.' }
    $null = Docker @('network', 'create', '--label', ('org.mediahub.acceptance=' + $Name), $network)
    $null = Docker @('volume', 'create', '--label', ('org.mediahub.acceptance=' + $Name), ($Name + '-config'))
    $null = Docker @('volume', 'create', '--label', ('org.mediahub.acceptance=' + $Name), ($Name + '-cache'))
    $null = Docker @('run', '-d', '--name', $Name, '--label', ('org.mediahub.acceptance=' + $Name), '--network', $network,
        '--publish', ('127.0.0.1:' + $Port + ':8096'), '--mount', ('type=volume,source=' + $Name + '-config,target=/config'), '--mount', ('type=volume,source=' + $Name + '-cache,target=/cache'),
        '--read-only', '--tmpfs', '/tmp:rw,size=268435456', '--memory', '2g', '--cpus', '2', '--pids-limit', '512', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', $Image)
    Configure-Fixture (Ready)
    exit
}
OwnContainer
if (!(Test-Path $stateFile)) { throw 'This test server has no matching private acceptance state file.' }
$state = Get-Content $stateFile -Raw | ConvertFrom-Json
if ($state.Name -ne $Name -or $state.Port -ne $Port) { throw 'Test state does not match the selected container/address.' }
if ($state.BasePath -and $state.BasePath -notmatch '^/[a-z0-9-]{1,32}$') { throw 'Unrecognized fixture base path.' }
if ($BasePath -and $BasePath -ne $state.BasePath) { throw 'Refusing to change a retained fixture base path.' }
$url += $state.BasePath
if ($Mode -eq 'Configure') { Configure-Fixture (Ready); exit }
$state.Snapshot = Normalize-Snapshot $state.Snapshot
if ($Mode -eq 'Stop') { $null = Docker @('stop', '--time', '20', $Name); Write-Output 'Only the owned test container was stopped. Volumes and private test credentials were retained.'; exit }
if ($Mode -eq 'Install') {
    if (!$Package -or !(Test-Path $Package)) { throw 'Specify a generated local preview ZIP.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Package))
    try {
        $allowed = @('Jellyfin.Plugin.MediaHub.dll', 'Jellyfin.Plugin.MediaHub.deps.json', 'Discord.Net.Core.dll', 'Discord.Net.Dave.dll', 'Discord.Net.Rest.dll', 'Discord.Net.WebSocket.dll', 'Newtonsoft.Json.dll', 'Jellyfin.Plugin.PrivateSyncPlay.Api.dll', 'LICENSE', 'THIRD_PARTY_NOTICES.md')
        $seen = @{}; $expanded = 0L
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -cnotin $allowed -or $seen.ContainsKey($entry.FullName) -or $entry.Length -gt 33554432) { throw 'Unexpected, duplicate or oversized archive entry.' }
            $seen[$entry.FullName] = $true; $expanded += $entry.Length
            if ($expanded -gt 67108864) { throw 'Test package exceeds its expansion budget.' }
        }
        if ($seen.Count -ne $allowed.Count) { throw 'Test package is missing an allowlisted runtime or license file.' }
    } finally { $zip.Dispose() }
    $stage = Join-Path $root ($Name + '-package-' + [Guid]::NewGuid().ToString('N'))
    [IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path $Package), $stage)
    foreach ($hostAssembly in @('Jellyfin.Controller.dll', 'Jellyfin.Model.dll', 'MediaBrowser.Controller.dll', 'MediaBrowser.Model.dll', 'Microsoft.Data.Sqlite.dll')) {
        if (Test-Path (Join-Path $stage $hostAssembly)) { throw 'Host replacement assemblies are not permitted in the test package.' }
    }
    $directories = @(Docker @('exec', $Name, 'find', '/config/plugins', '-maxdepth', '1', '-name', ('MediaHub_' + $PackageVersion)))
    if ($directories.Count -ne 0) { throw 'Test plugin package already exists. Uninstall the owned test package before installing a different local build.' }
    $null = Docker @('exec', $Name, 'mkdir', '-p', '/config/plugins')
    $null = Docker @('stop', '--time', '20', $Name)
    $null = Docker @('cp', $stage, ($Name + ':/config/plugins/MediaHub_' + $PackageVersion))
    $null = Docker @('start', $Name)
    $null = Ready
}
if ($Mode -eq 'Uninstall') {
    $null = Ready
    $retained = Plugin-StateDigest
    $personal = Api 'MediaHub/Me' $null $state.ViewerToken 'mediahub-acceptance-viewer'
    $state | Add-Member -NotePropertyName RetainedPersonal -NotePropertyValue $personal -Force
    Save $state
    # Exercise Jellyfin's actual uninstall hook. Only the GUID/version in this owned fixture is selected.
    $null = Api ('Plugins/c70297bf-77bd-4ad4-93c9-f67598519777/' + $PackageVersion) $null $state.AdminToken 'mediahub-acceptance-admin' 15 'DELETE'
    $null = Docker @('restart', '--time', '20', $Name)
    $null = Ready
    $after = Snapshot $state
    $afterRetained = Plugin-StateDigest
    if ($retained -ne $afterRetained) { throw 'Plugin settings/key were not retained unchanged during uninstall.' }
    $null = Docker @('exec', $Name, 'test', '-f', '/config/data/media-hub/hub.db')
    Expect-Status 'MediaHub/Me' 404 $state.ViewerToken
    if (($after | ConvertTo-Json -Depth 20 -Compress) -ne ($state.Snapshot | ConvertTo-Json -Depth 20 -Compress)) { throw 'Native users/library/configuration changed during the test.' }
    [pscustomobject]@{ Step = 'Uninstall/restart'; NativeFixtureUnchanged = $true; PluginStateRetained = $true } | ConvertTo-Json
    exit
}
$info = Ready
$plugins = @(Api 'Plugins' $null $state.AdminToken)
$plugin = @($plugins | Where-Object { [Guid]$_.Id -eq [Guid]'c70297bf-77bd-4ad4-93c9-f67598519777' })
if ($plugin.Count -ne 1) { throw 'The test server did not discover exactly one Media Hub plugin.' }
if ($plugin[0].Status -ne 'Active') { throw 'Jellyfin did not report the test plugin as Active.' }
if ($plugin[0].Version -ne $PackageVersion) { throw 'Loaded plugin version differs from the selected release.' }
if ($Mode -eq 'Install') {
    $expected = (Get-FileHash -Algorithm SHA256 (Join-Path $stage 'Jellyfin.Plugin.MediaHub.dll')).Hash.ToLowerInvariant()
    $installed = (Docker @('exec', $Name, 'sha256sum', ('/config/plugins/MediaHub_' + $PackageVersion + '/Jellyfin.Plugin.MediaHub.dll'))) -split '\s+'
    if ($installed[0] -ne $expected) { throw 'Installed assembly differs from the exact ZIP.' }
}
$settings = Api 'MediaHub/Settings' $null $state.AdminToken
if ($settings.ConfigurationReviewRequired) { Expect-Status 'MediaHub/Me' 400 $state.ViewerToken }
if ($settings.DiscordEnabled -or $settings.AutomaticDeletion) { throw 'Fresh install must not enable Discord or automatic deletion.' }
if (!$settings.PublicJellyfinUrl -or $settings.ConfigurationReviewRequired) {
    if ($settings.HasDiscordToken -or $settings.HasSeerrKey -or $settings.SeerrUrl -or $settings.Features.Lidarr.HasKey -or $settings.Features.Readarr.HasKey -or $settings.Features.HasDiscordClientSecret -or
        ($settings.PublicJellyfinUrl -and $settings.PublicJellyfinUrl -ne 'https://mediahub.invalid')) { throw 'This fixture has retained integration settings requiring manual review. No settings were approved or replaced.' }
    # Exercise only this plugin's own settings persistence, with a non-routable test URL.
    $settings = Api 'MediaHub/Settings' @{ Revision = $settings.Revision; DiscordEnabled = $false; AutomaticDeletion = $false;
        PublicJellyfinUrl = 'https://mediahub.invalid'; AllowedGuilds = @(); SeerrUrl = ''; DiscordToken = $null; SeerrKey = $null } $state.AdminToken
}
$before = Api 'MediaHub/Me' $null $state.ViewerToken 'mediahub-acceptance-viewer'
$rejected = Reject-MalformedJson $state
$afterMalformed = Api 'MediaHub/Me' $null $state.ViewerToken 'mediahub-acceptance-viewer'
if (($before | ConvertTo-Json -Depth 20 -Compress) -ne ($afterMalformed | ConvertTo-Json -Depth 20 -Compress)) { throw 'Rejected malformed input changed personal state.' }
if ($Mode -eq 'Install' -and $state.PSObject.Properties['RetainedPersonal']) {
    if ($before.Revision -ne $state.RetainedPersonal.Revision -or $before.Preferences.Muted -ne $state.RetainedPersonal.Preferences.Muted) { throw 'Plugin preferences changed during uninstall/reinstall.' }
}
$saved = Api 'MediaHub/Me' @{ Revision = $before.Revision; Preferences = @{ JellyfinNotifications = $false; DiscordNotifications = $false; Muted = $true } } $state.ViewerToken 'mediahub-acceptance-viewer'
if ($Mode -eq 'Restart') {
    $null = Docker @('restart', '--time', '20', $Name)
    $null = Ready
    $persisted = Api 'MediaHub/Me' $null $state.ViewerToken 'mediahub-acceptance-viewer'
    if ($persisted.Revision -ne $saved.Revision -or !$persisted.Preferences.Muted) { throw 'Personal preferences did not persist across a real host restart.' }
    $persistedSettings = Api 'MediaHub/Settings' $null $state.AdminToken
    if ($persistedSettings.Revision -ne $settings.Revision -or $persistedSettings.PublicJellyfinUrl -ne $settings.PublicJellyfinUrl -or $persistedSettings.DiscordEnabled -or $persistedSettings.AutomaticDeletion) { throw 'Plugin settings did not persist unchanged across a real host restart.' }
}
$admin = Api 'MediaHub/Me' $null $state.AdminToken
if ($admin.Preferences.Muted -or !$saved.Preferences.Muted) { throw 'Per-user preference isolation failed.' }
Expect-Status 'MediaHub/Me' 401
Expect-Status 'MediaHub/Settings' 403 $state.ViewerToken
Expect-Status 'MediaHub/Messaging/Access' 403 $state.ViewerToken
Expect-Status 'MediaHub/Activity/Config' 401
Expect-Status 'MediaHub/Activity/Api/Me' 401
Expect-Status 'MediaHub/Players' 401
Expect-Status 'MediaHub/Players/History' 200 $state.ViewerToken 'mediahub-acceptance-viewer'
Expect-Status 'web/ConfigurationPage?name=mediahub' 200 $state.AdminToken 'mediahub-acceptance-admin'
Expect-Status 'web/ConfigurationPage?name=mediahub.js' 200 $state.AdminToken 'mediahub-acceptance-admin'
Expect-Status 'web/ConfigurationPage?name=mediahub.settings.js' 200 $state.AdminToken 'mediahub-acceptance-admin'
Expect-Status ('MediaHub/Configuration/Destinations?revision=' + $settings.Revision) 401
Expect-Status ('MediaHub/Configuration/Destinations?revision=' + $settings.Revision) 403 $state.ViewerToken
if (!$settings.SeerrUrl) { Expect-Status ('MediaHub/Configuration/Destinations?revision=' + $settings.Revision) 400 $state.AdminToken 'mediahub-acceptance-admin' }
$after = Snapshot $state
if (($after | ConvertTo-Json -Depth 20 -Compress) -ne ($state.Snapshot | ConvertTo-Json -Depth 20 -Compress)) { throw 'Native users/library/configuration changed during the test.' }
[pscustomobject]@{ Step = $Mode; Version = $info.Version; PluginDiscovered = $true; NativeFixtureUnchanged = $true; PersonalIsolation = $true; MalformedBodiesRejected = $rejected; DiscordDisabled = $true; AutomaticDeletionDisabled = $true } | ConvertTo-Json
