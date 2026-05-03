using Microsoft.Extensions.Options;

namespace NuCode.Configuration;

/// <summary>
/// Custom <see cref="IOptionsFactory{NuCodeConfig}"/> that produces a merged <see cref="NuCodeConfig"/>
/// from the <see cref="IConfigLoader"/> instead of using the default configure/post-configure pipeline.
/// This is needed because <see cref="NuCodeConfig"/> uses init-only properties.
/// </summary>
internal sealed class NuCodeConfigFactory : IOptionsFactory<NuCodeConfig>
{
    private readonly IConfigLoader _loader;

    internal NuCodeConfigFactory(IConfigLoader loader)
    {
        _loader = loader;
    }

    public NuCodeConfig Create(string name)
    {
        return _loader.Load();
    }
}
