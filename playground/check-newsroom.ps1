<#
.SYNOPSIS
  The newsroom check: a project approved in NewsRoom runs a temporary workflow of the newsroom activities.

.DESCRIPTION
  Runs against a playground that is already running, with NewsRoom loaded (dotnet run --project playground). As tenant
  acme it prepares what a project needs (a Section in Organization, a press and a category in Content, the post kind and
  the proposal form in DynamicFields), publishes a temporary workflow under the id the playground binds to approved
  projects, and approves a proposal. Everything after the approval is the real chain: NewsRoom publishes the event,
  RabbitMQ delivers it, Elsa's subscriber starts the workflow, and the activities call the other modules as
  elsa-workflows:

    Route to unit -> Ensure post in unit categories -> Assign in shift -> Deadline -> Wait for stage -> Desk review
    -> Publish -> Notify

  The editor's work (checking in, submitting, approving the post) is done here, and Content's events resume the
  workflow. Safe to run again on the same data. Exits with 1 when a check fails. DEVELOPMENT ONLY.
#>
param([string] $PlatformRoot, [int] $StartTimeoutSeconds = 600)

. (Join-Path $PSScriptRoot 'lib.ps1')
if ($PlatformRoot) { $PlatformRoot = (Resolve-Path $PlatformRoot).Path }
$script:failed = 0

