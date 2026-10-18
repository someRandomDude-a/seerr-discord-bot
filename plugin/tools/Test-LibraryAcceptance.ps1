param(
    [Parameter(Mandatory)][string]$Package,
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$PackageVersion = '0.1.0.0',
    [string]$PlaywrightModule,
    [switch]$ResumeEmptyFixture
)
$ErrorActionPreference = 'Stop'
$name = 'mediahub-test-visibility'; $port = 18496
$root = Join-Path $env:LOCALAPPDATA 'Temp/opencode'
$stateFile = Join-Path $root ($name + '.json')
if (!(Test-Path $Package -PathType Leaf)) { throw 'A selected exact plugin ZIP is required before any fixture change.' }
if ((Test-Path $stateFile) -and !$ResumeEmptyFixture) { throw 'This fresh media fixture refuses retained state. Nothing was replaced.' }
$dockerExe = (Get-Command docker -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
function Docker {
    $result = & $dockerExe @args
    if ($LASTEXITCODE -ne 0) { throw 'Owned media acceptance Docker operation failed.' }
    return $result
}
function Own {
    $labels = Docker inspect --format '{{json .Config.Labels}}' $name | ConvertFrom-Json
    if ($labels.'org.mediahub.acceptance' -ne $name) { throw 'Media fixture ownership mismatch.' }
}
$url = 'http://127.0.0.1:' + $port
function Api($path, $body = $null, $viewer = $false, $expected = 200) {
    $token = if ($viewer) { $state.ViewerToken } else { $state.AdminToken }
    $device = if ($viewer) { 'mediahub-acceptance-viewer' } else { 'mediahub-acceptance-admin' }
    $headers = @{Authorization=('MediaBrowser Client="Media Hub acceptance", Device="Disposable test", DeviceId="'+$device+'", Version="0.1", Token="'+$token+'"')}
    $arguments = @{Uri=($url+'/'+$path); Headers=$headers; TimeoutSec=20; UseBasicParsing=$true}
    if ($null -ne $body) { $arguments.Method='POST'; $arguments.ContentType='application/json'; $arguments.Body=$body|ConvertTo-Json -Depth 30 -Compress }
    $status=0; $content=''
    try { $response=Invoke-WebRequest @arguments; $status=[int]$response.StatusCode; $content=$response.Content }
    catch { if ($_.Exception.Response) { $status=[int]$_.Exception.Response.StatusCode } }
    if ($status -ne $expected) { throw ('Media fixture API check failed: '+($path -split '\?')[0]+' ('+$status+', expected '+$expected+'). Private content withheld.') }
    if ($status -eq 200 -and $content) { $parsed=$content|ConvertFrom-Json; return $parsed }
}
function Files {
    return (Docker exec $name find /config/mediahub-acceptance-visible /config/mediahub-acceptance-hidden -type f -exec sha256sum '{}' '+' | Sort-Object) -join '|'
}
try {
    if ($ResumeEmptyFixture) {
        Own
        $state=Get-Content $stateFile -Raw|ConvertFrom-Json
        if ($state.Name -ne $name -or $state.Port -ne $port -or $state.LibraryAcceptance) { throw 'Only this unpopulated owned media fixture can resume.' }
        $null=Docker start $name
        & (Join-Path $PSScriptRoot 'Test-Jellyfin.ps1') -Mode Check -Name $name -Port $port -PackageVersion $PackageVersion
        if (@(Api 'Library/VirtualFolders').Count -ne 0) { throw 'Resumed fixture has libraries; nothing was overwritten.' }
        & (Join-Path $PSScriptRoot 'Test-Jellyfin.ps1') -Mode Uninstall -Name $name -Port $port -PackageVersion $PackageVersion
    } else { & (Join-Path $PSScriptRoot 'Test-Jellyfin.ps1') -Mode Start -Name $name -Port $port -PackageVersion $PackageVersion }
    & (Join-Path $PSScriptRoot 'Test-Jellyfin.ps1') -Mode Install -Name $name -Port $port -Package $Package -PackageVersion $PackageVersion
    & (Join-Path $PSScriptRoot 'Test-WebDependencies.ps1') -Name $name -Port $port
    & (Join-Path $PSScriptRoot 'Test-Jellyfin.ps1') -Mode Check -Name $name -Port $port -PackageVersion $PackageVersion
    Own
    $state=Get-Content $stateFile -Raw|ConvertFrom-Json
    $users=@(Api 'Users'); $admin=@($users|Where-Object Name -eq 'mediahub-admin')[0]; $viewer=@($users|Where-Object Name -eq 'mediahub-viewer')[0]
    if (@(Api 'Library/VirtualFolders').Count -ne 0) { throw 'Fresh media fixture unexpectedly has libraries; nothing was changed.' }
    $public='/config/mediahub-acceptance-visible'; $private='/config/mediahub-acceptance-hidden'
    foreach ($path in @($public,$private)) {
        $existing=@(Docker exec $name find /config -maxdepth 1 -type d -path $path)
        if ($existing.Count -and @(Docker exec $name find $path -type f).Count) { throw 'Retained generated-media files exist; no files were overwritten.' }
    }
    $null=Docker exec $name mkdir -p $public $private ($public+'/Public One') ($public+'/Public Two') ($private+'/Private Movie')
    # Generated blank test video only, never existing media or mounted production paths.
    $null=Docker @('exec',$name,'/usr/lib/jellyfin-ffmpeg/ffmpeg','-nostdin','-hide_banner','-loglevel','error','-f','lavfi','-i','color=c=black:s=320x180:r=10','-f','lavfi','-i','anullsrc=r=44100:cl=stereo','-t','15','-c:v','libx264','-pix_fmt','yuv420p','-c:a','aac',($public+'/Public One/Public One.mp4'))
    $null=Docker exec $name cp ($public+'/Public One/Public One.mp4') ($public+'/Public Two/Public Two.mp4')
    $null=Docker exec $name cp ($public+'/Public One/Public One.mp4') ($private+'/Private Movie/Private Movie.mp4')
    foreach ($entry in @(@{Title='Public One'; Path=($public+'/Public One')}, @{Title='Public Two'; Path=($public+'/Public Two')}, @{Title='Private Movie'; Path=($private+'/Private Movie')})) {
        $nfo=Join-Path $root ('mediahub-nfo-'+[Guid]::NewGuid().ToString('N')+'.nfo')
        [IO.File]::WriteAllText($nfo, ('<movie><title>'+ $entry.Title +'</title><uniqueid type="tmdb" default="true">999999991</uniqueid></movie>'))
        $null=Docker cp $nfo ($name+':'+$entry.Path+'/movie.nfo')
    }
    foreach ($folder in @(@{Name='Acceptance Visible'; Path=$public}, @{Name='Acceptance Hidden'; Path=$private})) {
        $options=@{PathInfos=@(@{Path=$folder.Path}); EnableRealtimeMonitor=$false; SaveLocalMetadata=$false; EnableChapterImageExtraction=$false; EnableAutomaticSeriesGrouping=$false; MetadataSavers=@(); SubtitleDownloadLanguages=@(); TypeOptions=@(@{Type='Movie'; MetadataFetchers=@(); ImageFetchers=@(); MetadataFetcherOrder=@(); ImageFetcherOrder=@()})}
        $null=Api ('Library/VirtualFolders?name='+[Uri]::EscapeDataString($folder.Name)+'&collectionType=movies&refreshLibrary=false') @{LibraryOptions=$options} $false 204
    }
    $null=Api 'Library/Refresh' @{} $false 204
    $deadline=[DateTime]::UtcNow.AddSeconds(90); $items=@()
    while ([DateTime]::UtcNow -lt $deadline) {
        $items=@((Api ('Items?UserId='+$admin.Id+'&Recursive=true&IncludeItemTypes=Movie&Fields=ProviderIds')).Items)
        if ($items.Count -eq 3) { break }
        $null=[Threading.Tasks.Task]::Delay(1000).GetAwaiter().GetResult()
    }
    if ($items.Count -ne 3) { throw 'Generated test media was not completely discovered within the scan budget.' }
    $visible=@($items|Where-Object Name -eq 'Public One')[0]; $hidden=@($items|Where-Object Name -eq 'Private Movie')[0]
    if (!$visible -or !$hidden) { throw 'Generated movie identities are incomplete.' }
    $folders=@(Api 'Library/VirtualFolders'); $publicFolder=@($folders|Where-Object Name -eq 'Acceptance Visible')[0]
    if (!$publicFolder.ItemId) { throw 'Native visible-library identity unavailable.' }
    $policy=(Api ('Users/'+$viewer.Id)).Policy; $policy.EnableAllFolders=$false; $policy.EnabledFolders=@($publicFolder.ItemId)
    $null=Api ('Users/'+$viewer.Id+'/Policy') $policy $false 204
    $beforeFiles=Files
    $beforeUserData=(Api ('Users/'+$viewer.Id+'/Items/'+$visible.Id)).UserData|ConvertTo-Json -Depth 10 -Compress
    $adminLibrary=Api 'MediaHub/Catalog/Library'
    $viewerLibrary=Api 'MediaHub/Catalog/Library' $null $true
    if (!@($adminLibrary.Items|Where-Object { [Guid]$_.NativeId -eq [Guid]$hidden.Id }).Count -or @($viewerLibrary.Items|Where-Object { [Guid]$_.NativeId -eq [Guid]$hidden.Id }).Count -or !@($viewerLibrary.Items|Where-Object { [Guid]$_.NativeId -eq [Guid]$visible.Id }).Count) { throw 'Native library visibility projection failed.' }
    $null=Api ('MediaHub/Items/'+$visible.Id+'/Controls') $null $true
    $null=Api ('MediaHub/Items/'+$hidden.Id+'/Controls') $null $true 400
    $afterUserData=(Api ('Users/'+$viewer.Id+'/Items/'+$visible.Id)).UserData|ConvertTo-Json -Depth 10 -Compress
    if ($beforeUserData -ne $afterUserData) { throw 'Passive plugin reads modified native media user data.' }
    $me=Api 'MediaHub/Me' $null $true
    $null=Api ('MediaHub/Me/Watchlist/'+$visible.Id+'/Add') @{Revision=$me.Revision} $true
    $policy.EnabledFolders=@(); $null=Api ('Users/'+$viewer.Id+'/Policy') $policy $false 204
    if (@((Api 'MediaHub/Me' $null $true).Watchlist|Where-Object { [Guid]$_.ItemId -eq [Guid]$visible.Id }).Count) { throw 'An inaccessible retained watch was disclosed.' }
    $null=Api ('MediaHub/Items/'+$visible.Id+'/Controls') $null $true 400
    $policy.EnabledFolders=@($publicFolder.ItemId); $null=Api ('Users/'+$viewer.Id+'/Policy') $policy $false 204
    if (!@((Api 'MediaHub/Me' $null $true).Watchlist|Where-Object { [Guid]$_.ItemId -eq [Guid]$visible.Id }).Count) { throw 'An inaccessible watch was erased instead of retained.' }
    if ((Files) -ne $beforeFiles) { throw 'Generated native media files changed during visibility/watch tests.' }
    $state|Add-Member -NotePropertyName LibraryAcceptance -NotePropertyValue ([pscustomobject]@{Visible=$visible.Id; Hidden=$hidden.Id; Viewer=$viewer.Id; Admin=$admin.Id; PublicFolder=$publicFolder.ItemId; IntentionalFixtureSetup=$true})
    [IO.File]::WriteAllText($stateFile, ($state|ConvertTo-Json -Depth 30), (New-Object Text.UTF8Encoding($false)))
    Write-Output 'Generated-media native visibility, hidden-item rejection, passive user-data preservation, live watch hiding/restoration and media-file retention passed. Dedicated fixture setup intentionally created libraries and restricted only its disposable viewer; the original empty baseline is retained.'
    if ($PlaywrightModule) {
        & node (Join-Path $PSScriptRoot 'Test-PlayerAcceptance.mjs') $stateFile $PlaywrightModule
        if ($LASTEXITCODE -ne 0) { throw 'Real native-player acceptance failed; no uncertain command was retried.' }
        & node (Join-Path $PSScriptRoot 'Test-MultiPlayerAcceptance.mjs') $stateFile $PlaywrightModule
        if ($LASTEXITCODE -ne 0) { throw 'Two-player/permission-loss acceptance failed; no uncertain command was retried.' }
    }
} finally {
    $labels=& $dockerExe inspect --format '{{json .Config.Labels}}' $name 2>$null
    if ($LASTEXITCODE -eq 0 -and ($labels|ConvertFrom-Json).'org.mediahub.acceptance' -eq $name) { $null=& $dockerExe stop --time 20 $name }
}
