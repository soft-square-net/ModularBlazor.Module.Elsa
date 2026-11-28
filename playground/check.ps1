<#
.SYNOPSIS
  The workflow check: a trivial workflow runs for a tenant, survives a restart, and another tenant cannot see it.

.DESCRIPTION
  -Phase All (default) does the whole check by itself: it builds and starts the playground, runs the first half, stops
  every process and container of the playground, starts it again, runs the second half and stops it. The data stays
  in the Docker volume, so the restart is a real one: new processes, new PostgreSQL and RabbitMQ containers.

  With a playground that is already running (dotnet run --project playground), use -Phase Start, restart it by hand,
  then -Phase Verify.

  Start:  in tenant acme, publishes a definition (write a line, wait for an event, write a line) and runs it; it
          suspends at the event. Tenant globex, whose administrator has every workflow permission there, cannot see
          or touch the definition or the instance. The service subject elsa-workflows may call what it needs.
  Verify: after the restart the definition and the suspended instance are still there, globex still cannot reach them
          (its event does not resume them either), and acme's event resumes the instance, which finishes.

  Exits with 1 when a check fails. Windows PowerShell 5.1 or PowerShell 7. DEVELOPMENT ONLY.
#>
param(
    [ValidateSet('All', 'Start', 'Verify')] [string] $Phase = 'All',
    # The ModularBlazor.Platform checkout (default: next to this repo, as 'Platform').
    [string] $PlatformRoot,
    # Where the Module.Organization, Module.Shifts, ... checkouts are (default: next to this repo).
    [string] $SiblingsRoot,
    # The elsa-workflows client secret, if the app host has one in its user-secrets.
    [string] $ClientSecret = $(if ($env:ELSA_CLIENT_SECRET) { $env:ELSA_CLIENT_SECRET } else { 'dev-elsa-workflows-secret' }),
    [int] $StartTimeoutSeconds = 600
)

. (Join-Path $PSScriptRoot 'lib.ps1')

$Api = $Gateway + 'api/elsa/elsa/api'
$Data = Join-Path $PSScriptRoot '.data'
$StateFile = Join-Path $Data 'check-state.json'
$script:failed = 0

function Step([string] $text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Check([bool] $condition, [string] $text) {
    if ($condition) { Write-Host "   ok   $text" -ForegroundColor Green }
    else { Write-Host "   FAIL $text" -ForegroundColor Red; $script:failed++ }
}

# ------------------------------------------------------------------ the playground's processes (-Phase All)

function Get-AppHost {
    Get-Process 'soft.square.net.Elsa.Playground' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($PSScriptRoot, [StringComparison]::OrdinalIgnoreCase) }
}

function Get-PlaygroundContainer { @(docker ps --quiet --filter 'volume=elsa-playground-data') }

function Test-Listening([int] $port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try { $client.Connect('127.0.0.1', $port); return $true } catch { return $false } finally { $client.Dispose() }
}

