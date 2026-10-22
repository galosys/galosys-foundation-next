using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class NamespaceConventionTests
{
    [Fact]
    public void OptionsTypes_UseMicrosoftExtensionsOptionsNamespace()
    {
        var assembly = typeof(ChannelServerBase).Assembly;

        Assert.NotNull(assembly.GetType("Microsoft.Extensions.Options.ChannelServerOptions"));
        Assert.NotNull(assembly.GetType("Microsoft.Extensions.Options.ChannelKeepAliveOptions"));
        Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelServerOptions"));
        Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelKeepAliveOptions"));
    }

    [Fact]
    public void DiExtensions_UseMicrosoftExtensionsDependencyInjectionNamespace()
    {
        var assembly = typeof(ChannelServerBase).Assembly;

        Assert.NotNull(assembly.GetType("Microsoft.Extensions.DependencyInjection.ChannelServerServiceCollectionExtensions"));
        Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelServerServiceCollectionExtensions"));
    }
}
