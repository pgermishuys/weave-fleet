using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

public sealed class HarnessSignInServiceTests
{
    private const string Key = "sk-dummy-0123456789";

    private readonly FakeHarnessRegistry _registry = new();
    private readonly FakeHarnessRuntime _runtime = new("opencode2");
    private readonly RecordingSignIn _signIn = new();
    private readonly FleetOptions _options = new();
    private readonly RecordingLogger _logs = new();
    private readonly HarnessSignInService _sut;

    public HarnessSignInServiceTests()
    {
        _runtime.ProviderSignIn = _signIn;
        _registry.Register(_runtime);
        _registry.Register(new FakeHarness("opencode2", "OpenCode 2", new HarnessCapabilities { SupportsProviderSignIn = true }));
        _sut = new HarnessSignInService(_registry, new TestUserContext("user-1"), _options, _logs);
    }

    [Fact]
    public async Task Signs_in_as_the_signed_in_user_with_the_key_and_answers()
    {
        var answers = new Dictionary<string, JsonElement> { ["resourceName"] = JsonDocument.Parse("\"my-models\"").RootElement };

        var result = await _sut.SignInWithKeyAsync("opencode2", "azure", $"  {Key}  ", answers, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var call = _signIn.KeySignIns.ShouldHaveSingleItem();
        call.Owner.ShouldBe("user-1");
        call.Provider.ShouldBe("azure");
        call.Key.ShouldBe(Key);
        call.Answers["resourceName"].GetString().ShouldBe("my-models");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task An_empty_key_is_refused_before_the_harness_sees_it(string? key)
    {
        // OpenCode 2 stores an empty key as a sign-in of its own.
        var result = await _sut.SignInWithKeyAsync("opencode2", "anthropic", key, null, CancellationToken.None);

        result.Error.Code.ShouldBe("Validation.SignIn.Key");
        _signIn.KeySignIns.ShouldBeEmpty();
    }

    [Fact]
    public async Task Not_offered_when_Fleet_runs_with_sign_in()
    {
        // The harness's sign-ins are the machine's: one user's key would pay for every user's sessions.
        _options.Auth.Enabled = true;

        var result = await _sut.ListAsync("opencode2", CancellationToken.None);

        result.Error.Code.ShouldBe("Validation.SignIn.Unsupported");
        result.Error.Description.ShouldContain("when Fleet runs with sign-in");
        _signIn.Lists.ShouldBe(0);
        HarnessSignInService.Supports(new HarnessCapabilities { SupportsProviderSignIn = true }, _options).ShouldBeFalse();
    }

    [Fact]
    public async Task Not_offered_in_cloud_mode()
    {
        _options.Cloud.Enabled = true;

        var result = await _sut.SignInWithKeyAsync("opencode2", "anthropic", Key, null, CancellationToken.None);

        result.Error.Code.ShouldBe("Validation.SignIn.Unsupported");
        _signIn.KeySignIns.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_harness_without_sign_in_says_so()
    {
        _registry.Register(new FakeHarness("claude-code", "Claude Code", new HarnessCapabilities()));
        _registry.Register(new FakeHarnessRuntime("claude-code"));

        var result = await _sut.ListAsync("claude-code", CancellationToken.None);

        result.Error.Description.ShouldBe("Fleet can't sign in to Claude Code's providers.");
    }

    [Fact]
    public async Task An_unknown_harness_is_not_found()
    {
        var result = await _sut.ListAsync("nope", CancellationToken.None);

        result.Error.Code.ShouldEndWith(".NotFound");
    }

    [Fact]
    public async Task The_harness_refusal_comes_back_as_its_reason()
    {
        _signIn.Throw = new HarnessSignInException("Missing required form field: resourceName");

        var result = await _sut.SignInWithKeyAsync("opencode2", "azure", Key, null, CancellationToken.None);

        result.Error.Code.ShouldBe("Validation.SignIn.Refused");
        result.Error.Description.ShouldBe("Missing required form field: resourceName");
    }

    [Fact]
    public async Task Something_the_harness_does_not_know_is_not_found()
    {
        _signIn.Throw = new HarnessSignInException("Integration not found: nope", notFound: true);

        var result = await _sut.SignInWithKeyAsync("opencode2", "nope", Key, null, CancellationToken.None);

        result.Error.Code.ShouldEndWith(".NotFound");
    }

    [Fact]
    public async Task A_network_error_is_neither_repeated_nor_logged()
    {
        // A transport error can quote what was sent; only its type is logged, and the user gets Fleet's own sentence.
        _signIn.Throw = new HttpRequestException($"Something went wrong sending {Key}");

        var result = await _sut.SignInWithKeyAsync("opencode2", "anthropic", Key, null, CancellationToken.None);

        result.Error.Code.ShouldBe("SignIn.Failed");
        result.Error.Description.ShouldBe("OpenCode 2 didn't answer when Fleet tried to sign in. Try again.");
        _logs.Lines.ShouldHaveSingleItem().ShouldContain("HttpRequestException");
        _logs.Lines.ShouldAllBe(line => !line.Contains(Key));
    }

    [Fact]
    public async Task A_harness_that_is_not_installed_says_so()
    {
        _signIn.Throw = new InvalidOperationException("OpenCode 2 isn't installed. Set it up in Settings → Harnesses.");

        var result = await _sut.ListAsync("opencode2", CancellationToken.None);

        result.Error.Description.ShouldBe("OpenCode 2 isn't installed. Set it up in Settings → Harnesses.");
    }

    [Fact]
    public async Task A_code_is_passed_on_trimmed()
    {
        var result = await _sut.SubmitCodeAsync("opencode2", "openai", "con_1", " abc#def ", CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _signIn.Codes.ShouldHaveSingleItem().ShouldBe(("openai", "con_1", "abc#def"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("file:///etc/passwd?code=1")]
    [InlineData("http://localhost:1455/auth/callback")]
    public async Task A_pasted_address_needs_to_be_the_page_with_its_query(string address)
    {
        var result = await _sut.ForwardCallbackAsync("opencode2", "openai", "con_1", address, CancellationToken.None);

        result.Error.Code.ShouldBe("Validation.SignIn.Address");
        _signIn.Callbacks.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_pasted_address_goes_to_the_harness()
    {
        var result = await _sut.ForwardCallbackAsync(
            "opencode2", "openai", "con_1", " http://localhost:1455/auth/callback?code=abc&state=xyz ", CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _signIn.Callbacks.ShouldHaveSingleItem().ShouldBe(("con_1", "http://localhost:1455/auth/callback?code=abc&state=xyz"));
    }

    [Fact]
    public async Task Starting_needs_a_method()
    {
        var result = await _sut.StartAsync("opencode2", "openai", " ", null, CancellationToken.None);

        result.Error.Code.ShouldBe("Validation.SignIn.Id");
    }

    [Fact]
    public async Task Switches_and_signs_out_by_sign_in()
    {
        (await _sut.UseAsync("opencode2", "cred_1", CancellationToken.None)).IsSuccess.ShouldBeTrue();
        (await _sut.SignOutAsync("opencode2", "cred_2", CancellationToken.None)).IsSuccess.ShouldBeTrue();

        _signIn.Used.ShouldBe(["cred_1"]);
        _signIn.SignedOut.ShouldBe(["cred_2"]);
    }

    private sealed class RecordingSignIn : IHarnessProviderSignIn
    {
        public Exception? Throw { get; set; }
        public int Lists { get; private set; }
        public List<(string Owner, string Provider, string Key, IReadOnlyDictionary<string, JsonElement> Answers)> KeySignIns { get; } = [];
        public List<(string Provider, string Attempt, string Code)> Codes { get; } = [];
        public List<(string Attempt, string Address)> Callbacks { get; } = [];
        public List<string> Used { get; } = [];
        public List<string> SignedOut { get; } = [];

        private Task Run(Action record)
        {
            if (Throw is not null)
                throw Throw;
            record();
            return Task.CompletedTask;
        }

        public async Task<HarnessSignIns> ListAsync(string ownerUserId, CancellationToken ct)
        {
            await Run(() => Lists++);
            return new HarnessSignIns([], null);
        }

        public Task SignInWithKeyAsync(string ownerUserId, string providerId, string key, IReadOnlyDictionary<string, JsonElement> answers, CancellationToken ct)
            => Run(() => KeySignIns.Add((ownerUserId, providerId, key, answers)));

        public async Task<HarnessSignInAttempt> StartAsync(string ownerUserId, string providerId, string methodId, IReadOnlyDictionary<string, JsonElement> answers, CancellationToken ct)
        {
            await Run(() => { });
            return new HarnessSignInAttempt("con_1", "https://example.com", "", false, DateTimeOffset.UnixEpoch);
        }

        public async Task<HarnessSignInAttemptStatus> GetAttemptAsync(string ownerUserId, string providerId, string attemptId, CancellationToken ct)
        {
            await Run(() => { });
            return new HarnessSignInAttemptStatus(HarnessSignInAttemptStates.Pending);
        }

        public Task SubmitCodeAsync(string ownerUserId, string providerId, string attemptId, string code, CancellationToken ct)
            => Run(() => Codes.Add((providerId, attemptId, code)));

        public Task ForwardCallbackAsync(string ownerUserId, string providerId, string attemptId, Uri landedOn, CancellationToken ct)
            => Run(() => Callbacks.Add((attemptId, landedOn.OriginalString)));

        public Task CancelAsync(string ownerUserId, string providerId, string attemptId, CancellationToken ct) => Run(() => { });

        public Task UseAsync(string ownerUserId, string connectionId, CancellationToken ct) => Run(() => Used.Add(connectionId));

        public Task SignOutAsync(string ownerUserId, string connectionId, CancellationToken ct) => Run(() => SignedOut.Add(connectionId));
    }

    private sealed class RecordingLogger : ILogger<HarnessSignInService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
