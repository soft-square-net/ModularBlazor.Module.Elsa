using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Activities;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Workflows;

/// <summary>
/// Give the content to the unit that produces its kind, and have the people on shift there produce it until it is
/// accepted. RELEASED: do not change it (see <see cref="DefaultWorkflows"/>).
/// </summary>
public sealed class ContentWorkflowV1() : VersionedWorkflow(DefaultWorkflows.Content, 1, "Newsroom content")
{
    protected override IActivity BuildRoot()
    {
        var file = new FileContentInUnit { Id = "file" };
        var work = new WorkInShift
        {
            Id = "work",
            RoleKey = new SettingInput<string>(NewsroomSettings.ContentRoleOf),
            ManagerDelay = new SettingInput<TimeSpan?>(NewsroomSettings.ManagerDelayOf)
        };

        return new Flowchart
        {
            Id = "content",
            Start = file,
            Activities = { file, work },
            Connections =
            {
                new Connection(new Endpoint(file, FileContentInUnit.Filed), new Endpoint(work)),
                new Connection(new Endpoint(file, FileContentInUnit.Kept), new Endpoint(work))
            }
        };
    }
}
