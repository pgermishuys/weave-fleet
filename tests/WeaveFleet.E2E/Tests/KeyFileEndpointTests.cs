using System.Text.Json;
using WeaveFleet.E2E.Infrastructure;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for the key files endpoints.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Lane", "Regression")]
public sealed class KeyFileEndpointTests : E2ETestBase,
    IClassFixture<FleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
{
    public KeyFileEndpointTests(FleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    [Fact]
    public async Task GetKeyFiles_NonExistentDirectory_Returns400()
    {
        await WithFailureCapture(async () =>
        {
            var response = await Page.APIRequest.GetAsync(
                $"{ServerUrl}/api/key-files?directory=/does/not/exist/xyz123");

            response.Status.ShouldBe(400);
        });
    }

    [Fact]
    public async Task GetKeyFiles_DirectoryOutsideAllowedRoots_Returns400()
    {
        await WithFailureCapture(async () =>
        {
            // Use a directory that exists but is not under the allowed temp root
            var systemDir = OperatingSystem.IsWindows() ? @"C:\Windows" : "/usr";
            var encodedDir = Uri.EscapeDataString(systemDir);

            var response = await Page.APIRequest.GetAsync(
                $"{ServerUrl}/api/key-files?directory={encodedDir}");

            response.Status.ShouldBe(400);
        });
    }

    [Fact]
    public async Task GetKeyFiles_DirectoryWithNoKeyFiles_ReturnsEmptyResult()
    {
        await WithFailureCapture(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"fleet-kf-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var encodedDir = Uri.EscapeDataString(tempDir);
                var response = await Page.APIRequest.GetAsync(
                    $"{ServerUrl}/api/key-files?directory={encodedDir}");

                response.Status.ShouldBe(200);

                var body = await response.JsonAsync();
                body.ShouldNotBeNull();
                var json = body.Value.GetRawText();
                var doc = JsonDocument.Parse(json);
                var filesByTool = doc.RootElement.GetProperty("filesByTool");
                filesByTool.EnumerateObject().Count().ShouldBe(0);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        });
    }

    [Fact]
    public async Task OpenFile_NonExistentFile_Returns400()
    {
        await WithFailureCapture(async () =>
        {
            var response = await Page.APIRequest.PostAsync(
                $"{ServerUrl}/api/open-file",
                new() {
                    DataObject = new { filePath = "/does/not/exist/Foo.slnx", tool = "rider" }
                });

            response.Status.ShouldBe(400);
        });
    }

    [Fact]
    public async Task OpenFile_FileOutsideAllowedRoots_Returns400()
    {
        await WithFailureCapture(async () =>
        {
            // Create a real file so the "file exists" check passes,
            // but it's outside the allowed temp root registered in base
            var outDir = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, $"fleet-kf-outside-{Guid.NewGuid():N}");
            // Don't actually create it — just rely on the path being outside roots
            // (the endpoint validates roots before checking file existence)
            var response = await Page.APIRequest.PostAsync(
                $"{ServerUrl}/api/open-file",
                new() {
                    DataObject = new { filePath = "/etc/passwd", tool = "rider" }
                });

            response.Status.ShouldBe(400);
        });
    }
}
