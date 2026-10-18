param(
    [Parameter(Mandatory)][ValidatePattern('^mediahub-test-[a-z0-9-]{1,35}$')][string]$Name,
    [Parameter(Mandatory)][ValidateSet('fresh-start','install-restart','explicit-restart','uninstall-restart','base-path-restart','configure','check','request-rejection')][string]$Phase,
    [scriptblock]$Docker
)
$ErrorActionPreference = 'Stop'
# Return only an allowlisted summary. Never emit Docker logs, labels, environment,
# inspect bodies, HTTP bodies, account IDs, tokens or exception messages.
function Invoke-DiagnosticDocker([string[]]$Arguments) {
    if ($Docker) { return & $Docker -Arguments $Arguments }
    return & (Join-Path $PSScriptRoot 'Invoke-TestDocker.ps1') -Arguments $Arguments -TimeoutSeconds 5
}
$result = [ordered]@{ Phase = $Phase; OwnershipVerified = $false; ContainerMetadataAvailable = $false;
    ContainerState = 'unknown'; ExitCode = $null; OOMKilled = $null; RestartCount = $null;
    LogScanAvailable = $false; LogScanSource = 'bounded Docker stdout only'; FailureCategories = @(); RawLogsIncluded = $false; CredentialsLogged = $false }
try {
    $owned = @(Invoke-DiagnosticDocker @('ps', '-a', '--filter', ('name=^/' + $Name + '$'), '--filter', ('label=org.mediahub.acceptance=' + $Name), '--format', '{{.ID}} {{.Names}}'))
    if ($owned.Count -ne 1 -or $owned[0] -cnotmatch ('^([a-f0-9]{12,64}) ' + [regex]::Escape($Name) + '$')) { return [pscustomobject]$result }
    $container = $Matches[1]; $result.OwnershipVerified = $true
    try {
        $template = '{"Status":"{{.State.Status}}","ExitCode":{{.State.ExitCode}},"OOMKilled":{{.State.OOMKilled}},"RestartCount":{{.RestartCount}}}'
        $encoded = (Invoke-DiagnosticDocker @('inspect', '--format', $template, $container)) -join "`n"
        if ($encoded.Length -gt 4096) { throw 'Oversized diagnostic metadata.' }
        $state = $encoded | ConvertFrom-Json
        if ($state.Status -notin @('created','running','paused','restarting','removing','exited','dead') -or
            $state.ExitCode -isnot [ValueType] -or $state.OOMKilled -isnot [bool] -or $state.RestartCount -isnot [ValueType]) { throw 'Invalid diagnostic metadata.' }
        $result.ContainerState = $state.Status; $result.ExitCode = [int]$state.ExitCode; $result.OOMKilled = $state.OOMKilled; $result.RestartCount = [int]$state.RestartCount
        $result.ContainerMetadataAvailable = $true
    } catch { } # Never echo untrusted Docker error content.
    try {
        $text = (Invoke-DiagnosticDocker @('logs', '--tail', '120', '--since', '5m', $container)) -join "`n"
        if ($text.Length -gt 1048576) { throw 'Diagnostic log budget reached.' }
        $categories = [ordered]@{
            HostStartupFailure = 'Application startup exception|Error while starting server|Failed to start the server'
            AssemblyLoadFailure = '(?:FileNotFound|FileLoad|TypeLoad|ReflectionTypeLoad|MissingMethod)Exception'
            DependencyInjectionFailure = 'Unable to resolve service for type|circular dependency|Unable to activate type|Unable to cast object of type'
            DatabaseFailure = 'SqliteException|database (?:is locked|disk image is malformed)'
            FileSystemFailure = 'UnauthorizedAccessException|Read-only file system|Permission denied'
            MemoryFailure = 'OutOfMemoryException|not enough memory'
            AddressInUse = 'Address already in use|AddressInUseException'
        }
        $result.FailureCategories = @($categories.Keys | Where-Object { $text -match $categories[$_] })
        $result.LogScanAvailable = $true
    } catch { }
} catch { }
[pscustomobject]$result
