using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Tests.Auth;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalTokenAuthServiceTestsGroup : ICollectionFixture<LocalTokenAuthServiceTestsGroupFixture>
{
    public const string Name = "LocalTokenAuthService";
}

public sealed class LocalTokenAuthServiceTestsGroupFixture
{
}

[Collection(LocalTokenAuthServiceTestsGroup.Name)]
public sealed class LocalTokenAuthServiceTests
{
    private const string AuthTokenEnvironmentVariable = "WEAVE_FLEET_AUTH_TOKEN";

    [Fact]
    public void Should_generate_token_when_environment_variable_is_absent()
    {
        using var _ = new EnvironmentVariableScope(AuthTokenEnvironmentVariable, value: null);

        var sut = new LocalTokenAuthService();

        sut.Token.ShouldNotBeNullOrWhiteSpace();
        sut.ValidateToken(sut.Token).ShouldBeTrue();
    }

    [Fact]
    public void Should_use_environment_variable_token_when_present()
    {
        const string configuredToken = "1234567890abcdef";
        using var _ = new EnvironmentVariableScope(AuthTokenEnvironmentVariable, configuredToken);

        var sut = new LocalTokenAuthService();

        sut.Token.ShouldBe(configuredToken);
    }

    [Fact]
    public void Should_return_true_when_token_is_valid()
    {
        const string configuredToken = "fedcba0987654321";
        using var _ = new EnvironmentVariableScope(AuthTokenEnvironmentVariable, configuredToken);

        var sut = new LocalTokenAuthService();

        sut.ValidateToken(configuredToken).ShouldBeTrue();
    }

    [Fact]
    public void Should_return_false_when_token_is_invalid()
    {
        const string configuredToken = "fedcba0987654321";
        using var _ = new EnvironmentVariableScope(AuthTokenEnvironmentVariable, configuredToken);

        var sut = new LocalTokenAuthService();

        sut.ValidateToken("0123456789abcdef").ShouldBeFalse();
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _originalValue);
        }
    }
}
