using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// The agents, models and commands an OpenCode 2 folder offers, and a session keeping the agent and model its
/// prompts pick. Response shapes are from a real 2.0.8 server with a scripted model.
/// </summary>
public sealed class OpenCode2AgentsTests
{
    private const string Session = "ses_f4757bafdffeumBBeC3TnOloNn";
    private const string Folder = "/work/rocket";

    [Fact]
    public async Task The_catalog_loads_the_folder_before_listing_what_it_offers()
    {
        // V2 reads a folder's config lazily; listed before that, its agents come back without the user's own.
        var api = CatalogApi();
        await using var server = LoadingServer(api);

        await OpenCode2Catalog.ReadAsync(server, Folder, CancellationToken.None);

        api.Requests[0].Path.ShouldBe("/api/location");
        api.Requests.Skip(1).Select(r => r.Path).Order().ShouldBe(
            ["/api/agent", "/api/config", "/api/model", "/api/model/default", "/api/provider"]);
    }

    [Fact]
    public async Task Every_catalog_request_names_the_folder()
    {
        var queries = new List<string>();
        var api = CatalogApi(request => queries.Add(request.RequestUri!.Query));
        await using var server = LoadingServer(api);

        await OpenCode2Catalog.ReadAsync(server, Folder, CancellationToken.None);

        queries.ShouldAllBe(query => query == "?location%5Bdirectory%5D=%2Fwork%2Frocket");
    }

