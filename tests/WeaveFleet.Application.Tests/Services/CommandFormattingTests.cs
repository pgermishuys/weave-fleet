using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Tests.Services;

public sealed class CommandFormattingTests
{
    [Fact]
    public void FormatCommandPrompt_NullOptions_Throws()
    {
        Should.Throw<ArgumentNullException>(() => CommandFormatting.FormatCommandPrompt(null!));
    }

    [Fact]
    public void FormatCommandPrompt_NoArguments_ReturnsSlashCommand()
    {
        var options = new CommandOptions { Command = "help" };
        CommandFormatting.FormatCommandPrompt(options).ShouldBe("/help");
    }

    [Fact]
    public void FormatCommandPrompt_WhitespaceArguments_ReturnsSlashCommandOnly()
    {
        var options = new CommandOptions { Command = "help", Arguments = "   " };
        CommandFormatting.FormatCommandPrompt(options).ShouldBe("/help");
    }

    [Fact]
    public void FormatCommandPrompt_WithArguments_ReturnsSlashCommandAndArgs()
    {
        var options = new CommandOptions { Command = "run", Arguments = "tests --verbose" };
        CommandFormatting.FormatCommandPrompt(options).ShouldBe("/run tests --verbose");
    }

    [Fact]
    public void FormatCommandPrompt_NewlinesInArguments_CollapsedToSpaces()
    {
        var options = new CommandOptions { Command = "run", Arguments = "line1\nline2\r\nline3" };
        var result = CommandFormatting.FormatCommandPrompt(options);
        result.ShouldNotContain("\n");
        result.ShouldNotContain("\r");
        result.ShouldBe("/run line1 line2 line3");
    }

    [Fact]
    public void FormatCommandPrompt_NullArguments_ReturnsSlashCommandOnly()
    {
        var options = new CommandOptions { Command = "status", Arguments = null };
        CommandFormatting.FormatCommandPrompt(options).ShouldBe("/status");
    }
}