function Step([string] $text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Check([bool] $condition, [string] $text) {
    if ($condition) { Write-Host "   ok   $text" -ForegroundColor Green }
    else { Write-Host "   FAIL $text" -ForegroundColor Red; $script:failed++ }
}

$Tenant = 'acme'
$Definition = 'playground-newsroom-project'   # AppHost.cs binds approved projects to it
$Category = '7b0f3f6e-0c1a-4a52-9d8e-1f0000000001'
$PressId = '7b0f3f6e-0c1a-4a52-9d8e-1f00000000a0'
$Central = $Gateway + 'api/organization/'

function Get-PlatformAdminToken {
    $settings = Join-Path $(if ($PlatformRoot) { $PlatformRoot } else { Join-Path $PSScriptRoot '..\..\Platform' }) 'src/WebApi/Identity/WebApi.Identity.Server/appsettings.Development.json'
    $user = (Get-Content $settings -Raw | ConvertFrom-Json).Identity.Seed.Users | Where-Object Email -eq 'admin@modular.local'
    return Get-UserToken $user.Email $user.Password $null
}

# A call as acme's administrator.
function Api([string] $method, [string] $path, $body = $null, [switch] $AllowFailure) {
    Send $method ($Gateway + "api/$path") $body -token $script:token -tenant $Tenant -AllowFailure:$AllowFailure
}

function Wait-Ok([string] $path, [string] $what) {
    return (Wait-Until { $r = Api GET $path -AllowFailure; if ($r.Status -eq 200) { $r } } $what $StartTimeoutSeconds)
}

Step 'The playground, with NewsRoom'
Wait-Until { (Send GET ($Gateway + 'health') -AllowFailure).Status -eq 200 } 'the gateway' $StartTimeoutSeconds | Out-Null
$adminAccount = Get-TenantAdmin $Tenant $PlatformRoot
$script:token = Wait-Until { Get-UserToken $adminAccount.Email $adminAccount.Password $Tenant } "$($adminAccount.Email) to sign in"
$adminSub = (Get-Claims $script:token).sub
$issuer = (Get-Claims $script:token).iss
Wait-Ok 'newsroom/projects' 'NewsRoom (is it built, and loaded?)' | Out-Null
Wait-Ok 'elsa/elsa/api/workflow-instances?page=0&pageSize=1' 'Elsa' | Out-Null
$platformAdmin = Get-PlatformAdminToken

Step 'A Section in Organization (central), seeded into tenant acme'
function Org([string] $method, [string] $path, $body = $null) { Send $method ($Central + $path) $body -token $platformAdmin }
$org = (Wait-Until { $r = Org GET 'orgs'; [pscustomobject]@{ List = $r.Json } } 'the central service').List | Where-Object key -eq 'elsa-playground'
if (-not $org) { $org = (Org POST 'orgs' @{ key = 'elsa-playground'; nameEn = 'Elsa Playground'; nameAr = 'تجربة' }).Json }
$orgPath = "orgs/$($org.id)"
function Find-OrCreate-Unit($parentId, [string] $type, [string] $name, $categories = $null) {
    $unit = @((Org GET "$orgPath/units").Json) | Where-Object { $_.parentId -eq $parentId -and $_.typeKey -eq $type } | Select-Object -First 1
    if ($unit) { return $unit }
    $body = @{ parentId = $parentId; typeKey = $type; nameEn = $name; nameAr = $name }
    if ($categories) { $body.categories = @($categories) }
    return (Org POST "$orgPath/units" $body).Json
}
$sector = Find-OrCreate-Unit $org.rootUnitId 'sector' 'News Sector'
$department = Find-OrCreate-Unit $sector.id 'department' 'Newsdesk'
$section = Find-OrCreate-Unit $department.id 'section' 'Local News' $Category
# A shift's rota holds members of the unit only.
$person = @((Org GET "$orgPath/persons").Json) | Where-Object subject -eq $adminSub | Select-Object -First 1
if (-not $person) { $person = (Org POST "$orgPath/persons" @{ issuer = $issuer; subject = $adminSub; displayName = 'Acme Admin' }).Json }
if (-not (@((Org GET "$orgPath/memberships").Json) | Where-Object { $_.personId -eq $person.id -and $_.unitId -eq $section.id })) {
    Org POST "$orgPath/memberships" @{ personId = $person.id; unitId = $section.id; primary = $true; from = [DateTimeOffset]::UtcNow.AddDays(-1).ToString('o') } | Out-Null
}
$seed = @((Org GET "$orgPath/seeds").Json) | Where-Object tenantId -eq $Tenant | Select-Object -First 1
if (-not $seed) {
    $seed = (Org POST "$orgPath/seeds" @{
        rootUnitId = $department.id; deploymentId = 'elsa-playground'; tenantId = $Tenant; name = 'Acme'; hosts = @("$Tenant.localhost")
        databaseServer = 'tenant-data'; databaseName = "tenant-$Tenant" }).Json
}
$units = (Wait-Until {
    $r = Api GET 'organization/local/units' -AllowFailure
    if ($r.Status -eq 200 -and @($r.Json | Where-Object id -eq $section.id).Count -gt 0) { $r }
} "acme's replica to hold the Section" 180).Json
Check ($true) "acme holds the Section $($section.id) ($(@($units).Count) units)"

Step 'A press and the category in Content; the post kind and the proposal form in DynamicFields'
$r = Api POST 'content/presses' @{ id = $PressId; name = 'Elsa Playground' } -AllowFailure
if ($r.Status -ge 400 -and $r.Status -ne 409) { throw "Creating the press -> $($r.Status): $($r.Text)" }
$r = Api POST 'content/categories' @{ id = $Category; pressId = $PressId; nameEn = 'Local'; nameAr = 'محلي'; name = 'Local' } -AllowFailure
if ($r.Status -ge 400 -and $r.Status -ne 409) { throw "Creating the category -> $($r.Status): $($r.Text)" }
$r = Api POST 'dynamicfields/catalog/starter-fields/install' @{ keys = @('post.news') } -AllowFailure
Check ($r.Status -lt 300) "installed the post kind post.news ($($r.Status)$(if ($r.Status -ge 300) { ": $($r.Text)" }))"
if ((Api GET 'dynamicfields/fields/proposal.default/versions/0' -AllowFailure).Status -ne 200) {
    $form = @{ key = 'proposal.default'; draft = @{
            title = 'Story proposal'; schemaType = 'CreativeWork'; canBeRoot = $false; style = @(); children = @()
            properties = @(
                @{ key = 'title'; label = 'Title'; kind = 'Text'; required = $true; schemaProperty = 'headline'; maxLength = 300; translatable = $false },
                @{ key = 'body'; label = 'Body'; kind = 'RichText'; required = $false; schemaProperty = 'text'; translatable = $false }) } }
    $created = Api POST 'dynamicfields/fields' $form
    Api POST 'dynamicfields/fields/proposal.default/publish' @{ version = $created.Json.version } | Out-Null
}

# Content approves a post only with all its kind's required fields, and post.news requires an uploaded image: the
# projects here are of a kind that requires only the headline NewsRoom fills in.
$PostKind = 'post.elsacheck'
if ((Api GET "dynamicfields/fields/$PostKind/versions/0" -AllowFailure).Status -ne 200) {
    $kind = @{ key = $PostKind; draft = @{
            title = 'Elsa check post'; schemaType = 'NewsArticle'; canBeRoot = $true; style = @(); children = @(); urlPrefix = 'elsa-check'
            properties = @(@{ key = 'headline'; label = 'Headline'; kind = 'Text'; required = $true; schemaProperty = 'headline'; maxLength = 300 }) } }
    $created = Api POST 'dynamicfields/fields' $kind
    Api POST "dynamicfields/fields/$PostKind/publish" @{ version = $created.Json.version } | Out-Null
}
# Content keeps the list of kinds for a short while.
Wait-Until { (Api GET 'content/kinds').Text -like "*$PostKind*" } "Content to list the post kind $PostKind" 150 | Out-Null

Step 'A proposal in NewsRoom'
$proposal = (Api POST 'newsroom/proposals' @{ unitId = $section.id; values = @{ title = "Elsa check $([guid]::NewGuid().ToString('N').Substring(0, 8))" }; suggestedCategories = @($Category) }).Json
$proposal = (Api POST "newsroom/proposals/$($proposal.id)/submit" @{ version = $proposal.version }).Json
Check ($proposal.state -eq 'Submitted') "proposal $($proposal.id) is $($proposal.state)"

Step 'A content item to produce, and a running shift with a writer checked in'
# Shifts reads the unit's members as the caller, which takes org.read on the unit and the units above it.
$held = @((Api GET "identity/grants?userId=$adminSub").Json) | Where-Object { $_.permission -eq 'org.read' -and $_.scope -eq 'tenant' }
if (-not $held) { Api POST 'identity/grants' @{ userId = $adminSub; permission = 'org.read'; scope = 'tenant' } | Out-Null }
$values = @{ nodeId = [guid]::NewGuid().ToString(); schemaId = 'post.news'; schemaVersion = 1; content = @{ }; style = @{ }; children = @(); postKind = 'post.news' }
$item = (Api POST 'content/contents' @{ categoryId = $Category; values = $values }).Json
Check ($item.stage -ne $null -and -not $item.assigneeId) "content $($item.id) is $($item.stage), assigned to nobody"
# An earlier run's shift may still have the writer checked in: the assignment must start with nobody there.
$from = [Uri]::EscapeDataString([DateTimeOffset]::UtcNow.AddDays(-2).ToString('o'))
$to = [Uri]::EscapeDataString([DateTimeOffset]::UtcNow.AddDays(1).ToString('o'))
foreach ($old in @((Api GET "shifts/shifts?from=$from&to=$to").Json) | Where-Object { $_.unitId -eq $section.id -and $_.running }) {
    if ($old.rota | Where-Object { $_.userId -eq $adminSub -and $_.checkedInAt -and -not $_.checkedOutAt }) {
        Api POST "shifts/shifts/$($old.id)/check-out" | Out-Null
    }
}
# The role's permissions are held while its shift runs: Content's desk accepts and assigns a post, and the editor reviews
# it, only with these held in the shift.
$role = @{ key = 'lead-editor'; nameEn = 'Lead editor'; nameAr = 'كبير المحررين'; permissions = @('content.accept', 'content.assign', 'content.review'); isLead = $true }
$r = Api POST 'shifts/roles' $role -AllowFailure
if ($r.Status -ge 400 -and $r.Status -ne 409) { throw "Creating the role -> $($r.Status): $($r.Text)" }
$shift = (Api POST 'shifts/shifts' @{ unitId = $section.id; startsAt = [DateTimeOffset]::UtcNow.ToString('o')
        endsAt = [DateTimeOffset]::UtcNow.AddHours(2).ToString('o'); rota = @(@{ userId = $adminSub; roleKey = 'lead-editor' }) }).Json
# Publishing needs the unit's members, which acme's replica may still be pulling.
Api POST "shifts/shifts/$($shift.id)/publish" | Out-Null

Step 'A temporary workflow of the newsroom activities, under the id approved projects run'
$Elsa = 'elsa/elsa/api'
function Text([string] $value) { @{ typeName = 'String'; expression = @{ type = 'Literal'; value = $value } } }
function Json($value) { @{ typeName = 'Object'; expression = @{ type = 'Object'; value = (ConvertTo-Json -InputObject $value -Depth 5 -Compress) } } }
function Activity([string] $type, [string] $id, [hashtable] $inputs = @{}) {
    $node = @{ type = "SoftSquare.Newsroom.$type"; version = 1; id = $id }
    foreach ($name in $inputs.Keys) { $node[$name] = $inputs[$name] }
    return $node
}
# The stage the check can reach with an empty content item: submitting one takes all its kind's fields.
$produced = @('in-progress', 'submitted', 'in-review', 'accepted')
$root = @{ type = 'Elsa.Sequence'; version = 1; id = 'sequence'; activities = @(
        (Activity 'RouteToUnit' 'route' @{ contentKind = (Text 'text'); rules = (Json @(@{ contentKind = 'text'; unitId = $section.id })) }),
        (Activity 'EnsurePostInUnitCategories' 'ensure'),
        (Activity 'AssignInShift' 'assign' @{ contentId = (Text $item.id); unitId = (Text $section.id); roleKey = (Text 'lead-editor') }),
        (Activity 'Deadline' 'deadline' @{ contentId = (Text $item.id); stages = (Json $produced); dueIn = @{ typeName = 'TimeSpan'; expression = @{ type = 'Literal'; value = '00:00:05' } } }),
        (Activity 'WaitForStage' 'stage' @{ contentId = (Text $item.id); stages = (Json $produced) }),
        (Activity 'DeskReview' 'desk'),
        (Activity 'PublishPost' 'publish'),
        (Activity 'Notify' 'notify' @{ userId = (Text $adminSub); message = (Text 'The story is out.') })) }
$r = Api POST "$Elsa/workflow-definitions" @{ publish = $true; model = @{ definitionId = $Definition; name = 'Playground newsroom check'; root = $root } } -AllowFailure
Check ($r.Status -eq 200) "published $Definition ($($r.Status)$(if ($r.Status -ne 200) { ": $($r.Text)" }))"

Step 'The moderator approves the proposal; the event starts the workflow'
$r = Api POST "newsroom/proposals/$($proposal.id)/approve" @{ version = $proposal.version; projects = @(@{ postKind = $PostKind; sectionId = $section.id; categories = @($Category) }) }
$project = (Wait-Until {
    $found = @((Api GET "newsroom/projects?proposalId=$($proposal.id)").Json.items) | Where-Object postId | Select-Object -First 1
    if ($found) { $found }
} 'the project and its draft post' 60)
Check ($true) "project $($project.id) has the draft post $($project.postId)"

function Get-Instance { @((Api GET "$Elsa/workflow-instances?correlationId=newsroom-project:$($project.id)").Json.items | Where-Object { $_ }) }
function Get-ProjectState { (Api GET "newsroom/projects/$($project.id)").Json.state }
function Test-Reached([string] $activity) {
    $r = Api GET "$Elsa/activity-executions/list?workflowInstanceId=$($script:instanceId)&activityNodeId=Workflow1:sequence:$activity" -AllowFailure
    return $r.Status -eq 200 -and @($r.Json.items | Where-Object { $_ }).Count -gt 0
}
# Waits for something the workflow should do; a failed check, not an exception, when it does not.
function Expect([scriptblock] $probe, [string] $text, [int] $seconds = 30) {
    $started = Get-Date
    try { Wait-Until $probe $text $seconds | Out-Null; Check $true "$text ($([int]((Get-Date) - $started).TotalSeconds) s)" }
    catch { Check $false "$text. $_"; $script:instance = Get-Instance; $script:instance | Format-List status, subStatus, incidentCount | Out-Host; throw 'The workflow did not go on.' }
}

Expect { @(Get-Instance).Count -eq 1 } 'one workflow instance started for the project' 60
$script:instanceId = (Get-Instance)[0].id
Expect { Test-Reached 'assign' } 'it routed, filed the post and came to the assignment'
Expect { (Api GET "content/posts/$($project.postId)").Json.unitId -eq $section.id } 'the post is filed in the Section'
Expect { (Get-ProjectState) -eq 'InProduction' } 'NewsRoom has the project InProduction'
Check (-not (Api GET "content/contents/$($item.id)").Json.assigneeId) 'with nobody checked in, the content is not assigned'

Step 'The writer checks in; the shift event resumes the assignment'
Api POST "shifts/shifts/$($shift.id)/check-in" | Out-Null
Expect { (Api GET "content/contents/$($item.id)").Json.assigneeId -eq $adminSub } 'the content is assigned to the writer on shift' 45

Step 'Nobody submits in time: the deadline fires and the workflow waits on'
Expect { Test-Reached 'stage' } 'the deadline passed and escalated'

Step 'The writer starts the work; Content''s event resumes the workflow'
$current = (Api GET "content/contents/$($item.id)").Json
$r = Api POST "content/contents/$($item.id)/act" @{ action = 'start'; version = $current.version } -AllowFailure
Check ($r.Status -eq 200) "the writer started the content ($($r.Status)$(if ($r.Status -ne 200) { ": $($r.Text)" }))"
$current = $r.Json
Expect { Test-Reached 'desk' } "the content is $($current.stage), and the workflow came to the desk" 20
Expect { (Get-ProjectState) -eq 'ReadyForDesk' } 'NewsRoom has the project ReadyForDesk'

Step 'The desk approves the post; the workflow publishes it'
function Move-Post([string] $action, [hashtable] $more = @{}) {
    $post = (Api GET "content/posts/$($project.postId)").Json
    $r = Api POST "content/posts/$($project.postId)/$action" (@{ version = $post.version } + $more) -AllowFailure
    Check ($r.Status -eq 200) "desk: $action ($($r.Status)$(if ($r.Status -ne 200) { ": $($r.Text)" }))"
}
Move-Post 'submit'
Move-Post 'accept' @{ shiftId = "$($shift.id)" }
Move-Post 'assign' @{ shiftId = "$($shift.id)"; assigneeId = $adminSub }
Move-Post 'approve'
Expect { (Api GET "content/posts/$($project.postId)").Json.state -eq 'Published' } 'elsa-workflows published the post'
Expect { (Get-Instance)[0].status -eq 'Finished' } 'the workflow finished'
$finished = (Get-Instance)[0]
Check ($finished.subStatus -eq 'Finished' -and $finished.incidentCount -eq 0) "with no incidents ($($finished.subStatus), $($finished.incidentCount))"
Expect { (Get-ProjectState) -eq 'Published' } 'NewsRoom has the project Published'
Check (@(Get-Instance).Count -eq 1) 'there is still one instance for the project'

# The workflow was temporary.
Api DELETE "$Elsa/workflow-definitions/$Definition" -AllowFailure | Out-Null

Write-Host ''
if ($script:failed -gt 0) { Write-Host "$script:failed check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All checks passed.' -ForegroundColor Green
