using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// Sign-in to OpenCode 2's providers over its integration API. Response shapes are from a real 2.0.9 server in a
/// scratch HOME, signed in with dummy keys only.
/// </summary>
public sealed class OpenCode2SignInTests
{
    private const string Key = "sk-dummy-FLEETTEST-0000";

    // GET /api/integration on 2.0.9, cut down to the kinds of provider there are: two stored keys (the one in use
    // first) and a variable from the environment, OAuth by browser and by device, forms with choices and conditions.
    private const string Integrations = """
        {"location":{"directory":"/home/you"},"data":[
          {"id":"openai","name":"OpenAI","methods":[
            {"type":"key"},{"type":"env","names":["OPENAI_API_KEY"]},
            {"id":"chatgpt-browser","type":"oauth","label":"ChatGPT Pro/Plus (browser)"},
            {"id":"chatgpt-headless","type":"oauth","label":"ChatGPT Pro/Plus (headless)"}],
           "connections":[]},
          {"id":"anthropic","name":"Anthropic","methods":[{"type":"key"},{"type":"env","names":["ANTHROPIC_API_KEY"]}],
           "connections":[
             {"type":"credential","id":"cred_work","label":"Work"},
             {"type":"credential","id":"cred_home","label":"Anthropic"},
             {"type":"env","name":"ANTHROPIC_API_KEY"}]},
          {"id":"github-copilot","name":"GitHub Copilot","methods":[
            {"type":"env","names":["GITHUB_TOKEN"]},
            {"id":"device","type":"oauth","label":"Login with GitHub Copilot","form":[
              {"key":"deploymentType","title":"Select GitHub deployment type","required":true,"type":"string","options":[
                {"value":"github.com","label":"GitHub.com","description":"Public"},
                {"value":"enterprise","label":"GitHub Enterprise","description":"Data residency or self-hosted"}]},
              {"key":"enterpriseUrl","title":"Enter your GitHub Enterprise URL or domain","required":true,
               "when":[{"key":"deploymentType","op":"eq","value":"enterprise"}],"type":"string","placeholder":"company.ghe.com"}]}],
           "connections":[]},
          {"id":"azure","name":"Azure","methods":[
            {"type":"key","label":"API key","form":[{"key":"resourceName","title":"Enter Azure Resource Name","required":true,"type":"string","placeholder":"e.g. my-models"}]},
            {"type":"env","names":["AZURE_API_KEY","AZURE_COGNITIVE_SERVICES_API_KEY"]}],
           "connections":[]},
          {"id":"opencode","name":"OpenCode","methods":[
            {"id":"device","type":"oauth","label":"OpenCode Console account","form":[
              {"key":"server","hidden":true,"type":"string","format":"uri","default":"https://opencode.ai/console"}]},
            {"id":"az","type":"command","label":"Azure CLI","command":["az","account","get-access-token"]},
            {"type":"something-new"}],
           "connections":[{"type":"env","name":"OPENCODE_API_KEY"}]}
        ]}
        """;

    // POST /api/integration/openai/connect/oauth with chatgpt-browser on 2.0.9: V2 listens on localhost:1455.
    private const string BrowserAttempt = """
        {"location":{"directory":"/home/you"},"data":{"attemptID":"con_browser",
          "url":"https://auth.openai.com/oauth/authorize?response_type=code&client_id=app_x&redirect_uri=http%3A%2F%2Flocalhost%3A1455%2Fauth%2Fcallback&scope=openid&state=s1",
          "instructions":"Complete authorization in your browser. This window will close automatically.","mode":"auto",
          "time":{"created":1790113905285,"expires":1790114505285}}}
        """;

    // POST /api/integration/github-copilot/connect/oauth with device on 2.0.9: V2 polls GitHub itself.
    private const string DeviceAttempt = """
        {"location":{"directory":"/home/you"},"data":{"attemptID":"con_device","url":"https://github.com/login/device",
          "instructions":"Enter code: ABCD-1234","mode":"auto","time":{"created":1790113930070,"expires":1790114530070}}}
        """;

