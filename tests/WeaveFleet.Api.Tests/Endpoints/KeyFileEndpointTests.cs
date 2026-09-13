using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class KeyFileEndpointTests
{
    [Fact]
    public async Task get_key_files_returns_400_for_a_directory_that_does_not_exist()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/key-files?directory=/does/not/exist/xyz123");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task get_key_files_returns_400_for_a_directory_outside_the_allowed_roots()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        using var root = new TempRoot();
        await RegisterRootAsync(factory, root.Path);
        var outside = OperatingSystem.IsWindows() ? @"C:\Windows" : "/usr";

        var response = await client.GetAsync($"/api/key-files?directory={Uri.EscapeDataString(outside)}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task get_key_files_returns_no_files_for_an_empty_directory_inside_an_allowed_root()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        using var root = new TempRoot();
        await RegisterRootAsync(factory, root.Path);

        var response = await client.GetAsync($"/api/key-files?directory={Uri.EscapeDataString(root.Path)}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        json.GetProperty("filesByTool").EnumerateObject().Count().ShouldBe(0);
    }

    [Fact]
    public async Task open_file_returns_400_for_a_file_that_does_not_exist()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/open-file",
            new { filePath = "/does/not/exist/Foo.slnx", tool = "rider" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task open_file_returns_400_for_an_existing_file_outside_the_allowed_roots()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();
        using var root = new TempRoot();
        await RegisterRootAsync(factory, root.Path);
        using var outsideRoot = new TempRoot();
        var outsideFile = Path.Combine(outsideRoot.Path, "Outside.slnx");
        await File.WriteAllTextAsync(outsideFile, "");

        var response = await client.PostAsJsonAsync(
            "/api/open-file",
            new { filePath = outsideFile, tool = "rider" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
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
        public string Path { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fleet-key-files-{Guid.NewGuid():N}")).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
