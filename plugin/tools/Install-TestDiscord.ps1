param(
    [Parameter(Mandatory)][ValidatePattern('^mediahub-test-[a-z0-9-]{1,35}$')][string]$Name,
    [Parameter(Mandatory)][ValidateRange(1024,65535)][int]$Port,
    [Parameter(Mandatory)][string]$DiscordPackage,
    [Parameter(Mandatory)][string]$ProviderZip,
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version = '0.1.0.0'
)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'Temp/opencode'; $stateFile = Join-Path $root ($Name + '.json')
$state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
if ($state.Name -ne $Name -or $state.Port -ne $Port -or $state.Version -ne '12.2.0') { throw 'Discord acceptance fixture identity mismatch. Nothing installed.' }
if ((Get-FileHash -LiteralPath $ProviderZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'd467ba8f4122dcb015f80afac5c18bad02037200fcf727c3bff88d8ea0d2ea8a') { throw 'Unreviewed Private SyncPlay provider; nothing installed.' }
$docker = Join-Path $PSScriptRoot 'Invoke-TestDocker.ps1'
function Own {
    $owned = @(& $docker -Arguments @('ps','-a','--filter',('name=^/' + $Name + '$'),'--filter',('label=org.mediahub.acceptance=' + $Name),'--format','{{.Names}}') -TimeoutSeconds 10)
    if ($owned.Count -ne 1 -or $owned[0] -cne $Name) { throw 'Exact test fixture ownership could not be verified.' }
}
function Docker([string[]]$Arguments) { Own; return & $docker -Arguments $Arguments -TimeoutSeconds 60 }
Own
Add-Type -AssemblyName System.IO.Compression.FileSystem
$adapterFiles = @('Jellyfin.Plugin.MediaHub.Discord.dll','Jellyfin.Plugin.MediaHub.Discord.deps.json','Jellyfin.Plugin.WatchlistDeletion.Api.dll','Discord.Net.Core.dll','Discord.Net.Dave.dll','Discord.Net.Rest.dll','Discord.Net.WebSocket.dll','Newtonsoft.Json.dll','Jellyfin.Plugin.PrivateSyncPlay.Api.dll','LICENSE','THIRD_PARTY_NOTICES.md')
$providerFiles = @('Jellyfin.Plugin.PrivateSyncPlay.dll','Jellyfin.Plugin.PrivateSyncPlay.Api.dll','LICENSE','README.md','hls.js.LICENSE')
$plans = @()
foreach ($selection in @(@{ Path=$DiscordPackage; Files=$adapterFiles; Directory=('MediaHub.Discord_' + $Version); Assembly='Jellyfin.Plugin.MediaHub.Discord.dll'; Version=$Version },
    @{ Path=$ProviderZip; Files=$providerFiles; Directory='PrivateSyncPlay_0.1.5.0'; Assembly='Jellyfin.Plugin.PrivateSyncPlay.dll'; Version='0.1.5.0' })) {
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $selection.Path))
    try {
        $seen = @{}; $size = 0L
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -cnotin $selection.Files -or $seen.ContainsKey($entry.FullName) -or $entry.Length -gt 33554432) { throw 'Unexpected, duplicate or oversized test dependency member.' }
            $seen[$entry.FullName] = $true; $size += $entry.Length
        }
        if ($seen.Count -ne $selection.Files.Count -or $size -gt 67108864) { throw 'Incomplete or oversized test dependency package.' }
    } finally { $zip.Dispose() }
    $stage = Join-Path $root ('mediahub-discord-dependency-' + [Guid]::NewGuid().ToString('N'))
    [IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $selection.Path), $stage)
    if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $stage $selection.Assembly)).Version.ToString() -ne $selection.Version) { throw 'Test dependency version differs from its selected package.' }
    if ((Get-FileHash (Join-Path $stage 'Jellyfin.Plugin.PrivateSyncPlay.Api.dll') -Algorithm SHA256).Hash.ToLowerInvariant() -ne '0cf21a33c9d44fbb294503e52ad61479501811c2447d3e5dd925b49dd62ca136') { throw 'Test dependency SDK differs from reviewed bytes.' }
    $existing = @(Docker @('exec',$Name,'find','/config/plugins','-maxdepth','1','-name',$selection.Directory))
    if ($existing.Count) { throw 'Test dependency directory already exists. No replacement or uncertain copy retry was inferred.' }
    $plans += @{ Stage=$stage; Target=('/config/plugins/' + $selection.Directory) }
}
$null = Docker @('stop','--time','20',$Name)
# A failed/uncertain copy leaves only this test container stopped for inspection;
# never automatically boot a partial plugin set or delete it to retry.
foreach ($plan in $plans) { $null = Docker @('cp',$plan.Stage,($Name + ':' + $plan.Target)) }
$null = Docker @('start',$Name)
Write-Output 'Separate Discord adapter and checksum-pinned provider installed only into the owned disposable fixture. No Discord credentials configured.'
