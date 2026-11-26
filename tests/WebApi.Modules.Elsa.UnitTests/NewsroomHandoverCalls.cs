using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure;

namespace soft.square.net.WebApi.Modules.Elsa.UnitTests;

public class NewsroomHandoverCalls
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakePlatform _platform = new();

    private NewsroomAutomationClient Client(string? lookback = null) => new(new HttpClient(_platform),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Elsa:IdentityAuthority"] = "http://identity.test",
            ["Elsa:PlatformApiBaseUrl"] = "http://api.test",
            ["Elsa:HandoverLookback"] = lookback,
            ["Identity:ElsaClient:ClientSecret"] = "test"
        }).Build());

    private IEnumerable<string> Handovers => _platform.Requests.Where(r => r.EndsWith("/handover", StringComparison.Ordinal));

    private TimeSpan LookbackAsked()
    {
        var query = _platform.Requests.First(r => r.Contains("/shifts/ended?", StringComparison.Ordinal));
        var since = Uri.UnescapeDataString(query.Split("since=")[1].Split('&')[0]);
        since.ShouldEndWith("Z");
        return DateTimeOffset.UtcNow - DateTimeOffset.Parse(since, CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task ItHandsOverTheEndedShiftsOnceAndDoesNotReadTheOverdueList()
    {
        _platform.Ended = $$"""[{"id":7,"endedAt":"{{DateTimeOffset.UtcNow:O}}","endedBy":"shifts:clock"}]""";
        var client = Client();

        await client.ReconcileAsync("tenant-a", Ct);
        await client.ReconcileAsync("tenant-a", Ct);

        Handovers.ShouldBe(["POST /api/content/automation/shifts/7/handover"]);
        _platform.Requests.ShouldContain(r => r.EndsWith("&take=500", StringComparison.Ordinal));
        _platform.Requests.ShouldNotContain(r => r.Contains("/overdue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EachTenantHasItsOwnCursor()
    {
        _platform.Ended = $$"""[{"id":7,"endedAt":"{{DateTimeOffset.UtcNow:O}}"}]""";
        var client = Client();

        await client.ReconcileAsync("tenant-a", Ct);
        await client.ReconcileAsync("tenant-b", Ct);

        Handovers.Count().ShouldBe(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task AHandoverThatContentRefusesIsTriedAgain(HttpStatusCode refusal)
    {
        _platform.Ended = $$"""[{"id":7,"endedAt":"{{DateTimeOffset.UtcNow:O}}"}]""";
        _platform.HandoverStatus = refusal;
        var client = Client();

        await Should.ThrowAsync<HttpRequestException>(() => client.ReconcileAsync("tenant-a", Ct));
        _platform.HandoverStatus = HttpStatusCode.OK;
        await client.ReconcileAsync("tenant-a", Ct);

        Handovers.Count().ShouldBe(2);
    }

    [Fact]
    public async Task AnOlderShiftsIsServedFromTheOverdueListAfterTheGrace()
    {
        _platform.Ended = null; // 404
        _platform.Overdue = """[{"id":8},{"id":9}]""";
        _platform.Grace[8] = (DateTimeOffset.UtcNow.AddHours(-1), "00:15:00");
        _platform.Grace[9] = (DateTimeOffset.UtcNow.AddMinutes(-5), "00:15:00");

        await Client().ReconcileAsync("tenant-a", Ct);

        Handovers.ShouldBe(["POST /api/content/automation/shifts/8/handover"]);
        _platform.Requests.ShouldNotContain(r => r.Contains("check-out", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, 24)]
    [InlineData("00:00:00", 24)]
    [InlineData("06:00:00", 6)]
    [InlineData("2.00:00:00", 48)]
    public async Task TheFirstReadLooksBackTheConfiguredWindow(string? configured, int hours)
    {
        await Client(configured).ReconcileAsync("tenant-a", Ct);

        LookbackAsked().ShouldBe(TimeSpan.FromHours(hours), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task TheManifestsDefaultLookbackIsOneDay()
    {
        await Client(new ElsaModule().Manifest.DefaultSettings!["Elsa:HandoverLookback"]).ReconcileAsync("tenant-a", Ct);

        LookbackAsked().ShouldBe(TimeSpan.FromDays(1), TimeSpan.FromMinutes(1));
    }

    private sealed class FakePlatform : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string? Ended { get; set; } = "[]";
        public string Overdue { get; set; } = "[]";
        public Dictionary<int, (DateTimeOffset EndsAt, string Grace)> Grace { get; } = [];
        public HttpStatusCode HandoverStatus { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {request.RequestUri.PathAndQuery}");

            if (path == "/connect/token") return Answer("""{"access_token":"token","expires_in":300}""");
            if (path == "/api/shifts/shifts/ended") return Ended is null ? Answer("", HttpStatusCode.NotFound) : Answer(Ended);
            if (path == "/api/shifts/overdue") return Answer(Overdue);
            if (path.EndsWith("/handover", StringComparison.Ordinal)) return Answer("0", HandoverStatus);
            if (path.EndsWith("/automation", StringComparison.Ordinal))
            {
                var (endsAt, grace) = Grace[int.Parse(path.Split('/')[^2], CultureInfo.InvariantCulture)];
                return Answer($$"""{"endsAt":"{{endsAt:O}}","checkoutGrace":"{{grace}}"}""");
            }
            return Answer("[]");
        }

        private static Task<HttpResponseMessage> Answer(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
