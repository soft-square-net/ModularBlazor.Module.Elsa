using Microsoft.AspNetCore.Http;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure;

namespace soft.square.net.WebApi.Modules.Elsa.UnitTests;

public class ElsaWorkflowPermissions
{
    [Theory]
    [InlineData("GET", "/elsa/api/workflow-definitions", "workflow.view")]
    [InlineData("HEAD", "/elsa/api/activity-descriptors", "workflow.view")]
    [InlineData("POST", "/elsa/api/workflow-definitions", "workflow.design")]
    [InlineData("DELETE", "/elsa/api/workflow-definitions/abc", "workflow.design")]
    [InlineData("POST", "/elsa/api/bulk-actions/publish/workflow-definitions", "workflow.design")]
    [InlineData("POST", "/elsa/api/workflow-definitions/abc/execute", "workflow.run")]
    [InlineData("GET", "/elsa/api/workflow-definitions/abc/execute", "workflow.run")]
    [InlineData("POST", "/elsa/api/workflow-definitions/abc/dispatch", "workflow.run")]
    [InlineData("POST", "/elsa/api/workflow-definitions/abc/bulk-dispatch", "workflow.run")]
    [InlineData("POST", "/ELSA/API/Workflow-Definitions/abc/Execute", "workflow.run")]
    [InlineData("GET", "/elsa/api/workflow-instances", "workflow.run")]
    [InlineData("DELETE", "/elsa/api/workflow-instances/abc", "workflow.run")]
    [InlineData("POST", "/elsa/api/events/approved/trigger", "workflow.run")]
    [InlineData("GET", "/elsa/api/bookmarks/resume", "workflow.run")]
    [InlineData("GET", "/elsa/api/resume/abc", "workflow.run")]
    [InlineData("POST", "/elsa/api/tasks/abc/complete", "workflow.run")]
    [InlineData("GET", "/workflows/anything", "workflow.run")]
    public void A_request_needs_the_permission_of_what_it_does(string method, string path, string permission) =>
        ElsaWorkflowPermissionMiddleware.RequiredPermission(method, new PathString(path)).ShouldBe(permission);

    [Fact]
    public async Task An_anonymous_caller_never_reaches_the_engine()
    {
        var reached = false;
        var middleware = new ElsaWorkflowPermissionMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context, null!); // no grant is even looked up

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        reached.ShouldBeFalse();
    }
}
