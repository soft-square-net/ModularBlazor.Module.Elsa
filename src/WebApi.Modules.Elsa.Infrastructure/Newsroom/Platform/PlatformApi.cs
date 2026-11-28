using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Elsa.Common.Multitenancy;
using Microsoft.Extensions.Configuration;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;

/// <summary>
/// The platform could not be asked, or refused the service itself (it is down, starting, or a grant is missing).
/// Nothing was decided: a newsroom activity waits and tries again.
/// </summary>
public sealed class PlatformUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What a module answered when the service is allowed to ask: success, or the refusal of this one request.</summary>
public sealed record PlatformAnswer(HttpStatusCode Status, string Body)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;

    public T Read<T>() => JsonSerializer.Deserialize<T>(Body, PlatformApi.Json)
        ?? throw new PlatformUnavailableException($"The platform answered {(int)Status} with no content.");

    /// <summary>The problem's detail, or the raw body, short enough to keep with a workflow.</summary>
    public string Reason()
    {
        var text = Body;
        try
        {
            using var document = JsonDocument.Parse(Body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var name in new[] { "detail", "title" })
                    if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        text = value.GetString()!;
                        return Short(text);
                    }
        }
        catch (JsonException) { }

        return Short(text);

        static string Short(string value) => value.Length > 500 ? value[..500] : value;
    }
}

/// <summary>
/// Calls the platform's APIs through the gateway as the workflows' service subject, in the tenant the workflow runs
/// for. The tenant comes from Elsa's tenant context: without one, nothing is sent.
/// </summary>
internal sealed class PlatformApi(
    IHttpClientFactory clients, PlatformServiceToken token, ITenantAccessor tenant, IConfiguration settings)
{
    public const string ClientName = "newsroom-platform";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A GET that must succeed; null when the resource does not exist (404).</summary>
    public async Task<T?> FindAsync<T>(string path, CancellationToken ct) where T : class
    {
        var answer = await SendAsync(HttpMethod.Get, path, null, ct);
        if (answer.Status == HttpStatusCode.NotFound) return null;
        if (!answer.IsSuccess)
            throw new PlatformUnavailableException($"GET {path} answered {(int)answer.Status}: {answer.Reason()}");
        return answer.Read<T>();
    }

    /// <summary>
    /// Sends one request. 401, 403, 408, 429 and 5xx, and no answer at all, are
    /// <see cref="PlatformUnavailableException"/>; every other status is the caller's to interpret.
    /// </summary>
    public async Task<PlatformAnswer> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var tenantId = tenant.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("A newsroom activity ran without a tenant; nothing was sent.");

        var api = (settings["Elsa:PlatformApiBaseUrl"] ?? "http://localhost:5080").TrimEnd('/');
        try
        {
            var client = clients.CreateClient(ClientName);
            using var request = new HttpRequestMessage(method, api + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token.GetAsync(client, ct));
            request.Headers.Add("X-Tenant", tenantId);
            if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
            using var response = await client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            var status = response.StatusCode;
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.RequestTimeout
                    or HttpStatusCode.TooManyRequests || (int)status >= 500)
                throw new PlatformUnavailableException($"{method} {path} answered {(int)status} in tenant {tenantId}.");
            return new PlatformAnswer(status, text);
        }
        catch (HttpRequestException exception)
        {
            throw new PlatformUnavailableException($"{method} {path} failed in tenant {tenantId}.", exception);
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            throw new PlatformUnavailableException($"{method} {path} timed out in tenant {tenantId}.", exception);
        }
    }
}

/// <summary>The client-credentials token of the workflows' service subject (its tenant is each request's header).</summary>
internal sealed class PlatformServiceToken(IConfiguration settings) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetAsync(HttpClient http, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_token is not null && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return _token;
            var authority = (settings["Elsa:IdentityAuthority"]
                ?? throw new InvalidOperationException("Elsa:IdentityAuthority is required.")).TrimEnd('/');
            var secret = settings["Identity:ElsaClient:ClientSecret"]
                ?? throw new InvalidOperationException("The Elsa OAuth client secret is required.");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{authority}/connect/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = settings["Identity:ElsaClient:ClientId"] ?? "elsa-workflows",
                    ["client_secret"] = secret,
                    ["scope"] = "api"
                })
            };
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new PlatformUnavailableException($"The identity service answered {(int)response.StatusCode} to the token request.");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            _token = document.RootElement.GetProperty("access_token").GetString()
                ?? throw new PlatformUnavailableException("Identity returned an empty access token.");
            var seconds = document.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var value)
                ? value
                : 300;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
            return _token;
        }
        finally { _lock.Release(); }
    }

    public void Dispose() => _lock.Dispose();
}
