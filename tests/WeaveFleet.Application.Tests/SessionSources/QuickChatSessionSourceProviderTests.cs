using System.Text.Json;
using Shouldly;
using WeaveFleet.Application.SessionSources;

namespace WeaveFleet.Application.Tests.SessionSources;

public sealed class QuickChatSessionSourceProviderTests : IDisposable
{
    private readonly QuickChatSessionSourceProvider _sut = new();
    private readonly List<string> _createdDirectories = [];

    [Fact]
    public async Task ResolveAsync_WithMatchingKey_ReturnsSuccessAndCreatesDirectory()
    {
        var selection = new SessionSourceSelection
        {
            Key = SessionSourceCatalog.QuickChatStartSession.Key,
            Input = JsonDocument.Parse("{}").RootElement
        };

        var result = await _sut.ResolveAsync(selection, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value.Descriptor.ShouldBe(SessionSourceCatalog.QuickChatStartSession);
        result.Value.Input.WorkspaceIntent.ShouldNotBeNull();
        result.Value.Input.WorkspaceIntent.IsolationStrategy.ShouldBe("existing");
        result.Value.Input.Provenance.ProviderId.ShouldBe(SessionSourceProviderIds.QuickChat);
        result.Value.Input.Provenance.ActionId.ShouldBe(SessionSourceActions.StartSession);

        _createdDirectories.Add(result.Value.Input.WorkspaceIntent.Directory);
    }

    [Fact]
    public async Task ResolveAsync_WithNonMatchingKey_ReturnsValidationError()
    {
        var selection = new SessionSourceSelection
        {
            Key = new SessionSourceKey
            {
                ProviderId = "provider.forged",
                SourceType = SessionSourceTypeNames.QuickChat,
                ActionId = SessionSourceActions.StartSession,
                ContractVersion = 1
            },
            Input = JsonDocument.Parse("{}").RootElement
        };

        var result = await _sut.ResolveAsync(selection, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.SessionSource.Key");
    }

    [Fact]
    public async Task ResolveAsync_CreatedDirectoryExistsOnDisk()
    {
        var selection = new SessionSourceSelection
        {
            Key = SessionSourceCatalog.QuickChatStartSession.Key,
            Input = JsonDocument.Parse("{}").RootElement
        };

        var result = await _sut.ResolveAsync(selection, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var directoryPath = result.Value.Input.WorkspaceIntent!.Directory;
        Directory.Exists(directoryPath).ShouldBeTrue();

        _createdDirectories.Add(directoryPath);
    }

    [Fact]
    public void GetDescriptors_ReturnsExactlyOneDescriptorMatchingCatalogEntry()
    {
        var descriptors = _sut.GetDescriptors();

        descriptors.ShouldNotBeNull();
        descriptors.Count.ShouldBe(1);
        descriptors[0].ShouldBe(SessionSourceCatalog.QuickChatStartSession);
    }

    [Fact]
    public async Task MultipleResolves_CreateUniqueDirectories()
    {
        var selection = new SessionSourceSelection
        {
            Key = SessionSourceCatalog.QuickChatStartSession.Key,
            Input = JsonDocument.Parse("{}").RootElement
        };

        var result1 = await _sut.ResolveAsync(selection, CancellationToken.None);
        var result2 = await _sut.ResolveAsync(selection, CancellationToken.None);

        result1.IsSuccess.ShouldBeTrue();
        result2.IsSuccess.ShouldBeTrue();

        var dir1 = result1.Value.Input.WorkspaceIntent!.Directory;
        var dir2 = result2.Value.Input.WorkspaceIntent!.Directory;

        dir1.ShouldNotBe(dir2);
        Directory.Exists(dir1).ShouldBeTrue();
        Directory.Exists(dir2).ShouldBeTrue();

        _createdDirectories.Add(dir1);
        _createdDirectories.Add(dir2);
    }

    public void Dispose()
    {
        foreach (var directory in _createdDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}
