param(
    [ValidatePattern('^mediahub-test-[a-z0-9-]{1,35}$')][string]$Name = 'mediahub-test-settings',
    [ValidateRange(1024,65535)][int]$Port = 18296
)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'Temp/opencode'
$stateFile = Join-Path $root ($Name + '.json')
if (!(Test-Path $stateFile)) { throw 'No matching private disposable fixture state. Nothing was installed.' }
$state = Get-Content $stateFile -Raw | ConvertFrom-Json
if ($state.Name -ne $Name -or $state.Port -ne $Port -or $state.Version -ne '12.2.0') { throw 'Fixture identity/ABI mismatch.' }
$dockerExe = (Get-Command docker -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
function Docker {
    $result = & $dockerExe @args
    if ($LASTEXITCODE -ne 0) { throw ('Dependency test Docker operation failed: ' + $args[0]) }
    return $result
}
function Own {
    $labels = (Docker inspect --format '{{json .Config.Labels}}' $Name) | ConvertFrom-Json
    if ($labels.'org.mediahub.acceptance' -ne $Name) { throw 'Refusing to modify an unowned container.' }
}
Own
if ((Docker inspect --format '{{.State.Running}}' $Name) -ne 'true') { throw 'Start only this owned fixture with the guarded harness first.' }
$lock = Get-Content (Join-Path $PSScriptRoot '../repository/web-dependencies.json') -Raw | ConvertFrom-Json
if ($lock.formatVersion -ne 1 -or $lock.targetAbi -ne '12.2.0' -or $lock.packages.Count -ne 2) { throw 'Unsupported dependency lock.' }
$expectedAssemblies = @('Jellyfin.Plugin.FileTransformation','Jellyfin.Plugin.PluginPages')
$cache = Join-Path $root 'mediahub-web-dependencies-12.2.0'
$null = New-Item -ItemType Directory -Path $cache -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Net.Http
$plans = @()
foreach ($p in $lock.packages) {
    if ($p.assembly -cnotin $expectedAssemblies -or $p.version -ne '3.0.2.0' -or $p.directory -notmatch '^(FileTransformation|PluginPages)_3\.0\.2\.0$' -or
        $p.sha256 -notmatch '^[a-f0-9]{64}$' -or $p.sourceUrl -notmatch '^https://github\.com/IAmParadox27/jellyfin-plugin-(file-transformation|pages)/releases/download/3\.0\.2\.0/Release-12\.2\.0\.zip$') { throw 'Unreviewed dependency lock entry.' }
    $archive = Join-Path $cache ($p.assembly + '-' + $p.sha256 + '.zip')
    if (!(Test-Path $archive)) {
        $client = New-Object System.Net.Http.HttpClient
        $client.Timeout = [TimeSpan]::FromSeconds(60); $client.MaxResponseContentBufferSize = 2097152
        try { $bytes = $client.GetByteArrayAsync($p.sourceUrl).GetAwaiter().GetResult() }
        catch { throw ('Pinned dependency download failed: ' + $p.name) }
        finally { $client.Dispose() }
        $temporary = $archive + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        $file = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $file.Write($bytes, 0, $bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
        if ((Get-FileHash $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $p.sha256) { throw 'Dependency digest mismatch; failed bytes retained for review, not installed.' }
        [IO.File]::Move($temporary, $archive)
    }
    if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $p.sha256) { throw 'Cached dependency digest mismatch. Nothing was replaced.' }
    $allowed = @(($p.assembly+'.dll'), ($p.assembly+'.deps.json'), ($p.assembly+'.pdb'), 'logo.png')
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $seen = @{}; $expanded = 0L
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -cnotin $allowed -or $seen.ContainsKey($entry.FullName) -or $entry.Length -gt 1048576) { throw 'Unexpected dependency archive member.' }
            $seen[$entry.FullName] = $true; $expanded += $entry.Length
        }
        if ($seen.Count -ne 4 -or $expanded -gt 4194304) { throw 'Incomplete or oversized dependency archive.' }
    } finally { $zip.Dispose() }
    $stage = Join-Path $cache ($p.directory + '-' + [Guid]::NewGuid().ToString('N'))
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $stage)
    $dll = Join-Path $stage ($p.assembly + '.dll')
    $metadata = [Reflection.AssemblyName]::GetAssemblyName($dll)
    if ($metadata.Name -ne $p.assembly -or $metadata.Version.ToString() -ne $p.version) { throw 'Dependency assembly metadata mismatch.' }
    $digest = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    $installed = @(Docker exec $Name find /config/plugins -maxdepth 2 -name ($p.assembly+'.dll'))
    $target = '/config/plugins/' + $p.directory
    if ($installed.Count -gt 0) {
        if ($installed.Count -ne 1 -or $installed[0] -ne ($target+'/'+$p.assembly+'.dll')) { throw 'Another dependency installation already exists; no upgrade or replacement was inferred.' }
        $remote = (Docker exec $Name sha256sum $installed[0]) -split '\s+'
        if ($remote[0] -ne $digest) { throw 'Installed dependency differs from its reviewed archive. No files were overwritten.' }
    } else {
        $directories = @(Docker exec $Name find /config/plugins -maxdepth 1 -name $p.directory)
        if ($directories.Count -ne 0) { throw 'Retained dependency directory is incomplete; no reset was performed.' }
        $plans += [pscustomobject]@{ Stage=$stage; Target=$target; Name=$p.name }
    }
}
if ($plans.Count -gt 0) {
    Own
    $null = Docker stop --time 20 $Name
    try { foreach ($plan in $plans) { Own; $null = Docker cp $plan.Stage ($Name+':'+$plan.Target) } }
    finally { Own; $null = Docker start $Name }
}
Write-Output 'Reviewed File Transformation 3.0.2.0 and Plugin Pages 3.0.2.0 installed/verified only in the owned Jellyfin 12.2 fixture. Original upstream ZIP bytes verified; no provider engines bundled in Media Hub.'
