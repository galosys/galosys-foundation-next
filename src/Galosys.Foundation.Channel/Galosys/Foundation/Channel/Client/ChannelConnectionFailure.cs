using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Galosys.Foundation.Channel
{
    /// <summary>连接失败分类。</summary>
    public enum ChannelConnectionFailureKind
    {
        /// <summary>对端拒绝连接。</summary>
        ConnectionRefused,

        /// <summary>连接或握手超时。</summary>
        TimedOut,

        /// <summary>主机或网络不可达。</summary>
        HostUnreachable,

        /// <summary>其他网络层错误。</summary>
        Network,

        /// <summary>TLS 认证失败。</summary>
        Authentication,

        /// <summary>调用方取消或客户端关闭。</summary>
        Canceled,

        /// <summary>配置无效。</summary>
        Configuration,

        /// <summary>无法归类的失败。</summary>
        Unknown,
    }

    /// <summary>一次连接失败的分类结果。</summary>
    public sealed class ChannelConnectionFailure
    {
        /// <summary>创建失败分类。</summary>
        /// <param name="kind">失败分类。</param>
        /// <param name="exception">原始异常。</param>
        public ChannelConnectionFailure(ChannelConnectionFailureKind kind, Exception exception)
        {
            Kind = kind;
            Exception = exception ?? throw new ArgumentNullException(nameof(exception));
        }

        /// <summary>失败分类。</summary>
        public ChannelConnectionFailureKind Kind { get; }

        /// <summary>原始异常。</summary>
        public Exception Exception { get; }

        /// <summary>是否值得重试；仅网络类瞬时失败为 true。</summary>
        public bool IsRetryable => Kind is
            ChannelConnectionFailureKind.ConnectionRefused or
            ChannelConnectionFailureKind.TimedOut or
            ChannelConnectionFailureKind.HostUnreachable or
            ChannelConnectionFailureKind.Network;

        internal static ChannelConnectionFailure FromException(Exception exception)
            => exception switch
            {
                SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
                    new(ChannelConnectionFailureKind.ConnectionRefused, exception),
                SocketException { SocketErrorCode: SocketError.TimedOut } =>
                    new(ChannelConnectionFailureKind.TimedOut, exception),
                SocketException { SocketErrorCode: SocketError.HostUnreachable or SocketError.NetworkUnreachable } =>
                    new(ChannelConnectionFailureKind.HostUnreachable, exception),
                SocketException or IOException =>
                    new(ChannelConnectionFailureKind.Network, exception),
                TimeoutException =>
                    new(ChannelConnectionFailureKind.TimedOut, exception),
                AuthenticationException =>
                    new(ChannelConnectionFailureKind.Authentication, exception),
                OperationCanceledException =>
                    new(ChannelConnectionFailureKind.Canceled, exception),
                ArgumentException or InvalidOperationException =>
                    new(ChannelConnectionFailureKind.Configuration, exception),
                _ =>
                    new(ChannelConnectionFailureKind.Unknown, exception),
            };
    }
}
