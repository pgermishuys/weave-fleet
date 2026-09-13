using System.Text;
using WeaveFleet.Application.Terminals;

namespace WeaveFleet.Application.Tests.Terminals;

public sealed class TerminalHistoryTests
{
    [Fact]
    public void KeepsEverything_UnderTheCaps()
    {
        var history = new TerminalHistory(maxLines: 10, maxBytes: 1024);

        history.Append(Bytes("one\r\n"));
        history.Append(Bytes("two\r\n"));

        Text(history.ToArray()).ShouldBe("one\r\ntwo\r\n");
        history.LineBreaks.ShouldBe(2);
        history.Length.ShouldBe(10);
    }

    [Fact]
    public void DropsTheOldestLines_OverTheLineCap()
    {
        var history = new TerminalHistory(maxLines: 3, maxBytes: 1024);

        for (var i = 1; i <= 6; i++)
            history.Append(Bytes($"line {i}\n"));

        Text(history.ToArray()).ShouldBe("line 4\nline 5\nline 6\n");
        history.LineBreaks.ShouldBe(3);
    }

    [Fact]
    public void KeepsAPartialLastLine_WhenTrimmingByLines()
    {
        var history = new TerminalHistory(maxLines: 2, maxBytes: 1024);

        history.Append(Bytes("a\nb\nc\n$ "));

        Text(history.ToArray()).ShouldBe("b\nc\n$ ");
    }

    [Fact]
    public void DropsTheOldestLines_OverTheByteCap_CuttingAtALineBreak()
    {
        var history = new TerminalHistory(maxLines: 1000, maxBytes: 12);

        history.Append(Bytes("aaaa\nbbbb\ncccc\n"));

        Text(history.ToArray()).ShouldBe("bbbb\ncccc\n");
        history.Length.ShouldBe(10);
    }

    [Fact]
    public void CutsALongLineAtACharacterBoundary_WhenThereIsNoLineBreak()
    {
        var history = new TerminalHistory(maxLines: 1000, maxBytes: 5);

        history.Append(Bytes("ab✓✓")); // 2 + 3 + 3 bytes

        var kept = Text(history.ToArray());
        kept.ShouldBe("✓");
        history.Length.ShouldBeLessThanOrEqualTo(5);
    }

    [Fact]
    public void HandlesAppendsLargerThanOneChunk()
    {
        var history = new TerminalHistory(maxLines: 100_000, maxBytes: 1024 * 1024);
        var big = string.Concat(Enumerable.Range(0, 5000).Select(i => $"row {i:D5}\n"));

        history.Append(Bytes(big));

        Text(history.ToArray()).ShouldBe(big);
        history.LineBreaks.ShouldBe(5000);
    }

    [Fact]
    public void TrimsAcrossChunks()
    {
        var history = new TerminalHistory(maxLines: 2, maxBytes: 1024 * 1024);
        var big = string.Concat(Enumerable.Range(0, 5000).Select(i => $"row {i:D5}\n"));

        history.Append(Bytes(big));

        Text(history.ToArray()).ShouldBe("row 04998\nrow 04999\n");
    }

    [Fact]
    public void Load_ReplacesAndAppliesTheCaps()
    {
        var history = new TerminalHistory(maxLines: 2, maxBytes: 1024);
        history.Append(Bytes("old\n"));

        history.Load(Bytes("1\n2\n3\n"));

        Text(history.ToArray()).ShouldBe("2\n3\n");
    }

    [Fact]
    public void Clear_EmptiesIt()
    {
        var history = new TerminalHistory(maxLines: 10, maxBytes: 1024);
        history.Append(Bytes("x\n"));

        history.Clear();

        history.ToArray().ShouldBeEmpty();
        history.Length.ShouldBe(0);
        history.LineBreaks.ShouldBe(0);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
