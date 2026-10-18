param([Parameter(Mandatory)][string]$ProviderZip, [Parameter(Mandatory)][string]$PlaywrightModule)
$ErrorActionPreference='Stop'
$name='mediahub-test-visibility'; $port=18496
$root=Join-Path $env:LOCALAPPDATA 'Temp/opencode'; $stateFile=Join-Path $root ($name+'.json')
$dockerExe=(Get-Command docker -CommandType Application -ErrorAction Stop|Select-Object -First 1).Source
function Docker { $result=& $dockerExe @args; if($LASTEXITCODE -ne 0){throw 'Owned provider acceptance Docker operation failed.'}; return $result }
function Own { $labels=Docker inspect --format '{{json .Config.Labels}}' $name|ConvertFrom-Json;if($labels.'org.mediahub.acceptance' -ne $name){throw 'Owned provider fixture label mismatch.'} }
function Ready { $deadline=[DateTime]::UtcNow.AddSeconds(90);while([DateTime]::UtcNow -lt $deadline){try{if((Invoke-RestMethod -Uri ('http://127.0.0.1:'+$port+'/health') -TimeoutSec 2) -eq 'Healthy'){return}}catch{};$null=[Threading.Tasks.Task]::Delay(1000).GetAwaiter().GetResult()};throw 'Owned provider fixture did not become ready.' }
Own
$state=Get-Content $stateFile -Raw|ConvertFrom-Json
if($state.Name -ne $name -or $state.Port -ne $port -or !$state.LibraryAcceptance.IntentionalFixtureSetup){throw 'Use only the generated-media fixture.'}
if((Docker inspect --format '{{.State.Running}}' $name) -ne 'false'){throw 'The owned provider fixture must be stopped before this dedicated test.'}
if((Get-FileHash $ProviderZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'd467ba8f4122dcb015f80afac5c18bad02037200fcf727c3bff88d8ea0d2ea8a'){throw 'Provider release checksum mismatch.'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead((Resolve-Path $ProviderZip))
try{
    $allowed=@('Jellyfin.Plugin.PrivateSyncPlay.dll','Jellyfin.Plugin.PrivateSyncPlay.Api.dll','LICENSE','README.md','hls.js.LICENSE'); $seen=@{}
    foreach($entry in $archive.Entries){if($entry.FullName -cnotin $allowed -or $seen.ContainsKey($entry.FullName) -or $entry.Length -gt 2097152){throw 'Invalid provider archive member.'};$seen[$entry.FullName]=$true}
    if($seen.Count -ne $allowed.Count){throw 'Incomplete provider archive.'}
}finally{$archive.Dispose()}
$stage=Join-Path $root ('mediahub-provider-stage-'+[Guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path $ProviderZip),$stage)
if((Get-FileHash (Join-Path $stage 'Jellyfin.Plugin.PrivateSyncPlay.Api.dll') -Algorithm SHA256).Hash.ToLowerInvariant() -ne '0cf21a33c9d44fbb294503e52ad61479501811c2447d3e5dd925b49dd62ca136'){throw 'Provider public SDK mismatch.'}
$copy=Join-Path $root ('mediahub-provider-retained-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $copy
$target='/config/plugins/PrivateSyncPlay_0.1.5.0'
try {
    Own; $null=Docker start $name; Ready
    $headers=@{Authorization=('MediaBrowser Client="Media Hub acceptance", Device="Disposable test", DeviceId="mediahub-acceptance-admin", Version="0.1", Token="'+$state.AdminToken+'"')}
    try{$response=Invoke-WebRequest -UseBasicParsing -Uri ('http://127.0.0.1:'+$port+'/Plugins') -Headers $headers -TimeoutSec 15;$plugins=$response.Content|ConvertFrom-Json}
    catch{throw 'Owned provider inventory could not be verified. Private response withheld.'}
    $existing=@($plugins|Where-Object {$_.Name -match 'Private.*SyncPlay'})
} finally {Own; $null=Docker stop --time 20 $name}
if($existing.Count){
    if($existing.Count -ne 1 -or $existing[0].Version -ne '0.1.5.0' -or $existing[0].Status -ne 'Active'){throw 'Retained provider differs; no replacement was inferred.'}
    $null=Docker cp ($name+':'+$target+'/Jellyfin.Plugin.PrivateSyncPlay.dll') $copy
    if((Get-FileHash (Join-Path $copy 'Jellyfin.Plugin.PrivateSyncPlay.dll') -Algorithm SHA256).Hash -ne (Get-FileHash (Join-Path $stage 'Jellyfin.Plugin.PrivateSyncPlay.dll') -Algorithm SHA256).Hash){throw 'Retained provider differs from reviewed bytes; no replacement was inferred.'}
}else{Own; $null=Docker cp $stage ($name+':'+$target)}
try{
    Own; $null=Docker start $name
    Ready
    & node (Join-Path $PSScriptRoot 'Test-ProviderClients.mjs') $stateFile $PlaywrightModule
    if($LASTEXITCODE -ne 0){throw 'Real provider-client acceptance did not pass; no uncertain operation was retried.'}
}finally{Own; $null=Docker stop --time 20 $name}
