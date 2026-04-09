using WeaveFleet.Domain.Common;

namespace WeaveFleet.Domain.Tests;

public sealed class ResultTests
{
    [Fact]
    public void Success_ReturnsSuccessResult()
    {
        var result = Result.Success(42);
        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void Failure_ReturnsFailureResult()
    {
        var error = FleetError.NotFound;
        var result = Result.Failure<int>(error);
        result.IsSuccess.ShouldBeFalse();
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(error);
    }

    [Fact]
    public void Value_ThrowsOnFailure()
    {
        var result = Result.Failure<int>(FleetError.NotFound);
        Should.Throw<InvalidOperationException>(() => _ = result.Value);
    }

    [Fact]
    public void Match_CallsCorrectBranch()
    {
        var success = Result.Success(10);
        var output = success.Match(v => v * 2, _ => -1);
        output.ShouldBe(20);
    }
}

public sealed class FleetErrorTests
{
    [Fact]
    public void NotFoundFor_BuildsCorrectCode()
    {
        var error = FleetError.NotFoundFor("Session", "abc-123");
        error.Code.ShouldBe("Session.NotFound");
        error.Description.ShouldContain("abc-123");
    }

    [Fact]
    public void ValidationError_BuildsCorrectCode()
    {
        var error = FleetError.ValidationError("Name", "Name is required");
        error.Code.ShouldBe("Validation.Name");
    }
}

public sealed class UnitTypeTests
{
    [Fact]
    public void Unit_EqualsAnotherUnit()
    {
        Unit.Value.ShouldBe(Unit.Value);
    }
}
