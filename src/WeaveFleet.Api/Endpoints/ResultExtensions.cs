using WeaveFleet.Domain.Common;

namespace WeaveFleet.Api.Endpoints;

/// <summary>
/// Extension methods for converting <see cref="Result{T}"/> to <see cref="IResult"/> for minimal API responses.
/// </summary>
public static class ResultExtensions
{
    public static IResult ToApiResult<T>(this Result<T> result) =>
        result.Match(
            value => Results.Ok(value),
            error => error.Code switch
            {
                var c when c.EndsWith(".NotFound", StringComparison.Ordinal) || c == "General.NotFound"
                    => Results.NotFound(new ApiErrorResponse(error.Description)),
                "General.Conflict"
                    => Results.Conflict(new ApiErrorResponse(error.Description)),
                var c when c.StartsWith("Validation.", StringComparison.Ordinal)
                    => Results.BadRequest(new ApiErrorResponse(error.Description)),
                _   => Results.Problem(error.Description)
            });

    /// <summary>
    /// The same status mapping for an endpoint that has already taken the success path apart and
    /// only needs the failure turned into a response.
    /// </summary>
    public static IResult ToErrorResult(this FleetError error) =>
        error.Code switch
        {
            var c when c.EndsWith(".NotFound", StringComparison.Ordinal) || c == "General.NotFound"
                => Results.NotFound(new ApiErrorResponse(error.Description)),
            "General.Conflict"
                => Results.Conflict(new ApiErrorResponse(error.Description)),
            var c when c.StartsWith("Validation.", StringComparison.Ordinal)
                => Results.BadRequest(new ApiErrorResponse(error.Description)),
            _   => Results.Problem(error.Description)
        };

    public static IResult ToNoContentResult(this Result<Unit> result) =>
        result.Match(
            _ => Results.NoContent(),
            error => error.Code switch
            {
                var c when c.EndsWith(".NotFound", StringComparison.Ordinal) || c == "General.NotFound"
                    => Results.NotFound(new ApiErrorResponse(error.Description)),
                "General.Conflict"
                    => Results.Conflict(new ApiErrorResponse(error.Description)),
                var c when c.StartsWith("Validation.", StringComparison.Ordinal)
                    => Results.BadRequest(new ApiErrorResponse(error.Description)),
                _   => Results.Problem(error.Description)
            });
}
