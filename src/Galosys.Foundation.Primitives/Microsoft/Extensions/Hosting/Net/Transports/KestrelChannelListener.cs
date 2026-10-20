using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    internal sealed class KestrelChannelListener : IAsyncDisposable
    {
        private readonly IConnectionListener _listener;

        private KestrelChannelListener(IConnectionListener listener) => _listener = listener;

        public EndPoint? LocalEndPoint => _listener.EndPoint;

        public static async Task<KestrelChannelListener> BindAsync(
            ChannelServerOptions options,
            ILoggerFactory? loggerFactory,
            CancellationToken ct)
        {
            var factory = new SocketTransportFactory(
                OptionsFactory.Create(CreateTransportOptions(options)),
                loggerFactory ?? NullLoggerFactory.Instance);

            try
            {
                var listener = await factory.BindAsync(options.EndPoint, ct).ConfigureAwait(false);
                return new KestrelChannelListener(listener);
            }
            catch (AddressInUseException)
            {
                // Kestrel 将 bind 失败包装为 AddressInUseException；公共契约要求向调用方暴露 SocketException。
                throw new SocketException((int)SocketError.AddressAlreadyInUse);
            }
        }

        private static SocketTransportOptions CreateTransportOptions(ChannelServerOptions options) => new()
        {
            Backlog = options.Backlog,
            NoDelay = options.NoDelay,
            MaxReadBufferSize = options.MaxReadBufferSize,
            MaxWriteBufferSize = options.MaxWriteBufferSize,
            IOQueueCount = options.IOQueueCount,
            CreateBoundListenSocket = endPoint => CreateBoundSocket(endPoint, options),
        };

        private static Socket CreateBoundSocket(EndPoint endPoint, ChannelServerOptions options)
        {
            var socket = options.CreateBoundListenSocket is { } factory
                ? factory(endPoint)
                : new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            ApplySocketOptions(socket, options);

            if (options.CreateBoundListenSocket is null)
                socket.Bind(endPoint);

            return socket;
        }

        private static void ApplySocketOptions(Socket socket, ChannelServerOptions options)
        {
            socket.NoDelay = options.NoDelay;

            if (options.LingerState is not null)
                socket.LingerState = options.LingerState;

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, options.ReuseAddress);

            if (socket.AddressFamily == AddressFamily.InterNetworkV6 && options.DualMode)
                socket.DualMode = true;

            if (options.KeepAlive is { Enabled: true } keepAlive)
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                try
                {
                    socket.SetSocketOption(
                        SocketOptionLevel.Tcp,
                        SocketOptionName.TcpKeepAliveTime,
                        (int)keepAlive.KeepAliveTime.TotalSeconds);
                    socket.SetSocketOption(
                        SocketOptionLevel.Tcp,
                        SocketOptionName.TcpKeepAliveInterval,
                        (int)keepAlive.KeepAliveInterval.TotalSeconds);
                    socket.SetSocketOption(
                        SocketOptionLevel.Tcp,
                        SocketOptionName.TcpKeepAliveRetryCount,
                        keepAlive.KeepAliveRetryCount);
                }
                catch (SocketException)
                {
                }
            }
        }

        public ValueTask<ConnectionContext?> AcceptAsync(CancellationToken ct) => _listener.AcceptAsync(ct);

        public ValueTask UnbindAsync(CancellationToken ct) => _listener.UnbindAsync(ct);

        public ValueTask DisposeAsync() => _listener.DisposeAsync();
    }
}
