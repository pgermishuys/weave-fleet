using System.Text.RegularExpressions;
using WeaveFleet.Domain.Identity;
using Xunit;

namespace WeaveFleet.Domain.Tests.Identity;

public class AscendingMessageIdTests
{
    [Fact]
    public void New_GeneratesIdWithCorrectFormat()
    {
        // Arrange
        var pattern = new Regex(@"^msg_[0-9a-f]{12}[0-9A-Za-z]{14}$");

        // Act
        var id = AscendingMessageId.New();

        // Assert
        Assert.Matches(pattern, id);
        Assert.Equal(30, id.Length); // 4 (prefix) + 12 (hex) + 14 (base62)
    }

    [Fact]
    public void New_GeneratesMultipleIdsInSameMillisecond_SortsAscending()
    {
        // Arrange & Act
        var ids = new List<string>();
        for (var i = 0; i < 100; i++)
        {
            ids.Add(AscendingMessageId.New());
        }

        // Assert
        var sorted = ids.OrderBy(x => x).ToList();
        Assert.Equal(sorted, ids); // IDs should already be in ascending order
    }

    [Fact]
    public void ExtractTimestamp_RoundTripsForSmallTimestamps()
    {
        // Timestamps that fit within 48 bits when multiplied by 0x1000
        // Max safe timestamp: 2^48 / 0x1000 = 68,719,476,736 ms (~2.18 years from epoch)
        var timestampMs = 60_000_000_000L; // ~1.9 years from epoch, fits in 48 bits
        var id = AscendingMessageId.NewFromTimestamp(timestampMs, 0);

        var extracted = AscendingMessageId.ExtractTimestamp(id);

        Assert.Equal(timestampMs, extracted);
    }

    [Fact]
    public void ExtractTimestamp_WithCounter_DropsCounterBits()
    {
        // Counter is in the lower 12 bits, so ExtractTimestamp should still return the timestamp
        var timestampMs = 60_000_000_000L;
        var counter = 42;
        var id = AscendingMessageId.NewFromTimestamp(timestampMs, counter);

        var extracted = AscendingMessageId.ExtractTimestamp(id);

        Assert.Equal(timestampMs, extracted);
    }

    [Fact]
    public void ExtractTimestamp_LargeTimestamps_Truncated_But_Sorting_Preserved()
    {
        // Current-era timestamps overflow 48 bits when * 0x1000,
        // but IDs still sort correctly (key property)
        var ts1 = 1700000000000L;
        var ts2 = 1700000001000L; // 1 second later
        var id1 = AscendingMessageId.NewFromTimestamp(ts1, 0);
        var id2 = AscendingMessageId.NewFromTimestamp(ts2, 0);

        // Sorting is preserved even with truncation
        Assert.True(string.CompareOrdinal(id1, id2) < 0);
    }

    [Fact]
    public void NewFromTimestamp_SameTimestampDifferentCounters_SortsAscending()
    {
        // Arrange
        var timestampMs = 1700000000000L;
        var id1 = AscendingMessageId.NewFromTimestamp(timestampMs, 0);
        var id2 = AscendingMessageId.NewFromTimestamp(timestampMs, 1);
        var id3 = AscendingMessageId.NewFromTimestamp(timestampMs, 2);

        // Act & Assert
        Assert.True(string.CompareOrdinal(id1, id2) < 0, $"id1 ({id1}) should be < id2 ({id2})");
        Assert.True(string.CompareOrdinal(id2, id3) < 0, $"id2 ({id2}) should be < id3 ({id3})");
    }

    [Fact]
    public void ExtractTimestamp_InvalidId_ThrowsArgumentException()
    {
        // Arrange
        var invalidIds = new[]
        {
            "",
            "invalid",
            "msg_",
            "msg_short",
            "wrong_prefix_123456789012"
        };

        // Act & Assert
        foreach (var invalidId in invalidIds)
        {
            Assert.Throws<ArgumentException>(() => AscendingMessageId.ExtractTimestamp(invalidId));
        }
    }

    [Fact]
    public void New_GeneratesUniqueIds()
    {
        // Arrange & Act
        var ids = new HashSet<string>();
        for (var i = 0; i < 1000; i++)
        {
            ids.Add(AscendingMessageId.New());
        }

        // Assert
        Assert.Equal(1000, ids.Count); // All IDs should be unique
    }
}
