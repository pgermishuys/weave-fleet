using WeaveFleet.Infrastructure.Skills;
using WeaveFleet.Infrastructure.Tools;

namespace WeaveFleet.Infrastructure.Tests.Skills;

[Collection("SkillTests")]
public sealed class CorruptManifestTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"weave-corrupt-{Guid.NewGuid():N}");

    public CorruptManifestTests() => Directory.CreateDirectory(_home);

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public async Task Skill_manifest_that_is_not_json_is_set_aside_and_loads_empty()
    {
        var path = Path.Combine(_home, ".weave", "skills", "local-user.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"skills\": [ {\"name\": \"half writ");

        var manifest = await new JsonSkillManifestStore(_home).LoadAsync("local-user");

        Assert.Empty(manifest.Skills);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "local-user.json.corrupt-*"));
    }

    [Fact]
    public async Task Tool_manifest_that_is_not_json_is_set_aside_and_loads_empty()
    {
        var path = Path.Combine(_home, ".config", "weave-fleet", "tool-manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string('\0', 64));

        var manifest = await new JsonToolManifestStore(_home).LoadAsync("local-user");

        Assert.Empty(manifest.Tools);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "tool-manifest.json.corrupt-*"));
    }
}
