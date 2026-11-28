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
}

internal sealed class FakePost
{
    public required int Id { get; init; }
    public int Version { get; set; } = 1;
    public string State { get; set; } = "Draft";
    public List<string> Categories { get; set; } = ["news"];
    public Guid? UnitId { get; set; }
}

internal sealed class FakeContent
{
    public required Guid Id { get; init; }
    public Guid? UnitId { get; init; }
    public int Version { get; set; } = 1;
    public string Stage { get; set; } = "in-progress";
    public string? AssigneeId { get; set; }
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

    /// <summary>Every call but the token's, as "tenant METHOD path".</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    public volatile bool Down;
    public string? PublishRefusal;

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

        lock (_gate) return Answer(request.Method.Method, path.Trim('/').Split('/'), body);
    }

    private HttpResponseMessage Answer(string method, string[] s, JsonElement body)
    {
        switch (method, s)
        {
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
                return Posts.TryGetValue(int.Parse(id), out var post) ? Json(post) : NotFound();

            case ("PUT", ["api", "content", "posts", var id, "unit"]):
            {
                var target = Posts[int.Parse(id)];
                if (body.GetProperty("version").GetInt32() != target.Version) return Conflict("Stale version.");
                target.UnitId = body.GetProperty("unitId").ValueKind == JsonValueKind.Null
                    ? null
                    : body.GetProperty("unitId").GetGuid();
                target.Version++;
                return Json(target);
            }

            case ("POST", ["api", "content", "posts", var id, "publish"]):
            {
                var target = Posts[int.Parse(id)];
                if (PublishRefusal is { } refusal)
                    return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new { detail = refusal }))
                    };
                target.State = "Published";
                target.Version++;
                return Json(target);
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
                return Json(new[]
                {
                    new ShiftSnapshot(1, ShiftUnit, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(7),
                        Published: true, Running: true, Ended: false, Rota.ToArray())
                });

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
