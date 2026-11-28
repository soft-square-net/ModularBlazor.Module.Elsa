using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Activities;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Workflows;

/// <summary>
/// File the post in its Section, produce its contents, send it to the desk, publish it, tell the person who proposed it.
/// A project the Section cannot take waits for the moderator; a returned post is produced again; a refused publication
/// goes back to the desk. RELEASED: do not change it (see <see cref="DefaultWorkflows"/>).
/// </summary>
public sealed class PostWorkflowV1() : VersionedWorkflow(DefaultWorkflows.Post, 1, "Newsroom post")
{
    protected override IActivity BuildRoot()
    {
        var ensure = new EnsurePostInUnitCategories { Id = "ensure" };
        var refile = new WaitForRefiling { Id = "refile" };
        var produce = new ProduceContents { Id = "produce" };
        var desk = new DeskReview
        {
            Id = "desk",
            EndOnRefiling = new(true),
            ReviewerRoleKey = new SettingInput<string?>(NewsroomSettings.ReviewerRoleOf),
            ReviewerDelay = new SettingInput<TimeSpan?>(NewsroomSettings.ReviewerDelayOf)
        };
        var publish = new PublishPost { Id = "publish" };
        var refused = new Notify
        {
            Id = "refused",
            ToProjectAuthor = new(true),
            NotificationType = new("workflow.publish-refused"),
            Message = new("The post could not be published; it is back with the desk.")
        };
        var announce = new Notify
        {
            Id = "announce",
            ToProjectAuthor = new(true),
            NotificationType = new("workflow.published"),
            Message = new("The post is published.")
        };

        return new Flowchart
        {
            Id = "post",
            Start = ensure,
            Activities = { ensure, refile, produce, desk, publish, refused, announce },
            Connections =
            {
                To(ensure, EnsurePostInUnitCategories.Filed, produce),
                To(ensure, EnsurePostInUnitCategories.NeedsModeration, refile),
                To(refile, WaitForRefiling.Refiled, ensure),
                To(produce, ProduceContents.Produced, desk),
                To(produce, ProduceContents.Refiled, ensure),
                To(desk, DeskReview.Approved, publish),
                To(desk, DeskReview.Returned, produce),
                To(desk, DeskReview.Refiled, ensure),
                To(publish, PublishPost.Published, announce),
                To(publish, PublishPost.Refused, refused),
                To(refused, Notify.Sent, desk)
            }
        };

        static Connection To(IActivity from, string outcome, IActivity to) => new(new Endpoint(from, outcome), new Endpoint(to));
    }
}
