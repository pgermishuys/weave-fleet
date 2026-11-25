using System.Reflection;
using System.Text;
using Shouldly;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Memory;

namespace WeaveFleet.Api.Tests;

/// <summary>User text cut to a length must never end on half an emoji: a lone surrogate becomes U+FFFD (or a throw) in JSON and SQLite.</summary>
public sealed class EmojiCutTests
{
    public static TheoryData<Type, string> Cutters => new()
    {
        { typeof(MachinePeerEndpoints), "Clip" },
        { typeof(FleetRoster), "Clip" },
        { typeof(DeviceGrantService), "Clip" },
        { typeof(AgentMemoryBridge), "Shorten" },
        { typeof(CanvasEndpoints), "Shorten" },
    };

    [Theory]
    [MemberData(nameof(Cutters))]
    public void A_cut_never_leaves_half_an_emoji(Type owner, string name)
    {
        var method = owner.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        method.ShouldNotBeNull();
        var parameters = method.GetParameters();
        var limit = parameters.Length > 1 ? 12 : 200;

        // The emoji's two halves astride the limit.
        var text = new string('a', limit - 1) + "😀" + new string('b', 20);
        var args = parameters.Length > 1 ? new object?[] { text, limit } : [text];
        var cut = (string)method.Invoke(null, args)!;

        cut.ShouldNotBeEmpty();
        // A lone surrogate doesn't survive UTF-8: it comes back as U+FFFD.
        Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(cut)).ShouldBe(cut);
        cut.ShouldNotContain('�');
    }
}
