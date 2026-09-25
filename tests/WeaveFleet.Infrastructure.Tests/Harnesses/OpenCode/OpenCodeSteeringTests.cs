using System.Text.Json;
using Shouldly;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>
/// OpenCode reads a prompt sent while a turn runs at the turn's next step. Fleet marks such a prompt on its text part,
/// which OpenCode keeps and never sends to the model, so history can say it went in mid-turn.
/// </summary>
public sealed class OpenCodeSteeringTests
{
    [Fact]
    public void a_steered_prompt_carries_the_mark_on_its_text_part()
    {
        var request = new OpenCodePromptRequest
        {
            Parts =
            [
                new OpenCodePromptTextPart
                {
                    Text = "stop, wrong file",
                    Metadata = new OpenCodePromptPartMetadata { FleetDelivery = OpenCodePromptPartMetadata.Steer },
                },
            ],
        };

        var json = JsonSerializer.Serialize(request, OpenCodeJsonContext.Default.OpenCodePromptRequest);

        json.ShouldContain("""{"type":"text","text":"stop, wrong file","metadata":{"fleetDelivery":"steer"}}""");
    }

    [Fact]
    public void a_prompt_that_waited_for_the_turn_has_no_metadata()
    {
        var request = new OpenCodePromptRequest { Parts = [new OpenCodePromptTextPart { Text = "after the turn" }] };

        JsonSerializer.Serialize(request, OpenCodeJsonContext.Default.OpenCodePromptRequest).ShouldNotContain("metadata");
    }

    [Fact]
    public void history_shows_the_marked_prompt_as_steered()
    {
        var messages = OpenCodeMapper.ToHarnessMessages(
        [
            UserMessage("msg_1", "run the tests", metadata: null),
            new OpenCodeMessageWithParts
            {
                Info = new OpenCodeAssistantMessage { Id = "msg_2", SessionId = "ses_1", Time = new OpenCodeMessageTime { Created = 2 }, Finish = "tool-calls" },
                Parts = [new OpenCodeTextPart { Text = "Running them" }],
            },
            UserMessage("msg_3", "stop, wrong file", metadata: """{"fleetDelivery":"steer"}"""),
            UserMessage("msg_4", "something else", metadata: """{"source":"elsewhere"}"""),
        ]);

        messages.Select(m => (m.Id, m.Steered)).ShouldBe([("msg_1", false), ("msg_2", false), ("msg_3", true), ("msg_4", false)]);
    }

    private static OpenCodeMessageWithParts UserMessage(string id, string text, string? metadata) => new()
    {
        Info = new OpenCodeUserMessage { Id = id, SessionId = "ses_1", Time = new OpenCodeMessageTime { Created = 1 } },
        Parts = [new OpenCodeTextPart { Text = text, Metadata = metadata is null ? null : JsonDocument.Parse(metadata).RootElement.Clone() }],
    };
}
