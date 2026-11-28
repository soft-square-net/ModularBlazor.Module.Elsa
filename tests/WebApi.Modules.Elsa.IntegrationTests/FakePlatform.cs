using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;

namespace soft.square.net.WebApi.Modules.Elsa.IntegrationTests;

internal sealed class FakeProject
{
    public required Guid Id { get; init; }
    public required Guid SectionId { get; set; }
    public required int PostId { get; init; }
    public string State { get; set; } = ProjectStates.Approved;
    public int ApprovalNumber { get; set; } = 1;
    public string ProposalAuthorId { get; init; } = "author";
    public string PostKind { get; init; } = "post.news";
}

internal sealed class FakePost
{
    public required int Id { get; init; }
    public int Version { get; set; } = 1;
    public string State { get; set; } = "Draft";
    public List<string> Categories { get; set; } = ["news"];
    public Guid? UnitId { get; set; }
    public string OwnerId { get; set; } = "author";
    public string PostKind { get; set; } = "post.news";
    public string? ReviewerId { get; set; }
    public DateTimeOffset? ScheduledFor { get; set; }

    /// <summary>The actions of the post's history, oldest first.</summary>
    public List<string> History { get; } = [];

    /// <summary>The post's contents: the content's id and whether the post requires it.</summary>
    public List<(Guid ContentId, bool Required)> Attached { get; } = [];
}

internal sealed class FakeContent
{
    public required Guid Id { get; init; }
    public Guid? UnitId { get; set; }
    public string FieldKey { get; init; } = "article";
    public int Version { get; set; } = 1;
    public string Stage { get; set; } = "in-progress";
    public string? AssigneeId { get; set; }
}

internal sealed class FakeShift
{
    public required int Id { get; init; }
    public required Guid UnitId { get; init; }
    public List<RotaMember> Rota { get; init; } = [];
    public bool Ended { get; set; }
}

/// <summary>The other modules, as far as the workflows ask them. One per test; each tenant sees the same data.</summary>
internal sealed class FakePlatform : HttpMessageHandler
{
    private readonly Lock _gate = new();

    public Dictionary<Guid, FakeProject> Projects { get; } = [];
    public Dictionary<int, FakePost> Posts { get; } = [];
    public Dictionary<Guid, FakeContent> Contents { get; } = [];
    public Dictionary<Guid, string[]> UnitCategories { get; } = [];
    public List<RotaMember> Rota { get; } = [];
    public Guid ShiftUnit { get; set; }

    /// <summary>The shift that <see cref="Rota"/> and <see cref="ShiftUnit"/> set up has id 1; these come after it.</summary>
    public List<FakeShift> Shifts { get; } = [];
    public bool FirstShiftEnded { get; set; }

    /// <summary>The definition NewsRoom binds to the project's post kind; null when nothing is bound.</summary>
    public string? PostBinding { get; set; } = "project-workflow";

    /// <summary>The version the post binding pins; null for the latest published one.</summary>
    public int? PostBindingVersion { get; set; }

    /// <summary>What happens to the post while Content handles a publish call, which then fails.</summary>
    public Action<FakePost>? DuringPublish;

    /// <summary>The definitions bound to content kinds.</summary>
    public Dictionary<string, string> ContentBindings { get; } = [];

    /// <summary>The unit that NewsRoom's routing rules give a content kind.</summary>
    public Dictionary<string, Guid> Routes { get; } = [];

    public Dictionary<string, int> Loads { get; } = [];

    /// <summary>Every call but the token's, as "tenant METHOD path".</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    public volatile bool Down;
    public string? PublishRefusal;

    /// <summary>How many unschedule calls find the post changed since it was read.</summary>
    public int UnscheduleConflicts;

    public int Count(string text) => Calls.Count(c => c.Contains(text, StringComparison.Ordinal));

