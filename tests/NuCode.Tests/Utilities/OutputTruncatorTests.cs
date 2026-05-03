using NuCode.Utilities;

namespace NuCode;

public sealed class OutputTruncatorTests
{
    [Fact]
    public void ReturnsUnchangedWhenUnderBothLimits()
    {
        var text = "line1\nline2\nline3";

        var result = OutputTruncator.Truncate(text);

        result.Content.ShouldBe(text);
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void TruncatesWhenExceedingMaxLines()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"line {i}");
        var text = string.Join('\n', lines);

        var result = OutputTruncator.Truncate(text, maxLines: 10);

        result.Truncated.ShouldBeTrue();
        result.Content.ShouldContain("90 lines truncated");
        result.Content.ShouldContain("line 1");
        result.Content.ShouldContain("line 10");
        result.Content.ShouldNotContain("line 11");
    }

    [Fact]
    public void TruncatesWhenExceedingMaxBytes()
    {
        // Each line is ~10 bytes + \n. 200 lines = ~2200 bytes.
        var lines = Enumerable.Range(1, 200).Select(i => $"data-{i:D4}");
        var text = string.Join('\n', lines);

        var result = OutputTruncator.Truncate(text, maxBytes: 500);

        result.Truncated.ShouldBeTrue();
        result.Content.ShouldContain("bytes truncated");
    }

    [Fact]
    public void EmptyStringReturnsUnchanged()
    {
        var result = OutputTruncator.Truncate("");

        result.Content.ShouldBe("");
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void SingleLineUnderLimitReturnsUnchanged()
    {
        var result = OutputTruncator.Truncate("hello world");

        result.Content.ShouldBe("hello world");
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void ExactlyAtLineLimitReturnsUnchanged()
    {
        var lines = Enumerable.Range(1, 10).Select(i => $"line {i}");
        var text = string.Join('\n', lines);

        var result = OutputTruncator.Truncate(text, maxLines: 10);

        result.Content.ShouldBe(text);
        result.Truncated.ShouldBeFalse();
    }
}
