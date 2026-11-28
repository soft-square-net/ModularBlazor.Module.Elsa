using Microsoft.AspNetCore.Http;
using soft.square.net.WebApi.Contracts.Security;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure;

/// <summary>Checks Elsa API operations against host-owned tenant grants, independent of Elsa's built-in user store.</summary>
internal sealed class ElsaWorkflowPermissionMiddleware(RequestDelegate next)
{
    private const string View = "workflow.view";
    private const string Design = "workflow.design";
    private const string Run = "workflow.run";

    public async Task InvokeAsync(HttpContext context, IAccessScopes access)
    {
        // Everything behind this runs with every Elsa permission, so an anonymous caller never gets past it.
        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var permission = RequiredPermission(context.Request.Method, context.Request.Path);
        if (!await access.HasPermissionAsync(permission, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    /// <summary>
    /// The platform permission of a request, by its path inside the module (<c>/elsa/api/...</c> and
    /// <c>/workflows/...</c>): <c>workflow.run</c> for instances and for whatever starts or resumes a workflow (whatever
    /// the method, since some of these also answer GET), else <c>workflow.view</c> to read and <c>workflow.design</c>
    /// to change.
    /// </summary>
    internal static string RequiredPermission(string method, PathString path)
    {
        var segments = (path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && Is(segments[0], "workflows")) return Run;
        if (segments.Length > 2 && Is(segments[0], "elsa") && Is(segments[1], "api"))
        {
            var resource = segments[2];
            if (Is(resource, "workflow-instances") || Is(resource, "events") || Is(resource, "bookmarks") ||
                Is(resource, "resume") || Is(resource, "tasks"))
                return Run;
            if ((Is(resource, "workflow-definitions") || Is(resource, "bulk-actions")) &&
                segments.Skip(3).Any(s => Is(s, "execute") || Is(s, "dispatch") || Is(s, "bulk-dispatch")))
                return Run;
        }

        return HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ? View : Design;
    }

    private static bool Is(string segment, string value) => segment.Equals(value, StringComparison.OrdinalIgnoreCase);
}
