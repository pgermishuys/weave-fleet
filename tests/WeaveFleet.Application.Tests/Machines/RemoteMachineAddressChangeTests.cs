using System.Net;
using Shouldly;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>
/// Moving a machine to another address must not send it the token saved for the old one: the new address could be anyone.
/// </summary>
public sealed class RemoteMachineAddressChangeTests : IDisposable
{
    private const string Identity = """{"id":"machine-atlas","name":"atlas","apiVersion":1,"authMode":"token"}""";

    private readonly FakeMachine _atlas = new();

    public void Dispose() => _atlas.Dispose();

    [Fact]
    public async Task A_new_address_without_a_new_token_is_refused_and_the_saved_token_is_not_sent_there()
    {
        _atlas.Answer = _ => (HttpStatusCode.OK, Identity);

        var (machine, error) = await _atlas.Service.UpdateAsync(FakeMachine.Id, "https://elsewhere.test", null, null, CancellationToken.None);

        machine.ShouldBeNull();
        error.ShouldNotBeNullOrEmpty();
        _atlas.Requests.ShouldBeEmpty();
        (await _atlas.Machines.GetAsync(FakeMachine.Id))!.BaseUrl.ShouldBe("https://atlas.test");
    }

    [Fact]
    public async Task A_new_address_with_a_new_token_is_checked_with_that_token()
    {
        _atlas.Answer = _ => (HttpStatusCode.OK, Identity);

        var (machine, error) = await _atlas.Service.UpdateAsync(FakeMachine.Id, "https://elsewhere.test", "fmt_new", null, CancellationToken.None);

        error.ShouldBeNull();
        machine!.BaseUrl.ShouldBe("https://elsewhere.test");
        _atlas.Requests.ShouldHaveSingleItem().Token.ShouldBe("fmt_new");
    }

    [Fact]
    public async Task The_same_address_again_keeps_the_saved_token()
    {
        _atlas.Answer = _ => (HttpStatusCode.OK, Identity);

        var (_, error) = await _atlas.Service.UpdateAsync(FakeMachine.Id, "https://atlas.test/", null, null, CancellationToken.None);

        error.ShouldBeNull();
        _atlas.Requests.ShouldHaveSingleItem().Token.ShouldBe(FakeMachine.Token);
    }
}
