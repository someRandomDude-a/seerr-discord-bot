param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../src/Jellyfin.Plugin.MediaHub/Jellyfin.Plugin.MediaHub.csproj'
$json = & $Dotnet list $project package --vulnerable --include-transitive --format json
if ($LASTEXITCODE -ne 0) { throw 'NuGet advisory lookup failed; no clean result was inferred.' }
try { $report = ($json -join "`n") | ConvertFrom-Json }
catch { throw 'NuGet advisory report was not valid JSON.' }
if ($report.version -ne 1 -or @($report.projects).Count -eq 0) { throw 'NuGet advisory report was incomplete.' }
if (@($report.problems).Where({ $null -ne $_ }).Count -gt 0) { throw 'NuGet reported advisory lookup problems; no clean result was inferred.' }
$findings = @()
foreach ($projectReport in $report.projects) {
    if (@($projectReport.problems).Where({ $null -ne $_ }).Count -gt 0) { throw 'NuGet reported a project advisory lookup problem.' }
    if (@($projectReport.frameworks).Count -eq 0) { throw 'NuGet advisory report omitted target frameworks.' }
    foreach ($framework in $projectReport.frameworks) {
        if (@($framework.problems).Where({ $null -ne $_ }).Count -gt 0) { throw 'NuGet reported a framework advisory lookup problem.' }
        foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
            if ($null -ne $package -and @($package.vulnerabilities).Where({ $null -ne $_ }).Count -gt 0) { $findings += $package.id }
        }
    }
}
if ($findings.Count -gt 0) { throw ('Known NuGet vulnerabilities require review: ' + (($findings | Sort-Object -Unique) -join ', ')) }
Write-Output 'No known direct/transitive plugin NuGet vulnerabilities were reported. This is not a security certification.'
