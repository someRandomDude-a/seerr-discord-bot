param(
    [string]$EnvironmentFile = '.env',
    [Parameter(Mandatory)][ValidatePattern('^[0-9]{1,20}$')][string]$Guild,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]{1,20}$')][string]$Channel,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]{1,20}$')][string]$User,
    [switch]$ExerciseMessages
)
$ErrorActionPreference = 'Stop'
# Explicitly selected test targets only. No gateway connection, guild-command
# registration, native credentials, user-token collection or other guild access.
if ((Get-Item -LiteralPath $EnvironmentFile).Length -gt 65536) { throw 'Test environment exceeds its supported size.' }
$wanted = @('DISCORD_TOKEN','DISCORD_CLIENT_ID','DISCORD_CLIENT_SECRET')
$values = @{}
foreach ($line in [IO.File]::ReadLines((Resolve-Path -LiteralPath $EnvironmentFile))) {
    if ($line -notmatch '^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') { continue }
    $key = $Matches[1]; $value = $Matches[2].Trim()
    if ($key -notin $wanted) { continue }
    if ($values.ContainsKey($key)) { throw 'A Discord test credential assignment is ambiguous. Values withheld.' }
    if ($value.StartsWith('"') -or $value.StartsWith("'")) {
        $quote = [regex]::Escape($value.Substring(0,1))
        if ($value -match ('^' + $quote + '([A-Za-z0-9._-]*)' + $quote + '\s*(?:#.*)?$')) { $value = $Matches[1] }
        else { throw 'A quoted test credential could not be parsed safely. Values withheld.' }
    } else { $value = ($value -split '\s+#',2)[0].Trim() }
    if ($value.Length -gt 4096 -or $value -notmatch '^[A-Za-z0-9._-]*$') { throw 'Invalid test credential syntax. Values withheld.' }
    $values[$key] = $value
}
if (!$values['DISCORD_TOKEN']) { throw 'A test bot token is required. No request sent.' }
Add-Type -AssemblyName System.Net.Http
$handler = New-Object Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false; $handler.UseCookies = $false
$http = New-Object Net.Http.HttpClient($handler)
$http.Timeout = [TimeSpan]::FromSeconds(15); $http.MaxResponseContentBufferSize = 2097152
function Request([string]$method, [string]$route, $content = $null, [bool]$bot = $true) {
    $message = New-Object Net.Http.HttpRequestMessage((New-Object Net.Http.HttpMethod($method)), ('https://discord.com/api/v10/' + $route))
    if ($bot) { $message.Headers.Authorization = New-Object Net.Http.Headers.AuthenticationHeaderValue('Bot', $values['DISCORD_TOKEN']) }
    if ($null -ne $content) { $message.Content = $content }
    try {
        $response = $http.SendAsync($message).GetAwaiter().GetResult()
        try {
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([int]$response.StatusCode -notin @(200,201,204)) { throw ('Discord test request was not confirmed (HTTP ' + [int]$response.StatusCode + '). Credential, request and response content withheld; no retry was sent.') }
            if ($text) { return $text | ConvertFrom-Json }
            return $null
        } finally { $response.Dispose() }
    } finally { $message.Dispose() }
}
function Form($entries) {
    $dictionary = New-Object 'Collections.Generic.Dictionary[string,string]'
    foreach ($key in $entries.Keys) { $dictionary.Add($key, [string]$entries[$key]) }
    return New-Object Net.Http.FormUrlEncodedContent($dictionary)
}
$report = [ordered]@{ TransportProbeOnly = $true; TokenAccepted = $false; ClientSecretAccepted = $false; ClientGrantRevoked = $false; TestChannelVerified = $false; TestHumanMemberVerified = $false; MessagesSent = 0; FilesUploaded = 0; ProbeMessagesDeleted = 0; CommandsChanged = 0; NativeServerChanged = $false; HumanOAuthTested = $false; PluginWorkflowTested = $false; CredentialsLogged = $false }
try {
    $application = Request 'GET' 'oauth2/applications/@me'; $report.TokenAccepted = $true
    if ($values['DISCORD_CLIENT_ID'] -and $values['DISCORD_CLIENT_ID'] -ne $application.id) { throw 'Client ID does not match the bot application. No test write was sent.' }
    $target = Request 'GET' ('channels/' + $Channel)
    if ($target.guild_id -ne $Guild -or $target.type -notin @(0,5)) { throw 'The designated channel is not a text channel in the designated test guild. No message was sent.' }
    $report.TestChannelVerified = $true
    $member = Request 'GET' ('guilds/' + $Guild + '/members/' + $User)
    if ($member.user.id -ne $User -or $member.user.bot) { throw 'The designated human test membership could not be verified. No message was sent.' }
    $report.TestHumanMemberVerified = $true
    if ($values['DISCORD_CLIENT_ID'] -and $values['DISCORD_CLIENT_SECRET']) {
        $grant = Request 'POST' 'oauth2/token' (Form @{ client_id = $values['DISCORD_CLIENT_ID']; client_secret = $values['DISCORD_CLIENT_SECRET']; grant_type = 'client_credentials'; scope = 'applications.commands.update' }) $false
        if (!$grant.access_token -or $grant.token_type -ine 'Bearer') { throw 'The application credential grant could not be verified. Token content withheld.' }
        $report.ClientSecretAccepted = $true
        $null = Request 'POST' 'oauth2/token/revoke' (Form @{ client_id = $values['DISCORD_CLIENT_ID']; client_secret = $values['DISCORD_CLIENT_SECRET']; token = $grant.access_token; token_type_hint = 'access_token' }) $false
        $report.ClientGrantRevoked = $true; $grant = $null
    }
    if ($ExerciseMessages) {
        foreach ($file in @($false,$true)) {
            $nonce = [Guid]::NewGuid().ToString('N').Substring(0,24)
            $payload = @{ content = 'Media Hub isolated transport acceptance probe. No media or native settings were changed.'; allowed_mentions = @{ parse = @() }; nonce = $nonce; enforce_nonce = $true } | ConvertTo-Json -Depth 5 -Compress
            if ($file) {
                $content = New-Object Net.Http.MultipartFormDataContent
                $content.Add((New-Object Net.Http.StringContent($payload, [Text.Encoding]::UTF8, 'application/json')), 'payload_json')
                $content.Add((New-Object Net.Http.ByteArrayContent(,[Text.Encoding]::UTF8.GetBytes('Generated test attachment; no credentials or real media.'))), 'files[0]', 'mediahub-acceptance.txt')
            } else { $content = New-Object Net.Http.StringContent($payload, [Text.Encoding]::UTF8, 'application/json') }
            $sent = Request 'POST' ('channels/' + $Channel + '/messages') $content
            if ($sent.channel_id -ne $Channel -or $sent.author.id -ne $application.bot.id -or $sent.id -notmatch '^[0-9]{1,20}$') { throw 'Probe receipt could not be verified. Inspect only the designated test channel; no automatic resend or deletion was performed.' }
            $report.MessagesSent++
            if ($file) { if (@($sent.attachments).Count -ne 1 -or $sent.attachments[0].filename -ne 'mediahub-acceptance.txt') { throw 'Probe attachment was not confirmed. No retry was sent.' }; $report.FilesUploaded++ }
            # Delete only the exact message returned from this invocation's own POST.
            $null = Request 'DELETE' ('channels/' + $Channel + '/messages/' + $sent.id)
            $report.ProbeMessagesDeleted++
        }
    }
    [pscustomobject]$report | ConvertTo-Json
} catch {
    # Exception text from HTTP or multipart internals must never print a header/token.
    [pscustomobject]$report | ConvertTo-Json
    throw 'Discord transport acceptance was incomplete. Credentials and detailed call logs withheld. No automatic resend, command update or native-server mutation was attempted.'
} finally { $http.Dispose(); $handler.Dispose(); $values.Clear(); $application = $null; $member = $null; $target = $null }
