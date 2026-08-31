using System.Text.Json;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Infrastructure.Tests.Events;

public sealed class FilesChangedSerializationTests
{
    [Fact]
    public void Should_serialize_and_deserialize_files_changed_event()
    {
        // Arrange
        var original = new FilesChanged
        {
            Payload = new FilesChangedPayload
            {
                SessionId = "test-session-1",
                Files = new[]
                {
                    new FileChangeEntry { Path = "/src/Program.cs", ChangeType = "modified" },
                    new FileChangeEntry { Path = "/src/NewFile.cs", ChangeType = "created" }
                }
            }
        };

        // Act - serialize as polymorphic DomainEvent base type
        var json = JsonSerializer.Serialize<DomainEvent>(original);
        var deserialized = JsonSerializer.Deserialize<DomainEvent>(json);

        // Assert
        var filesChanged = deserialized.ShouldBeOfType<FilesChanged>();
        filesChanged.Payload.SessionId.ShouldBe("test-session-1");
        filesChanged.Payload.Files.Count.ShouldBe(2);
        filesChanged.Payload.Files[0].Path.ShouldBe("/src/Program.cs");
        filesChanged.Payload.Files[0].ChangeType.ShouldBe("modified");
        filesChanged.Payload.Files[1].Path.ShouldBe("/src/NewFile.cs");
        filesChanged.Payload.Files[1].ChangeType.ShouldBe("created");
    }

    [Fact]
    public void Should_include_type_discriminator_in_serialized_json()
    {
        // Arrange
        var evt = new FilesChanged
        {
            Payload = new FilesChangedPayload
            {
                SessionId = "test-session-1",
                Files = []
            }
        };

        // Act
        var json = JsonSerializer.Serialize<DomainEvent>(evt);

        // Assert
        json.ShouldContain("\"type\":\"files.changed\"");
    }

    [Fact]
    public void Should_deserialize_from_json_with_type_discriminator()
    {
        // Arrange
        var json = """
            {
                "type": "files.changed",
                "Payload": {
                    "SessionId": "test-session-1",
                    "Files": [
                        { "Path": "/test.cs", "ChangeType": "modified" }
                    ]
                }
            }
            """;

        // Act
        var deserialized = JsonSerializer.Deserialize<DomainEvent>(json);

        // Assert
        var filesChanged = deserialized.ShouldBeOfType<FilesChanged>();
        filesChanged.Payload.SessionId.ShouldBe("test-session-1");
        filesChanged.Payload.Files.Count.ShouldBe(1);
    }
}
