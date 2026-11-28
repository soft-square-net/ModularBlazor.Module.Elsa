<#
.SYNOPSIS
  The newsroom check: projects approved in NewsRoom run the default newsroom workflows.

.DESCRIPTION
  Runs against a playground that is already running, with NewsRoom loaded (dotnet run --project playground). As tenant
  acme it prepares what a project needs (a Section in Organization, a press and a category in Content, the post kind and
  the proposal form in DynamicFields, a shift, a routing rule in NewsRoom). Everything after an approval is the real
  chain: NewsRoom publishes the event, RabbitMQ delivers it, Elsa's subscriber starts the workflow, and the activities
  call the other modules as elsa-workflows. It checks:

    - no binding: no workflow, a setup error, and a dry run that says why; the binding's event then starts it;
    - newsroom-post and its newsroom-content sub-workflow, from filing to publication, with one return by the desk;
    - a dry run that returns the steps and changes nothing;
    - the workflow assigns the reviewer, and again after Content returns the review of a reviewer who left the shift;
    - a killed project: the workflow and its sub-workflow are cancelled;
    - without a workflow: Content publishes a due post by itself, and killing a scheduled project unschedules its post.

  The people's work (checking in, writing, the desk; the review as editor@modular.local) is done here, and the modules' events resume the workflows. Safe
  to run again on the same data. Exits with 1 when a check fails. DEVELOPMENT ONLY.
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
# The reviewer: the workflow never gives a post to its owner, and the administrator owns NewsRoom's draft posts. Erin
# is an ordinary member of acme, who can review only while a shift gives her the right.
$platformSettings = Join-Path $(if ($PlatformRoot) { $PlatformRoot } else { Join-Path $PSScriptRoot '..\..\Platform' }) 'src/WebApi/Identity/WebApi.Identity.Server/appsettings.Development.json'
$erin = (Get-Content $platformSettings -Raw | ConvertFrom-Json).Identity.Seed.Users | Where-Object Email -eq 'editor@modular.local'
$reviewerSub = (Get-Claims (Get-UserToken $erin.Email $erin.Password $Tenant)).sub
# A shift's rota holds members of the unit only.
foreach ($who in @(@{ sub = $adminSub; name = 'Acme Admin' }, @{ sub = $reviewerSub; name = 'Erin Editor' })) {
    $person = @((Org GET "$orgPath/persons").Json) | Where-Object subject -eq $who.sub | Select-Object -First 1
    if (-not $person) { $person = (Org POST "$orgPath/persons" @{ issuer = $issuer; subject = $who.sub; displayName = $who.name }).Json }
    if (-not (@((Org GET "$orgPath/memberships").Json) | Where-Object { $_.personId -eq $person.id -and $_.unitId -eq $section.id })) {
        Org POST "$orgPath/memberships" @{ personId = $person.id; unitId = $section.id; primary = $true; from = [DateTimeOffset]::UtcNow.AddDays(-1).ToString('o') } | Out-Null
    }
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
# projects here are of a kind that requires only the headline NewsRoom fills in, and takes lines of text as contents.
$PostKind = 'post.elsatext'
if ((Api GET "dynamicfields/fields/$PostKind/versions/0" -AllowFailure).Status -ne 200) {
    $kind = @{ key = $PostKind; draft = @{
            title = 'Elsa check post'; schemaType = 'NewsArticle'; canBeRoot = $true; style = @(); children = @(@{ fieldKey = 'block.text'; version = 1; min = 0 }); urlPrefix = 'elsa-text'
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

Step 'A running shift with a writer and a reviewer, not yet checked in'
# Shifts reads the unit's members as the caller, which takes org.read on the unit and the units above it.
# Content's approval has DynamicFields check the post as the caller, so the reviewer needs to read DynamicFields.
foreach ($need in @(@{ user = $adminSub; permission = 'org.read' }, @{ user = $reviewerSub; permission = 'dynamicfields.read' })) {
    $held = @((Api GET "identity/grants?userId=$($need.user)").Json) | Where-Object { $_.permission -eq $need.permission -and $_.scope -eq 'tenant' }
    if (-not $held) { Api POST 'identity/grants' @{ userId = $need.user; permission = $need.permission; scope = 'tenant' } | Out-Null }
}
# An earlier run's shift may still have the writer checked in: the assignment must start with nobody there.
$from = [Uri]::EscapeDataString([DateTimeOffset]::UtcNow.AddDays(-2).ToString('o'))
$to = [Uri]::EscapeDataString([DateTimeOffset]::UtcNow.AddDays(1).ToString('o'))
foreach ($old in @((Api GET "shifts/shifts?from=$from&to=$to").Json) | Where-Object { $_.unitId -eq $section.id -and $_.running }) {
    if ($old.rota | Where-Object { $_.userId -in $adminSub, $reviewerSub -and $_.checkedInAt -and -not $_.checkedOutAt }) {
        Api POST "shifts/shifts/$($old.id)/check-out" | Out-Null
    }
}
# AppHost.cs makes this the role that produces contents. The reviewers' role is Shifts' own "editor" (content.review).
# A role's permissions are held while its shift runs.
$role = @{ key = 'lead-editor'; nameEn = 'Lead editor'; nameAr = 'كبير المحررين'; permissions = @('content.accept', 'content.assign', 'content.review'); isLead = $true }
$r = Api POST 'shifts/roles' $role -AllowFailure
if ($r.Status -ge 400 -and $r.Status -ne 409) { throw "Creating the role -> $($r.Status): $($r.Text)" }
function New-Shift {
    $new = (Api POST 'shifts/shifts' @{ unitId = $section.id; startsAt = [DateTimeOffset]::UtcNow.ToString('o')
            endsAt = [DateTimeOffset]::UtcNow.AddHours(2).ToString('o')
            rota = @(@{ userId = $adminSub; roleKey = 'lead-editor' }, @{ userId = $reviewerSub; roleKey = 'editor' }) }).Json
    # Publishing needs the unit's members, which acme's replica may still be pulling.
    Api POST "shifts/shifts/$($new.id)/publish" | Out-Null
    return $new
}
function Enter-Shift($which, [string] $user) { Api POST "shifts/shifts/$($which.id)/members/$user/check-in" | Out-Null }
$shift = New-Shift

Step 'NewsRoom: a routing rule for the content kind, and no binding for the post kind yet'
$ContentKind = 'block.text'   # its one field is all that submitting such a content takes
$r = Api POST 'newsroom/routing-rules' @{ contentKind = $ContentKind; unitId = $section.id } -AllowFailure
$rule = @((Api GET 'newsroom/routing-rules').Json) | Where-Object contentKind -eq $ContentKind | Select-Object -First 1
if ($rule -and $rule.unitId -ne $section.id) {
    Api PUT "newsroom/routing-rules/$($rule.id)" @{ version = $rule.version; contentKind = $ContentKind; unitId = $section.id } | Out-Null
}
Check ($null -ne $rule) "a routing rule sends $ContentKind to the Section ($($r.Status))"
foreach ($old in @((Api GET "newsroom/bindings?kindType=post&kind=$PostKind").Json) | Where-Object { $_ }) {
    Api DELETE "newsroom/bindings/$($old.id)?version=$($old.version)" | Out-Null
}

$Elsa = 'elsa/elsa/api'
function Get-Instances($project) { @((Api GET "$Elsa/workflow-instances?correlationId=newsroom-project:$($project.id)").Json.items | Where-Object { $_ }) }
function Get-Project($project) { (Api GET "newsroom/projects/$($project.id)").Json }
# Waits for something the workflow should do; a failed check, not an exception, when it does not.
function Expect([scriptblock] $probe, [string] $text, [int] $seconds = 30) {
    $started = Get-Date
    try { Wait-Until $probe $text $seconds | Out-Null; Check $true "$text ($([int]((Get-Date) - $started).TotalSeconds) s)" }
    catch { Check $false "$text. $_"; if ($script:project) { Get-Instances $script:project | Format-List definitionId, status, subStatus, incidentCount | Out-Host }; throw 'The workflow did not go on.' }
}

# Approves the proposal into one project, and attaches one content, which the post requires, to the project's draft post.
function Approve-Proposal($proposal) {
    Api POST "newsroom/proposals/$($proposal.id)/approve" @{ version = $proposal.version; projects = @(@{ postKind = $PostKind; sectionId = $section.id; categories = @($Category) }) } | Out-Null
    return (Wait-Until {
        $found = @((Api GET "newsroom/projects?proposalId=$($proposal.id)").Json.items) | Where-Object postId | Select-Object -First 1
        if ($found) { $found }
    } 'the project and its draft post' 60)
}
function Add-Content($project) {
    $post = (Api GET "content/posts/$($project.postId)").Json
    $values = @{ nodeId = [guid]::NewGuid().ToString(); schemaId = $ContentKind; schemaVersion = 1; content = @{ text = 'Elsa check content' }; style = @{ }; children = @(); postKind = $ContentKind }
    return (Api POST 'content/contents' @{ categoryId = $Category; values = $values; postId = $project.postId; postVersion = $post.version; required = $true }).Json
}
function Invoke-DryRun($project) { Api POST 'elsa/newsroom/dry-run' @{ postId = $project.postId; unitId = $section.id } -AllowFailure }

Step 'The moderator approves the proposal before any workflow is bound'
$script:project = Approve-Proposal $proposal
Check ($true) "project $($project.id) has the draft post $($project.postId)"
$item = Add-Content $project
Check ($item.stage -ne $null -and -not $item.assigneeId) "the post has content $($item.id) ($($item.fieldKey)), $($item.stage), assigned to nobody"
Start-Sleep -Seconds 8
Check (@(Get-Instances $project).Count -eq 0) 'no workflow started'
Check ([bool](Get-Project $project).setupError) "NewsRoom reports the setup error: $((Get-Project $project).setupError)"
$r = Invoke-DryRun $project
Check ($r.Status -eq 200 -and @($r.Json.problems).Count -gt 0 -and -not $r.Json.workflow) "a dry run says why ($($r.Status): $(@($r.Json.problems) -join ' '))"

Step 'The default workflow is bound; NewsRoom''s event starts it'
$r = Api POST 'newsroom/bindings' @{ kindType = 'post'; kind = $PostKind; unitId = $section.id; definitionId = 'newsroom-post' } -AllowFailure
Check ($r.Status -eq 201) "bound $PostKind to newsroom-post ($($r.Status)$(if ($r.Status -ne 201) { ": $($r.Text)" }))"
Expect { @(Get-Instances $project).Count -eq 2 } 'the main workflow and the content''s sub-workflow started' 60
$instances = Get-Instances $project
Check ((@($instances.definitionId | Sort-Object) -join ',') -eq 'newsroom-content,newsroom-post') "they are $(@($instances.definitionId) -join ' and ')"
$mainId = ($instances | Where-Object definitionId -eq 'newsroom-post').id
Expect { (Api GET "content/posts/$($project.postId)").Json.unitId -eq $section.id } 'the post is filed in the Section'
Expect { (Get-Project $project).state -eq 'InProduction' } 'NewsRoom has the project InProduction'
Expect { (Api GET "content/contents/$($item.id)").Json.unitId -eq $section.id } 'the content went to the unit its kind is routed to'
Check (-not (Api GET "content/contents/$($item.id)").Json.assigneeId) 'with nobody checked in, the content is not assigned'

Step 'A dry run of the bound workflow changes nothing'
$r = Invoke-DryRun $project
$steps = @($r.Json.workflow.steps.activityId)
Check ($r.Status -eq 200 -and ($steps -join ',') -eq 'ensure,produce,desk,publish,announce') "the steps are $($steps -join ', ') ($($r.Status)$(if ($r.Status -ne 200) { ": $($r.Text)" }))"
$sub = @($r.Json.workflow.steps | Where-Object activityId -eq 'produce').steps
Check (@($sub.steps.activityId) -join ',' -eq 'file,work') "and the content's: $(@($sub.steps.activityId) -join ', ')"
Check (@(Get-Instances $project).Count -eq 2 -and -not (Api GET "content/contents/$($item.id)").Json.assigneeId) 'no instance and no assignment came of it'

Step 'The writer checks in; the shift event resumes the sub-workflow'
Api POST "shifts/shifts/$($shift.id)/check-in" | Out-Null
Expect { (Api GET "content/contents/$($item.id)").Json.assigneeId -eq $adminSub } 'the content is assigned to the writer on shift' 45

Step 'The content is written and accepted; Content''s events resume both workflows'
foreach ($action in 'start', 'submit', 'review', 'accept') {
    $current = (Api GET "content/contents/$($item.id)").Json
    if ($current.stage -eq 'accepted') { break }
    $r = Api POST "content/contents/$($item.id)/act" @{ action = $action; version = $current.version } -AllowFailure
    Write-Host "        content '$action' -> $($r.Status) $(if ($r.Status -eq 200) { $r.Json.stage } else { $r.Text })"
}
Check ((Api GET "content/contents/$($item.id)").Json.stage -eq 'accepted') 'the content is accepted'
Expect { (Get-Instances $project | Where-Object definitionId -eq 'newsroom-content').status -eq 'Finished' } 'the sub-workflow finished'
Expect { (Get-Project $project).state -eq 'ReadyForDesk' } 'NewsRoom has the project ReadyForDesk'

Step 'The post is submitted: the workflow accepts it and assigns the reviewer'
function Move-Post([string] $action, [hashtable] $more = @{}, [string] $by = $script:token) {
    $post = (Api GET "content/posts/$($project.postId)").Json
    $r = Send POST ($Gateway + "api/content/posts/$($project.postId)/$action") (@{ version = $post.version } + $more) -token $by -tenant $Tenant -AllowFailure
    Check ($r.Status -eq 200) "desk: $action ($($r.Status)$(if ($r.Status -ne 200) { ": $($r.Text)" }))"
}
function Get-PostState { (Api GET "content/posts/$($project.postId)").Json.state }
function Get-ReviewerToken { Get-UserToken $erin.Email $erin.Password $Tenant }
Enter-Shift $shift $reviewerSub
Move-Post 'submit'
Expect { (Get-PostState) -eq 'InReview' } 'the workflow accepted the post and assigned the reviewer on shift' 90

Step 'The reviewer leaves the shift: Content returns the review, and the workflow assigns it again on the next shift'
Api POST "shifts/shifts/$($shift.id)/members/$reviewerSub/check-out" | Out-Null
Expect { (Get-PostState) -eq 'Accepted' } 'Content returned the review to the queue' 120
$next = New-Shift
Enter-Shift $next $reviewerSub
Expect { (Get-PostState) -eq 'InReview' } 'the workflow assigned the review again' 120

Step 'The desk returns the post once, then approves it; the workflow publishes it'
Move-Post 'return' @{ note = 'Once more.' }
Expect { (Get-Project $project).state -eq 'InProduction' } 'the returned post is in production again' 45
Move-Post 'submit'
Expect { (Get-Project $project).state -eq 'ReadyForDesk' } 'and back at the desk once submitted' 45
Expect { (Get-PostState) -eq 'InReview' } 'with a reviewer again' 90
Move-Post 'approve' -by (Get-ReviewerToken)
Expect { (Get-PostState) -eq 'Published' } 'elsa-workflows published the post' 45
Expect { @(Get-Instances $project | Where-Object status -ne 'Finished').Count -eq 0 } 'both workflows finished' 45
$finished = Get-Instances $project
Check (@($finished | Where-Object { $_.subStatus -ne 'Finished' -or $_.incidentCount -ne 0 }).Count -eq 0) 'with no incidents'
Expect { (Get-Project $project).state -eq 'Published' } 'NewsRoom has the project Published'
Check (@(Get-Instances $project).Count -eq 2) 'there are still two instances for the project'

Step 'A second project is killed while its content waits: both workflows are cancelled'
Api POST "shifts/shifts/$($shift.id)/check-out" | Out-Null
Api POST "shifts/shifts/$($next.id)/check-out" | Out-Null
$second = (Api POST 'newsroom/proposals' @{ unitId = $section.id; values = @{ title = "Elsa check $([guid]::NewGuid().ToString('N').Substring(0, 8))" }; suggestedCategories = @($Category) }).Json
$second = (Api POST "newsroom/proposals/$($second.id)/submit" @{ version = $second.version }).Json
$script:project = Approve-Proposal $second
Expect { @(Get-Instances $project).Count -eq 1 } 'a binding that exists starts the workflow on approval' 60
$null = Add-Content $project
# The post gained a content: its event makes the main workflow start the sub-workflow.
Expect { @(Get-Instances $project).Count -eq 2 } 'the added content gets its sub-workflow' 60
$r = Api POST "newsroom/projects/$($project.id)/kill" @{ version = (Get-Project $project).version; reason = 'Elsa check' } -AllowFailure
Check ($r.Status -lt 300) "killed the project ($($r.Status)$(if ($r.Status -ge 300) { ": $($r.Text)" }))"
Expect { @(Get-Instances $project | Where-Object subStatus -ne 'Cancelled').Count -eq 0 } 'both workflows are cancelled' 45

Step 'Posts scheduled by hand, for projects without a workflow'
foreach ($old in @((Api GET "newsroom/bindings?kindType=post&kind=$PostKind").Json) | Where-Object { $_ }) {
    Api DELETE "newsroom/bindings/$($old.id)?version=$($old.version)" | Out-Null
}
$desk = New-Shift
Enter-Shift $desk $reviewerSub
# An approved project whose post a person takes to Scheduled: no workflow assigns or publishes anything.
function New-ScheduledProject([int] $inSeconds) {
    $proposal = (Api POST 'newsroom/proposals' @{ unitId = $section.id; values = @{ title = "Elsa check $([guid]::NewGuid().ToString('N').Substring(0, 8))" }; suggestedCategories = @($Category) }).Json
    $proposal = (Api POST "newsroom/proposals/$($proposal.id)/submit" @{ version = $proposal.version }).Json
    $script:project = Approve-Proposal $proposal
    $post = (Api GET "content/posts/$($project.postId)").Json
    Api PUT "content/posts/$($project.postId)/unit" @{ version = $post.version; unitId = $section.id } | Out-Null
    Move-Post 'submit' | Out-Host
    Move-Post 'accept' | Out-Host
    Move-Post 'assign' @{ assigneeId = $reviewerSub } | Out-Host
    Move-Post 'approve' -by (Get-ReviewerToken) | Out-Host
    Move-Post 'schedule' @{ publishAt = [DateTimeOffset]::UtcNow.AddSeconds($inSeconds).ToString('o') } | Out-Host
    Check ((Get-PostState) -eq 'Scheduled') "post $($project.postId) is scheduled in $inSeconds s"
    return $script:project
}
$script:project = New-ScheduledProject 3600
$r = Api POST "newsroom/projects/$($project.id)/kill" @{ version = (Get-Project $project).version; reason = 'Elsa check' } -AllowFailure
Check ($r.Status -lt 300) "killed the scheduled project ($($r.Status)$(if ($r.Status -ge 300) { ": $($r.Text)" }))"
Expect { $p = (Api GET "content/posts/$($project.postId)").Json; $p.state -eq 'Approved' -and -not $p.scheduledFor } 'elsa-workflows unscheduled its post' 45
Check ((Get-Project $project).state -eq 'Killed') 'the project stays killed'
Check (@(Get-Instances $project).Count -eq 0) 'and no workflow ran for it'
# Last: it waits for Content's clock.
$script:project = New-ScheduledProject 20
Expect { (Get-PostState) -eq 'Published' } 'Content published the due post by itself' 120

Api POST "shifts/shifts/$($desk.id)/check-out" | Out-Null

Write-Host ''
if ($script:failed -gt 0) { Write-Host "$script:failed check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All checks passed.' -ForegroundColor Green
