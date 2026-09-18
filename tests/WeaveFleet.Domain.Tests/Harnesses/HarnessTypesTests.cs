using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Domain.Tests.Harnesses;

public sealed class HarnessTypesTests
{
    [Fact]
    public void HarnessCapabilities_DefaultsToAllFalse()
    {
        var caps = new HarnessCapabilities();
        caps.RequiresInitialPrompt.ShouldBeFalse();
        caps.SupportsAgents.ShouldBeFalse();
        caps.SupportsModelSelection.ShouldBeFalse();
        caps.SupportsCommands.ShouldBeFalse();
        caps.SupportsForking.ShouldBeFalse();
        caps.SupportsResume.ShouldBeFalse();
        caps.SupportsImageAttachments.ShouldBeFalse();
        caps.SupportsStreaming.ShouldBeFalse();
    }

    [Fact]
    public void HarnessCapabilities_WithInitReturnsNewInstance()
    {
        var caps = new HarnessCapabilities { SupportsStreaming = true, RequiresInitialPrompt = true };
        caps.SupportsStreaming.ShouldBeTrue();
        caps.RequiresInitialPrompt.ShouldBeTrue();
        caps.SupportsAgents.ShouldBeFalse();
    }

    [Fact]
    public void HarnessAvailability_RecordEquality()
    {
        var a = new HarnessAvailability(true, null);
        var b = new HarnessAvailability(true, null);
        a.ShouldBe(b);
    }

    [Fact]
    public void HarnessAvailability_DefaultsItsStateFromAvailable()
    {
        new HarnessAvailability(true, null).State.ShouldBe(HarnessStates.Ready);
        new HarnessAvailability(false, "broken").State.ShouldBe(HarnessStates.NotWorking);
    }

    [Fact]
    public void HarnessAvailability_FactoriesSayWhatTheHarnessNeeds()
    {
        var ready = HarnessAvailability.Ready("1.18.30", "/home/you/.opencode/bin/opencode");
        (ready.Available, ready.State, ready.Version, ready.ExecutablePath)
            .ShouldBe((true, HarnessStates.Ready, "1.18.30", "/home/you/.opencode/bin/opencode"));

        var missing = HarnessAvailability.NotInstalled("OpenCode isn't installed.");
        (missing.Available, missing.State, missing.Reason).ShouldBe((false, HarnessStates.NotInstalled, "OpenCode isn't installed."));

        var signIn = HarnessAvailability.SignInRequired("Sign in.", "2.1.276", "/home/you/.local/bin/claude");
        (signIn.Available, signIn.State, signIn.Version).ShouldBe((false, HarnessStates.SignInRequired, "2.1.276"));
    }

    [Theory]
    [InlineData("1.18.30", "1.18.31", -1)]
    [InlineData("1.18.31", "1.18.31", 0)]
    [InlineData("1.9.0", "1.15.10", -1)]
    [InlineData("2.1.276", "2.1.99", 1)]
    [InlineData("v1.15", "1.15.0", 0)]
    [InlineData("1.16.0-beta.2", "1.15.10", 1)]
    public void HarnessVersion_ComparesNumbersNotText(string a, string b, int expected)
    {
        Math.Sign(HarnessVersion.Compare(a, b)).ShouldBe(expected);
    }

    [Fact]
    public void HarnessSessionStatus_HasExpectedValues()
    {
        var values = Enum.GetValues<HarnessSessionStatus>();
        values.Length.ShouldBe(6);
        values.ShouldContain(HarnessSessionStatus.Starting);
        values.ShouldContain(HarnessSessionStatus.Error);
    }

    [Fact]
    public void HealthCheckResult_RecordEquality()
    {
        var a = new HealthCheckResult(true, null);
        var b = new HealthCheckResult(true, null);
        a.ShouldBe(b);
    }

    [Fact]
    public void HarnessMessage_RequiresAllProperties()
    {
        var msg = new HarnessMessage
        {
            Id = "msg-1",
            Role = "assistant",
            Parts = [new TextPart("Hello")],
            Timestamp = DateTimeOffset.UtcNow
        };
        msg.Role.ShouldBe("assistant");
        msg.TextContent.ShouldBe("Hello");
    }

    [Fact]
    public void HarnessMessage_TextContent_ExcludesReasoningParts()
    {
        var msg = new HarnessMessage
        {
            Id = "msg-2",
            Role = "assistant",
            Parts = [new ReasoningPart("Private thought"), new TextPart("Visible answer")],
            Timestamp = DateTimeOffset.UtcNow
        };

        msg.TextContent.ShouldBe("Visible answer");
    }

    [Fact]
    public void CommandOptions_Validate_AllowsColonForNamespacedCommands()
    {
        var options = new CommandOptions { Command = "weave:start" };
        options.Validate().ShouldBeNull();
    }

    [Theory]
    [InlineData("help")]
    [InlineData("run-tests")]
    [InlineData("do_thing")]
    [InlineData("weave:start")]
    [InlineData("a:b:c")]
    public void CommandOptions_Validate_AllowsLettersDigitsHyphensUnderscoresAndColons(string command)
    {
        var options = new CommandOptions { Command = command };
        options.Validate().ShouldBeNull();
    }

    [Fact]
    public void CommandOptions_Validate_RejectsMissingCommand()
    {
        var options = new CommandOptions { Command = "   " };
        options.Validate().ShouldBe("Command name is required.");
    }

    [Fact]
    public void CommandOptions_Validate_RejectsTooLongCommand()
    {
        var options = new CommandOptions { Command = new string('a', 65) };
        options.Validate().ShouldBe("Command name exceeds 64 characters.");
    }

    [Fact]
    public void CommandOptions_Validate_RejectsInvalidCharacterAndMentionsColonInMessage()
    {
        var options = new CommandOptions { Command = "bad/command" };
        options.Validate().ShouldBe(
            "Command name contains invalid character '/'. Only letters, digits, hyphens, underscores, and colons are allowed.");
    }

    [Fact]
    public void CommandOptions_Validate_RejectsTooLongArguments()
    {
        var options = new CommandOptions { Command = "help", Arguments = new string('a', 4097) };
        options.Validate().ShouldBe("Arguments exceed 4096 characters.");
    }
}
