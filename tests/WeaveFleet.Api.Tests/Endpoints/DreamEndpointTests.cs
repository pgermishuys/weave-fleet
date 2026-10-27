using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Memory;
using WeaveFleet.Application.Memory.Dreaming;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>Dreaming's routes: all 404 while it's off, and Undo puts a dreamed change back.</summary>
public sealed class DreamEndpointTests : IDisposable
{
    private const string User = "local-user";
    private readonly ApiWebApplicationFactory _factory = new(authEnabled: false);
    private readonly string _repository = Directory.CreateTempSubdirectory("fleet-dream-").FullName;

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_repository, recursive: true);
    }

    private async Task TurnOnAsync()
    {
        using var scope = _factory.Services.CreateScope();
        using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(User))
            await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().SetAsync(DreamRules.PreferenceKey, "true");
    }

    [Fact]
    public async Task Every_route_is_a_404_while_dreaming_is_off()
    {
        var client = _factory.CreateClient();

        var list = await client.GetAsync($"/api/memory/dreams?repository={Uri.EscapeDataString(_repository)}");
        var start = await client.PostAsJsonAsync("/api/memory/dreams", new { repository = _repository });
        var undo = await client.PostAsync("/api/memory/dreams/aabbccdd/changes/11223344/undo", null);

        foreach (var response in new[] { list, start, undo })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().ShouldBe(DreamRules.TurnedOffMessage);
        }
    }

    [Fact]
    public async Task When_on_the_list_starts_empty_and_a_dream_to_start_needs_a_folder()
    {
        await TurnOnAsync();
        var client = _factory.CreateClient();

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/memory/dreams?repository={Uri.EscapeDataString(_repository)}");
        var bad = await client.PostAsJsonAsync("/api/memory/dreams", new { repository = Path.Combine(_repository, "missing") });

        list.GetProperty("dreams").GetArrayLength().ShouldBe(0);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Undo_of_an_added_note_deletes_it_and_a_second_undo_is_refused()
    {
        await TurnOnAsync();
        var note = new MemoryNote("aa11bb22", MemoryList.Repository, "Use bun.", MemoryKinds.Learned, AgentMemory.RepositoryOf(_repository), "s", "Dreaming", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 7);
        var dream = new Dream
        {
            Id = "11112222",
            Repository = AgentMemory.RepositoryOf(_repository),
            Status = "done",
            Started = DateTimeOffset.UtcNow,
            ReadSessions = [new DreamSession { Label = "S1", Id = "sess", Title = "Fix", HarnessType = "opencode2" }],
            Changes = [new DreamChange { Id = "33334444", Kind = "added", List = "repository", NoteId = note.Id, Text = note.Text, SessionIds = ["sess"], Why = "twice", WroteHash = DreamRules.HashOf(note) }],
        };
        await _factory.Services.GetRequiredService<IMemoryStore>().SaveAsync(User, note);
        await _factory.Services.GetRequiredService<IDreamStore>().SaveAsync(User, dream);
        var client = _factory.CreateClient();

        var listed = await client.GetFromJsonAsync<JsonElement>($"/api/memory/dreams?repository={Uri.EscapeDataString(_repository)}");
        var first = listed.GetProperty("dreams")[0];
        first.GetProperty("status").GetString().ShouldBe("done");
        first.GetProperty("changes")[0].GetProperty("ofSessions").GetInt32().ShouldBe(1);
        first.GetProperty("changes")[0].GetProperty("sessions")[0].GetProperty("title").GetString().ShouldBe("Fix");

        var undone = await client.PostAsync("/api/memory/dreams/11112222/changes/33334444/undo", null);

        undone.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await undone.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changes")[0].GetProperty("undone").GetBoolean().ShouldBeTrue();
        (await _factory.Services.GetRequiredService<IMemoryStore>().FindAsync(User, note.Id)).ShouldBeNull();
        (await client.PostAsync("/api/memory/dreams/11112222/changes/33334444/undo", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