function Start-Playground([string] $log) {
    $arguments = @('run', '--project', "`"$PSScriptRoot`"", '--no-build')
    if ($SiblingsRoot) { $arguments += @('--', "`"--Playground:SiblingsRoot=$SiblingsRoot`"") }
    $options = @{ FilePath = 'dotnet'; ArgumentList = $arguments; PassThru = $true
        RedirectStandardOutput = (Join-Path $Data "$log.log"); RedirectStandardError = (Join-Path $Data "$log.err.log") }
    if ($env:OS -eq 'Windows_NT') { $options.WindowStyle = 'Hidden' }
    $script:runner = Start-Process @options
}

# Only the app host is killed: Aspire's orchestrator notices and stops the services and removes the containers.
function Stop-Playground {
    Get-AppHost | Stop-Process -Force
    Wait-Until {
        -not (Get-AppHost) -and (Get-PlaygroundContainer).Count -eq 0 -and
            -not (@(5080, 5081, 5082, 5090) | Where-Object { Test-Listening $_ })
    } 'the playground to stop' 180 | Out-Null
}

# Up: the gateway answers, Elsa serves both tenants, and the service subject has its grants (seed-grants).
function Wait-Playground {
    Wait-Until {
        if ($script:runner -and $script:runner.HasExited) { Write-Host "The app host exited; see $Data."; exit 1 }
        (Send GET ($Gateway + 'health') -AllowFailure).Status -eq 200
    } 'the gateway' $StartTimeoutSeconds | Out-Null
    foreach ($tenant in $Tenants) {
        $admin = Get-TenantAdmin $tenant $PlatformRoot
        $token = Wait-Until { Get-UserToken $admin.Email $admin.Password $tenant } "$($admin.Email) to sign in"
        Wait-Until { (Send GET "$Api/workflow-instances?page=0&pageSize=1" -token $token -tenant $tenant -AllowFailure).Status -eq 200 } "Elsa to serve $tenant" | Out-Null
    }
    $service = Get-ServiceToken
    foreach ($tenant in $Tenants) {
        Wait-Until { (Send GET ($Gateway + 'api/content/automation/accepted') -token $service -tenant $tenant -AllowFailure).Status -eq 200 } "the grants of $WorkflowSubject in $tenant" | Out-Null
    }
}

# ------------------------------------------------------------------ helpers

function Get-ServiceToken {
    New-Client
    return (Send POST ($Identity + 'connect/token') @{
        grant_type = 'client_credentials'; client_id = $WorkflowSubject; client_secret = $ClientSecret; scope = 'api'
    } -Form).Json.access_token
}

function Get-TenantTokens {
    $tokens = @{}
    foreach ($tenant in $Tenants) {
        $admin = Get-TenantAdmin $tenant $PlatformRoot
        $tokens[$tenant] = Get-UserToken $admin.Email $admin.Password $tenant
    }
    return $tokens
}

function Invoke-Elsa([string] $tenant, [string] $method, [string] $path, $body = $null) {
    Send $method "$Api/$path" $body -token $script:tokens[$tenant] -tenant $tenant -AllowFailure
}

function New-Text([string] $value) { @{ typeName = 'String'; expression = @{ type = 'Literal'; value = $value } } }

function Get-Instance([string] $tenant, [string] $definitionId) {
    $r = Invoke-Elsa $tenant GET "workflow-instances?definitionId=$definitionId"
    if ($r.Status -ne 200) { throw "Listing instances in $tenant -> $($r.Status): $($r.Text)" }
    return @($r.Json.items | Where-Object { $_ })
}

# Whether the activity ran in the instance, as the tenant sees it.
function Test-Ran([string] $tenant, [string] $instanceId, [string] $activityId) {
    $r = Invoke-Elsa $tenant GET "activity-executions/list?workflowInstanceId=$instanceId&activityNodeId=Workflow1:sequence:$activityId"
    return $r.Status -eq 200 -and @($r.Json.items | Where-Object { $_ }).Count -gt 0
}

function Test-OtherTenant($state) {
    $id = $state.definitionId; $instance = $state.instanceId
    $r = Invoke-Elsa globex GET "workflow-definitions?definitionIds=$id"
    Check ($r.Status -eq 200 -and $r.Json.totalCount -eq 0) "globex does not find acme's definition in its list ($($r.Status), $($r.Json.totalCount) found)"
    $r = Invoke-Elsa globex GET "workflow-definitions/by-definition-id/$($id)?versionOptions=Latest"
    Check ($r.Status -eq 404) "globex cannot read the definition by id ($($r.Status))"
    $r = Invoke-Elsa globex GET 'workflow-instances?page=0&pageSize=1000'
    Check ($r.Status -eq 200 -and -not ($r.Json.items | Where-Object { $_.id -eq $instance })) "acme's instance is not among globex's instances ($($r.Status))"
    $r = Invoke-Elsa globex GET "workflow-instances/$instance"
    Check ($r.Status -eq 404) "globex cannot read the instance by id ($($r.Status))"
    Check (-not (Test-Ran globex $instance 'before')) 'globex sees none of its activity executions'
    $r = Invoke-Elsa globex POST "workflow-definitions/$id/execute" @{}
    Check ($r.Status -eq 404) "globex cannot run the definition ($($r.Status))"
    $r = Invoke-Elsa globex DELETE "workflow-instances/$instance"
    Check ($r.Status -eq 404) "globex cannot delete the instance ($($r.Status))"
    $r = Invoke-Elsa globex DELETE "workflow-definitions/$id"
    Check ($r.Status -eq 404) "globex cannot delete the definition ($($r.Status))"

    # The same event, named and unnamed: neither may touch acme's instance.
    Invoke-Elsa globex POST "events/$($state.eventName)/trigger" @{ workflowInstanceId = $instance } | Out-Null
    Invoke-Elsa globex POST "events/$($state.eventName)/trigger" @{} | Out-Null
    Start-Sleep -Seconds 3
    $mine = @(Get-Instance acme $id)
    Check ($mine.Count -eq 1 -and $mine[0].subStatus -eq 'Suspended' -and -not (Test-Ran acme $instance 'after')) `
        "globex's events leave acme's instance suspended ($($mine[0].subStatus))"

    $r = Send GET "$Api/workflow-instances/$instance" -token $script:tokens['acme'] -tenant globex -AllowFailure
    Check ($r.Status -eq 403) "acme's token naming tenant globex is refused ($($r.Status))"
}

function Test-ServiceGrants {
    $service = Get-ServiceToken
    foreach ($tenant in $Tenants) {
        foreach ($path in 'api/content/automation/accepted', 'api/shifts/shifts/ended?since=2026-01-01T00:00:00Z&take=1') {
            $r = Send GET ($Gateway + $path) -token $service -tenant $tenant -AllowFailure
            Check ($r.Status -eq 200) "$WorkflowSubject may GET /$($path.Split('?')[0]) in $tenant ($($r.Status))"
        }
    }
    $r = Send GET "$Api/workflow-instances?page=0&pageSize=1" -token $service -tenant acme -AllowFailure
    Check ($r.Status -eq 403) "$WorkflowSubject holds no workflow permission, so Elsa's API refuses it ($($r.Status))"
}

# ------------------------------------------------------------------ the two halves

function Invoke-StartPhase {
    $script:tokens = Get-TenantTokens
    Step 'The grants of the service subject'
    Test-ServiceGrants

    Step 'A trivial workflow runs for tenant acme'
    $id = 'playground-check-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
    $state = [pscustomobject]@{ definitionId = $id; eventName = "$id-continue"; instanceId = $null }
    $root = @{ type = 'Elsa.Sequence'; version = 1; id = 'sequence'; activities = @(
            @{ type = 'Elsa.WriteLine'; version = 1; id = 'before'; text = (New-Text "$id started") },
            @{ type = 'Elsa.Event'; version = 1; id = 'wait'; eventName = (New-Text $state.eventName) },
            @{ type = 'Elsa.WriteLine'; version = 1; id = 'after'; text = (New-Text "$id finished") }) }
    $r = Invoke-Elsa acme POST 'workflow-definitions' @{ publish = $true; model = @{ definitionId = $id; name = $id; root = $root } }
    Check ($r.Status -eq 200) "published definition $id ($($r.Status)$(if ($r.Status -ne 200) { ": $($r.Text)" }))"
    $r = Invoke-Elsa acme POST "workflow-definitions/$id/execute" @{}
    Check ($r.Status -eq 200) "ran it ($($r.Status)$(if ($r.Status -ne 200) { ": $($r.Text)" }))"
    $instances = @(Get-Instance acme $id)
    Check ($instances.Count -eq 1 -and $instances[0].tenantId -eq 'acme' -and $instances[0].subStatus -eq 'Suspended') `
        "one instance, of tenant $($instances[0].tenantId), $($instances[0].subStatus) at the event"
    $state.instanceId = $instances[0].id
    Check ((Test-Ran acme $state.instanceId 'before') -and -not (Test-Ran acme $state.instanceId 'after')) 'it ran up to the event and no further'

    Step 'Tenant globex cannot see it'
    Test-OtherTenant $state

    New-Item -ItemType Directory -Force $Data | Out-Null
    $state | ConvertTo-Json | Set-Content $StateFile -Encoding UTF8
}

function Invoke-VerifyPhase {
    if (-not (Test-Path $StateFile)) { throw 'Run -Phase Start first.' }
    $state = Get-Content $StateFile -Raw | ConvertFrom-Json
    $script:tokens = Get-TenantTokens

    Step "It survived the restart (instance $($state.instanceId))"
    $r = Invoke-Elsa acme GET "workflow-definitions/by-definition-id/$($state.definitionId)?versionOptions=Published"
    Check ($r.Status -eq 200) "the published definition is there ($($r.Status))"
    $instances = @(Get-Instance acme $state.definitionId)
    Check ($instances.Count -eq 1 -and $instances[0].id -eq $state.instanceId -and $instances[0].subStatus -eq 'Suspended') `
        "the instance is there, still $($instances[0].subStatus)"

    Step 'Tenant globex still cannot see it'
    Test-OtherTenant $state

    Step 'Tenant acme resumes it'
    $r = Invoke-Elsa acme POST "events/$($state.eventName)/trigger" @{ workflowInstanceId = $state.instanceId }
    Check ($r.Status -lt 300) "sent the event ($($r.Status))"
    try {
        Wait-Until { @(Get-Instance acme $state.definitionId)[0].status -eq 'Finished' } 'the instance to finish' 60 | Out-Null
    } catch { Write-Host "   $_" }
    $instance = @(Get-Instance acme $state.definitionId)[0]
    Check ($instance.status -eq 'Finished' -and $instance.subStatus -eq 'Finished') "the instance finished ($($instance.status)/$($instance.subStatus))"
    Check (Test-Ran acme $state.instanceId 'after') 'it ran the step after the event'

    Step 'The grants of the service subject'
    Test-ServiceGrants
}

# ------------------------------------------------------------------ run

if ($PlatformRoot) { $PlatformRoot = (Resolve-Path $PlatformRoot).Path }
if ($SiblingsRoot) { $SiblingsRoot = (Resolve-Path $SiblingsRoot).Path }

if ($Phase -eq 'All') {
    if ((Get-AppHost) -or (Test-Listening 5080)) { throw 'Something already runs on port 5080: stop it first, or use -Phase Start and -Phase Verify.' }
    New-Item -ItemType Directory -Force $Data | Out-Null
    Step 'Build'
    $build = @('build', (Join-Path $PSScriptRoot 'Elsa.Playground.csproj'))
    if ($PlatformRoot) { $build += "-p:PlatformRoot=$($PlatformRoot.TrimEnd('\', '/'))/" }
    & dotnet @build | Select-Object -Last 4 | ForEach-Object { Write-Host "   $_" }
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
    try {
        Step 'Start the playground'
        Start-Playground 'check-first-run'
        Wait-Playground
        Invoke-StartPhase

        Step 'Restart: stop every process and container, start again'
        $before = Get-PlaygroundContainer
        Stop-Playground
        Write-Host '   stopped'
        Start-Playground 'check-second-run'
        Wait-Playground
        Check (-not (Get-PlaygroundContainer | Where-Object { $before -contains $_ })) 'PostgreSQL runs in a new container, on the same volume'

        Invoke-VerifyPhase
    }
    finally {
        Step 'Stop the playground'
        Stop-Playground
    }
}
elseif ($Phase -eq 'Start') { Wait-Playground; Invoke-StartPhase }
else { Wait-Playground; Invoke-VerifyPhase }

Write-Host ''
if ($script:failed -gt 0) { Write-Host "$script:failed check(s) failed." -ForegroundColor Red; exit 1 }
if ($Phase -eq 'Start') { Write-Host 'All checks passed. Restart the playground, then run check.ps1 -Phase Verify.' -ForegroundColor Green }
else { Write-Host 'All checks passed.' -ForegroundColor Green }
