using Galosys.Foundation.Channel;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel.Tests;

public sealed class NamespaceConventionTests
{
    [Fact]
    public void OptionsTypes_UseMicrosoftExtensionsOptionsNamespace()
    {
        var assembly = typeof(ChannelServer).Assembly;

        Assert.NotNull(assembly.GetType("Microsoft.Extensions.Options.ChannelServerOptions"));
        Assert.NotNull(assembly.GetType("Microsoft.Extensions.Options.ChannelKeepAliveOptions"));
        Assert.Null(assembly.GetType("Galosys.Foundation.Channel.ChannelServerOptions"));
        Assert.Null(assembly.GetType("Galosys.Foundation.Channel.ChannelKeepAliveOptions"));
    }

    [Fact]
    public void RemovedServerBaseAndDiExtensions_AreNotPublic()
    {
        var assembly = typeof(ChannelServer).Assembly;

        Assert.Null(assembly.GetType("Galosys.Foundation.Channel.ChannelServerBase"));
        Assert.Null(assembly.GetType("Microsoft.Extensions.DependencyInjection.ChannelServerServiceCollectionExtensions"));
    }

    [Fact]
    public void ChannelClientBase_IsRemoved()
    {
        var assembly = typeof(ChannelClient).Assembly;

        Assert.Null(assembly.GetType("Galosys.Foundation.Channel.ChannelClientBase"));
        Assert.Equal(
            "Galosys.Foundation.Channel",
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

        var core = assembly.GetType("Galosys.Foundation.Channel.ChannelServerCore");
        Assert.NotNull(core);
        Assert.False(core.IsPublic);
    }

    [Fact]
    public void HostedServiceAndDiExtensions_AreRemoved()
    {
        var assembly = typeof(ChannelServer).Assembly;

        Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.ChannelServerGroupHostedService"));
        Assert.Null(assembly.GetType("Microsoft.Extensions.DependencyInjection.ChannelServerGroupServiceCollectionExtensions"));
        Assert.Null(assembly.GetType("Galosys.Foundation.Channel.ChannelServerDefinition"));
        Assert.DoesNotContain(assembly.GetTypes(), t => t.Namespace == "Microsoft.Extensions.Hosting");
        Assert.DoesNotContain(assembly.GetTypes(), t => t.Namespace == "Microsoft.Extensions.DependencyInjection");
    }

    [Fact]
    public void Constructors_AreNotPublic()
    {
        foreach (var type in new[]
        {
            typeof(ChannelServer),
            typeof(ChannelClient),
            typeof(ChannelClientPool),
        })
        {
            Assert.Empty(type.GetConstructors(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance));
        }
    }

    [Fact]
    public void Assembly_ExposesNoTypesUnderLegacyHostingNetNamespace()
    {
        var assembly = typeof(ChannelServer).Assembly;

        Assert.DoesNotContain(assembly.GetTypes(), t => t.Namespace == "Microsoft.Extensions.Hosting.Net");
    }
}
