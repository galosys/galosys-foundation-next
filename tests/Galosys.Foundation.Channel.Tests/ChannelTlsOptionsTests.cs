using System.Net.Security;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ChannelTlsOptionsTests
{
    [Fact]
    public void Defaults_UseTenSecondHandshakeTimeout()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();
        var options = new ChannelTlsOptions
        {
            ServerOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
            },
        };

        Assert.Equal(TimeSpan.FromSeconds(10), options.HandshakeTimeout);
        options.ValidateServer();
    }

    [Fact]
    public void ValidateServer_Throws_WhenCertificateMissing()
    {
        var options = new ChannelTlsOptions
        {
            ServerOptions = new SslServerAuthenticationOptions(),
        };

        Assert.Throws<ArgumentException>(() => options.ValidateServer());
    }

    [Fact]
    public void Validate_Throws_WhenHandshakeTimeoutInvalid()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();
        var options = new ChannelTlsOptions
        {
            ServerOptions = new SslServerAuthenticationOptions { ServerCertificate = certificate },
            HandshakeTimeout = TimeSpan.Zero,
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void ValidateClient_Throws_WhenTargetHostMissing()
    {
        var options = new ChannelTlsOptions
        {
            ClientOptions = new SslClientAuthenticationOptions { TargetHost = " " },
        };

        Assert.Throws<ArgumentException>(() => options.ValidateClient());
    }
}
