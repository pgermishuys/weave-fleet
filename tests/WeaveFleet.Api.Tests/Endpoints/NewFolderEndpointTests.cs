using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Workspaces;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class NewFolderEndpointTests
{
    [Fact]
    public async Task create_makes_a_plain_folder_inside_a_root_and_says_what_it_made()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        using var root = new TempRoot();
        await RegisterRootAsync(factory, root.Path);
        var path = Path.Combine(root.Path, "notes");

        var response = await client.PostAsJsonAsync("/api/directories", new { path, git = false });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        json.GetProperty("path").GetString().ShouldBe(path);
        json.GetProperty("isGitRepo").GetBoolean().ShouldBeFalse();
        json.GetProperty("addedToFleet").GetBoolean().ShouldBeFalse();
        Directory.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public async Task create_returns_409_for_a_folder_that_already_exists()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        using var root = new TempRoot();
        await RegisterRootAsync(factory, root.Path);

        var response = await client.PostAsJsonAsync("/api/directories", new { path = root.Path, git = true });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        json.GetProperty("error").GetString().ShouldBe($"{root.Path} already exists.");
    }

    [Fact]
    public async Task create_starts_a_repository_on_the_branch_asked_for()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        using var root = new TempRoot();
        await RegisterRootAsync(factory, root.Path);
        var path = Path.Combine(root.Path, "clients", "acme-portal");

        var response = await client.PostAsJsonAsync("/api/directories", new { path, git = true, branch = "trunk" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await File.ReadAllTextAsync(Path.Combine(path, ".git", "HEAD"))).Trim().ShouldBe("ref: refs/heads/trunk");
    }

    [Fact]
    public async Task defaults_name_the_first_branch_of_a_new_repository_and_the_home_folder()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/directories/defaults", JsonSerializerOptions.Web);

        json.GetProperty("firstBranch").GetString().ShouldBe(await NewFolderService.FirstBranchAsync());
        json.GetProperty("home").GetString().ShouldBe(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    [Fact]
    public async Task listing_a_folder_that_isnt_there_names_the_nearest_one_that_is()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        using var root = new TempRoot();
        var path = Path.Combine(root.Path, "clients", "acme-portal");

        var json = await client.GetFromJsonAsync<JsonElement>(
            $"/api/directories?unconstrained=true&path={Uri.EscapeDataString(path)}", JsonSerializerOptions.Web);

        json.GetProperty("exists").GetBoolean().ShouldBeFalse();
        json.GetProperty("currentPath").GetString().ShouldBe(path);
        json.GetProperty("nearestExisting").GetString().ShouldBe(root.Path);
        json.GetProperty("entries").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task listing_a_folder_says_it_is_there_and_lists_what_is_in_it()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        using var root = new TempRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "clients"));

        var json = await client.GetFromJsonAsync<JsonElement>(
            $"/api/directories?unconstrained=true&path={Uri.EscapeDataString(root.Path)}", JsonSerializerOptions.Web);

        json.GetProperty("exists").GetBoolean().ShouldBeTrue();
        json.GetProperty("nearestExisting").ValueKind.ShouldBe(JsonValueKind.Null);
        json.GetProperty("entries")[0].GetProperty("name").GetString().ShouldBe("clients");
    }

    [Fact]
    public async Task clone_returns_400_before_running_git_for_an_address_it_refuses()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        using var root = new TempRoot();
        await RegisterRootAsync(factory, root.Path);

        var response = await client.PostAsJsonAsync(
            "/api/directories/clone",
            new { repository = "file:///etc", path = Path.Combine(root.Path, "etc") });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        Directory.Exists(Path.Combine(root.Path, "etc")).ShouldBeFalse();
    }

    [Fact]
    public void clone_lines_serialize_from_the_source_generated_context()
    {
        var progress = JsonSerializer.Serialize(
            new CloneStreamLine("progress", "Receiving objects", 38, null, null), ApiJsonContext.Default.CloneStreamLine);
        var done = JsonSerializer.Serialize(
            new CloneStreamLine("done", null, null, new NewFolderResponse("/src/recipe-box", true, false, null), null),
            ApiJsonContext.Default.CloneStreamLine);

        progress.ShouldBe("""{"type":"progress","phase":"Receiving objects","percent":38,"folder":null,"error":null}""");
        done.ShouldBe("""{"type":"done","phase":null,"percent":null,"folder":{"path":"/src/recipe-box","isGitRepo":true,"addedToFleet":false,"warning":null},"error":null}""");
    }

    private static async Task RegisterRootAsync(ApiWebApplicationFactory factory, string path)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var workspaceRoots = scope.ServiceProvider.GetRequiredService<WorkspaceRootService>();
        var result = await workspaceRoots.AddRootAsync(path);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = WorkspaceRootService.CanonicalizePath(Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fleet-new-folder-{Guid.NewGuid():N}")).FullName);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
