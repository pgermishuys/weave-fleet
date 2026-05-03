namespace NuCode;

public sealed class IdentifiersTests
{
    [Fact]
    public void SessionIdNewGeneratesUniqueValues()
    {
        var id1 = SessionId.New();
        var id2 = SessionId.New();

        id2.ShouldNotBe(id1);
        string.IsNullOrWhiteSpace(id1.Value).ShouldBeFalse();
    }

    [Fact]
    public void MessageIdNewGeneratesUniqueValues()
    {
        var id1 = MessageId.New();
        var id2 = MessageId.New();

        id2.ShouldNotBe(id1);
        string.IsNullOrWhiteSpace(id1.Value).ShouldBeFalse();
    }

    [Fact]
    public void PartIdNewGeneratesUniqueValues()
    {
        var id1 = PartId.New();
        var id2 = PartId.New();

        id2.ShouldNotBe(id1);
    }

    [Fact]
    public void ToolIdNewGeneratesUniqueValues()
    {
        var id1 = ToolId.New();
        var id2 = ToolId.New();

        id2.ShouldNotBe(id1);
    }

    [Fact]
    public void AgentIdNewGeneratesUniqueValues()
    {
        var id1 = AgentId.New();
        var id2 = AgentId.New();

        id2.ShouldNotBe(id1);
    }

    [Fact]
    public void ImplicitConversionToStringReturnsValue()
    {
        var sessionId = new SessionId("test-session-id");

        string? result = (SessionId?)sessionId;

        result.ShouldBe("test-session-id");
    }

    [Fact]
    public void ToStringReturnsValue()
    {
        var id = new SessionId("abc-123");

        id.ToString().ShouldBe("abc-123");
    }

    [Fact]
    public void EqualityWorksForSameValue()
    {
        var id1 = new SessionId("same");
        var id2 = new SessionId("same");

        id2.ShouldBe(id1);
    }
}

public sealed class ToolResultTests
{
    [Fact]
    public void CanCreateWithRequiredPropertiesOnly()
    {
        var result = new ToolResult("Read file", "file contents here");

        result.Title.ShouldBe("Read file");
        result.Output.ShouldBe("file contents here");
        result.Metadata.ShouldBeNull();
        result.Attachments.ShouldBeNull();
    }

    [Fact]
    public void CanCreateWithAllProperties()
    {
        var metadata = new Dictionary<string, object> { ["lines"] = 42 };
        var attachments = new List<ToolAttachment>
        {
            new("image.png", "image/png", new byte[] { 1, 2, 3 })
        };

        var result = new ToolResult("Read file", "contents", metadata, attachments);

        result.Metadata.ShouldNotBeNull();
        result.Metadata.ShouldHaveSingleItem();
        result.Attachments.ShouldNotBeNull();
        result.Attachments.ShouldHaveSingleItem();
    }
}

public sealed class ToolAttachmentTests
{
    [Fact]
    public void CanCreateAttachment()
    {
        var data = new byte[] { 0xFF, 0xD8, 0xFF };
        var attachment = new ToolAttachment("photo.jpg", "image/jpeg", data);

        attachment.Name.ShouldBe("photo.jpg");
        attachment.MimeType.ShouldBe("image/jpeg");
        attachment.Data.Length.ShouldBe(3);
    }
}
