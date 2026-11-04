using System;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Galosys.Foundation.Channel;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel.Tests;

internal static class TlsTestCertificates
{
    // 进程内即时生成的测试证书导出密码，不是任何真实凭据。
    private const string PfxPassword = "maf-sample-tls";

    public static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1));

        // CreateSelfSigned 生成的证书带临时密钥，Windows Schannel 会以
        // “the platform does not support ephemeral keys” 拒绝使用。
        // 这里导出 PFX 再重新导入，得到带持久化私钥的证书。
        return new X509Certificate2(
            ephemeral.Export(X509ContentType.Pfx, PfxPassword),
            PfxPassword,
            X509KeyStorageFlags.Exportable);
    }
}

/// <summary>
/// 用真实 loopback socket 建立的服务端/客户端 TLS 管道对。
/// 该类型持有底层的 <see cref="SocketChannelConnection"/>，避免连接在返回前被释放。
/// </summary>
internal sealed class TlsTestPair : IAsyncDisposable
{
    private readonly SocketChannelConnection _serverConnection;
    private readonly SocketChannelConnection _clientConnection;
    private int _disposed;

    private TlsTestPair(
        TlsDuplexPipe server,
        TlsDuplexPipe client,
        SocketChannelConnection serverConnection,
        SocketChannelConnection clientConnection)
    {
        Server = server;
        Client = client;
        _serverConnection = serverConnection;
        _clientConnection = clientConnection;
    }

    /// <summary>服务端 TLS 管道。</summary>
    public TlsDuplexPipe Server { get; }

    /// <summary>客户端 TLS 管道。</summary>
    public TlsDuplexPipe Client { get; }

    /// <summary>客户端 TLS 管道底层的连接；用于验证适配器不释放底层传输。</summary>
    public SocketChannelConnection ClientConnection => _clientConnection;

