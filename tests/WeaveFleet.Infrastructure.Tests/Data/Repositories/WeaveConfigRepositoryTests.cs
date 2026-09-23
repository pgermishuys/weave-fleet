using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class WeaveConfigRepositoryTests
{
    [Fact]
    public async Task nothing_saved_reads_as_the_users_own_files()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;

        var config = await new WeaveConfigRepository(factory, new TestUserContext()).GetAsync();

        config.Source.ShouldBe(WeaveConfigSource.Own);
        config.Files.ShouldBeEmpty();
        config.UpdatedAt.ShouldBeNull();
    }

    [Fact]
    public async Task a_save_replaces_the_source_and_every_file_and_belongs_to_its_owner()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        using var _ = keeper;
        var repo = new WeaveConfigRepository(factory, new TestUserContext());

        await repo.SaveAsync(new WeaveConfig
        {
            Source = WeaveConfigSource.Fleet,
            Files = new Dictionary<string, string> { ["config.weave"] = "first", ["prompts/old.md"] = "old" },
            UpdatedAt = "2026-09-23T00:00:00Z",
        });
        await repo.SaveAsync(new WeaveConfig
        {
            Source = WeaveConfigSource.Fleet,
            Files = new Dictionary<string, string> { ["config.weave"] = "second", ["prompts/team.md"] = "Be brief." },
            UpdatedAt = "2026-09-23T01:00:00Z",
        });

        var saved = await repo.GetAsync();
        saved.Source.ShouldBe(WeaveConfigSource.Fleet);
        saved.Files.ShouldBe(new Dictionary<string, string> { ["config.weave"] = "second", ["prompts/team.md"] = "Be brief." });
        saved.UpdatedAt.ShouldBe("2026-09-23T01:00:00Z");

        var stranger = await new WeaveConfigRepository(factory, new TestUserContext("someone-else")).GetAsync();
        stranger.Source.ShouldBe(WeaveConfigSource.Own);
        stranger.Files.ShouldBeEmpty();
    }
}
