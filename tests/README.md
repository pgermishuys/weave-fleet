# .NET Test Conventions

## Assertion Style

We use **Shouldly** as the preferred assertion style across all .NET test projects.

> New tests and any existing tests touched for feature work or bug fixes should use Shouldly assertions by default. Existing xUnit `Assert`-based tests do not need to be rewritten wholesale; migrate them incrementally as files are touched or as part of focused assertion-style cleanup. NSubstitute interaction assertions such as `Received()` and `DidNotReceive()` should remain unchanged.

## Policy

- Prefer Shouldly for value, null, type, string, boolean, and collection assertions.
- Keep xUnit for `[Fact]`, `[Theory]`, fixtures, and exception-hosting semantics.
- Migrate legacy `Assert` usage incrementally; do not do repo-wide mechanical rewrites unless explicitly planned.
- Preserve test behavior during conversion.
- Do **not** rewrite NSubstitute interaction assertions such as `Received()` / `DidNotReceive()`.

## Common Conversion Guide

| xUnit | Shouldly |
|---|---|
| `Assert.Equal(expected, actual)` | `actual.ShouldBe(expected)` |
| `Assert.NotEqual(notExpected, actual)` | `actual.ShouldNotBe(notExpected)` |
| `Assert.True(value)` | `value.ShouldBeTrue()` |
| `Assert.False(value)` | `value.ShouldBeFalse()` |
| `Assert.Null(value)` | `value.ShouldBeNull()` |
| `Assert.NotNull(value)` | `value.ShouldNotBeNull()` |
| `Assert.IsType<T>(value)` | `value.ShouldBeOfType<T>()` |
| `Assert.IsAssignableFrom<T>(value)` | `value.ShouldBeAssignableTo<T>()` |
| `Assert.Contains(expected, items)` | `items.ShouldContain(expected)` |
| `Assert.DoesNotContain(unexpected, items)` | `items.ShouldNotContain(unexpected)` |
| `Assert.Single(items)` | `items.ShouldHaveSingleItem()` |
| `Assert.Empty(items)` | `items.ShouldBeEmpty()` |
| `Assert.NotEmpty(items)` | `items.ShouldNotBeEmpty()` |
| `await Assert.ThrowsAsync<T>(act)` | `await Should.ThrowAsync<T>(act)` |

## Notes

- Prefer actual-first Shouldly idioms for consistency.
- If a naive conversion reads worse or changes semantics, keep the clearer form and document the exception in review.
- When touching a file that still uses xUnit `Assert`, convert the touched assertions unless there is a clear reason not to.