    /// <summary>建立一对已完成握手的 TLS 管道。</summary>
    public static async Task<TlsTestPair> ConnectAsync(
        ChannelTlsOptions serverOptions,
        ChannelTlsOptions clientOptions,
        CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        SocketChannelConnection? serverConnection = null;
        SocketChannelConnection? clientConnection = null;

        try
        {
            var endPoint = (IPEndPoint)listener.LocalEndpoint;
            var acceptTask = listener.AcceptSocketAsync();

            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await clientSocket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
            var serverSocket = await acceptTask.ConfigureAwait(false);

            var transportOptions = new ChannelServerOptions
            {
                EndPoint = endPoint,
            };

            // SocketChannelConnection 拥有 socket 的释放责任，不要再额外 using socket。
            serverConnection = new SocketChannelConnection(serverSocket, transportOptions, logger: null);
            clientConnection = new SocketChannelConnection(clientSocket, transportOptions, logger: null);

            var serverTask = TlsDuplexPipe.CreateAsync(
                serverConnection.Transport, serverOptions, isServer: true, cancellationToken).AsTask();
            var clientTask = TlsDuplexPipe.CreateAsync(
                clientConnection.Transport, clientOptions, isServer: false, cancellationToken).AsTask();

            TlsDuplexPipe server;
            TlsDuplexPipe client;

            try
            {
                server = await serverTask.ConfigureAwait(false);
                client = await clientTask.ConfigureAwait(false);
            }
            catch
            {
                // 握手失败时先清理已创建的连接，再把异常抛给调用方。
                await DisposeConnectionsAsync(clientConnection, serverConnection).ConfigureAwait(false);
                throw;
            }

            return new TlsTestPair(server, client, serverConnection, clientConnection);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 建立一个明文客户端连接到服务端 TLS 握手，用于验证握手失败路径。
    /// </summary>
    public static async Task<TlsRawScenario> OpenRawAsync(
        ChannelTlsOptions serverOptions,
        CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        SocketChannelConnection? serverConnection = null;

        try
        {
            var endPoint = (IPEndPoint)listener.LocalEndpoint;
            var acceptTask = listener.AcceptSocketAsync();

            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await clientSocket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
            var serverSocket = await acceptTask.ConfigureAwait(false);

            var transportOptions = new ChannelServerOptions
            {
                EndPoint = endPoint,
            };

            serverConnection = new SocketChannelConnection(serverSocket, transportOptions, logger: null);

            // 明文客户端直接写 socket，字节才会真正到达服务端的 TLS 握手。
            var raw = new RawDuplexPipe(clientSocket);
            var serverTask = TlsDuplexPipe.CreateAsync(
                serverConnection.Transport, serverOptions, isServer: true, cancellationToken).AsTask();

            return new TlsRawScenario(raw, serverTask, serverConnection);
        }
        catch
        {
            if (serverConnection is not null)
            {
                try
                {
                    await serverConnection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 测试清理路径：释放失败不应掩盖原始断言结果。
                }
            }

            throw;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>先释放两个 TLS 管道，再释放底层连接。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await Client.DisposeAsync().ConfigureAwait(false);
        await Server.DisposeAsync().ConfigureAwait(false);
        await DisposeConnectionsAsync(_clientConnection, _serverConnection).ConfigureAwait(false);
    }

    private static async Task DisposeConnectionsAsync(
        SocketChannelConnection? clientConnection,
        SocketChannelConnection? serverConnection)
    {
        if (clientConnection is not null)
        {
            try
            {
                await clientConnection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 测试清理路径：释放失败不应掩盖原始断言结果。
            }
        }

        if (serverConnection is not null)
        {
            try
            {
                await serverConnection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 测试清理路径：释放失败不应掩盖原始断言结果。
            }
        }
    }
}

/// <summary>
/// 明文客户端 + 服务端握手任务，用于握手失败/超时场景。
/// </summary>
internal sealed class TlsRawScenario : IAsyncDisposable
{
    private readonly SocketChannelConnection _serverConnection;
    private readonly Task<TlsDuplexPipe> _serverTask;
    private int _disposed;

    internal TlsRawScenario(RawDuplexPipe raw, Task<TlsDuplexPipe> serverTask, SocketChannelConnection serverConnection)
    {
        RawClient = raw;
        _serverTask = serverTask;
        _serverConnection = serverConnection;
    }

    /// <summary>明文客户端的写端。</summary>
    public RawDuplexPipe RawClient { get; }

    /// <summary>服务端握手是否成功；用于断言“失败路径不得暴露可用管道”。</summary>
    public bool ServerSucceeded { get; private set; }

    /// <summary>等待服务端握手任务结束。</summary>
    public async Task<TlsDuplexPipe> WaitForServerAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var pipe = await _serverTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            ServerSucceeded = true;
            return pipe;
        }
        finally
        {
            // 无论成功失败都等待任务真正结束，避免连接还在握手中就被释放。
            await ObserveAsync();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await ObserveAsync();

        RawClient.Dispose();

        try
        {
            await _serverConnection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 测试清理路径：释放失败不应掩盖原始断言结果。
        }
    }

    private async Task ObserveAsync()
    {
        try
        {
            var pipe = await _serverTask.ConfigureAwait(false);
            ServerSucceeded = true;
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 握手失败已由调用方的断言表达；这里只需确保任务被观察，避免未观测异常。
        }
    }
}

/// <summary>
/// 测试用的明文客户端：直接把明文字节写到 socket，用来触发服务端 TLS 握手失败。
/// </summary>
internal sealed class RawDuplexPipe : IDisposable
{
    private readonly Socket _socket;
    private int _disposed;

    internal RawDuplexPipe(Socket socket)
    {
        _socket = socket;
    }

    /// <summary>向服务端写入明文字节。</summary>
    public async Task WriteAsync(byte[] payload)
    {
        await _socket.SendAsync(payload, SocketFlags.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _socket.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>
/// 服务端 TLS 集成测试用的客户端：建立 TCP 连接后完成客户端握手。
/// 证书校验按 SHA-256 指纹匹配测试证书，不使用“全接受”的危险回调。
/// </summary>
internal sealed class TlsServerTestClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private int _disposed;

    private TlsServerTestClient(TcpClient tcp, SslStream stream)
    {
        _tcp = tcp;
        Stream = stream;
    }

    /// <summary>已完成握手的 TLS 流。</summary>
    public SslStream Stream { get; }

    /// <summary>
    /// 连接服务端并完成 TLS 握手。
    /// </summary>
    /// <param name="endPoint">服务端地址。</param>
    /// <param name="certificate">期望的服务端证书；按 SHA-256 指纹匹配。</param>
    /// <param name="alpn">可选的 ALPN 协议名。</param>
    /// <param name="rejectCertificate">为 true 时拒绝服务端证书，用于模拟证书校验失败。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<TlsServerTestClient> ConnectAsync(
        IPEndPoint endPoint,
        X509Certificate2 certificate,
        string? alpn = null,
        bool rejectCertificate = false,
        CancellationToken cancellationToken = default)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(endPoint.Address, endPoint.Port).WaitAsync(TestWaits.DefaultTimeout, cancellationToken)
            .ConfigureAwait(false);

        SslStream? ssl = null;

        try
        {
            var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);

            ssl = new SslStream(
                tcp.GetStream(),
                leaveInnerStreamOpen: false,
                (_, remote, _, _) =>
                {
                    if (rejectCertificate)
                        return false;

                    return remote is not null
                        && remote.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint;
                });

            var options = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
            };

            if (alpn is not null)
            {
                options.ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    new SslApplicationProtocol(alpn),
                };
            }

            await ssl.AuthenticateAsClientAsync(options, cancellationToken).ConfigureAwait(false);
            return new TlsServerTestClient(tcp, ssl);
        }
        catch
        {
            // 握手失败时先释放已创建的流与套接字，再把异常抛给调用方。
            if (ssl is not null)
            {
                try
                {
                    await ssl.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 测试清理路径：释放失败不应掩盖原始异常。
                }
            }

            tcp.Dispose();
            throw;
        }
    }

    /// <summary>写入明文（加密前的）字节到 TLS 流。</summary>
    public Task WriteAsync(byte[] payload)
        => Stream.WriteAsync(payload).AsTask();

    /// <summary>
    /// 读取指定长度的字节；若连接被中止，抛出实际异常（不假装成干净的 EOF）。
    /// </summary>
    public async Task<byte[]> ReadExactlyAsync(int count)
    {
        var buffer = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var read = await Stream.ReadAsync(buffer.AsMemory(offset))
                .AsTask().WaitAsync(TestWaits.DefaultTimeout).ConfigureAwait(false);

            if (read == 0)
                throw new IOException("连接在读取完成前关闭。");

            offset += read;
        }

        return buffer;
    }

    /// <summary>
    /// 断言连接已被关闭或中止：读到干净的 EOF，或抛出 IO/套接字异常。
    /// Windows 上服务端中止连接时客户端常收到 RST，而不是干净的 <c>read == 0</c>。
    /// </summary>
    public async Task AssertClosedAsync()
    {
        try
        {
            var read = await Stream.ReadAsync(new byte[1]).AsTask()
                .WaitAsync(TestWaits.DefaultTimeout).ConfigureAwait(false);

            Assert.Equal(0, read);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // 连接被中止（RST）；与干净 EOF 一样表示“连接已结束”。
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await Stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 测试清理路径：释放失败不应掩盖原始断言结果。
        }

        try
        {
            _tcp.Dispose();
        }
        catch (Exception)
        {
            // 测试清理路径：释放失败不应掩盖原始断言结果。
        }
    }
}
