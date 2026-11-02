using System;
using System.Net.Security;

namespace Microsoft.Extensions.Options
{
    /// <summary>TLS 握手配置。</summary>
    public sealed class ChannelTlsOptions
    {
        /// <summary>服务端认证选项；服务端模式必须设置。</summary>
        public SslServerAuthenticationOptions? ServerOptions { get; set; }

        /// <summary>客户端认证选项；客户端模式必须设置。</summary>
        public SslClientAuthenticationOptions? ClientOptions { get; set; }

        /// <summary>握手超时，默认 10 秒。</summary>
        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

        internal void Validate()
        {
            if (ServerOptions is null && ClientOptions is null)
                throw new ArgumentException("必须配置 ServerOptions 或 ClientOptions。", nameof(ChannelTlsOptions));

            if (HandshakeTimeout <= TimeSpan.Zero || HandshakeTimeout > TimeSpan.FromDays(1))
                throw new ArgumentException("HandshakeTimeout 必须大于 0 且不超过 1 天。", nameof(HandshakeTimeout));
        }

        internal void ValidateServer()
        {
            Validate();

            if (ServerOptions is null)
                throw new ArgumentException("服务端模式必须设置 ServerOptions。", nameof(ServerOptions));

            if (ServerOptions.ServerCertificate is null
                && ServerOptions.ServerCertificateContext is null
                && ServerOptions.ServerCertificateSelectionCallback is null)
            {
                throw new ArgumentException(
                    "ServerOptions 必须提供 ServerCertificate、ServerCertificateContext 或 ServerCertificateSelectionCallback。",
                    nameof(ServerOptions));
            }
        }

        internal void ValidateClient()
        {
            Validate();

            if (ClientOptions is null)
                throw new ArgumentException("客户端模式必须设置 ClientOptions。", nameof(ClientOptions));

            if (string.IsNullOrWhiteSpace(ClientOptions.TargetHost))
                throw new ArgumentException("ClientOptions.TargetHost 不能为空。", nameof(ClientOptions));
        }
    }
}
