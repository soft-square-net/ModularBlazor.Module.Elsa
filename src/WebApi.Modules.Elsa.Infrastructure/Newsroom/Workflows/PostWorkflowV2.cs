using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Activities;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Workflows;

/// <summary>
/// File the post in its Section, produce its contents, send it to the desk, wait until the desk publishes it (now or on
/// a schedule), tell the person who proposed it. Unlike version 1, a reviewer's approval publishes nothing: the desk does,
/// in Content. A project the Section cannot take waits for the moderator; a returned post is produced again.
/// RELEASED: do not change it (see <see cref="DefaultWorkflows"/>).
/// </summary>
public sealed class PostWorkflowV2() : VersionedWorkflow(DefaultWorkflows.Post, 2, "Newsroom post")
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
        var publication = new WaitForPublication { Id = "publication", EndOnRefiling = new(true) };
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
            Activities = { ensure, refile, produce, desk, publication, announce },
            Connections =
            {
                To(ensure, EnsurePostInUnitCategories.Filed, produce),
                To(ensure, EnsurePostInUnitCategories.NeedsModeration, refile),
                To(refile, WaitForRefiling.Refiled, ensure),
                To(produce, ProduceContents.Produced, desk),
                To(produce, ProduceContents.Refiled, ensure),
                To(desk, DeskReview.Approved, publication),
                To(desk, DeskReview.Returned, produce),
                To(desk, DeskReview.Refiled, ensure),
                To(publication, WaitForPublication.Published, announce),
                To(publication, WaitForPublication.BackToDesk, desk),
                To(publication, WaitForPublication.Refiled, ensure)
            }
        };

        static Connection To(IActivity from, string outcome, IActivity to) => new(new Endpoint(from, outcome), new Endpoint(to));
    }
}
