using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>基于 <see cref="Socket"/> 的监听器。</summary>
    internal sealed class SocketChannelListener : IChannelListener
    {
        private readonly Socket _listenSocket;
        private readonly ChannelServerOptions _options;
        private readonly ILogger? _logger;
        private int _unbound;

        private SocketChannelListener(Socket listenSocket, ChannelServerOptions options, ILogger? logger)
        {
            _listenSocket = listenSocket;
            _options = options;
            _logger = logger;
            LocalEndPoint = listenSocket.LocalEndPoint
                ?? throw new InvalidOperationException("监听 socket 尚未绑定。");
        }

        public EndPoint? LocalEndPoint { get; }

        public static ValueTask<SocketChannelListener> BindAsync(
            ChannelServerOptions options,
            ILoggerFactory? loggerFactory,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var logger = loggerFactory?.CreateLogger<SocketChannelListener>();
            Socket? socket = null;

            try
            {
                if (options.CreateBoundListenSocket is { } factory)
                {
                    socket = factory(options.EndPoint);

                    if (socket is null)
                        throw new InvalidOperationException("CreateBoundListenSocket 返回了 null。");
                }
                else
                {
                    socket = CreateBoundSocket(options, logger);
                }

                socket.Listen(options.Backlog);
                return ValueTask.FromResult(new SocketChannelListener(socket, options, logger));
            }
            catch
            {
                socket?.Dispose();
                throw;
            }
        }

        public async ValueTask<IChannelConnection?> AcceptAsync(CancellationToken ct)
        {
            var socket = await _listenSocket.AcceptAsync(ct).ConfigureAwait(false);

            try
            {
                ApplyAcceptedOptions(socket, _options, _logger);
                return new SocketChannelConnection(socket, _options, _logger);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public ValueTask UnbindAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _unbound, 1) == 0)
                _listenSocket.Dispose();

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            UnbindAsync(CancellationToken.None);
            return ValueTask.CompletedTask;
        }

        private static Socket CreateBoundSocket(ChannelServerOptions options, ILogger? logger)
        {
            var protocol = options.EndPoint is IPEndPoint ? ProtocolType.Tcp : ProtocolType.Unspecified;
            var socket = new Socket(options.EndPoint.AddressFamily, SocketType.Stream, protocol);

            try
            {
                ApplyListenerOptions(socket, options, logger);
                socket.Bind(options.EndPoint);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private static void ApplyListenerOptions(
            Socket socket,
            ChannelServerOptions options,
            ILogger? logger)
        {
            if (options.ReuseAddress)
                TrySetSocketOption(
                    () => socket.SetSocketOption(
                        SocketOptionLevel.Socket,
                        SocketOptionName.ReuseAddress,
                        true),
                    logger,
                    "应用 SO_REUSEADDR 失败。");

            if (socket.AddressFamily == AddressFamily.InterNetworkV6 && options.DualMode)
                TrySetSocketOption(() => socket.DualMode = true, logger, "启用 IPv6 双栈失败。");
        }

        private static void ApplyAcceptedOptions(
            Socket socket,
            ChannelServerOptions options,
            ILogger? logger)
        {
            if (socket.ProtocolType == ProtocolType.Tcp)
            {
                TrySetSocketOption(() => socket.NoDelay = options.NoDelay, logger, "设置 TCP_NODELAY 失败。");

                if (options.KeepAlive is { Enabled: true } keepAlive)
                    ApplyKeepAlive(socket, keepAlive, logger);
            }

            if (options.LingerState is not null)
                TrySetSocketOption(() => socket.LingerState = options.LingerState, logger, "设置 SO_LINGER 失败。");
        }

        private static void ApplyKeepAlive(
            Socket socket,
            ChannelKeepAliveOptions keepAlive,
            ILogger? logger)
        {
            TrySetSocketOption(
                () => socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true),
                logger,
                "启用 TCP keep-alive 失败。");

            TrySetSocketOption(
                () => socket.SetSocketOption(
                    SocketOptionLevel.Tcp,
                    SocketOptionName.TcpKeepAliveTime,
                    (int)keepAlive.KeepAliveTime.TotalSeconds),
                logger,
                "设置 TCP keep-alive 空闲时间失败。");

            TrySetSocketOption(
                () => socket.SetSocketOption(
                    SocketOptionLevel.Tcp,
                    SocketOptionName.TcpKeepAliveInterval,
                    (int)keepAlive.KeepAliveInterval.TotalSeconds),
                logger,
                "设置 TCP keep-alive 探测间隔失败。");

            TrySetSocketOption(
                () => socket.SetSocketOption(
                    SocketOptionLevel.Tcp,
                    SocketOptionName.TcpKeepAliveRetryCount,
                    keepAlive.KeepAliveRetryCount),
                logger,
                "设置 TCP keep-alive 重试次数失败。");
        }

        private static void TrySetSocketOption(Action action, ILogger? logger, string message)
        {
            try
            {
                action();
            }
            catch (SocketException ex)
            {
                logger?.LogWarning(ex, message);
            }
            catch (PlatformNotSupportedException ex)
            {
                logger?.LogWarning(ex, message);
            }
            catch (ArgumentException ex)
            {
                logger?.LogWarning(ex, message);
            }
            catch (InvalidOperationException ex)
            {
                logger?.LogWarning(ex, message);
            }
        }
    }
}
