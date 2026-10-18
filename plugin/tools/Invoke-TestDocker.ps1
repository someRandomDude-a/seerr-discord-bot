param(
    [Parameter(Mandatory)][string[]]$Arguments,
    [ValidateRange(1, 300)][int]$TimeoutSeconds = 60
)
$ErrorActionPreference = 'Stop'
$operation = if ($Arguments[0] -match '^[a-z-]{1,20}$') { $Arguments[0] } else { 'unknown' }
# Test tooling only. Callers must verify the exact org.mediahub.acceptance label
# before mutating a retained fixture. Never use this to reset a Docker daemon.
function Quote-Argument([string]$value) {
    if ($null -eq $value -or $value -match '[\r\n\x00]') { throw 'Invalid Docker test argument.' }
    $escaped = [regex]::Replace($value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = (Get-Command docker -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$start.Arguments = (@($Arguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($start)
$output = $process.StandardOutput.ReadToEndAsync()
$errors = $process.StandardError.ReadToEndAsync()
try {
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill() } catch { }
        throw 'Docker test operation timed out. Only its client was stopped. The outcome may be uncertain; inspect owned fixture state before any further mutation. No daemon reset or automatic retry was performed.'
    }
    if (!$output.Wait(1000) -or !$errors.Wait(1000) -or $process.ExitCode -ne 0) { throw ('Docker test ' + $operation + ' operation was not confirmed. Arguments, credentials and Docker error content withheld.') }
    if ($output.Result.Length -gt 16 * 1024 * 1024) { throw 'Docker test output exceeded its supported budget.' }
    if ($output.Result.Length -gt 0) { $output.Result.TrimEnd("`r", "`n") -split '\r?\n' }
} finally { $process.Dispose() }
