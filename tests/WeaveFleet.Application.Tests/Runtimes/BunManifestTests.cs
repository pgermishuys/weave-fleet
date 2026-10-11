using System.Text;
using Shouldly;
using WeaveFleet.Application.Runtimes;

namespace WeaveFleet.Application.Tests.Runtimes;

public sealed class BunManifestTests
{
    private const string Sha = "c678040f14fe0440eb839d37cbd0ce4c051a32da72806ac97de6a6aab6bf728f";

    private static string Asset(string name, string sha = Sha, string extra = "", string size = "36646949") =>
        $$"""{ "name": "{{name}}", "sha256": "{{sha}}", "size": {{size}}{{extra}} }""";

    /// <summary>A valid manifest with each part replaceable, so a test changes exactly one thing.</summary>
    private static string Manifest(
        string schema = "1",
        string version = "\"1.4.2\"",
        string oldestSafe = "\"1.4.0\"",
        string note = "null",
        string? assets = null,
        string extraTop = "") =>
        $$"""{ "schema": {{schema}}, "version": {{version}}, "oldestSafe": {{oldestSafe}}, "note": {{note}}, "assets": {{assets ?? Assets()}}{{extraTop}} }""";

    private static string Assets(
        string? skip = null,
        string? sha = null,
        string? nameFor = null,
        string? nameValue = null,
        string extraRid = "",
        string extraField = "",
        string size = "36646949")
    {
        var entries = BunManifest.AssetNames
            .Where(pair => pair.Key != skip)
            .Select(pair => $"\"{pair.Key}\": " + Asset(
                pair.Key == nameFor ? nameValue! : pair.Value,
                sha ?? Sha,
                pair.Key == nameFor ? extraField : "",
                size));
        return "{ " + string.Join(", ", entries) + extraRid + " }";
    }

    private static bool Parse(string json, out BunRelease? release, out string? error) =>
        BunManifest.TryParse(Encoding.UTF8.GetBytes(json), out release, out error);

    [Fact]
    public void A_valid_manifest_parses()
    {
        Parse(Manifest(version: "\"1.4.3\"", oldestSafe: "\"1.4.2\"", note: "\"Bun 1.4.3 fixes a thing.\""), out var release, out var error)
            .ShouldBeTrue(error);

        release!.Version.ShouldBe("1.4.3");
        release.OldestSafe.ShouldBe("1.4.2");
        release.Note.ShouldBe("Bun 1.4.3 fixes a thing.");
        release.Assets.Count.ShouldBe(6);
        release.AssetFor("win-arm64")!.FileName.ShouldBe("bun-windows-aarch64.zip");
        release.AssetFor("win-arm64")!.Sha256.ShouldBe(Sha);
        release.AssetFor("win-arm64")!.Size.ShouldBe(36646949);
    }

    [Fact]
    public void The_size_may_be_1_up_to_512_MiB()
    {
        Parse(Manifest(assets: Assets(size: "1")), out var release, out var error).ShouldBeTrue(error);
        release!.Assets.ShouldAllBe(asset => asset.Size == 1);

        Parse(Manifest(assets: Assets(size: "536870912")), out release, out error).ShouldBeTrue(error);
        release!.Assets.ShouldAllBe(asset => asset.Size == 536870912);
    }

    [Fact]
    public void A_null_note_is_null_and_the_note_is_trimmed()
    {
        Parse(Manifest(), out var release, out _).ShouldBeTrue();
        release!.Note.ShouldBeNull();

        Parse(Manifest(note: "\"  hello  \""), out release, out _).ShouldBeTrue();
        release!.Note.ShouldBe("hello");
    }

    [Fact]
    public void The_assets_come_out_in_AssetNames_order()
    {
        var reversed = "{ " + string.Join(", ", BunManifest.AssetNames.Reverse().Select(pair => $"\"{pair.Key}\": " + Asset(pair.Value))) + " }";

        Parse(Manifest(assets: reversed), out var release, out var error).ShouldBeTrue(error);

        release!.Assets.Select(asset => asset.Rid).ShouldBe(BunManifest.AssetNames.Keys);
    }

