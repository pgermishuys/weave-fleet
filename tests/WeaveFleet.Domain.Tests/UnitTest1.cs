using WeaveFleet.Domain.Common;

namespace WeaveFleet.Domain.Tests;

public sealed class ResultTests
{
    [Fact]
    public void Success_ReturnsSuccessResult()
    {
        var result = Result.Success(42);
        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Failure_ReturnsFailureResult()
    {
        var error = FleetError.NotFound;
        var result = Result.Failure<int>(error);
        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void Value_ThrowsOnFailure()
    {
        var result = Result.Failure<int>(FleetError.NotFound);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Match_CallsCorrectBranch()
    {
        var success = Result.Success(10);
        var output = success.Match(v => v * 2, _ => -1);
        Assert.Equal(20, output);
    }
}

public sealed class FleetErrorTests
{
    [Fact]
    public void NotFoundFor_BuildsCorrectCode()
    {
        var error = FleetError.NotFoundFor("Session", "abc-123");
        Assert.Equal("Session.NotFound", error.Code);
        Assert.Contains("abc-123", error.Description);
    }

    [Fact]
    public void ValidationError_BuildsCorrectCode()
    {
        var error = FleetError.ValidationError("Name", "Name is required");
        Assert.Equal("Validation.Name", error.Code);
    }
}

public sealed class UnitTypeTests
{
    [Fact]
    public void Unit_EqualsAnotherUnit()
    {
        Assert.Equal(Unit.Value, Unit.Value);
    }
}
