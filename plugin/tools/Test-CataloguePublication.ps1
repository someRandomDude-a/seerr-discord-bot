$ErrorActionPreference = 'Stop'
$publisher = Join-Path $PSScriptRoot 'Publish-PluginCatalogue.ps1'
function Require([bool]$condition, [string]$message) { if (!$condition) { throw $message } }
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } elseif ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Temp/opencode' } else { [IO.Path]::GetTempPath() }
$root = Join-Path $temporaryRoot ('mediahub-catalogue-test-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
try {
    $version = '0.1.0.0'; $repository = 'example/plugins'; $catalog = @(); $checksums = @()
    foreach ($kind in @('core','discord')) {
        $adapter = $kind -eq 'discord'
        $assembly = if ($adapter) { 'Jellyfin.Plugin.MediaHub.Discord' } else { 'Jellyfin.Plugin.MediaHub' }
        $id = if ($adapter) { '8456d36a-3c89-4b6d-9959-9a74566cc252' } else { 'c70297bf-77bd-4ad4-93c9-f67598519777' }
        $tag = if ($adapter) { 'discord-v' + $version } else { 'plugin-v' + $version }
        $name = $assembly + '_' + $version + '.zip'; $archive = Join-Path $root $name
        # These are publication-transport fixtures, not installable plugin packages.
        [IO.File]::WriteAllText($archive, ('synthetic package for ' + $kind))
        $checksums += (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $name
        $catalog += @{ guid=$id; name=$kind; versions=@(@{ version=$version; targetAbi='12.2.0'; sourceUrl=('https://github.com/' + $repository + '/releases/download/' + $tag + '/' + $name); checksum=(Get-FileHash $archive -Algorithm MD5).Hash.ToLowerInvariant() }) }
    }
    [IO.File]::WriteAllText((Join-Path $root 'manifest.json'), (ConvertTo-Json -InputObject $catalog -Depth 8))
    [IO.File]::WriteAllLines((Join-Path $root 'SHA256SUMS'), $checksums)
    $store = @{ Releases=@{}; Calls=(New-Object 'System.Collections.Generic.List[string]'); Fault='' }
    $client = {
        param([string[]]$Arguments)
        $a = $Arguments; $operation = $a[0] + '/' + $a[1]; $store.Calls.Add($operation)
        if ($operation -eq 'api/--paginate') { return @($store.Releases.Keys) }
        $tag = $a[2]
        if ($operation -eq 'release/create') {
            if ($store.Fault -eq 'second-create' -and $tag.StartsWith('discord-')) { throw 'Synthetic create failure.' }
            if ($store.Releases.ContainsKey($tag)) { throw 'Duplicate synthetic release.' }
            $assets = @{}
            foreach ($file in $a | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }) { $assets[[IO.Path]::GetFileName($file)] = [IO.File]::ReadAllBytes($file) }
            $store.Releases[$tag] = @{ Draft=$true; Assets=$assets }; return
        }
        if (!$store.Releases.ContainsKey($tag)) { throw 'Synthetic release not found.' }
        $release = $store.Releases[$tag]
        switch ($operation) {
            'release/view' {
                $result = @{ tagName=$tag; isDraft=[bool]$release.Draft; assets=@($release.Assets.Keys | ForEach-Object { @{ name=$_ } }) }
                if ($store.Fault -eq 'missing-draft' -and !$release.Draft) { $result.Remove('isDraft') }
                return $result | ConvertTo-Json -Depth 6 -Compress
            }
            'release/upload' {
                $file=$a[3]; $name=[IO.Path]::GetFileName($file)
                if (!$release.Draft -or $release.Assets.ContainsKey($name)) { throw 'Immutable synthetic upload rejected.' }
                $release.Assets[$name]=[IO.File]::ReadAllBytes($file); return
            }
            'release/download' {
                $name=$a[[Array]::IndexOf($a,'--pattern')+1]; $directory=$a[[Array]::IndexOf($a,'--dir')+1]
                if (!$release.Assets.ContainsKey($name)) { throw 'Synthetic asset missing.' }
                $bytes=$release.Assets[$name]
                if ($store.Fault -eq 'download-corruption') { $bytes=[Text.Encoding]::UTF8.GetBytes('corrupt synthetic download') }
                [IO.File]::WriteAllBytes((Join-Path $directory $name), $bytes); return
            }
            'release/edit' {
                if ($store.Fault -eq 'second-edit' -and $tag.StartsWith('discord-')) { throw 'Synthetic uncertain publication failure.' }
                $release.Draft=$false; return
            }
            default { throw 'Unexpected synthetic GitHub operation.' }
        }
    }.GetNewClosure()
    $parameters = @{ Directory=$root; Version=$version; Repository=$repository; Commit=('a' * 40); GitHubClient=$client }
    function Run-Publication { return ((& $publisher @parameters -Publish) -join "`n") | ConvertFrom-Json }
    function Must-Fail {
        $failed=$false
        try { $null=Run-Publication } catch { $failed=$true }
        Require $failed 'An unsafe publication was allowed.'
    }
    $failed=$false
    try { $null=& $publisher @parameters } catch { $failed=$true }
    Require ($failed -and $store.Calls.Count -eq 0) 'Publication did not require explicit authorization.'
    $result=Run-Publication
    Require ($result.CatalogDeploymentAllowed -and $store.Releases.Count -eq 2 -and @($store.Releases.Values | Where-Object Draft).Count -eq 0) 'Both separate releases were not verified and published.'
    $store.Calls.Clear(); $null=Run-Publication
    Require (@($store.Calls | Where-Object { $_ -in @('release/create','release/upload','release/edit') }).Count -eq 0) 'A successful rerun modified immutable releases.'
    $saved=$store.Releases['discord-v0.1.0.0'].Assets['manifest.json']
    $store.Releases['discord-v0.1.0.0'].Assets['manifest.json']=[Text.Encoding]::UTF8.GetBytes('different existing bytes')
    $store.Calls.Clear(); Must-Fail
    Require (@($store.Calls | Where-Object { $_ -in @('release/upload','release/edit') }).Count -eq 0) 'A conflicting release was overwritten or published.'
    $store.Releases['discord-v0.1.0.0'].Assets['manifest.json']=$saved
    $store.Releases['discord-v0.1.0.0'].Assets.Remove('manifest.json'); $store.Calls.Clear(); Must-Fail
    Require (!$store.Calls.Contains('release/upload')) 'An incomplete published release was modified.'
    $store.Releases['discord-v0.1.0.0'].Draft=$true; $store.Calls.Clear(); $null=Run-Publication
    Require (@($store.Calls | Where-Object { $_ -eq 'release/upload' }).Count -eq 1) 'A partial draft did not resume only its missing asset.'
    $store.Fault='download-corruption'; $store.Calls.Clear(); Must-Fail
    Require (!$store.Calls.Contains('release/edit')) 'Corrupt downloads reached publication.'
    $store.Fault='missing-draft'; Must-Fail
    $store.Releases.Clear(); $store.Calls.Clear(); $store.Fault='second-create'; Must-Fail
    Require (!$store.Calls.Contains('release/edit') -and $store.Releases['plugin-v0.1.0.0'].Draft) 'One plugin was published before both packages verified.'
    $store.Fault='second-edit'; Must-Fail
    Require (!$store.Releases['plugin-v0.1.0.0'].Draft -and $store.Releases['discord-v0.1.0.0'].Draft) 'Interrupted publication fixture did not model its partial visibility.'
    $store.Fault=''; $result=Run-Publication
    Require $result.CatalogDeploymentAllowed 'An interrupted publication could not resume from the exact retained artifact.'
    $store.Calls.Clear(); [IO.File]::WriteAllText((Join-Path $root 'SHA256SUMS'), 'bad checksum')
    Must-Fail
    Require ($store.Calls.Count -eq 0) 'Invalid local packages caused remote operations.'
    Write-Output 'Catalogue publication tests passed: explicit authorization, two releases, immutable rerun, conflict rejection, missing published asset, partial draft recovery, corrupt download, incomplete visibility proof, create/edit failure and retained-artifact recovery. No network or real GitHub operations used.'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