    [Fact]
    public async Task Lists_every_provider_by_name_with_the_sign_in_in_use_first()
    {
        var (signIn, _) = SignIn(Answer(Integrations));

        var list = await signIn.ListAsync("local-user", CancellationToken.None);

        list.Providers.Select(p => p.Id).ShouldBe(["anthropic", "azure", "github-copilot", "openai", "opencode"]);
        var anthropic = list.Providers.Single(p => p.Id == "anthropic");
        anthropic.Connections.ShouldBe(
        [
            new HarnessSignInConnection("credential", "cred_work", "Work", Active: true),
            new HarnessSignInConnection("credential", "cred_home", "Anthropic", Active: false),
            new HarnessSignInConnection("env", "ANTHROPIC_API_KEY", "ANTHROPIC_API_KEY", Active: false),
        ]);
    }

    [Fact]
    public async Task A_provider_signed_in_only_through_the_environment_uses_it()
    {
        var (signIn, _) = SignIn(Answer(Integrations));

        var list = await signIn.ListAsync("local-user", CancellationToken.None);

        list.Providers.Single(p => p.Id == "opencode").Connections
            .ShouldHaveSingleItem().ShouldBe(new HarnessSignInConnection("env", "OPENCODE_API_KEY", "OPENCODE_API_KEY", Active: true));
    }

    [Fact]
    public async Task Describes_each_way_to_sign_in()
    {
        var (signIn, _) = SignIn(Answer(Integrations));

        var providers = (await signIn.ListAsync("local-user", CancellationToken.None)).Providers;

        var openai = providers.Single(p => p.Id == "openai").Methods;
        openai.Select(m => (m.Type, m.Id, m.Label)).ShouldBe(
        [
            ("key", null, "API key"),
            ("env", null, "Environment variable"),
            ("oauth", "chatgpt-browser", "ChatGPT Pro/Plus (browser)"),
            ("oauth", "chatgpt-headless", "ChatGPT Pro/Plus (headless)"),
        ]);
        openai[1].EnvironmentVariables.ShouldBe(["OPENAI_API_KEY"]);

        var opencode = providers.Single(p => p.Id == "opencode").Methods;
        opencode.Select(m => m.Type).ShouldBe(["oauth", "command"]); // a kind Fleet doesn't know is left out
        opencode[1].Command.ShouldBe(["az", "account", "get-access-token"]);
        var server = opencode[0].Fields.ShouldHaveSingleItem();
        server.Hidden.ShouldBeTrue();
        server.Default!.Value.GetString().ShouldBe("https://opencode.ai/console");
    }

    [Fact]
    public async Task A_form_keeps_its_choices_and_when_each_field_is_asked()
    {
        var (signIn, _) = SignIn(Answer(Integrations));

        var providers = (await signIn.ListAsync("local-user", CancellationToken.None)).Providers;

        var fields = providers.Single(p => p.Id == "github-copilot").Methods.Single(m => m.Type == "oauth").Fields;
        fields[0].Options!.Select(o => (o.Value, o.Label, o.Description)).ShouldBe(
            [("github.com", "GitHub.com", "Public"), ("enterprise", "GitHub Enterprise", "Data residency or self-hosted")]);
        var condition = fields[1].When.ShouldHaveSingleItem();
        (condition.Key, condition.Op, condition.Value.GetString()).ShouldBe(("deploymentType", "eq", "enterprise"));
        fields[1].Required.ShouldBeTrue();
        fields[1].Placeholder.ShouldBe("company.ghe.com");
    }

    [Theory]
    [InlineData(true, "OpenCode 1's sign-ins don't carry over")]
    [InlineData(false, "the same as running opencode2 auth login")]
    public async Task Says_where_the_sign_ins_are_kept(bool separate, string says)
    {
        var (signIn, _) = SignIn(Answer(Integrations), separate ? OpenCode2InstallMode.Separate : OpenCode2InstallMode.Default);

        (await signIn.ListAsync("local-user", CancellationToken.None)).Note!.ShouldContain(says);
    }

