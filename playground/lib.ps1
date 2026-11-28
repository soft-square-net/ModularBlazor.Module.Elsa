# Shared by seed-grants.ps1 and check.ps1: HTTP calls and the dev accounts' sign-in. Windows PowerShell 5.1 or
# PowerShell 7. DEVELOPMENT ONLY: it signs in with the playground's dev accounts.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Net.Http

$Identity = 'http://localhost:5090/'
$Gateway = 'http://localhost:5080/'
$Tenants = 'acme', 'globex'

# The service subject of the workflows (the client id is the token's subject) and what it may do in a tenant, for the
# whole tenant. The coordinator uses content.assign, content.transfer, content.publish and shifts.manage. The newsroom
# activities use content.unit.assign (file a post in its Section, and see it), newsroom.workflow (read projects and report
# their state), content.assign, content.publish, dynamicfields.read (Content's publish has DynamicFields check the post as
# the caller) and org.read (Organization's members of a unit, to find its managers when a deadline passes).
$WorkflowSubject = 'elsa-workflows'
$WorkflowGrants = 'content.assign', 'content.transfer', 'content.publish', 'content.unit.assign', 'shifts.manage',
    'newsroom.workflow', 'org.read', 'dynamicfields.read'

# The tenants' dev administrators. globex's is added by the playground (AppHost.cs).
function Get-TenantAdmin([string] $tenant, [string] $platformRoot) {
    if ($tenant -eq 'globex') { return @{ Email = 'globex-admin@modular.local'; Password = 'Dev-GlobexAdmin-2026!' } }
    if (-not $platformRoot) { $platformRoot = Join-Path $PSScriptRoot '..\..\Platform' }
    $settings = Join-Path $platformRoot 'src/WebApi/Identity/WebApi.Identity.Server/appsettings.Development.json'
    if (-not (Test-Path $settings)) { throw "No platform checkout at $platformRoot (pass -PlatformRoot)." }
    $email = "$tenant-admin@modular.local"
    $user = (Get-Content $settings -Raw | ConvertFrom-Json).Identity.Seed.Users | Where-Object Email -eq $email
    return @{ Email = $email; Password = $user.Password }
}

# One client per identity session (its cookies); redirects are followed by hand.
function New-Client {
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $handler.CookieContainer = New-Object System.Net.CookieContainer
    $script:http = New-Object System.Net.Http.HttpClient($handler)
    $script:http.Timeout = [TimeSpan]::FromSeconds(60)
}
New-Client

function Send([string] $method, [string] $url, $body = $null, [string] $token = $null, [string] $tenant = $null, [switch] $Form, [switch] $AllowFailure) {
    $request = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), $url)
    if ($token) { $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $token) }
    if ($tenant) { $request.Headers.Add('X-Tenant', $tenant) }
    if ($null -ne $body) {
        if ($Form) {
            $pairs = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,string]]'
            foreach ($k in $body.Keys) { $pairs.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,string]'($k, [string]$body[$k]))) }
            $request.Content = New-Object System.Net.Http.FormUrlEncodedContent -ArgumentList (,$pairs)
        } else {
            $json = $body | ConvertTo-Json -Depth 20 -Compress
            $request.Content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, 'application/json')
        }
    }
    $response = $script:http.SendAsync($request).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $result = [pscustomobject]@{ Status = [int]$response.StatusCode; Location = $response.Headers.Location; Text = $text; Json = $null }
    if ($text -and $response.Content.Headers.ContentType -and $response.Content.Headers.ContentType.MediaType -like '*json*') {
        $result.Json = $text | ConvertFrom-Json
    }
    if (-not $AllowFailure -and $result.Status -ge 400) { throw "$method $url -> $($result.Status): $text" }
    return $result
}

function Resolve-Location([string] $base, $location) {
    if ($location.IsAbsoluteUri) { return $location.AbsoluteUri }
    return (New-Object Uri((New-Object Uri($base)), $location.OriginalString)).AbsoluteUri
}

function ConvertTo-Base64Url([byte[]] $bytes) { [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }

# Authorization code + PKCE, like the Blazor client.
function Get-UserToken([string] $email, [string] $password, [string] $tenant) {
    New-Client   # a fresh identity session per account
    $redirect = 'http://localhost:5070/authentication/login-callback'
    $bytes = New-Object byte[] 32; [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $verifier = ConvertTo-Base64Url $bytes
    $challenge = ConvertTo-Base64Url ([System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::ASCII.GetBytes($verifier)))
    $query = 'client_id=blazor-client&response_type=code&scope=' + [Uri]::EscapeDataString('openid profile email api') +
        '&redirect_uri=' + [Uri]::EscapeDataString($redirect) + "&code_challenge=$challenge&code_challenge_method=S256&state=playground&tenant=$tenant"
    $url = $Identity + "connect/authorize?$query"
    $code = $null
    for ($i = 0; $i -lt 10 -and -not $code; $i++) {
        $r = Send GET $url -AllowFailure
        if ($r.Status -ge 300 -and $r.Status -lt 400) {
            $next = Resolve-Location $url $r.Location
            if ($next.StartsWith($redirect)) {
                if ($next -notmatch '[?&]code=([^&]+)') { throw "Sign-in of $email refused: $next" }
                $code = [Uri]::UnescapeDataString($Matches[1])
                break
            }
            $url = $next; continue
        }
        if ($r.Status -eq 200 -and $r.Text -match 'name="Password"') {
            $antiforgery = [regex]::Match($r.Text, 'name="__RequestVerificationToken" type="hidden" value="([^"]+)"').Groups[1].Value
            $returnUrl = [System.Net.WebUtility]::HtmlDecode([regex]::Match($r.Text, 'name="ReturnUrl" value="([^"]*)"').Groups[1].Value)
            $r = Send POST $url @{ Email = $email; Password = $password; ReturnUrl = $returnUrl; __RequestVerificationToken = $antiforgery } -Form -AllowFailure
            if ($r.Status -lt 300 -or $r.Status -ge 400) { throw "Sign-in of $email failed ($($r.Status))." }
            $url = Resolve-Location $url $r.Location; continue
        }
        throw "Unexpected answer while signing in $email ($($r.Status)) at $url"
    }
    if (-not $code) { throw "No authorization code for $email." }
    $token = Send POST ($Identity + 'connect/token') @{
        grant_type = 'authorization_code'; client_id = 'blazor-client'; code = $code; redirect_uri = $redirect; code_verifier = $verifier
    } -Form
    return $token.Json.access_token
}

function Get-Claims([string] $jwt) {
    $payload = $jwt.Split('.')[1].Replace('-', '+').Replace('_', '/')
    switch ($payload.Length % 4) { 2 { $payload += '==' } 3 { $payload += '=' } }
    return [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
}

function Wait-Until([scriptblock] $probe, [string] $what, [int] $seconds = 180) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        try { $value = & $probe; if ($value) { return $value } } catch { $last = "$_" }
        Start-Sleep -Seconds 2
    }
    throw "Timed out after $seconds s waiting for $what. $last"
}
