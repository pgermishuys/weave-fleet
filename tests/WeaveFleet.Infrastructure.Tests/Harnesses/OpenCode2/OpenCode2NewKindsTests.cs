using System.Text.Json;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>Kinds OpenCode 2.3 added (a <c>video</c> model, a <c>codemode</c> job) that Fleet doesn't use must read, not throw.</summary>
public sealed class OpenCode2NewKindsTests
{
    [Fact]
    public void A_video_model_reads_like_any_other_in_the_catalog()
    {
        var model = JsonSerializer.Deserialize(
            """{"id":"clip-1","providerID":"p","name":"Clip","type":"video","limit":{"context":0,"output":0}}""",
            OpenCode2JsonContext.Default.OpenCode2ModelInfo);

        model.ShouldNotBeNull().Id.ShouldBe("clip-1");
    }

    [Fact]
    public void A_codemode_job_event_reads_as_an_event_Fleet_has_no_use_for()
    {
        var evt = JsonSerializer.Deserialize(
            """{"id":"evt_1","created":1789804433751,"type":"job.updated","data":{"id":"job_1","kind":"codemode","status":"running"}}""",
            OpenCode2JsonContext.Default.OpenCode2Event);

        evt.ShouldNotBeNull().Type.ShouldBe("job.updated");
    }
}