    [Fact]
    public async Task A_key_goes_only_in_the_body_of_one_request()
    {
        var api = Answer("", HttpStatusCode.NoContent);
        var (signIn, _) = SignIn(api);
        var answers = new Dictionary<string, JsonElement> { ["resourceName"] = JsonDocument.Parse("\"my-models\"").RootElement };

        await signIn.SignInWithKeyAsync("local-user", "azure", Key, answers, CancellationToken.None);

        var request = Sent(api).ShouldHaveSingleItem();
        (request.Method, request.Path).ShouldBe((HttpMethod.Post, "/api/integration/azure/connect/key"));
        var body = JsonDocument.Parse(request.Body!).RootElement;
        body.GetProperty("key").GetString().ShouldBe(Key);
        body.GetProperty("answer").GetProperty("resourceName").GetString().ShouldBe("my-models");
        api.Uris.ShouldAllBe(uri => !uri.Contains("FLEETTEST"));
    }

    [Fact]
    public async Task No_answers_sends_no_answer()
    {
        var api = Answer("", HttpStatusCode.NoContent);
        var (signIn, _) = SignIn(api);

        await signIn.SignInWithKeyAsync("local-user", "anthropic", Key, new Dictionary<string, JsonElement>(), CancellationToken.None);

        JsonDocument.Parse(Sent(api)[0].Body!).RootElement.TryGetProperty("answer", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task V2s_refusal_comes_back_in_its_own_words()
    {
        var (signIn, _) = SignIn(Answer(
            """{"_tag":"InvalidRequestError","message":"Missing required form field: resourceName","kind":"integration_authorization"}""",
            HttpStatusCode.BadRequest));

        var error = await Should.ThrowAsync<HarnessSignInException>(
            () => signIn.SignInWithKeyAsync("local-user", "azure", Key, new Dictionary<string, JsonElement>(), CancellationToken.None));

        error.Message.ShouldBe("Missing required form field: resourceName");
        error.NotFound.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_provider_is_not_found()
    {
        var (signIn, _) = SignIn(Answer(
            """{"_tag":"IntegrationNotFoundError","integrationID":"nope","message":"Integration not found: nope"}""",
            HttpStatusCode.NotFound));

        var error = await Should.ThrowAsync<HarnessSignInException>(
            () => signIn.SignInWithKeyAsync("local-user", "nope", Key, new Dictionary<string, JsonElement>(), CancellationToken.None));

        error.NotFound.ShouldBeTrue();
    }

    [Fact]
    public async Task A_refusal_that_quotes_the_key_is_not_repeated()
    {
        var (signIn, _) = SignIn(Answer($$"""{"_tag":"InvalidRequestError","message":"Bad key {{Key}}"}""", HttpStatusCode.BadRequest));

        var error = await Should.ThrowAsync<HarnessSignInException>(
            () => signIn.SignInWithKeyAsync("local-user", "anthropic", Key, new Dictionary<string, JsonElement>(), CancellationToken.None));

        error.Message.ShouldBe("OpenCode 2 didn't accept that key.");
    }

    [Fact]
    public async Task A_server_error_says_what_failed_and_not_what_V2_answered()
    {
        var (signIn, _) = SignIn(Answer($$"""{"error":"boom while storing {{Key}}"}""", HttpStatusCode.InternalServerError));

        var error = await Should.ThrowAsync<HttpRequestException>(
            () => signIn.SignInWithKeyAsync("local-user", "anthropic", Key, new Dictionary<string, JsonElement>(), CancellationToken.None));

        error.Message.ShouldBe("OpenCode 2 couldn't sign in: 500 Internal Server Error.");
    }

    [Fact]
    public async Task A_browser_sign_in_that_comes_back_to_this_machine_says_where()
    {
        var api = Answer(BrowserAttempt);
        var (signIn, _) = SignIn(api);

        var attempt = await signIn.StartAsync("local-user", "openai", "chatgpt-browser", new Dictionary<string, JsonElement>(), CancellationToken.None);

        attempt.Id.ShouldBe("con_browser");
        attempt.Url.ShouldStartWith("https://auth.openai.com/oauth/authorize?");
        attempt.NeedsCode.ShouldBeFalse();
        attempt.CallbackAddress.ShouldBe("http://localhost:1455/auth/callback");
        attempt.ExpiresAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1790114505285));
        JsonDocument.Parse(Sent(api)[0].Body!).RootElement.GetProperty("methodID").GetString().ShouldBe("chatgpt-browser");
    }

    [Fact]
    public async Task A_device_sign_in_finishes_anywhere()
    {
        var (signIn, _) = SignIn(Answer(DeviceAttempt));

        var attempt = await signIn.StartAsync("local-user", "github-copilot", "device", new Dictionary<string, JsonElement>(), CancellationToken.None);

        attempt.Instructions.ShouldBe("Enter code: ABCD-1234");
        attempt.CallbackAddress.ShouldBeNull();
    }

    [Fact]
    public async Task A_sign_in_in_code_mode_asks_for_the_code()
    {
        var (signIn, _) = SignIn(Answer(BrowserAttempt.Replace("\"mode\":\"auto\"", "\"mode\":\"code\"")));

        var attempt = await signIn.StartAsync("local-user", "openai", "chatgpt-browser", new Dictionary<string, JsonElement>(), CancellationToken.None);

        attempt.NeedsCode.ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{"data":{"status":"pending","time":{"created":1,"expires":2}}}""", HttpStatusCode.OK, "pending", null)]
    [InlineData("""{"data":{"status":"failed","message":"access_denied","time":{"created":1,"expires":2}}}""", HttpStatusCode.OK, "failed", "access_denied")]
    [InlineData("""{"data":{"status":"complete","time":{"created":1,"expires":2}}}""", HttpStatusCode.OK, "complete", null)]
    [InlineData("""{"_tag":"IntegrationAttemptNotFoundError","message":"OAuth attempt not found: con_1"}""", HttpStatusCode.NotFound, "gone", null)]
    public async Task Follows_a_browser_sign_in(string response, HttpStatusCode status, string state, string? message)
    {
        var api = Answer(response, status);
        var (signIn, _) = SignIn(api);

        var attempt = await signIn.GetAttemptAsync("local-user", "openai", "con_1", CancellationToken.None);

        attempt.ShouldBe(new HarnessSignInAttemptStatus(state, message));
        Sent(api).ShouldHaveSingleItem().Path.ShouldBe("/api/integration/openai/connect/oauth/con_1");
    }

    [Fact]
    public async Task The_page_a_browser_elsewhere_landed_on_is_passed_to_the_listener_here()
    {
        var (signIn, callbacks) = SignIn(Answer(BrowserAttempt));
        await signIn.StartAsync("local-user", "openai", "chatgpt-browser", new Dictionary<string, JsonElement>(), CancellationToken.None);

        // Copied from a phone's address bar: its host is the phone's own localhost, which is why the page didn't load.
        await signIn.ForwardCallbackAsync("local-user", "openai", "con_browser",
            new Uri("http://localhost:1455/auth/callback?code=abc&state=s1"), CancellationToken.None);

        callbacks.Uris.ShouldBe(["http://localhost:1455/auth/callback?code=abc&state=s1"]);
    }

    [Fact]
    public async Task Only_the_query_of_a_pasted_address_is_used()
    {
        var (signIn, callbacks) = SignIn(Answer(BrowserAttempt));
        await signIn.StartAsync("local-user", "openai", "chatgpt-browser", new Dictionary<string, JsonElement>(), CancellationToken.None);

        await signIn.ForwardCallbackAsync("local-user", "openai", "con_browser",
            new Uri("http://127.0.0.2:1455/auth/callback?error=access_denied"), CancellationToken.None);

        callbacks.Uris.ShouldBe(["http://localhost:1455/auth/callback?error=access_denied"]);
    }

    [Theory]
    [InlineData("http://localhost:1455/somewhere/else?code=abc")]
    [InlineData("http://localhost:9999/auth/callback?code=abc")]
    [InlineData("https://auth.openai.com/oauth/authorize?code=abc")]
    public async Task Another_page_is_refused_before_the_listener_sees_it(string address)
    {
        // A listener that gets the wrong query fails the sign-in, so nothing is sent.
        var (signIn, callbacks) = SignIn(Answer(BrowserAttempt));
        await signIn.StartAsync("local-user", "openai", "chatgpt-browser", new Dictionary<string, JsonElement>(), CancellationToken.None);

        var error = await Should.ThrowAsync<HarnessSignInException>(
            () => signIn.ForwardCallbackAsync("local-user", "openai", "con_browser", new Uri(address), CancellationToken.None));

        error.Message.ShouldContain("http://localhost:1455/auth/callback");
        callbacks.Uris.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_sign_in_that_does_not_come_back_here_has_nothing_to_pass_on()
    {
        var (signIn, callbacks) = SignIn(Answer(DeviceAttempt));
        await signIn.StartAsync("local-user", "github-copilot", "device", new Dictionary<string, JsonElement>(), CancellationToken.None);

        var error = await Should.ThrowAsync<HarnessSignInException>(() => signIn.ForwardCallbackAsync(
            "local-user", "github-copilot", "con_device", new Uri("http://localhost:1455/auth/callback?code=abc"), CancellationToken.None));

        error.NotFound.ShouldBeTrue();
        callbacks.Uris.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_expired_sign_in_takes_no_callback()
    {
        var (signIn, callbacks) = SignIn(Answer(BrowserAttempt.Replace("1790114505285", "1790113935285")));
        await signIn.StartAsync("local-user", "openai", "chatgpt-browser", new Dictionary<string, JsonElement>(), CancellationToken.None);

        await Should.ThrowAsync<HarnessSignInException>(() => signIn.ForwardCallbackAsync(
            "local-user", "openai", "con_browser", new Uri("http://localhost:1455/auth/callback?code=abc"), CancellationToken.None));

        callbacks.Uris.ShouldBeEmpty();
    }

    [Fact]
    public async Task Another_user_cannot_pass_on_a_callback()
    {
        var (signIn, callbacks) = SignIn(Answer(BrowserAttempt));
        await signIn.StartAsync("local-user", "openai", "chatgpt-browser", new Dictionary<string, JsonElement>(), CancellationToken.None);

        await Should.ThrowAsync<HarnessSignInException>(() => signIn.ForwardCallbackAsync(
            "someone-else", "openai", "con_browser", new Uri("http://localhost:1455/auth/callback?code=abc"), CancellationToken.None));

        callbacks.Uris.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cancelled_sign_in_stops_in_V2_and_takes_no_callback()
    {
        var api = Serving(request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : Json(BrowserAttempt));
        var (signIn, callbacks) = SignIn(api);
        await signIn.StartAsync("local-user", "openai", "chatgpt-browser", new Dictionary<string, JsonElement>(), CancellationToken.None);

        await signIn.CancelAsync("local-user", "openai", "con_browser", CancellationToken.None);

        Sent(api)[^1].ShouldBe((HttpMethod.Delete, "/api/integration/openai/connect/oauth/con_browser", null));
        await Should.ThrowAsync<HarnessSignInException>(() => signIn.ForwardCallbackAsync(
            "local-user", "openai", "con_browser", new Uri("http://localhost:1455/auth/callback?code=abc"), CancellationToken.None));
        callbacks.Uris.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_sign_in_under_way_keeps_its_server_busy_until_V2_says_it_has_ended()
    {
        // V2 keeps the attempt in this server's memory, and a server whose settings changed is replaced once it's idle.
        var status = "pending";
        var api = IdleServing(request => Json(request.Method == HttpMethod.Post
            ? BrowserAttempt
            : """{"data":{"status":"STATUS","message":"access_denied","time":{"created":1,"expires":2}}}""".Replace("STATUS", status)));
        var (signIn, _, server) = SignInOn(api);
        (await server.IsIdleAsync(CancellationToken.None)).ShouldBeTrue();

        await signIn.StartAsync("local-user", "openai", "chatgpt-browser", new Dictionary<string, JsonElement>(), CancellationToken.None);
        (await server.IsIdleAsync(CancellationToken.None)).ShouldBeFalse();
        await signIn.GetAttemptAsync("local-user", "openai", "con_browser", CancellationToken.None);
        (await server.IsIdleAsync(CancellationToken.None)).ShouldBeFalse();

        status = "failed";
        await signIn.GetAttemptAsync("local-user", "openai", "con_browser", CancellationToken.None);
        (await server.IsIdleAsync(CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_cancelled_sign_in_leaves_its_server_idle()
    {
        var api = IdleServing(request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : Json(BrowserAttempt));
        var (signIn, _, server) = SignInOn(api);
        await signIn.StartAsync("local-user", "openai", "chatgpt-browser", new Dictionary<string, JsonElement>(), CancellationToken.None);

        await signIn.CancelAsync("local-user", "openai", "con_browser", CancellationToken.None);

        (await server.IsIdleAsync(CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_sign_in_past_its_expiry_no_longer_keeps_its_server_busy()
    {
        // Nobody asked about it again, and V2 has forgotten it by now.
        var (signIn, _, server) = SignInOn(IdleServing(_ => Json(BrowserAttempt.Replace("1790114505285", "1790113935285"))));

        await signIn.StartAsync("local-user", "openai", "chatgpt-browser", new Dictionary<string, JsonElement>(), CancellationToken.None);

        (await server.IsIdleAsync(CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Cancelling_a_sign_in_V2_already_forgot_is_fine()
    {
        var (signIn, _) = SignIn(Answer("""{"_tag":"IntegrationAttemptNotFoundError","message":"gone"}""", HttpStatusCode.NotFound));

        await signIn.CancelAsync("local-user", "openai", "con_old", CancellationToken.None);
    }

    [Fact]
    public async Task A_code_finishes_the_sign_in()
    {
        var api = Answer("", HttpStatusCode.NoContent);
        var (signIn, _) = SignIn(api);

        await signIn.SubmitCodeAsync("local-user", "openai", "con_1", "abc#def", CancellationToken.None);

        Sent(api).ShouldHaveSingleItem().ShouldBe((HttpMethod.Post, "/api/integration/openai/connect/oauth/con_1/complete", """{"code":"abc#def"}"""));
    }

    [Fact]
    public async Task Switches_to_and_removes_a_sign_in()
    {
        var api = Answer("", HttpStatusCode.NoContent);
        var (signIn, _) = SignIn(api);

        await signIn.UseAsync("local-user", "cred_home", CancellationToken.None);
        await signIn.SignOutAsync("local-user", "cred_work", CancellationToken.None);

        Sent(api).Select(r => (r.Method, r.Path)).ShouldBe(
        [
            (HttpMethod.Post, "/api/credential/cred_home/activate"),
            (HttpMethod.Delete, "/api/credential/cred_work"),
        ]);
    }

    [Theory]
    [InlineData("https://auth.openai.com/a?redirect_uri=http%3A%2F%2Flocalhost%3A1455%2Fauth%2Fcallback", "http://localhost:1455/auth/callback")]
    [InlineData("https://poe.com/oauth?redirect_uri=http%3A%2F%2F127.0.0.1%3A40123%2Fcallback&x=1", "http://127.0.0.1:40123/callback")]
    [InlineData("https://cloud.digitalocean.com/v1/oauth/authorize?redirect_uri=https%3A%2F%2Fexample.com%2Fcb", null)]
    [InlineData("https://github.com/login/device", null)]
    [InlineData("not a url", null)]
    public void Knows_a_callback_on_this_machine(string url, string? callback)
        => OpenCode2SignIn.LoopbackCallback(url)?.ToString().ShouldBe(callback);

    [Fact]
    public async Task Every_integration_request_names_sign_ins_folder_once_it_has_loaded()
    {
        // V2 registers a location's integrations only once it has loaded, and keeps browser sign-ins per location.
        var api = Answer(BrowserAttempt);
        var (signIn, _) = SignIn(api);

        await signIn.StartAsync("local-user", "openai", "chatgpt-browser", new Dictionary<string, JsonElement>(), CancellationToken.None);
        await signIn.GetAttemptAsync("local-user", "openai", "con_browser", CancellationToken.None);

        api.Requests[0].Path.ShouldBe("/api/location");
        api.Uris.Where(uri => uri.Contains("/api/integration/")).ShouldAllBe(uri => uri.EndsWith("?location%5Bdirectory%5D=%2Fdata%2Fopencode2%2Fsign-in"));
    }

    private const string Folder = "/data/opencode2/sign-in";

    /// <summary>The catalog events V2 sends for a folder once it has loaded it.</summary>
    private static readonly string[] LoadedEvents = ["provider.updated", "model.updated", "agent.updated", "command.updated"];

    /// <summary>What was asked of V2 besides loading sign-in's folder.</summary>
    private static List<(HttpMethod Method, string Path, string? Body)> Sent(StubHandler api)
        => api.Requests.Where(r => r.Path != "/api/location").ToList();

    /// <summary>V2 answering <paramref name="respond"/>, and loading a folder when asked to.</summary>
    private static StubHandler Serving(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(request => request.RequestUri!.AbsolutePath == "/api/location" ? new HttpResponseMessage(HttpStatusCode.OK) : respond(request));

    /// <summary>V2 with no turn running and no folder loaded, answering the rest with <paramref name="respond"/>.</summary>
    private static StubHandler IdleServing(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => Serving(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/session/active" => Json("""{"data":{}}"""),
            "/api/debug/location" => Json("[]"),
            _ => respond(request),
        });

    private static (OpenCode2SignIn SignIn, StubHandler Callbacks) SignIn(StubHandler api, OpenCode2InstallMode mode = OpenCode2InstallMode.Separate)
    {
        var (signIn, callbacks, _) = SignInOn(api, mode);
        return (signIn, callbacks);
    }

    private static (OpenCode2SignIn SignIn, StubHandler Callbacks, OpenCode2Server Server) SignInOn(StubHandler api, OpenCode2InstallMode mode = OpenCode2InstallMode.Separate)
    {
        var now = new FixedTime(DateTimeOffset.FromUnixTimeMilliseconds(1790113905285) + TimeSpan.FromMinutes(1));
        var server = new OpenCode2Server("local-user", OpenCode2Fixtures.ClientServing("", api), "token", process: null, NullLogger.Instance) { Time = now };
        api.OnRequest = request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/location")
                _ = Task.Run(() => { foreach (var evt in Loaded(Folder)) server.Route(evt); });
        };
        var callbacks = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var signIn = new OpenCode2SignIn(
            (_, _) => Task.FromResult(server),
            () => Folder,
            () => mode,
            () => new HttpClient(callbacks, disposeHandler: false),
            now);
        return (signIn, callbacks, server);
    }

    /// <summary>A minute after the recorded attempts started, well inside their ten minutes.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static StubHandler Answer(string body, HttpStatusCode status = HttpStatusCode.OK)
        => Serving(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    private static IEnumerable<OpenCode2Event> Loaded(string directory)
        => LoadedEvents.Select(type => new OpenCode2Event
        {
            Id = $"evt_{type}",
            Type = type,
            Location = new OpenCode2EventLocation { Directory = directory },
            Data = JsonDocument.Parse("{}").RootElement.Clone(),
        });

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
