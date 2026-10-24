using System.Net;
using Shouldly;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>
/// Adding a machine with a one-time code: ask who answers (the code isn't used up), refuse this machine, redeem the
/// code for a token of its own, then check that token against the machine like any other add.
/// </summary>
public sealed class RemoteMachineAddByCodeTests : IDisposable
{
    private const string Preview = """{"machineId":"machine-falcon","machineName":"falcon","os":"Linux","expiresAt":"2026-10-08T07:40:00Z"}""";
    private const string Redeemed = """{"deviceId":"d1","token":"fdt_d1.secret","machine":{"id":"machine-falcon","name":"falcon"}}""";
    private const string Identity = """{"id":"machine-falcon","name":"falcon","os":"Linux","apiVersion":1,"authMode":"token"}""";

    private readonly FakeMachine _falcon = new();

    public void Dispose() => _falcon.Dispose();

    [Fact]
    public async Task A_code_adds_the_machine_with_a_token_of_its_own()
    {
        _falcon.Answer = line => line switch
        {
            "POST /api/pairing/preview" => (HttpStatusCode.OK, Preview),
            "POST /api/pairing/redeem" => (HttpStatusCode.OK, Redeemed),
            "GET /api/machine" => (HttpStatusCode.OK, Identity),
            _ => null,
        };

        var (machine, error) = await _falcon.Service.AddByCodeAsync("http://falcon.test:2113/", "abcd-efgh", CancellationToken.None);

        error.ShouldBeNull();
        machine!.Id.ShouldBe("machine-falcon");
        machine.BaseUrl.ShouldBe("http://falcon.test:2113");
        _falcon.Service.TokenOf(machine).ShouldBe("fdt_d1.secret");

        var redeem = _falcon.Requests.Single(r => r.Request == "POST /api/pairing/redeem");
        redeem.Token.ShouldBeNull();
        var sent = redeem.Body.ShouldNotBeNull();
        sent.ShouldContain("\"kind\":\"machine\"");
        sent.ShouldContain("\"manualCode\":\"abcd-efgh\"");
        _falcon.Requests.Single(r => r.Request == "GET /api/machine").Token.ShouldBe("fdt_d1.secret");
    }

    [Fact]
    public async Task An_expired_code_says_how_to_get_a_new_one()
    {
        var (machine, error) = await _falcon.Service.AddByCodeAsync("http://falcon.test:2113", "ABCD-EFGH", CancellationToken.None);

        machine.ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain("fleet node pair");
        _falcon.Requests.ShouldNotContain(r => r.Request == "POST /api/pairing/redeem");
    }

    [Fact]
    public async Task This_machine_is_refused_before_the_code_is_used()
    {
        var self = new FakeMachine();
        using var disposeSelf = self;
        self.Answer = line => line == "POST /api/pairing/preview"
            ? (HttpStatusCode.OK, $$"""{"machineId":"{{self.SelfId}}","machineName":"me","os":"Linux","expiresAt":"2026-10-08T07:40:00Z"}""")
            : null;

        var (machine, error) = await self.Service.AddByCodeAsync("http://me.test:2113", "ABCD-EFGH", CancellationToken.None);

        machine.ShouldBeNull();
        error.ShouldBe("That's this machine.");
        self.Requests.ShouldNotContain(r => r.Request == "POST /api/pairing/redeem");
    }

    [Theory]
    [InlineData("not an address ::", "ABCD-EFGH")]
    [InlineData("http://falcon.test:2113", "")]
    [InlineData("http://falcon.test:2113", "12")]
    public async Task A_bad_address_or_code_sends_nothing(string address, string code)
    {
        var (machine, error) = await _falcon.Service.AddByCodeAsync(address, code, CancellationToken.None);

        machine.ShouldBeNull();
        error.ShouldNotBeNullOrEmpty();
        _falcon.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_machine_that_is_off_says_so()
    {
        _falcon.Away = true;

        var (_, error) = await _falcon.Service.AddByCodeAsync("http://falcon.test:2113", "ABCD-EFGH", CancellationToken.None);

        error.ShouldNotBeNull().ShouldContain("Couldn't reach");
    }
}
