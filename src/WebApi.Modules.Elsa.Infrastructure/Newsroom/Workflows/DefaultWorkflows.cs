using Elsa.Extensions;
using Elsa.Expressions.Models;
using Elsa.Features.Services;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Workflows;

/// <summary>
/// The newsroom workflows that ship with the module. Every tenant has them under the same definition id; NewsRoom binds
/// post kinds and content kinds to them, or to definitions authored in Elsa Studio (whose ids must be unique across
/// tenants).
/// <para>
/// <b>A released version never changes.</b> Elsa 3.9.0 keeps no copy of a code workflow: a suspended instance resumes
/// on whatever the class builds now. With the same version, an edited graph takes the instance elsewhere without an
/// error (node ids follow positions); with the old class gone, the instance cannot be resumed at all. So a change is a
/// new class with the next version, registered beside the old ones, which stay for as long as an instance may use them.
/// A test holds each version to its recorded graph.
/// </para>
/// </summary>
public static class DefaultWorkflows
{
    /// <summary>The main workflow of a post project, whatever its post kind.</summary>
    public const string Post = "newsroom-post";

    /// <summary>The sub-workflow of one content, whatever its content kind.</summary>
    public const string Content = "newsroom-content";

    internal static void Register(IModule elsa)
    {
        // Every version that was ever released.
        elsa.AddWorkflow<PostWorkflowV1>();
        elsa.AddWorkflow<ContentWorkflowV1>();
    }
}

/// <summary>One version of a code workflow, with ids that do not depend on the class's name or on positions.</summary>
public abstract class VersionedWorkflow(string definitionId, int version, string name) : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.WithDefinitionId(definitionId);
        builder.Id = $"{definitionId}:v{version}";
        builder.Version = version;
        builder.Name = name;
        builder.Root = BuildRoot();
    }

    protected abstract IActivity BuildRoot();
}

/// <summary>An input whose value is a module setting, read when the activity runs (and by a dry run).</summary>
internal interface ISettingInput
{
    object? Read(IConfiguration settings);
}

internal sealed class SettingInput<T>(Func<IConfiguration, T> read) : Input<T>(Evaluate(read)), ISettingInput
{
    public object? Read(IConfiguration settings) => read(settings);

    private static Func<ExpressionExecutionContext, T> Evaluate(Func<IConfiguration, T> read) =>
        context => read(context.GetRequiredService<IConfiguration>());
}

/// <summary>The module settings the default workflows read.</summary>
internal static class NewsroomSettings
{
    /// <summary>The shift role that produces contents.</summary>
    public const string ContentRole = "Elsa:Newsroom:ContentRole";

    /// <summary>How long a manager has to assign a content by hand.</summary>
    public const string ManagerDelay = "Elsa:Newsroom:ManagerDelay";

    /// <summary>The shift role that reviews posts; empty when people or the coordinator assign reviews.</summary>
    public const string ReviewerRole = "Elsa:Newsroom:ReviewerRole";

    /// <summary>How long a manager has to accept and assign a review by hand.</summary>
    public const string ReviewerDelay = "Elsa:Newsroom:ReviewerDelay";

    public const string DefaultContentRole = "writer";

    public static string ContentRoleOf(IConfiguration settings) =>
        settings[ContentRole] is { Length: > 0 } role ? role : DefaultContentRole;

    public static TimeSpan? ManagerDelayOf(IConfiguration settings) =>
        settings.GetValue(ManagerDelay, TimeSpan.FromMinutes(5));

    public static string? ReviewerRoleOf(IConfiguration settings) => settings[ReviewerRole];

    public static TimeSpan? ReviewerDelayOf(IConfiguration settings) =>
        settings.GetValue(ReviewerDelay, TimeSpan.FromMinutes(5));
}
