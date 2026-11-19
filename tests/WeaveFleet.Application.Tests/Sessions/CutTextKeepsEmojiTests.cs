using WeaveFleet.Application.Push;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.Tests.Sessions;

public sealed class CutTextKeepsEmojiTests
{
    private const string Rocket = "\U0001F680";

    [Fact]
    public void Head_StepsBackOffAHighSurrogate()
    {
        TextCut.Head("ab" + Rocket + "cd", 3).ShouldBe("ab");
        TextCut.Head("ab" + Rocket + "cd", 4).ShouldBe("ab" + Rocket);
        TextCut.Head("abc", 10).ShouldBe("abc");
        TextCut.Head("abc", 0).ShouldBe(string.Empty);
    }

    [Fact]
    public void Shorten_AReplyCutInsideAnEmoji_HasNoBrokenCharacter()
    {
        var reply = new string('a', SessionUpdateSender.MaxReplyLength - 1) + Rocket + "tail";

        var shortened = SessionUpdateSender.Shorten(reply);

        shortened.ShouldNotContain('�');
        HasLoneSurrogate(shortened).ShouldBeFalse();
    }

    [Fact]
    public void PushClip_CutInsideAnEmoji_HasNoBrokenCharacter()
    {
        var clipped = PushPayload.Clip(new string('a', 8) + Rocket + "tail", 10);

        HasLoneSurrogate(clipped).ShouldBeFalse();
    }

    private static bool HasLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            else if (char.IsSurrogate(text[i]))
                return true;
        }
        return false;
    }
}
