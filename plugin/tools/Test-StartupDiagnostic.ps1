$ErrorActionPreference = 'Stop'
$collector = Join-Path $PSScriptRoot 'Get-TestStartupDiagnostic.ps1'
function Require([bool]$condition, [string]$message) { if (!$condition) { throw $message } }
$owned = {
    param([string[]]$Arguments)
    switch ($Arguments[0]) {
        'ps' { return 'abcdef123456 mediahub-test-diagnostic-unit' }
        'inspect' { return '{"Status":"exited","ExitCode":1,"OOMKilled":false,"RestartCount":0}' }
        'logs' { return 'Application startup exception FileNotFoundException Unable to resolve service for type synthetic-private-marker-that-must-never-escape' }
        default { throw 'Unexpected diagnostic operation.' }
    }
}
$result = & $collector -Name mediahub-test-diagnostic-unit -Phase install-restart -Docker $owned
Require ($result.OwnershipVerified -and $result.ContainerMetadataAvailable -and $result.ContainerState -eq 'exited') 'Owned metadata was not safely classified.'
Require ($result.ExitCode -eq 1 -and !$result.OOMKilled) 'Exit status was not retained.'
Require ($result.FailureCategories -contains 'HostStartupFailure' -and $result.FailureCategories -contains 'AssemblyLoadFailure' -and $result.FailureCategories -contains 'DependencyInjectionFailure') 'Startup error categories were not classified.'
$json = $result | ConvertTo-Json -Depth 4
Require (!$json.Contains('synthetic-private-marker') -and !$json.Contains('abcdef123456') -and !$result.RawLogsIncluded) 'Untrusted log contents or container identity leaked into diagnostics.'
$unowned = { param([string[]]$Arguments) if ($Arguments[0] -ne 'ps') { throw 'Unowned containers must not be inspected.' }; return 'abcdef123456 mediahub-test-another-fixture' }
$result = & $collector -Name mediahub-test-diagnostic-unit -Phase fresh-start -Docker $unowned
Require (!$result.OwnershipVerified -and !$result.ContainerMetadataAvailable -and !$result.LogScanAvailable) 'Unowned fixture data was accepted.'
$unavailable = { param([string[]]$Arguments) throw 'synthetic-private-marker-from-daemon-error' }
$result = & $collector -Name mediahub-test-diagnostic-unit -Phase explicit-restart -Docker $unavailable
Require (!$result.OwnershipVerified -and !(($result | ConvertTo-Json -Depth 4).Contains('synthetic-private-marker'))) 'Daemon errors escaped redaction.'
$malformed = {
    param([string[]]$Arguments)
    if ($Arguments[0] -eq 'ps') { return 'abcdef123456 mediahub-test-diagnostic-unit' }
    if ($Arguments[0] -eq 'inspect') { return '{"Status":"synthetic-private-marker","ExitCode":"private","OOMKilled":false,"RestartCount":0}' }
    return ''
}
$result = & $collector -Name mediahub-test-diagnostic-unit -Phase install-restart -Docker $malformed
Require (!$result.ContainerMetadataAvailable -and $result.ContainerState -eq 'unknown' -and !(($result | ConvertTo-Json -Depth 4).Contains('synthetic-private-marker'))) 'Malformed metadata escaped the allowlist.'
Write-Output 'Startup diagnostic tests passed: owned exit classification, log redaction, ownership rejection, unavailable daemon and malformed metadata. No Docker daemon or credentials used.'
