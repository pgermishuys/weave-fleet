using WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

namespace WeaveFleet.Infrastructure.Tests.Plugins.BuiltIn.GitHub;

public sealed class CiLogParserTests
{
    [Fact]
    public void extract_relevant_log_lines_returns_empty_for_null_input()
    {
        var result = CiLogParser.ExtractRelevantLogLines(string.Empty);
        result.ShouldBe(string.Empty);
    }

    [Fact]
    public void extract_relevant_log_lines_strips_timestamp_prefixes()
    {
        var input = "2024-01-01T00:00:00.0000000Z Line one\n2024-01-01T00:00:01.0000000Z Line two";
        var result = CiLogParser.ExtractRelevantLogLines(input);
        result.ShouldContain("Line one");
        result.ShouldContain("Line two");
        result.ShouldNotContain("2024-01-01T");
    }

    [Fact]
    public void extract_relevant_log_lines_returns_last_n_lines_when_no_errors()
    {
        var lines = Enumerable.Range(1, 300).Select(i => $"Line {i}");
        var input = string.Join('\n', lines);

        var result = CiLogParser.ExtractRelevantLogLines(input, maxLines: 200);
        var resultLines = result.Split('\n');

        resultLines.Length.ShouldBeLessThanOrEqualTo(200);
        result.ShouldContain("Line 300");
        result.ShouldNotContain("Line 1\n");
    }

    [Fact]
    public void extract_relevant_log_lines_prioritizes_error_lines()
    {
        var lines = Enumerable.Range(1, 100)
            .Select(i => i == 50 ? "##[error]Build failed" : $"Normal line {i}")
            .ToList();
        var input = string.Join('\n', lines);

        var result = CiLogParser.ExtractRelevantLogLines(input);

        result.ShouldContain("##[error]Build failed");
        // Should include context (5 lines around the error)
        result.ShouldContain("Normal line 45");
        result.ShouldContain("Normal line 55");
    }

    [Fact]
    public void extract_relevant_log_lines_includes_context_around_errors()
    {
        var lines = new List<string>();
        for (var i = 0; i < 20; i++)
            lines.Add($"Line {i}");
        lines[10] = "Error: something went wrong";

        var input = string.Join('\n', lines);
        var result = CiLogParser.ExtractRelevantLogLines(input);

        // Should include 5 lines before and after
        result.ShouldContain("Line 5");
        result.ShouldContain("Line 15");
        result.ShouldContain("Error: something went wrong");
    }

    [Fact]
    public void extract_relevant_log_lines_respects_max_lines()
    {
        var manyErrorLines = Enumerable.Range(1, 500)
            .Select(i => $"##[error]Failure {i}")
            .ToList();
        var input = string.Join('\n', manyErrorLines);

        var result = CiLogParser.ExtractRelevantLogLines(input, maxLines: 50);
        var resultLines = result.Split('\n');

        resultLines.Length.ShouldBeLessThanOrEqualTo(50);
    }

    [Fact]
    public void extract_relevant_log_lines_handles_exception_markers()
    {
        const string input = "Normal line\nException: NullReferenceException\n   at SomeMethod()";

        var result = CiLogParser.ExtractRelevantLogLines(input);

        result.ShouldContain("Exception: NullReferenceException");
    }

    [Fact]
    public void extract_relevant_log_lines_adds_ellipsis_between_non_contiguous_blocks()
    {
        var lines = Enumerable.Range(1, 100)
            .Select(i => i == 10 ? "##[error]First error" : i == 90 ? "##[error]Second error" : $"Line {i}")
            .ToList();
        var input = string.Join('\n', lines);

        var result = CiLogParser.ExtractRelevantLogLines(input);

        result.ShouldContain("...");
        result.ShouldContain("##[error]First error");
        result.ShouldContain("##[error]Second error");
    }
}
