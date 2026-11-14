using System.Text.Json;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>
/// OpenCode's history shows where it compacted the conversation as a <see cref="CompactionPart"/> divider, and marks
/// its own summary message so it shows once, behind its divider, not again as a reply (see <c>CompactionDividerTests</c>
/// for every harness).
/// </summary>
public sealed class OpenCodeCompactionTests
{
    [Fact]
    public void OpenCode_history_shows_its_compaction_part_as_a_divider_and_marks_its_summary_message()
    {
        var compaction = OpenCodeMapper.ToHarnessMessage(new OpenCodeMessageWithParts
        {
            Info = new OpenCodeUserMessage { Id = "msg_1", SessionId = "ses_1", Time = new OpenCodeMessageTime { Created = 1 } },
            Parts = [new OpenCodeCompactionPart { Id = "prt_1", Auto = true }],
        });
        var summary = OpenCodeMapper.ToHarnessMessage(new OpenCodeMessageWithParts
        {
            Info = new OpenCodeAssistantMessage { Id = "msg_2", SessionId = "ses_1", Time = new OpenCodeMessageTime { Created = 2 }, Mode = "compaction", Summary = JsonDocument.Parse("true").RootElement },
            Parts = [new OpenCodeTextPart { Id = "prt_2", Text = "What we did so far." }],
        });

        var divider = compaction.Parts.ShouldHaveSingleItem().ShouldBeOfType<CompactionPart>();
        divider.Trigger.ShouldBe(ContextCompactionTriggers.Auto);
        divider.PartId.ShouldBe("prt_1");
        summary.CompactionSummary.ShouldBeTrue();
        compaction.CompactionSummary.ShouldBeFalse();
    }
}
