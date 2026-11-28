using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.DryRun;

/// <summary>
/// <c>POST /api/elsa/newsroom/dry-run</c> (<see cref="DryRunRequest"/>). It changes nothing, and is a POST on purpose:
/// the module's permission check then asks for <c>workflow.design</c>, and what the answer says about the post is read
/// with the workflows' own rights, not the caller's.
/// </summary>
internal static class DryRunEndpoint
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/newsroom/dry-run", async (HttpContext http) =>
        {
            DryRunRequest? request;
            try { request = await http.Request.ReadFromJsonAsync<DryRunRequest>(http.RequestAborted); }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException) { request = null; }
            if (request is not { PostId: > 0 })
                return Results.Problem("Give a postId.", statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var result = await http.RequestServices.GetRequiredService<DryRunner>().RunAsync(request, http.RequestAborted);
                return result is null
                    ? Results.Problem($"Content does not serve post {request.PostId}.", statusCode: StatusCodes.Status404NotFound)
                    : Results.Ok(result);
            }
            catch (PlatformUnavailableException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
}