    public static TheoryData<string, string, string> Refused() => new()
    {
        { "missing schema", """{ "version": "1.4.2", "oldestSafe": "1.4.0", "note": null, "assets": {} }""", "schema" },
        { "schema 2", Manifest(schema: "2"), "schema" },
        { "schema as string", Manifest(schema: "\"1\""), "schema" },
        { "schema 1.5", Manifest(schema: "1.5"), "schema" },
        { "version missing", """{ "schema": 1, "oldestSafe": "1.4.0", "note": null, "assets": {} }""", "version" },
        { "version a number", Manifest(version: "1"), "version" },
        { "version junk", Manifest(version: "\"latest\""), "version" },
        { "version pre-release", Manifest(version: "\"1.4.3-canary.1\""), "version" },
        { "version below minimum", Manifest(version: "\"1.3.9\"", oldestSafe: "\"1.4.0\""), "version" },
        { "oldestSafe missing", """{ "schema": 1, "version": "1.4.2", "note": null, "assets": {} }""", "oldestSafe" },
        { "oldestSafe junk", Manifest(oldestSafe: "\"x\""), "oldestSafe" },
        { "oldestSafe pre-release", Manifest(oldestSafe: "\"1.4.1-rc.1\""), "oldestSafe" },
        { "oldestSafe below minimum", Manifest(oldestSafe: "\"1.3.0\""), "oldestSafe" },
        { "oldestSafe above version", Manifest(oldestSafe: "\"1.4.3\""), "oldestSafe" },
        { "note missing", """{ "schema": 1, "version": "1.4.2", "oldestSafe": "1.4.0", "assets": {} }""", "note" },
        { "note a number", Manifest(note: "5"), "note" },
        { "note empty", Manifest(note: "\"\""), "note" },
        { "note blank", Manifest(note: "\"   \""), "note" },
        { "note too long", Manifest(note: "\"" + new string('a', 281) + "\""), "note" },
        { "note with newline", Manifest(note: "\"a\\nb\""), "note" },
        { "note with escape char", Manifest(note: "\"a\\u001bb\""), "note" },
        { "assets missing", """{ "schema": 1, "version": "1.4.2", "oldestSafe": "1.4.0", "note": null }""", "assets" },
        { "assets an array", Manifest(assets: "[]"), "assets" },
        { "assets empty", Manifest(assets: "{}"), "assets" },
        { "rid missing", Manifest(assets: Assets(skip: "osx-arm64")), "osx-arm64" },
        { "rid extra", Manifest(assets: Assets(extraRid: ", \"freebsd-x64\": " + Asset("bun-freebsd.zip"))), "freebsd-x64" },
        { "wrong asset name", Manifest(assets: Assets(nameFor: "linux-x64", nameValue: "bun-linux-x64.zip")), "linux-x64" },
        { "asset extra field", Manifest(assets: Assets(nameFor: "win-x64", nameValue: "bun-windows-x64-baseline.zip", extraField: ", \"url\": \"x\"")), "url" },
        { "asset not an object", Manifest(assets: Assets().Replace(Asset("bun-linux-aarch64.zip"), "\"nope\"")), "linux-arm64" },
        { "sha uppercase", Manifest(assets: Assets(sha: Sha.ToUpperInvariant())), "sha256" },
        { "sha 63 chars", Manifest(assets: Assets(sha: Sha[..63])), "sha256" },
        { "sha 65 chars", Manifest(assets: Assets(sha: Sha + "a")), "sha256" },
        { "sha not hex", Manifest(assets: Assets(sha: new string('g', 64))), "sha256" },
        { "asset sha missing", Manifest(assets: Assets().Replace($", \"sha256\": \"{Sha}\"", "")), "sha256" },
        { "size missing", Manifest(assets: Assets().Replace(", \"size\": 36646949", "")), "size" },
        { "size zero", Manifest(assets: Assets(size: "0")), "size" },
        { "size negative", Manifest(assets: Assets(size: "-5")), "size" },
        { "size fraction", Manifest(assets: Assets(size: "10.5")), "size" },
        { "size exponent", Manifest(assets: Assets(size: "1e3")), "size" },
        { "size a string", Manifest(assets: Assets(size: "\"100\"")), "size" },
        { "size too big", Manifest(assets: Assets(size: "536870913")), "size" },
        { "unknown top-level field", Manifest(extraTop: ", \"channel\": \"stable\""), "channel" },
        { "duplicate top-level field", Manifest(extraTop: ", \"version\": \"1.4.2\""), "version" },
        { "duplicate asset field", Manifest(assets: Assets().Replace("\"sha256\"", "\"name\": \"x\", \"sha256\"")), "name" },
        { "not an object", "[1]", "object" },
        { "a string", "\"hi\"", "object" },
        { "comment", "// hi\n" + Manifest(), "JSON" },
        { "empty", "", "JSON" },
        { "too deep", Manifest(extraTop: ", \"x\": " + new string('[', 9) + new string(']', 9)), "deep" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_bad_manifest_is_refused_with_an_error_naming_the_field(string why, string json, string field)
    {
        Parse(json, out var release, out var error).ShouldBeFalse(why);

        release.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
        error.ShouldContain(field, Case.Insensitive, why);
        error.Contains('\n').ShouldBeFalse();
    }

    [Fact]
    public void A_trailing_comma_is_refused()
    {
        var json = Manifest().TrimEnd().TrimEnd('}').TrimEnd() + ",}";

        Parse(json, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void A_manifest_over_MaxBytes_is_refused_unread()
    {
        var padded = Manifest() + new string(' ', BunManifest.MaxBytes);

        Parse(padded, out var release, out var error).ShouldBeFalse();

        release.ShouldBeNull();
        error!.ShouldContain("64");
    }
}
