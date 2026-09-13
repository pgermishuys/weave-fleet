using System.Text;
using WeaveFleet.Application.Terminals;

namespace WeaveFleet.Application.Tests.Terminals;

public sealed class TerminalReplaySanitizerTests
{
    private const string Esc = "";

    [Theory]
    [InlineData("plain text\r\n")]
    [InlineData(Esc + "[31mred" + Esc + "[0m")]
    [InlineData(Esc + "[2J" + Esc + "[H")]
    [InlineData(Esc + "[!p")]                       // DECSTR, a setter
    [InlineData(Esc + "[2 q")]                      // DECSCUSR, cursor shape
    [InlineData(Esc + "[u")]                        // restore cursor
    [InlineData(Esc + "[?2004h")]                   // bracketed paste on
    [InlineData(Esc + "]0;zsh: ~/src")]       // window title
    [InlineData(Esc + "]8;;https://example.com" + Esc + "\\link" + Esc + "]8;;" + Esc + "\\")]
    [InlineData(Esc + "Pq#0;2;0;0;0" + Esc + "\\")] // sixel DCS
    [InlineData(Esc + "7" + Esc + "8")]             // save and restore cursor
    [InlineData(Esc + "(B")]                        // charset
    public void Keeps(string text)
    {
        Run(text).ShouldBe(text);
    }

    [Theory]
    [InlineData(Esc + "[6n")]              // cursor position request
    [InlineData(Esc + "[5n")]              // status request
    [InlineData(Esc + "[12;40R")]          // cursor position report
    [InlineData(Esc + "[c")]               // primary device attributes
    [InlineData(Esc + "[>0c")]             // secondary device attributes
    [InlineData(Esc + "[?1;2c")]           // device attributes reply
    [InlineData(Esc + "[?2026$p")]         // DECRQM
    [InlineData(Esc + "[?2026;2$y")]       // DECRPM
    [InlineData(Esc + "[>q")]              // XTVERSION
    [InlineData(Esc + "[?u")]              // kitty keyboard query
    [InlineData(Esc + "]11;?")]      // background colour query, BEL
    [InlineData(Esc + "]10;?" + Esc + "\\")] // foreground colour query, ST
    [InlineData(Esc + "]11;rgb:1e1e/1e1e/1e1e" + Esc + "\\")]
    [InlineData(Esc + "P$qm" + Esc + "\\")]  // DECRQSS
    [InlineData(Esc + "P+q544e" + Esc + "\\")] // XTGETTCAP
    [InlineData(Esc + "P1$r0m" + Esc + "\\")] // DECRQSS reply
    public void Strips(string query)
    {
        Run("a" + query + "b").ShouldBe("ab");
    }

    [Fact]
    public void Strips_AQuerySplitAcrossChunks()
    {
        var sanitizer = new TerminalReplaySanitizer();

        var first = sanitizer.Process(Bytes("prompt " + Esc + "["));
        var second = sanitizer.Process(Bytes("6n$ "));

        Text(first).ShouldBe("prompt ");
        Text(second).ShouldBe("$ ");
    }

    [Fact]
    public void Strips_AnOscQuerySplitAtItsTerminator()
    {
        var sanitizer = new TerminalReplaySanitizer();

        var output = Text(sanitizer.Process(Bytes("x" + Esc + "]11;?" + Esc))) + Text(sanitizer.Process(Bytes("\\y")));

        output.ShouldBe("xy");
    }

    [Fact]
    public void HoldsALoneEscapeUntilTheNextChunk()
    {
        var sanitizer = new TerminalReplaySanitizer();

        Text(sanitizer.Process(Bytes("end" + Esc))).ShouldBe("end");
        Text(sanitizer.Process(Bytes("[1mbold"))).ShouldBe(Esc + "[1mbold");
    }

    [Fact]
    public void KeepsUtf8ThatContainsC1Bytes()
    {
        // U+201B is E2 80 9B: a 0x9B byte inside an ordinary character, not a CSI.
        const string text = "quote ‛here’ and © done";

        Run(text).ShouldBe(text);
    }

    [Fact]
    public void KeepsText_WhenACsiIsBrokenByAControlByte()
    {
        Run(Esc + "[12\nnext").ShouldBe(Esc + "[12\nnext");
    }

    [Fact]
    public void GivesUpHoldingAnUnfinishedSequenceThatNeverEnds()
    {
        var sanitizer = new TerminalReplaySanitizer();
        var runaway = Esc + "]0;" + new string('x', 5000);

        Text(sanitizer.Process(Bytes(runaway))).ShouldBe(runaway);
        sanitizer.Flush().ShouldBeEmpty();
    }

    [Fact]
    public void Flush_ReturnsWhatWasHeldBack()
    {
        var sanitizer = new TerminalReplaySanitizer();
        sanitizer.Process(Bytes("a" + Esc + "[3"));

        Text(sanitizer.Flush()).ShouldBe(Esc + "[3");
        sanitizer.Flush().ShouldBeEmpty();
    }

    private static string Run(string text) => Text(new TerminalReplaySanitizer().Process(Bytes(text)));

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
