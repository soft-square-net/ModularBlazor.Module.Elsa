<#
.SYNOPSIS
  Gives the dev tenants the grants of the workflows' service subject, elsa-workflows.

.DESCRIPTION
  A service client's token names no tenant and carries no permissions: what it may do is a grant in each tenant's
  database, like a person's. In each tenant (acme, globex) this signs in as the tenant's administrator and adds the
  grants that are missing (POST /api/identity/grants). Idempotent. The app host runs it as the "seed-grants" resource
  once the gateway is up; it can also be run by hand against a running playground. DEVELOPMENT ONLY.
#>
param([string] $PlatformRoot = $env:PLAYGROUND_PLATFORM_ROOT)

. (Join-Path $PSScriptRoot 'lib.ps1')

foreach ($tenant in $Tenants) {
    $admin = Get-TenantAdmin $tenant $PlatformRoot
    # The first call may find the tenant or the identity service still starting.
    $token = Wait-Until { Get-UserToken $admin.Email $admin.Password $tenant } "$($admin.Email) to sign in"
    $url = $Gateway + 'api/identity/grants'
    # Wrapped in an object: an empty list is an answer, and Wait-Until reads an empty value as "not yet".
    $answer = Wait-Until {
        $r = Send GET "$($url)?userId=$WorkflowSubject" -token $token -tenant $tenant
        [pscustomobject]@{ Grants = $r.Json }
    } "the grants of tenant $tenant"
    $held = @($answer.Grants | Where-Object { $_ })
    $added = 0
    foreach ($permission in $WorkflowGrants) {
        if ($held | Where-Object { $_.permission -eq $permission -and $_.scope -eq 'tenant' -and -not $_.validTo }) { continue }
        Send POST $url @{ userId = $WorkflowSubject; permission = $permission; scope = 'tenant' } -token $token -tenant $tenant | Out-Null
        $added++
    }
    Write-Host "$tenant`: $WorkflowSubject holds $($WorkflowGrants -join ', ') ($added added)."
}
