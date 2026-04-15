using System.Text.Json;
using Shouldly;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class OpenCodeMapperTests
{
    // ---------------------------------------------------------------------------
    // ToHarnessMessage — UserMessage
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToHarnessMessage_UserMessage_MapsCorrectly()
    {
        var msg = new OpenCodeMessageWithParts
        {
            Info = new OpenCodeUserMessage
            {
                Id = "msg-user-1",
                SessionId = "sess-1",
                Time = new OpenCodeMessageTime { Created = 1_000_000L },
                Agent = "default",
            },
            Parts = [new OpenCodeTextPart { Text = "Hello" }],
        };

        var result = OpenCodeMapper.ToHarnessMessage(msg);

        result.Id.ShouldBe("msg-user-1");
        result.Role.ShouldBe("user");
        result.Timestamp.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000L));
        result.Parts.Count.ShouldBe(1);
        result.TextContent.ShouldBe("Hello");
    }

    // ---------------------------------------------------------------------------
    // ToHarnessMessage — AssistantMessage
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToHarnessMessage_AssistantMessage_MapsCorrectly()
    {
        var msg = new OpenCodeMessageWithParts
        {
            Info = new OpenCodeAssistantMessage
            {
                Id = "msg-asst-1",
                SessionId = "sess-1",
                Time = new OpenCodeMessageTime { Created = 2_000_000L },
                ModelId = "gpt-4o",
                ProviderId = "openai",
            },
            Parts = [],
        };

        var result = OpenCodeMapper.ToHarnessMessage(msg);

        result.Id.ShouldBe("msg-asst-1");
        result.Role.ShouldBe("assistant");
        result.Timestamp.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(2_000_000L));
        result.Parts.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------
    // ToHarnessMessage — ExtractsTextFromParts
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToHarnessMessage_ExtractsTextFromParts()
    {
        var msg = new OpenCodeMessageWithParts
        {
            Info = new OpenCodeAssistantMessage
            {
                Id = "msg-2",
                SessionId = "sess-1",
                Time = new OpenCodeMessageTime { Created = 0L },
            },
            Parts =
            [
                new OpenCodeTextPart { Text = "Part one. " },
                new OpenCodeTextPart { Text = "Part two." },
            ],
        };

        var result = OpenCodeMapper.ToHarnessMessage(msg);

        result.Parts.Count.ShouldBe(2);
        result.TextContent.ShouldBe("Part one. Part two.");
    }

    [Fact]
    public void ToHarnessMessage_MapsReasoningPart_WithoutAddingToTextContent()
    {
        var msg = new OpenCodeMessageWithParts
        {
            Info = new OpenCodeAssistantMessage
            {
                Id = "msg-reason-1",
                SessionId = "sess-1",
                Time = new OpenCodeMessageTime { Created = 0L },
            },
            Parts =
            [
                new OpenCodeReasoningPart { Text = "Let me think", Summary = "analysis" },
                new OpenCodeTextPart { Text = "Final answer" },
            ],
        };

        var result = OpenCodeMapper.ToHarnessMessage(msg);

        result.Parts.Count.ShouldBe(2);
        var reasoning = result.Parts[0].ShouldBeOfType<ReasoningPart>();
        reasoning.Text.ShouldBe("Let me think");
        reasoning.Summary.ShouldBe("analysis");
        result.TextContent.ShouldBe("Final answer");
    }

    // ---------------------------------------------------------------------------
    // ToHarnessMessage — ToolPart maps to ToolUsePart
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToHarnessMessage_ToolPart_MapsToToolUsePart()
    {
        var inputJson = JsonDocument.Parse("""{"command":"ls"}""").RootElement;
        var msg = new OpenCodeMessageWithParts
        {
            Info = new OpenCodeAssistantMessage
            {
                Id = "msg-3",
                SessionId = "sess-1",
                Time = new OpenCodeMessageTime { Created = 0L },
            },
            Parts =
            [
                new OpenCodeToolPart
                {
                    CallId = "call-1",
                    Tool = "bash",
                    State = new OpenCodeToolCompleted { Input = inputJson },
                },
            ],
        };

        var result = OpenCodeMapper.ToHarnessMessage(msg);

        result.Parts.Count.ShouldBe(1);
        var toolPart = result.Parts[0].ShouldBeOfType<ToolUsePart>();
        toolPart.ToolCallId.ShouldBe("call-1");
        toolPart.ToolName.ShouldBe("bash");
        toolPart.State.ShouldBe(ToolUseState.Completed);
    }

    [Fact]
    public void ToHarnessMessage_ToolPart_DoesNotPersistCompletedOutputBody()
    {
        var inputJson = JsonDocument.Parse("""{"command":"ls"}""").RootElement;
        var outputJson = JsonDocument.Parse("""{"result":"file.txt"}""").RootElement;
        var msg = new OpenCodeMessageWithParts
        {
            Info = new OpenCodeAssistantMessage
            {
                Id = "msg-tool-output-1",
                SessionId = "sess-1",
                Time = new OpenCodeMessageTime { Created = 0L },
            },
            Parts =
            [
                new OpenCodeToolPart
                {
                    CallId = "call-1",
                    Tool = "bash",
                    State = new OpenCodeToolCompleted { Input = inputJson, Output = outputJson },
                },
            ],
        };

        var result = OpenCodeMapper.ToHarnessMessage(msg);

        var toolPart = result.Parts[0].ShouldBeOfType<ToolUsePart>();
        toolPart.Arguments.GetProperty("command").GetString().ShouldBe("ls");
        toolPart.Arguments.ToString().ShouldNotContain("file.txt");
    }

    [Fact]
    public void ToHarnessMessage_FilePart_MapsToFilePart()
    {
        var msg = new OpenCodeMessageWithParts
        {
            Info = new OpenCodeAssistantMessage
            {
                Id = "msg-file-1",
                SessionId = "sess-1",
                Time = new OpenCodeMessageTime { Created = 0L },
            },
            Parts =
            [
                new OpenCodeFilePart
                {
                    Id = "file-1",
                    Mime = "image/png",
                    Url = "https://example.test/file.png",
                    Filename = "file.png",
                },
            ],
        };

        var result = OpenCodeMapper.ToHarnessMessage(msg);

        result.Parts.Count.ShouldBe(1);
        var filePart = result.Parts[0].ShouldBeOfType<FilePart>();
        filePart.PartId.ShouldBe("file-1");
        filePart.Mime.ShouldBe("image/png");
        filePart.Url.ShouldBe("https://example.test/file.png");
        filePart.Filename.ShouldBe("file.png");
    }

    [Fact]
    public void ToHarnessMessage_StepFinishPart_MapsToStepFinishPart()
    {
        var msg = new OpenCodeMessageWithParts
        {
            Info = new OpenCodeAssistantMessage
            {
                Id = "msg-step-1",
                SessionId = "sess-1",
                Time = new OpenCodeMessageTime { Created = 0L },
            },
            Parts =
            [
                new OpenCodeStepFinishPart
                {
                    Id = "step-1",
                    Index = 2,
                    Reason = "completed",
                },
            ],
        };

        var result = OpenCodeMapper.ToHarnessMessage(msg);

        result.Parts.Count.ShouldBe(1);
        var stepFinishPart = result.Parts[0].ShouldBeOfType<StepFinishPart>();
        stepFinishPart.Index.ShouldBe(2);
        stepFinishPart.Reason.ShouldBe("completed");
    }

    // ---------------------------------------------------------------------------
    // ToHarnessEvent
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToHarnessEvent_MapsTypeAndPayload()
    {
        var properties = JsonDocument.Parse("""{"sessionId":"sess-42","status":"idle"}""").RootElement;
        var evt = new OpenCodeSseEvent { Type = "session.status", Properties = properties };

        var before = DateTimeOffset.UtcNow;
        var result = OpenCodeMapper.ToHarnessEvent(evt, "fallback-session");
        var after = DateTimeOffset.UtcNow;

        result.Type.ShouldBe("session.status");
        result.SessionId.ShouldBe("sess-42"); // extracted from properties
        result.Timestamp.ShouldBeInRange(before, after);
        result.Payload!.Value.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void ToHarnessEvent_FallsBackToProvidedSessionId_WhenPropertiesLackIt()
    {
        var properties = JsonDocument.Parse("""{"status":"busy"}""").RootElement;
        var evt = new OpenCodeSseEvent { Type = "session.status", Properties = properties };

        var result = OpenCodeMapper.ToHarnessEvent(evt, "fallback-id");

        result.SessionId.ShouldBe("fallback-id");
    }

    [Fact]
    public void ToHarnessEvent_UsesNestedInfoSessionId_WhenTopLevelSessionIdMissing()
    {
        var properties = JsonDocument.Parse(
            """
            {
              "info": {
                "id": "msg-1",
                "sessionId": "nested-session",
                "role": "assistant"
              }
            }
            """).RootElement;
        var evt = new OpenCodeSseEvent { Type = "message.updated", Properties = properties };

        var result = OpenCodeMapper.ToHarnessEvent(evt, "fallback-id");

        result.SessionId.ShouldBe("nested-session");
    }

    [Fact]
    public void ToHarnessEvent_UsesNestedPartSessionId_WhenTopLevelSessionIdMissing()
    {
        var properties = JsonDocument.Parse(
            """
            {
              "part": {
                "id": "part-1",
                "sessionID": "part-session",
                "messageID": "msg-1",
                "type": "text",
                "text": "hello"
              }
            }
            """).RootElement;
        var evt = new OpenCodeSseEvent { Type = "message.part.updated", Properties = properties };

        var result = OpenCodeMapper.ToHarnessEvent(evt, "fallback-id");

        result.SessionId.ShouldBe("part-session");
    }

    // ---------------------------------------------------------------------------
    // ToHarnessAgents
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToHarnessAgents_MapsNameAndDescription()
    {
        var agents = new[]
        {
            new OpenCodeAgentInfo { Name = "coder", Description = "writes code", Mode = "build" },
            new OpenCodeAgentInfo { Name = "reviewer", Description = null, Mode = null },
        };

        var result = OpenCodeMapper.ToHarnessAgents(agents);

        result.Count.ShouldBe(2);
        result[0].Name.ShouldBe("coder");
        result[0].Description.ShouldBe("writes code");
        result[0].Mode.ShouldBe("build");
        result[1].Name.ShouldBe("reviewer");
        result[1].Description.ShouldBeNull();
        result[1].Mode.ShouldBeNull();
    }

    [Fact]
    public void ToHarnessAgents_SkipsAgentsWithNullName()
    {
        // Agents with null Name are filtered out — a nameless agent cannot be referenced
        var agents = new[]
        {
            new OpenCodeAgentInfo { Name = "valid-agent", Description = "desc" },
            new OpenCodeAgentInfo { Name = null, Description = "nameless" },
        };

        var result = OpenCodeMapper.ToHarnessAgents(agents);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("valid-agent");
    }

    [Fact]
    public void ToHarnessMessage_UserMessage_WithNullModelRefFields_DoesNotThrow()
    {
        // Reproduces the deserialization scenario where "model": {} produces an
        // OpenCodeModelRef with both fields null. The mapper must not throw.
        var msg = new OpenCodeMessageWithParts
        {
            Info = new OpenCodeUserMessage
            {
                Id = "msg-null-model",
                SessionId = "sess-1",
                Time = new OpenCodeMessageTime { Created = 1_000_000L },
                Model = new OpenCodeModelRef(), // ProviderId = null, ModelId = null
            },
            Parts = [new OpenCodeTextPart { Text = "test" }],
        };

        var result = OpenCodeMapper.ToHarnessMessage(msg);

        result.Id.ShouldBe("msg-null-model");
        result.Role.ShouldBe("user");
        result.Parts.Count.ShouldBe(1);
    }

    // ---------------------------------------------------------------------------
    // ToHarnessProviders
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToHarnessProviders_MapsProviderAndModels()
    {
        var response = new OpenCodeProvidersResponse
        {
            All =
            [
                new OpenCodeProviderInfo
                {
                    Id = "openai",
                    Name = "OpenAI",
                    Models = new Dictionary<string, OpenCodeProviderModel>
                    {
                        ["gpt-4o"] = new OpenCodeProviderModel { Id = "gpt-4o", Name = "GPT-4o" },
                        ["gpt-3.5-turbo"] = new OpenCodeProviderModel { Id = "gpt-3.5-turbo", Name = null },
                    },
                },
            ],
        };

        var result = OpenCodeMapper.ToHarnessProviders(response);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe("openai");
        result[0].Name.ShouldBe("OpenAI");
        result[0].Models.Count.ShouldBe(2);
        result[0].Models[0].Id.ShouldBe("gpt-4o");
        result[0].Models[0].Name.ShouldBe("GPT-4o");
        // Model with null Name falls back to Id
        result[0].Models[1].Name.ShouldBe("gpt-3.5-turbo");
    }

    // ---------------------------------------------------------------------------
    // DateTimeOffsetFromUnixMs
    // ---------------------------------------------------------------------------

    [Fact]
    public void DateTimeOffsetFromUnixMs_ConvertsCorrectly()
    {
        // Unix epoch = 0ms → 1970-01-01 00:00:00 UTC
        var epoch = OpenCodeMapper.DateTimeOffsetFromUnixMs(0L);
        epoch.ShouldBe(DateTimeOffset.UnixEpoch);

        // 1_000ms = 1 second after epoch
        var oneSecond = OpenCodeMapper.DateTimeOffsetFromUnixMs(1_000L);
        oneSecond.ShouldBe(DateTimeOffset.UnixEpoch.AddSeconds(1));
    }

    // ---------------------------------------------------------------------------
    // TryExtractTokenEvent
    // ---------------------------------------------------------------------------

    private static OpenCodeSseEvent MakeMessageUpdatedEvent(string propertiesJson)
    {
        var properties = JsonDocument.Parse(propertiesJson).RootElement;
        return new OpenCodeSseEvent { Type = "message.updated", Properties = properties };
    }

    // ---------------------------------------------------------------------------
    // ToHarnessMessages — skips messages with missing/unknown role discriminator
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToHarnessMessages_SkipsMessagesWithUnknownRole()
    {
        // Regression: messages deserialized as base OpenCodeMessageInfo (Role == "unknown")
        // because the role discriminator was missing must be silently skipped rather
        // than causing a downstream mapping error.
        var msgs = new List<OpenCodeMessageWithParts>
        {
            new()
            {
                Info = new OpenCodeUserMessage
                {
                    Id = "msg-1",
                    SessionId = "sess-1",
                    Time = new OpenCodeMessageTime { Created = 1_000_000L },
                },
                Parts = [new OpenCodeTextPart { Text = "Hello" }],
            },
            new()
            {
                // Base type — missing role discriminator, Role == "unknown"
                Info = new OpenCodeMessageInfo
                {
                    Id = "msg-bad",
                    SessionId = "sess-1",
                    Time = new OpenCodeMessageTime { Created = 2_000_000L },
                },
                Parts = [new OpenCodeTextPart { Text = "ghost" }],
            },
            new()
            {
                Info = new OpenCodeAssistantMessage
                {
                    Id = "msg-2",
                    SessionId = "sess-1",
                    Time = new OpenCodeMessageTime { Created = 3_000_000L },
                },
                Parts = [new OpenCodeTextPart { Text = "World" }],
            },
        };

        var result = OpenCodeMapper.ToHarnessMessages(msgs);

        result.Count.ShouldBe(2);
        result[0].Id.ShouldBe("msg-1");
        result[1].Id.ShouldBe("msg-2");
    }

    // ---------------------------------------------------------------------------
    // TryExtractTokenEvent
    // ---------------------------------------------------------------------------

    [Fact]
    public void TryExtractTokenEvent_ValidAssistantMessage_ReturnsTokenEventData()
    {
        var evt = MakeMessageUpdatedEvent("""
            {
              "info": {
                "id": "msg-1",
                "sessionId": "oc-sess-1",
                "role": "assistant",
                "time": { "created": 1000000 },
                "modelId": "claude-sonnet-4",
                "providerId": "anthropic",
                "tokens": {
                  "input": 100,
                  "output": 200,
                  "reasoning": 10,
                  "cache": { "read": 50, "write": 0 },
                  "total": 360
                },
                "cost": 0.005
              }
            }
            """);

        var result = OpenCodeMapper.TryExtractTokenEvent(
            evt, "fleet-sess-1", "proj-1", "MyProject", "/workspace");

        result.ShouldNotBeNull();
        result.EventId.ShouldBe("fleet-sess-1:msg-1");
        result.SessionId.ShouldBe("fleet-sess-1");
        result.ModelId.ShouldBe("claude-sonnet-4");
        result.ProviderId.ShouldBe("anthropic");
        result.TokensInput.ShouldBe(100);
        result.TokensOutput.ShouldBe(200);
        result.TokensReasoning.ShouldBe(10);
        result.TokensCacheRead.ShouldBe(50);
        result.TokensTotal.ShouldBe(360);
        result.Cost.ShouldBe(0.005);
        result.ProjectId.ShouldBe("proj-1");
        result.ProjectName.ShouldBe("MyProject");
    }

    [Fact]
    public void TryExtractTokenEvent_UserMessage_ReturnsNull()
    {
        var evt = MakeMessageUpdatedEvent("""
            {
              "info": {
                "id": "msg-user-1",
                "sessionId": "oc-sess-1",
                "role": "user",
                "time": { "created": 1000000 }
              }
            }
            """);

        var result = OpenCodeMapper.TryExtractTokenEvent(evt, "sess", null, null, null);

        result.ShouldBeNull();
    }

    [Fact]
    public void TryExtractTokenEvent_NonMessageUpdatedEvent_ReturnsNull()
    {
        var properties = JsonDocument.Parse("""{"status":"idle"}""").RootElement;
        var evt = new OpenCodeSseEvent { Type = "session.status", Properties = properties };

        var result = OpenCodeMapper.TryExtractTokenEvent(evt, "sess", null, null, null);

        result.ShouldBeNull();
    }

    [Fact]
    public void TryExtractTokenEvent_MessageCreatedEvent_ReturnsNull()
    {
        // message.created has unverified Properties structure — must be excluded
        var properties = JsonDocument.Parse("""{"info":{"id":"msg-1","role":"assistant","time":{"created":1000}}}""").RootElement;
        var evt = new OpenCodeSseEvent { Type = "message.created", Properties = properties };

        var result = OpenCodeMapper.TryExtractTokenEvent(evt, "sess", null, null, null);

        result.ShouldBeNull();
    }

    [Fact]
    public void TryExtractTokenEvent_AssistantMessageNoTokenData_ReturnsNull()
    {
        var evt = MakeMessageUpdatedEvent("""
            {
              "info": {
                "id": "msg-no-tokens",
                "sessionId": "oc-sess-1",
                "role": "assistant",
                "time": { "created": 1000000 }
              }
            }
            """);

        var result = OpenCodeMapper.TryExtractTokenEvent(evt, "sess", null, null, null);

        result.ShouldBeNull();
    }

    [Fact]
    public void TryExtractTokenEvent_PropertiesLacksInfoKey_ReturnsNull()
    {
        var properties = JsonDocument.Parse("""{"other":"value"}""").RootElement;
        var evt = new OpenCodeSseEvent { Type = "message.updated", Properties = properties };

        var result = OpenCodeMapper.TryExtractTokenEvent(evt, "sess", null, null, null);

        result.ShouldBeNull();
    }

    [Fact]
    public void TryExtractTokenEvent_EstimatedCost_ComputedForKnownModel()
    {
        var evt = MakeMessageUpdatedEvent("""
            {
              "info": {
                "id": "msg-cost",
                "sessionId": "oc-sess-1",
                "role": "assistant",
                "time": { "created": 1000000 },
                "modelId": "claude-sonnet-4-20250514",
                "tokens": { "input": 1000, "output": 500, "total": 1500 },
                "cost": 0.01
              }
            }
            """);

        var result = OpenCodeMapper.TryExtractTokenEvent(evt, "sess", null, null, null);

        result.ShouldNotBeNull();
        result.EstimatedCost.ShouldNotBeNull();
        (result.EstimatedCost > 0).ShouldBeTrue();
    }

    [Fact]
    public void TryExtractTokenEvent_AllZeroTokensAndCost_ReturnsNull()
    {
        // Events where both tokens.total and cost are zero carry no meaningful data
        // and should be filtered out to avoid unnecessary writes.
        var evt = MakeMessageUpdatedEvent("""
            {
              "info": {
                "id": "msg-zero",
                "sessionId": "oc-sess-1",
                "role": "assistant",
                "time": { "created": 1000000 },
                "modelId": "claude-sonnet-4",
                "tokens": {
                  "input": 0,
                  "output": 0,
                  "reasoning": 0,
                  "cache": { "read": 0, "write": 0 },
                  "total": 0
                },
                "cost": 0
              }
            }
            """);

        var result = OpenCodeMapper.TryExtractTokenEvent(evt, "sess", null, null, null);

        result.ShouldBeNull();
    }

    [Fact]
    public void TryExtractDelegation_TaskToolPending_ReturnsExtraction()
    {
        var evt = new OpenCodeSseEvent
        {
            Type = "message.part.updated",
            Properties = JsonDocument.Parse(
                """
                {
                  "part": {
                    "id": "part-1",
                    "sessionID": "oc-parent",
                    "messageID": "msg-1",
                    "type": "tool",
                    "tool": "task",
                    "callID": "tool-1",
                    "state": {
                      "status": "pending",
                      "input": {
                        "subagent_type": "reviewer",
                        "description": "Review the patch"
                      }
                    }
                  }
                }
                """).RootElement
        };

        var result = OpenCodeMapper.TryExtractDelegation(evt, "fleet-parent");

        result.ShouldNotBeNull();
        result.ParentSessionId.ShouldBe("fleet-parent");
        result.ToolCallId.ShouldBe("tool-1");
        result.Title.ShouldBe("reviewer");
        result.Status.ShouldBe("pending");
        result.ChildSessionId.ShouldBeNull();
    }

    [Fact]
    public void TryExtractDelegation_TaskToolRunningWithChildSession_ReturnsExtraction()
    {
        var evt = new OpenCodeSseEvent
        {
            Type = "message.part.updated",
            Properties = JsonDocument.Parse(
                """
                {
                  "part": {
                    "id": "part-1",
                    "sessionID": "oc-parent",
                    "messageID": "msg-1",
                    "type": "tool",
                    "tool": "task",
                    "callID": "tool-1",
                    "state": {
                      "status": "running",
                      "input": {
                        "subagent_type": "reviewer"
                      },
                      "metadata": {
                        "sessionId": "fleet-child"
                      }
                    }
                  }
                }
                """).RootElement
        };

        var result = OpenCodeMapper.TryExtractDelegation(evt, "fleet-parent");

        result.ShouldNotBeNull();
        result.Status.ShouldBe("running");
        result.ChildSessionId.ShouldBe("fleet-child");
    }

    [Fact]
    public void TryExtractDelegation_TaskToolRunningWithNestedChildMetadata_ReturnsExtraction()
    {
        var evt = new OpenCodeSseEvent
        {
            Type = "message.part.updated",
            Properties = JsonDocument.Parse(
                """
                {
                  "part": {
                    "id": "part-1",
                    "sessionID": "oc-parent",
                    "messageID": "msg-1",
                    "type": "tool",
                    "tool": "task",
                    "callID": "tool-1",
                    "state": {
                      "status": "running",
                      "input": {
                        "subagent_type": "reviewer"
                      },
                      "metadata": {
                        "child": {
                          "sessionId": "fleet-child"
                        }
                      }
                    }
                  }
                }
                """).RootElement
        };

        var result = OpenCodeMapper.TryExtractDelegation(evt, "fleet-parent");

        result.ShouldNotBeNull();
        result.ChildSessionId.ShouldBe("fleet-child");
    }

    [Fact]
    public void TryExtractDelegation_TaskToolRunningWithNestedSessionMetadata_ReturnsExtraction()
    {
        var evt = new OpenCodeSseEvent
        {
            Type = "message.part.updated",
            Properties = JsonDocument.Parse(
                """
                {
                  "part": {
                    "id": "part-1",
                    "sessionID": "oc-parent",
                    "messageID": "msg-1",
                    "type": "tool",
                    "tool": "task",
                    "callID": "tool-1",
                    "state": {
                      "status": "running",
                      "input": {
                        "subagent_type": "reviewer"
                      },
                      "metadata": {
                        "session": {
                          "session_id": "fleet-child"
                        }
                      }
                    }
                  }
                }
                """).RootElement
        };

        var result = OpenCodeMapper.TryExtractDelegation(evt, "fleet-parent");

        result.ShouldNotBeNull();
        result.ChildSessionId.ShouldBe("fleet-child");
    }

    [Fact]
    public void TryExtractDelegation_TaskToolCancelled_ReturnsExtraction()
    {
        var evt = new OpenCodeSseEvent
        {
            Type = "message.part.updated",
            Properties = JsonDocument.Parse(
                """
                {
                  "part": {
                    "id": "part-1",
                    "type": "tool",
                    "tool": "task",
                    "callID": "tool-1",
                    "state": {
                      "status": "cancelled",
                      "input": {
                        "subagent_type": "reviewer"
                      }
                    }
                  }
                }
                """).RootElement
        };

        var result = OpenCodeMapper.TryExtractDelegation(evt, "fleet-parent");

        result.ShouldNotBeNull();
        result.Status.ShouldBe("cancelled");
    }

    [Fact]
    public void TryExtractDelegation_UsesAgentField_WhenSubagentTypeMissing()
    {
        var evt = new OpenCodeSseEvent
        {
            Type = "message.part.updated",
            Properties = JsonDocument.Parse(
                """
                {
                  "part": {
                    "id": "part-1",
                    "type": "tool",
                    "tool": "task",
                    "callID": "tool-1",
                    "state": {
                      "status": "pending",
                      "input": {
                        "agent": "thread"
                      }
                    }
                  }
                }
                """).RootElement
        };

        var result = OpenCodeMapper.TryExtractDelegation(evt, "fleet-parent");

        result.ShouldNotBeNull();
        result.Title.ShouldBe("thread");
    }

    [Fact]
    public void TryExtractDelegation_SubtaskPartWithChildSession_ReturnsRunningExtraction()
    {
        var evt = new OpenCodeSseEvent
        {
            Type = "message.part.updated",
            Properties = JsonDocument.Parse(
                """
                {
                  "part": {
                    "id": "subtask-1",
                    "type": "subtask",
                    "callId": "tool-1",
                    "agent": "thread",
                    "description": "Investigate issue",
                    "metadata": {
                      "child": {
                        "sessionId": "child-1"
                      }
                    }
                  }
                }
                """).RootElement
        };

        var result = OpenCodeMapper.TryExtractDelegation(evt, "fleet-parent");

        result.ShouldNotBeNull();
        result.ToolCallId.ShouldBe("tool-1");
        result.Title.ShouldBe("thread");
        result.Status.ShouldBe("running");
        result.ChildSessionId.ShouldBe("child-1");
    }

    [Fact]
    public void TryExtractDelegation_SubtaskPartWithoutChildSession_ReturnsPendingExtraction()
    {
        var evt = new OpenCodeSseEvent
        {
            Type = "message.part.updated",
            Properties = JsonDocument.Parse(
                """
                {
                  "part": {
                    "id": "subtask-1",
                    "type": "subtask",
                    "callId": "tool-1",
                    "description": "Investigate issue",
                    "agent": "thread"
                  }
                }
                """).RootElement
        };

        var result = OpenCodeMapper.TryExtractDelegation(evt, "fleet-parent");

        result.ShouldNotBeNull();
        result.Status.ShouldBe("pending");
        result.ChildSessionId.ShouldBeNull();
    }

    [Fact]
    public void TryExtractDelegation_NonTaskTool_ReturnsNull()
    {
        var evt = new OpenCodeSseEvent
        {
            Type = "message.part.updated",
            Properties = JsonDocument.Parse(
                """
                {
                  "part": {
                    "id": "part-1",
                    "type": "tool",
                    "tool": "bash",
                    "callID": "tool-1",
                    "state": {
                      "status": "running",
                      "input": {
                        "subagent_type": "reviewer"
                      }
                    }
                  }
                }
                """).RootElement
        };

        var result = OpenCodeMapper.TryExtractDelegation(evt, "fleet-parent");

        result.ShouldBeNull();
    }
}
