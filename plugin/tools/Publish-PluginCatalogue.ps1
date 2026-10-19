param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$')][string]$Repository,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$Commit,
    [switch]$Publish,
    [scriptblock]$GitHubClient
)
$ErrorActionPreference = 'Stop'
if (!$Publish) { throw 'Publication requires explicit -Publish after all acceptance gates. No GitHub request was sent.' }
function GitHub([string[]]$Arguments) {
    if ($GitHubClient) { return & $GitHubClient -Arguments $Arguments }
    try { $result = @(& gh @Arguments 2>&1); $code = $LASTEXITCODE }
    catch { throw 'GitHub publication operation was not confirmed. Detailed service content withheld; no retry was sent.' }
    if ($code -ne 0) { throw 'GitHub publication operation was not confirmed. Detailed service content withheld; no retry was sent.' }
    return $result
}
function Same-File([string]$first, [string]$second) {
    return (Test-Path -LiteralPath $first -PathType Leaf) -and (Test-Path -LiteralPath $second -PathType Leaf) -and
        (Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash -ceq (Get-FileHash -LiteralPath $second -Algorithm SHA256).Hash
}
$directoryPath = (Resolve-Path -LiteralPath $Directory).Path
$manifest = Join-Path $directoryPath 'manifest.json'
$manifestSha = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
if ((Get-Item -LiteralPath $manifest).Length -gt 4194304) { throw 'Combined catalog exceeds its supported size.' }
$catalog = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($manifest))
$checksums = @([IO.File]::ReadAllLines((Join-Path $directoryPath 'SHA256SUMS')) | Where-Object { $_ })
if ($checksums.Count -ne 2) { throw 'The repository must provide checksums for both separate plugin packages.' }
$plans = @()
foreach ($kind in @('core','discord')) {
    $adapter = $kind -eq 'discord'
    $id = if ($adapter) { '8456d36a-3c89-4b6d-9959-9a74566cc252' } else { 'c70297bf-77bd-4ad4-93c9-f67598519777' }
    $assembly = if ($adapter) { 'Jellyfin.Plugin.MediaHub.Discord' } else { 'Jellyfin.Plugin.MediaHub' }
    $tag = if ($adapter) { 'discord-v' + $Version } else { 'plugin-v' + $Version }
    $name = $assembly + '_' + $Version + '.zip'; $archive = Join-Path $directoryPath $name
    $entry = @($catalog | Where-Object { $_.guid -eq $id })
    if ($entry.Count -ne 1) { throw 'Both plugin identities must appear exactly once in the combined catalog.' }
    $release = @($entry[0].versions | Where-Object { $_.version -ceq $Version })
    if ($release.Count -ne 1 -or $release[0].sourceUrl -cne ('https://github.com/' + $Repository + '/releases/download/' + $tag + '/' + $name) -or
        $release[0].targetAbi -ne '12.2.0' -or $release[0].checksum -cne (Get-FileHash -LiteralPath $archive -Algorithm MD5).Hash.ToLowerInvariant()) {
        throw 'Package identity, ABI, immutable URL or manifest checksum does not match the selected repository.'
    }
    $sha = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if (@($checksums | Where-Object { $_ -ceq ($sha + '  ' + $name) }).Count -ne 1) { throw 'Package SHA-256 checksum mismatch. Nothing was published.' }
    $plans += [pscustomobject]@{ Kind = $kind; Tag = $tag; Name = $name; Archive = $archive; Sha = $sha; Title = $entry[0].name }
}
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } elseif ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Temp/opencode' } else { [IO.Path]::GetTempPath() }
$stage = Join-Path $temporaryRoot ('mediahub-publish-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $stage) { throw 'Publication staging already exists; nothing was overwritten.' }
$null = New-Item -ItemType Directory -Path $stage
try {
    $tags = @(GitHub @('api', '--paginate', ('repos/' + $Repository + '/releases'), '--jq', '.[].tag_name'))
    foreach ($plan in $plans) {
        $local = Join-Path $stage $plan.Kind; $null = New-Item -ItemType Directory -Path $local
        Copy-Item -LiteralPath $manifest -Destination (Join-Path $local 'manifest.json')
        [IO.File]::WriteAllText((Join-Path $local 'SHA256SUMS'), ($plan.Sha + '  ' + $plan.Name + "`n"), (New-Object Text.UTF8Encoding($false)))
        $files = @($plan.Archive, (Join-Path $local 'manifest.json'), (Join-Path $local 'SHA256SUMS'))
        if ($tags -notcontains $plan.Tag) {
            # Drafts prevent a partial asset upload from becoming an installable release.
            $null = GitHub (@('release','create',$plan.Tag,'--repo',$Repository,'--target',$Commit,'--title',($plan.Title + ' ' + $Version),
                '--notes','Two separately installable preview plugins; use the combined Jellyfin catalog. Acceptance limitations are documented in the repository.', '--draft','--prerelease','--latest=false') + $files)
        }
        $metadata = ((GitHub @('release','view',$plan.Tag,'--repo',$Repository,'--json','tagName,isDraft,assets')) -join "`n") | ConvertFrom-Json
        if ($metadata.tagName -cne $plan.Tag -or $metadata.isDraft -isnot [bool] -or 'assets' -notin $metadata.PSObject.Properties.Name) { throw 'GitHub returned an unexpected or incomplete release identity.' }
        $assetNames = @($metadata.assets | ForEach-Object { $_.name })
        $download = Join-Path $local 'download'; $null = New-Item -ItemType Directory -Path $download
        $missing = @()
        foreach ($asset in $files) {
            $assetName = [IO.Path]::GetFileName($asset)
            if (@($assetNames | Where-Object { $_ -ceq $assetName }).Count -gt 1) { throw 'Published release has ambiguous asset names.' }
            if ($assetNames -notcontains $assetName) {
                if (!$metadata.isDraft) { throw 'A published immutable release is incomplete. No asset was overwritten or uploaded; review the release.' }
                $missing += $asset
            } else {
                $null = GitHub @('release','download',$plan.Tag,'--repo',$Repository,'--pattern',$assetName,'--dir',$download)
                if (!(Same-File $asset (Join-Path $download $assetName))) { throw 'An existing release asset differs. No immutable asset was overwritten; publication is blocked.' }
            }
        }
        foreach ($asset in $missing) {
            # Resume only missing draft assets after every existing byte matches.
            # Never use --clobber or modify published bytes.
            $null = GitHub @('release','upload',$plan.Tag,$asset,'--repo',$Repository)
            $null = GitHub @('release','download',$plan.Tag,'--repo',$Repository,'--pattern',([IO.Path]::GetFileName($asset)),'--dir',$download)
        }
        foreach ($asset in $files) { if (!(Same-File $asset (Join-Path $download ([IO.Path]::GetFileName($asset))))) { throw 'An existing or uploaded release asset differs. No immutable asset was overwritten; publication is blocked.' } }
        if ((Get-FileHash -LiteralPath (Join-Path $download $plan.Name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $plan.Sha -or
            (Get-FileHash -LiteralPath (Join-Path $download 'manifest.json') -Algorithm SHA256).Hash -cne $manifestSha) { throw 'Input or downloaded artifact bytes changed during publication. Catalog deployment is blocked.' }
        $plan | Add-Member -NotePropertyName WasDraft -NotePropertyValue ([bool]$metadata.isDraft)
    }
    # Both ZIPs and the exact same combined manifest have now been downloaded and
    # verified. A failed job can resume from its retained artifact without rebuilding.
    foreach ($plan in $plans) {
        if ($plan.WasDraft) { $null = GitHub @('release','edit',$plan.Tag,'--repo',$Repository,'--draft=false','--prerelease','--latest=false') }
        $published = ((GitHub @('release','view',$plan.Tag,'--repo',$Repository,'--json','tagName,isDraft')) -join "`n") | ConvertFrom-Json
        if ($published.tagName -cne $plan.Tag -or $published.isDraft -isnot [bool] -or $published.isDraft) { throw 'Release visibility was not confirmed. Catalog deployment must remain blocked.' }
    }
    [pscustomobject]@{ BothPackagesVerified = $true; BothPreviewReleasesPublished = $true; Version = $Version; CatalogDeploymentAllowed = $true; CredentialsLogged = $false } | ConvertTo-Json
} finally {
    # Only this invocation's new private staging directory. Input packages and all
    # remote releases/assets are retained, including on an uncertain failure.
    Remove-Item -LiteralPath $stage -Recurse -Force
}