    public void Change(Action change)
    {
        lock (_gate) change();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/connect/token") return Json(new { access_token = "token", expires_in = 3600 });

        var tenant = request.Headers.GetValues("X-Tenant").Single();
        var body = request.Content is null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        Calls.Enqueue($"{tenant} {request.Method} {path}");
        if (Down) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var query = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair.Length > 1 ? pair[1] : ""));
        lock (_gate)
        {
            PublishDuePosts();
            return Answer(request.Method.Method, path.Trim('/').Split('/'), query, body);
        }
    }

    // Content publishes its own due posts; the fake does so before it answers.
    private void PublishDuePosts()
    {
        foreach (var due in Posts.Values.Where(p => p.State == "Scheduled" && p.ScheduledFor <= DateTimeOffset.UtcNow))
        {
            due.State = "Published";
            due.Version++;
        }
    }

    private HttpResponseMessage Answer(string method, string[] s, Dictionary<string, string> query, JsonElement body)
    {
        switch (method, s)
        {
            case ("GET", ["api", "newsroom", "projects", var id, "workflow"]):
                if (!Projects.ContainsKey(Guid.Parse(id))) return NotFound();
                return PostBinding is { } bound
                    ? Json(new { definitionId = bound, definitionVersion = PostBindingVersion })
                    : Conflict("No workflow is bound.");

            case ("GET", ["api", "newsroom", "bindings", "resolve"]):
            {
                var definition = query["kindType"] == "post" ? PostBinding : ContentBindings.GetValueOrDefault(query["kind"]);
                return definition is null
                    ? Conflict("No workflow is bound.")
                    : Json(new { definitionId = definition, definitionVersion = (int?)null });
            }

            case ("POST", ["api", "newsroom", "routing", "route"]):
                return Json(new
                {
                    unitId = Routes.TryGetValue(body.GetProperty("contentKind").GetString()!, out var routed)
                        ? routed : (Guid?)null
                });

            case ("POST", ["api", "newsroom", "workload", "query"]):
                return Json(new
                {
                    items = body.GetProperty("userIds").EnumerateArray().Select(u => u.GetString()!)
                        .Select(u => new { userId = u, openItems = Loads.GetValueOrDefault(u) }).ToArray()
                });

            case ("GET", ["api", "newsroom", "projects", var id]):
                return Projects.TryGetValue(Guid.Parse(id), out var project) ? Json(project) : NotFound();

            case ("POST", ["api", "newsroom", "projects", var id, "state"]):
            {
                var target = Projects[Guid.Parse(id)];
                if (body.GetProperty("approvalNumber").GetInt32() != target.ApprovalNumber) return Conflict("Stale approval.");
                target.State = body.GetProperty("state").GetString()!;
                return Json(target);
            }

            case ("GET", ["api", "content", "posts", var id]):
                return Posts.TryGetValue(int.Parse(id), out var post) ? Json(Snapshot(post)) : NotFound();

            case ("POST", ["api", "content", "posts", var id, "schedule"]):
                return Update(id, body, target =>
                {
                    target.State = "Scheduled";
                    target.ScheduledFor = body.GetProperty("publishAt").GetDateTimeOffset();
                    target.History.Add("scheduled");
                });

            case ("POST", ["api", "content", "posts", var id, "accept"]):
                return Update(id, body, target => target.State = "Accepted");

            case ("POST", ["api", "content", "posts", var id, "unschedule"]):
            {
                var target = Posts[int.Parse(id)];
                if (UnscheduleConflicts > 0)
                {
                    UnscheduleConflicts--;
                    target.Version++; // changed since it was read
                }

                if (target.State != "Scheduled") return Conflict("The post is not scheduled.");
                return Update(id, body, scheduled =>
                {
                    scheduled.State = "Approved";
                    scheduled.ScheduledFor = null;
                    scheduled.History.Add("unscheduled");
                });
            }

            case ("POST", ["api", "content", "posts", var id, "assign"]):
                return Update(id, body, target =>
                {
                    target.State = "InReview";
                    target.ReviewerId = body.GetProperty("assigneeId").GetString();
                });

            case ("PUT", ["api", "content", "posts", var id, "unit"]):
            {
                var target = Posts[int.Parse(id)];
                if (body.GetProperty("version").GetInt32() != target.Version) return Conflict("Stale version.");
                target.UnitId = body.GetProperty("unitId").ValueKind == JsonValueKind.Null
                    ? null
                    : body.GetProperty("unitId").GetGuid();
                target.Version++;
                return Json(Snapshot(target));
            }

            case ("PUT", ["api", "content", "contents", var id, "unit"]):
            {
                var target = Contents[Guid.Parse(id)];
                if (body.GetProperty("version").GetInt32() != target.Version) return Conflict("Stale version.");
                target.UnitId = body.GetProperty("unitId").GetGuid();
                target.Version++;
                return Json(target);
            }

            case ("POST", ["api", "content", "posts", var id, "publish"]):
            {
                var target = Posts[int.Parse(id)];
                if (DuringPublish is { } meanwhile)
                {
                    DuringPublish = null;
                    meanwhile(target);
                    // Not a 409: Content may find something else wrong with the post as it now is.
                    return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new { detail = "The post is returned." }))
                    };
                }

                if (PublishRefusal is { } refusal)
                    return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new { detail = refusal }))
                    };
                target.State = "Published";
                target.Version++;
                return Json(Snapshot(target));
            }

            case ("GET", ["api", "content", "contents"]):
                return Json(Array.Empty<FakeContent>());

            case ("GET", ["api", "content", "contents", var id]):
                return Contents.TryGetValue(Guid.Parse(id), out var content) ? Json(content) : NotFound();

            case ("POST", ["api", "content", "contents", var id, "assign"]):
            {
                var target = Contents[Guid.Parse(id)];
                target.AssigneeId = body.GetProperty("assigneeId").GetString();
                target.Version++;
                return Json(target);
            }

            case ("GET", ["api", "shifts", "shifts"]):
                return Json(AllShifts().ToArray());

            case ("GET", ["api", "shifts", "shifts", var id]):
                return AllShifts().FirstOrDefault(x => x.Id == int.Parse(id)) is { } shift ? Json(shift) : NotFound();

            case ("GET", ["api", "organization", "local", "units", var id, "categories"]):
                return UnitCategories.TryGetValue(Guid.Parse(id), out var categories) ? Json(categories) : NotFound();

            case ("GET", ["api", "organization", "local", "units", var id]):
                return Json(new UnitSnapshot(Guid.Parse(id), null, "/org/desk", [], []));

            case ("GET", ["api", "organization", "local", "units", _, "members"]):
                return Json(Array.Empty<UnitMember>());

            default:
                return NotFound();
        }
    }

    private IEnumerable<ShiftSnapshot> AllShifts() =>
        new[] { new FakeShift { Id = 1, UnitId = ShiftUnit, Rota = Rota, Ended = FirstShiftEnded } }.Concat(Shifts)
            .Select(x => new ShiftSnapshot(x.Id, x.UnitId, DateTimeOffset.UtcNow.AddHours(-1),
                DateTimeOffset.UtcNow.AddHours(7), Published: true, Running: !x.Ended, x.Ended, x.Rota.ToArray()));

    private object Snapshot(FakePost post) => new
    {
        post.Id, post.Version, post.State, post.Categories, post.UnitId, post.ScheduledFor, post.OwnerId,
        post.PostKind,
        history = post.History.Select(action => new { action }).ToArray(),
        contents = post.Attached.Select(a => new
        {
            a.ContentId, a.Required, fieldKey = Contents[a.ContentId].FieldKey, stage = Contents[a.ContentId].Stage,
            archived = false
        }).ToArray()
    };

    private HttpResponseMessage Update(string id, JsonElement body, Action<FakePost> change)
    {
        var target = Posts[int.Parse(id)];
        if (body.GetProperty("version").GetInt32() != target.Version) return Conflict("Stale version.");
        change(target);
        target.Version++;
        return Json(Snapshot(target));
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private static HttpResponseMessage Conflict(string detail) => new(HttpStatusCode.Conflict)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { detail }))
    };

    // The handler is shared by every client the factory makes, over several engines: nothing to dispose.
    protected override void Dispose(bool disposing) { }
}
