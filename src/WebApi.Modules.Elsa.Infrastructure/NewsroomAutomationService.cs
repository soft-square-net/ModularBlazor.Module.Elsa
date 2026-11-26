using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Elsa.Common.Multitenancy;
using Elsa.Tenants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure;

/// <summary>
/// Reconciles durable newsroom deadlines. Business state and deadlines remain in Content and Shifts; this process
/// only issues idempotent commands through their APIs, so it can restart or run beside them in a single pool. Its only
/// state, the place in Shifts' ended list (<see cref="EndedShiftCursor"/>), is rebuilt from a lookback after a restart.
/// </summary>
internal sealed class NewsroomAutomationService(
    NewsroomAutomationClient client,
    ITenantsProvider tenants,
    IConfiguration settings,
    ILogger<NewsroomAutomationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = settings.GetValue("Elsa:PollInterval", TimeSpan.FromSeconds(15));
        if (interval < TimeSpan.FromSeconds(1)) interval = TimeSpan.FromSeconds(15);

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var tenant in await tenants.ListAsync(stoppingToken))
            {
                try
                {
                    await client.ReconcileAsync(tenant.Id, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Newsroom workflow reconciliation failed for tenant {TenantId}.", tenant.Id);
                }
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}

internal sealed class NewsroomAutomationClient(HttpClient http, IConfiguration settings)
{
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly Dictionary<string, EndedShiftCursor> _cursors = new(StringComparer.Ordinal); // per tenant
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt;

    public async Task ReconcileAsync(string tenantId, CancellationToken cancellationToken)
    {
        var api = (settings["Elsa:PlatformApiBaseUrl"] ?? "http://localhost:5080").TrimEnd('/');
        var token = await GetTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{api}/api/content/automation/accepted");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Tenant", tenantId);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        foreach (var work in document.RootElement.EnumerateArray())
        {
            if (!work.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out var id) ||
                !work.TryGetProperty("acceptedAt", out var acceptedAtValue) ||
                acceptedAtValue.ValueKind != JsonValueKind.String || !acceptedAtValue.TryGetDateTimeOffset(out var acceptedAt) ||
                !work.TryGetProperty("acceptedShiftId", out var shiftValue) || shiftValue.ValueKind != JsonValueKind.String)
                continue;

            var shiftId = shiftValue.GetString();
            if (string.IsNullOrWhiteSpace(shiftId)) continue;
            var shift = await GetJsonAsync($"{api}/api/shifts/shifts/{Uri.EscapeDataString(shiftId)}/automation", tenantId, token, cancellationToken);
            if (!shift.TryGetProperty("autoAssignDelay", out var delayValue) || !TryGetTimeSpan(delayValue, out var delay)) continue;
            if (DateTimeOffset.UtcNow < acceptedAt + delay) continue;
            await SendAsync(HttpMethod.Post, $"{api}/api/content/automation/accepted/{id}/assign", tenantId, token, cancellationToken);
        }

        // A schedule is stored by Content. Its own endpoint rechecks due times and performs a version-safe transition.
        await SendAsync(HttpMethod.Post, $"{api}/api/content/posts/publish-due", tenantId, token, cancellationToken);

        await HandOverEndedShiftsAsync(api, tenantId, token, cancellationToken);
    }

    /// <summary>
    /// Returns Content's unfinished reviews for every shift that ended. Shifts ends shifts itself (a check-out, or its
    /// ShiftClock service); an older Shifts without the ended list is served by its overdue list.
    /// </summary>
    private async Task HandOverEndedShiftsAsync(string api, string tenantId, string token, CancellationToken cancellationToken)
    {
        if (!_cursors.TryGetValue(tenantId, out var cursor))
            _cursors[tenantId] = cursor = new EndedShiftCursor(DateTimeOffset.UtcNow - HandoverLookback());

        var listed = await cursor.DrainAsync(
            (since, ct) => ReadEndedShiftsAsync(api, since, tenantId, token, ct),
            async (shiftId, ct) =>
            {
                // Unlike SendAsync, only success counts: the cursor moves past a shift once Content has taken it.
                using var request = CreateRequest(HttpMethod.Post, HandoverUrl(api, shiftId), tenantId, token);
                using var response = await http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
            },
            cancellationToken);

        if (!listed) await HandOverOverdueShiftsAsync(api, tenantId, token, cancellationToken);
    }

    private async Task<IReadOnlyList<EndedShift>?> ReadEndedShiftsAsync(
        string api, DateTimeOffset since, string tenantId, string token, CancellationToken cancellationToken)
    {
        var from = Uri.EscapeDataString(since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        using var request = CreateRequest(HttpMethod.Get,
            $"{api}/api/shifts/shifts/ended?since={from}&take={EndedShiftCursor.PageSize}", tenantId, token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.EnumerateArray()
            .Select(s => new EndedShift(s.GetProperty("id").GetInt32(), s.GetProperty("endedAt").GetDateTimeOffset()))
            .ToArray();
    }

    /// <summary>
    /// For a Shifts without the ended list: hands over the shifts past their end plus the grace that are still listed as
    /// overdue. A shift checked out before a poll sees it gets no handover here.
    /// </summary>
    private async Task HandOverOverdueShiftsAsync(string api, string tenantId, string token, CancellationToken cancellationToken)
    {
        var overdue = await GetJsonAsync($"{api}/api/shifts/overdue", tenantId, token, cancellationToken);
        foreach (var shift in overdue.EnumerateArray())
        {
            if (!shift.TryGetProperty("id", out var id) || !id.TryGetInt32(out var shiftId)) continue;
            var automation = await GetJsonAsync($"{api}/api/shifts/shifts/{shiftId}/automation", tenantId, token, cancellationToken);
            if (!automation.TryGetProperty("endsAt", out var endsValue) || endsValue.ValueKind != JsonValueKind.String ||
                !endsValue.TryGetDateTimeOffset(out var endsAt) ||
                !automation.TryGetProperty("checkoutGrace", out var graceValue) || !TryGetTimeSpan(graceValue, out var grace) ||
                DateTimeOffset.UtcNow < endsAt + grace) continue;

            await SendAsync(HttpMethod.Post, HandoverUrl(api, shiftId), tenantId, token, cancellationToken);
        }
    }

    private static string HandoverUrl(string api, int shiftId) => $"{api}/api/content/automation/shifts/{shiftId}/handover";

    private TimeSpan HandoverLookback()
    {
        var fallback = TimeSpan.FromDays(1);
        var lookback = settings.GetValue("Elsa:HandoverLookback", fallback);
        return lookback > TimeSpan.Zero ? lookback : fallback;
    }

    /// <summary>
    /// System.Text.Json writes a TimeSpan as a constant-format string ("00:05:00", "1.00:00:00"); a number is read as
    /// seconds so a future contract change to numeric durations keeps working.
    /// </summary>
    internal static bool TryGetTimeSpan(JsonElement value, out TimeSpan result)
    {
        result = default;
        return value.ValueKind switch
        {
            JsonValueKind.String => TimeSpan.TryParseExact(value.GetString(), "c", CultureInfo.InvariantCulture, out result),
            JsonValueKind.Number when value.TryGetDouble(out var seconds) => (result = TimeSpan.FromSeconds(seconds)) >= TimeSpan.Zero,
            _ => false
        };
    }

    private async Task<JsonElement> GetJsonAsync(string url, string tenantId, string token, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, url, tenantId, token);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    private async Task SendAsync(HttpMethod method, string url, string tenantId, string token, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, url, tenantId, token);
        using var response = await http.SendAsync(request, cancellationToken);
        // Concurrent workflow replicas can race; the domain endpoints enforce versions and idempotent handover.
        if ((int)response.StatusCode is 409 or 404) return;
        response.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string tenantId, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Tenant", tenantId);
        return request;
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not null && _tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return _accessToken;
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && _tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return _accessToken;
            var authority = (settings["Elsa:IdentityAuthority"] ?? throw new InvalidOperationException("Elsa:IdentityAuthority is required.")).TrimEnd('/');
            var clientId = settings["Identity:ElsaClient:ClientId"] ?? "elsa-workflows";
            var secret = settings["Identity:ElsaClient:ClientSecret"] ?? throw new InvalidOperationException("The Elsa OAuth client secret is required.");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{authority}/connect/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials", ["client_id"] = clientId,
                    ["client_secret"] = secret, ["scope"] = "api"
                })
            };
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            _accessToken = document.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Identity returned an empty access token.");
            var seconds = document.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var value) ? value : 300;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
            return _accessToken;
        }
        finally { _tokenLock.Release(); }
    }
}