    [Fact]
    public async Task A_new_folder_is_listed_once_V2_says_it_has_loaded_it()
    {
        // GET /api/location returns before V2 has read the folder's config; its catalog events say when it has.
        await using var server = Server(CatalogApi());

        var loading = server.LoadLocationAsync(Folder, CancellationToken.None);
        await Task.Delay(200);
        loading.IsCompleted.ShouldBeFalse();

        foreach (var evt in LocationLoaded(Folder))
            server.Route(evt);

        await loading.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Events_for_another_folder_do_not_count()
    {
        await using var server = Server(CatalogApi());

        var loading = server.LoadLocationAsync(Folder, CancellationToken.None);
        foreach (var evt in LocationLoaded("/work/comet"))
            server.Route(evt);
        await Task.Delay(200);

        loading.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task A_folder_already_loaded_is_not_loaded_again()
    {
        var api = CatalogApi();
        await using var server = LoadingServer(api);

        await OpenCode2Catalog.ReadAsync(server, Folder, CancellationToken.None);
        await OpenCode2Catalog.ReadAsync(server, Folder, CancellationToken.None);

        api.Requests.Count(r => r.Path == "/api/location").ShouldBe(1);
    }

    [Fact]
    public async Task A_folder_V2_is_slow_to_load_is_listed_as_it_is_after_a_while()
    {
        var api = CatalogApi();
        await using var server = new OpenCode2Server("local-user", OpenCode2Fixtures.ClientServing("", api), "token", process: null, NullLogger.Instance)
        {
            LocationLoadTimeout = TimeSpan.FromMilliseconds(100),
        };

        var catalog = await OpenCode2Catalog.ReadAsync(server, Folder, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        catalog.Agents.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_catalog_offers_the_folders_agents_and_the_models_of_providers_that_are_on()
    {
        await using var server = LoadingServer(CatalogApi());

        var catalog = await OpenCode2Catalog.ReadAsync(server, Folder, CancellationToken.None);

        catalog.Agents.Select(a => a.Name).ShouldBe(["build", "general", "title", "plan", "reviewer", "rocket"]);
        catalog.Agents.Single(a => a.Name == "general").Mode.ShouldBe("subagent");
        catalog.Agents.Single(a => a.Name == "title").Hidden.ShouldBeTrue();
        catalog.Agents.Single(a => a.Name == "rocket").ModelId.ShouldBe("fake-model-2");

        // The switched-off provider and the switched-off model aren't offered.
        catalog.Providers.Select(p => p.Id).ShouldBe(["fakellm"]);
        var provider = catalog.Providers.Single();
        provider.Name.ShouldBe("Fake LLM");
        provider.Models.Select(m => (m.Id, m.Name)).ShouldBe([("fake-model", "Fake"), ("fake-model-2", "Fake Two")]);
        provider.Models[0].Variants.ShouldBe(["low", "medium", "high"]);
        provider.Models[1].Variants.ShouldBeNull();
    }

    [Fact]
    public async Task The_defaults_are_the_configured_agent_and_model()
    {
        await using var server = LoadingServer(CatalogApi());

        var catalog = await OpenCode2Catalog.ReadAsync(server, Folder, CancellationToken.None);

        // The folder's own config sets default_agent after the user's.
        catalog.DefaultAgent.ShouldBe("rocket");
        (catalog.DefaultModelProviderId, catalog.DefaultModelId).ShouldBe(("fakellm", "fake-model"));
    }

    [Theory]
    [InlineData(null, "build")]
    [InlineData("general", "build")]
    [InlineData("title", "build")]
    [InlineData("nosuch", "build")]
    [InlineData("plan", "plan")]
    public void A_default_agent_that_cannot_run_a_session_falls_back_to_build(string? configured, string expected)
    {
        var agents = JsonSerializer.Deserialize(AgentsJson, OpenCode2JsonContext.Default.OpenCode2EnvelopeListOpenCode2AgentInfo)!.Data!;

        var catalog = OpenCode2Catalog.ToCatalog(agents, [], [], defaultModel: null, configured);

        catalog.DefaultAgent.ShouldBe(expected);
    }

    [Fact]
    public async Task A_session_lists_the_commands_of_its_folder()
    {
        var api = CatalogApi();
        await using var server = LoadingServer(api);
        await using var session = NewSession(server);

        var commands = await session.GetCommandsAsync(CancellationToken.None);

        // The folder may have loaded (its events arrived) before the read asked for it.
        api.Requests[^1].Path.ShouldBe("/api/command");
        commands.Select(c => (c.Name, c.Description)).ShouldBe(
        [
            ("init", "guided AGENTS.md setup"),
            ("hello", "Says hello"),
        ]);
    }

    [Fact]
    public async Task A_prompt_switches_the_session_to_the_agent_and_model_it_names_first()
    {
        var api = Accepting();
        await using var server = Server(api);
        await using var session = NewSession(server);

        await session.SendPromptAsync("hi", new PromptOptions { Agent = "rocket", ProviderId = "fakellm", ModelId = "fake-model-2", MessageId = "msg_1" }, CancellationToken.None);

        api.Requests.Select(r => r.Path).ShouldBe(
        [
            $"/api/session/{Session}/agent",
            $"/api/session/{Session}/model",
            $"/api/session/{Session}/prompt",
        ]);
        Body(api, 0).GetProperty("agent").GetString().ShouldBe("rocket");
        Body(api, 1).GetProperty("model").GetRawText().ShouldBe("""{"id":"fake-model-2","providerID":"fakellm"}""");
        Body(api, 2).GetProperty("text").GetString().ShouldBe("hi");
    }

    [Fact]
    public async Task The_session_keeps_its_choice_so_the_next_prompt_switches_nothing()
    {
        var api = Accepting();
        await using var server = Server(api);
        await using var session = NewSession(server);
        var options = new PromptOptions { Agent = "rocket", ProviderId = "fakellm", ModelId = "fake-model-2" };

        await session.SendPromptAsync("hi", options, CancellationToken.None);
        await session.SendPromptAsync("again", options, CancellationToken.None);

        api.Requests.Count(r => r.Path.EndsWith("/agent", StringComparison.Ordinal)).ShouldBe(1);
        api.Requests.Count(r => r.Path.EndsWith("/model", StringComparison.Ordinal)).ShouldBe(1);
        api.Requests[^1].Path.ShouldBe($"/api/session/{Session}/prompt");
    }

    [Fact]
    public async Task A_resumed_session_starts_from_the_agent_and_model_V2_has_for_it()
    {
        var api = Accepting();
        await using var server = Server(api);
        await using var session = NewSession(server, new OpenCode2SessionInfo
        {
            Id = Session,
            Agent = "rocket",
            Model = new OpenCode2ModelRef { Id = "fake-model-2", ProviderId = "fakellm", Variant = "default" },
        });

        await session.SendPromptAsync("hi", new PromptOptions { Agent = "rocket", ProviderId = "fakellm", ModelId = "fake-model-2" }, CancellationToken.None);

        api.Requests.Select(r => r.Path).ShouldBe([$"/api/session/{Session}/prompt"]);
    }

    [Fact]
    public async Task A_switch_made_elsewhere_is_what_the_next_prompt_compares_with()
    {
        var api = Accepting();
        await using var server = Server(api);
        await using var session = NewSession(server);
        session.OnEvent(Event("session.agent.selected", $$"""{"sessionID":"{{Session}}","agent":"plan","previous":"build"}"""));

        await session.SendPromptAsync("hi", new PromptOptions { Agent = "plan" }, CancellationToken.None);

        api.Requests.Select(r => r.Path).ShouldBe([$"/api/session/{Session}/prompt"]);
    }

    [Theory]
    [InlineData("high", "high")]
    [InlineData(null, null)]
    public async Task Effort_is_the_models_variant(string? effort, string? variant)
    {
        var api = Accepting();
        await using var server = Server(api);
        await using var session = NewSession(server);

        await session.SendPromptAsync("hi", new PromptOptions { ProviderId = "fakellm", ModelId = "fake-model", Effort = effort }, CancellationToken.None);

        var model = Body(api, 0).GetProperty("model");
        model.TryGetProperty("variant", out var sent).ShouldBe(variant is not null);
        if (variant is not null)
            sent.GetString().ShouldBe(variant);
    }

    [Fact]
    public void Effort_alone_changes_the_variant_of_the_model_the_session_has()
    {
        var current = new OpenCode2ModelRef { Id = "fake-model", ProviderId = "fakellm", Variant = "default" };

        OpenCode2HarnessSession.ChosenModel(current, null, null, "high").ShouldBe(current with { Variant = "high" });
        OpenCode2HarnessSession.ChosenModel(null, null, null, "high").ShouldBeNull();
        OpenCode2HarnessSession.ChosenModel(current, null, null, null).ShouldBeNull();
    }

    [Fact]
    public void A_model_without_a_variant_is_the_same_as_its_default_variant()
    {
        var chosen = new OpenCode2ModelRef { Id = "fake-model", ProviderId = "fakellm" };

        OpenCode2HarnessSession.SameModel(chosen, chosen with { Variant = "default" }).ShouldBeTrue();
        OpenCode2HarnessSession.SameModel(chosen, chosen with { Variant = "high" }).ShouldBeFalse();
        OpenCode2HarnessSession.SameModel(chosen, null).ShouldBeFalse();
    }

    [Fact]
    public async Task A_command_runs_with_its_arguments_as_the_text()
    {
        var api = Accepting();
        await using var server = Server(api);
        await using var session = NewSession(server);

        await session.SendCommandAsync(new CommandOptions { Command = "hello", Arguments = "the world" }, CancellationToken.None);

        var request = api.Requests.ShouldHaveSingleItem();
        request.Path.ShouldBe($"/api/session/{Session}/command");
        JsonDocument.Parse(request.Body!).RootElement.GetRawText().ShouldBe("""{"name":"hello","text":"the world"}""");
    }

    [Fact]
    public async Task A_command_without_arguments_sends_empty_text()
    {
        // V2 requires the text.
        var api = Accepting();
        await using var server = Server(api);
        await using var session = NewSession(server);

        await session.SendCommandAsync(new CommandOptions { Command = "init" }, CancellationToken.None);

        Body(api, 0).GetProperty("text").GetString().ShouldBe("");
    }

    [Fact]
    public async Task An_off_the_record_question_is_answered_by_generate()
    {
        var api = new StubHandler(_ => Json("""{"data":{"text":"You asked for a greeting; it said hello."}}"""));
        await using var server = OpenCode2EventlessServer(api);
        await using var session = NewSession(server);

        var answer = await session.AskOffTheRecordAsync("Recap please", CancellationToken.None);

        answer.ShouldBe("You asked for a greeting; it said hello.");
        var request = api.Requests.Where(r => r.Path != "/api/event").ShouldHaveSingleItem();
        request.Path.ShouldBe($"/api/session/{Session}/generate");
        JsonDocument.Parse(request.Body!).RootElement.GetProperty("prompt").GetString().ShouldBe("Recap please");
    }

    [Fact]
    public void OpenCode_2_offers_agents_models_commands_subagents_and_recaps()
    {
        var capabilities = new OpenCode2Harness().Capabilities;

        capabilities.SupportsAgents.ShouldBeTrue();
        capabilities.SupportsModelSelection.ShouldBeTrue();
        capabilities.SupportsCommands.ShouldBeTrue();
        capabilities.SupportsDelegation.ShouldBeTrue();
        capabilities.SupportsOffTheRecordPrompt.ShouldBeTrue();
        // Fleet's fork starts a new session in the same folder for every harness; it doesn't use V2's.
        capabilities.SupportsForking.ShouldBeFalse();
        capabilities.SupportsProfiles.ShouldBeFalse();
    }

    private const string AgentsJson = """
        {"location":{"directory":"/work/rocket"},"data":[
          {"id":"build","name":"Build","mode":"primary","hidden":false,"permissions":[]},
          {"id":"general","name":"General","mode":"subagent","hidden":false,"permissions":[]},
          {"id":"title","name":"Title","mode":"primary","hidden":true,"permissions":[]},
          {"id":"plan","name":"Plan","mode":"primary","hidden":false,"permissions":[]},
          {"id":"reviewer","name":"reviewer","mode":"primary","hidden":false,"permissions":[]},
          {"id":"rocket","name":"rocket","mode":"primary","hidden":false,"model":{"id":"fake-model-2","providerID":"fakellm"},"permissions":[]}]}
        """;

    private static StubHandler CatalogApi(Action<HttpRequestMessage>? seen = null) => new(request =>
    {
        seen?.Invoke(request);
        return request.RequestUri!.AbsolutePath switch
        {
            "/api/location" => Json("""{"directory":"/work/rocket","project":{"id":"p","directory":"/work/rocket","canonical":"/work/rocket"}}"""),
            "/api/agent" => Json(AgentsJson),
            "/api/model" => Json("""
                {"location":{"directory":"/work/rocket"},"data":[
                  {"id":"big-pickle","modelID":"big-pickle","providerID":"opencode","name":"Big Pickle","enabled":true,"variants":[]},
                  {"id":"fake-model","modelID":"fake-model","providerID":"fakellm","name":"Fake","enabled":true,"variants":[{"id":"low"},{"id":"medium"},{"id":"high"}]},
                  {"id":"fake-model-2","modelID":"fake-model-2","providerID":"fakellm","name":"Fake Two","enabled":true,"variants":[]},
                  {"id":"fake-old","modelID":"fake-old","providerID":"fakellm","name":"Fake Old","enabled":false,"variants":[]}]}
                """),
            "/api/model/default" => Json("""{"location":{"directory":"/work/rocket"},"data":{"id":"fake-model","modelID":"fake-model","providerID":"fakellm","name":"Fake","variants":[]}}"""),
            "/api/provider" => Json("""
                {"location":{"directory":"/work/rocket"},"data":[
                  {"id":"opencode","name":"OpenCode Zen","activation":"disabled","package":"p"},
                  {"id":"fakellm","name":"Fake LLM","activation":"enabled","package":"p"}]}
                """),
            "/api/command" => Json("""{"location":{"directory":"/work/rocket"},"data":[{"name":"init","description":"guided AGENTS.md setup"},{"name":""},{"name":"hello","description":"Says hello"}]}"""),
            "/api/config" => Json("""
                [{"type":"document","path":"/home/u/.config/opencode/opencode.json","info":{"model":{"providerID":"fakellm","model":"fake-model"},"default_agent":"reviewer"}},
                 {"type":"directory","path":"/home/u/.config/opencode"},
                 {"type":"document","path":"/work/rocket/opencode.json","info":{"default_agent":"rocket"}}]
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    });

    private static readonly string[] CatalogEvents = ["provider.updated", "model.updated", "agent.updated", "command.updated"];

    /// <summary>The catalog events V2 sends for a folder once it has read its config.</summary>
    private static IEnumerable<OpenCode2Event> LocationLoaded(string directory)
        => CatalogEvents.Select(type => new OpenCode2Event
        {
            Id = $"evt_{type}",
            Type = type,
            Location = new OpenCode2EventLocation { Directory = directory },
            Data = JsonDocument.Parse("{}").RootElement.Clone(),
        });

    /// <summary>A server whose V2 loads a folder when asked to, and then says so in its catalog events.</summary>
    private static OpenCode2Server LoadingServer(StubHandler api)
    {
        var server = Server(api);
        api.OnRequest = request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/location")
                _ = Task.Run(() => { foreach (var evt in LocationLoaded(Folder)) server.Route(evt); });
        };
        return server;
    }

    private static StubHandler Accepting() => new(request => request.RequestUri!.AbsolutePath.EndsWith("/prompt", StringComparison.Ordinal)
        ? Json("""{"data":{"id":"msg_1","sessionID":"ses_1","time":{"created":1},"type":"user","payload":{"text":"hi"},"delivery":"steer"}}""")
        : new HttpResponseMessage(HttpStatusCode.NoContent));

    private static JsonElement Body(StubHandler api, int index) => JsonDocument.Parse(api.Requests[index].Body!).RootElement;

    private static OpenCode2Server Server(StubHandler api)
        => new("local-user", OpenCode2Fixtures.ClientServing("", api), "token", process: null, NullLogger.Instance);

    /// <summary>A server whose event client answers <paramref name="api"/> too: generate runs on it, having no timeout.</summary>
    private static OpenCode2Server OpenCode2EventlessServer(StubHandler api)
        => new(
            "local-user",
            new OpenCode2HttpClient(
                new HttpClient(api) { BaseAddress = new Uri("http://127.0.0.1:1/") },
                new HttpClient(api) { BaseAddress = new Uri("http://127.0.0.1:1/") },
                NullLogger<OpenCode2HttpClient>.Instance),
            "token",
            process: null,
            NullLogger.Instance);

    private static OpenCode2HarnessSession NewSession(OpenCode2Server server, OpenCode2SessionInfo? info = null)
        => new(
            "opencode2-test",
            info ?? new OpenCode2SessionInfo { Id = Session },
            new OpenCode2SessionContext("fleet-session-1", "local-user", Folder, null, null),
            server,
            _ => Task.FromResult(server),
            analytics: null,
            delegations: null,
            NullLogger.Instance);

    private static OpenCode2Event Event(string type, string data) => new()
    {
        Id = "evt_1",
        Created = 1_789_804_433_751,
        Type = type,
        Data = JsonDocument.Parse(data).RootElement.Clone(),
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
