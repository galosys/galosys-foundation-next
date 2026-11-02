using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class NamespaceConventionTests
{
    [Fact]
    public void OptionsTypes_UseMicrosoftExtensionsOptionsNamespace()
    {
        var assembly = typeof(ChannelServer).Assembly;

        Assert.NotNull(assembly.GetType("Microsoft.Extensions.Options.ChannelServerOptions"));
        Assert.NotNull(assembly.GetType("Microsoft.Extensions.Options.ChannelKeepAliveOptions"));
        Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelServerOptions"));
        Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelKeepAliveOptions"));
    }

    [Fact]
    public void RemovedServerBaseAndDiExtensions_AreNotPublic()
    {
        var assembly = typeof(ChannelServer).Assembly;

        Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelServerBase"));
        Assert.Null(assembly.GetType("Microsoft.Extensions.DependencyInjection.ChannelServerServiceCollectionExtensions"));
    }

    [Fact]
    public void ChannelClientBase_IsRemoved()
    {
        var assembly = typeof(ChannelClient).Assembly;

        Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelClientBase"));
        Assert.Equal(
            "Microsoft.Extensions.Hosting.Net",
            typeof(ChannelClient).Namespace);
        Assert.Equal(
            "Microsoft.Extensions.Options",
            typeof(ChannelClientOptions).Namespace);
    }

    [Fact]
    public void ChannelServer_IsOnlyPublicServerImplementation()
    {
        var assembly = typeof(ChannelServer).Assembly;

        Assert.True(typeof(ChannelServer).IsPublic);
        Assert.True(typeof(ChannelServer).IsSealed);
        Assert.True(typeof(IChannelServer).IsAssignableFrom(typeof(ChannelServer)));

        var core = assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelServerCore");
        Assert.NotNull(core);
        Assert.False(core.IsPublic);
    }
}
