# P3-1 TLS 配置与传输适配器 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。

**Goal:** 新增 `ChannelTlsOptions`、internal `SslStreamDuplexPipe` / `TlsDuplexPipe`，用自签名证书和 loopback socket 锁定握手、失败、超时、关闭与半关闭语义；本阶段不接管线。

**Architecture:** `SslStreamDuplexPipe` 把现有 `IDuplexPipe` 适配成 `SslStream` 需要的 `Stream`；`TlsDuplexPipe` 在 `SslStream` 上完成服务端/客户端握手，然后用读泵（解密输入）和写泵（加密输出）把字节搬到暴露的 `Pipe`。适配器不拥有底层传输，底层仍由 `ChannelServer` / `ChannelClient` 收尾。

**Tech Stack:** .NET 10、`System.Net.Security`、`System.IO.Pipelines`、`System.Security.Cryptography.X509Certificates`、xunit 2.9.3。

---

## 1. TLS 配置

- [ ] 1.1 写 `ChannelTlsOptions` 失败测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/ChannelTlsOptionsTests.cs`

**Step 1: 写入最小测试**

```csharp
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
}
```

**Step 2: 运行测试，确认编译失败**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelTlsOptionsTests"`
Expected: 编译失败，提示 `ChannelTlsOptions` 不存在（这是本任务的“红”）。

- [ ] 1.2 新增 `ChannelTlsOptions` 与校验

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/ChannelTlsOptions.cs`

```csharp
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
```

- [ ] 1.3 新增测试证书生成器

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/TlsTestCertificates.cs`

```csharp
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Galosys.Foundation.Primitives.Tests;

internal static class TlsTestCertificates
{
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

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1));
    }
}
```

- [ ] 1.4 补齐配置校验测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelTlsOptionsTests.cs`

追加以下测试：

```csharp
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
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelTlsOptionsTests"`
Expected: 全部 `ChannelTlsOptionsTests` 通过。

## 2. 双向管道与 `SslStream` 适配

- [ ] 2.1 实现 `SslStreamDuplexPipe`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/SslStreamDuplexPipe.cs`

```csharp
using System.IO.Pipelines;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>把 IDuplexPipe 适配成 SslStream 使用的只读/只写 Stream；不拥有底层管道。</summary>
    internal sealed class SslStreamDuplexPipe : Stream
    {
        private readonly IDuplexPipe _pipe;

        public SslStreamDuplexPipe(IDuplexPipe pipe)
        {
            _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        }

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => _pipe.Input.ReadAsync(buffer, cancellationToken);

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await _pipe.Output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await _pipe.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public override ValueTask FlushAsync(CancellationToken cancellationToken)
            => _pipe.Output.FlushAsync(cancellationToken);

        public override void Flush()
            => throw new NotSupportedException("请使用异步刷新。");

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException("请使用异步读取。");

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException("请使用异步写入。");

        protected override void Dispose(bool disposing)
        {
            // 底层管道由 ChannelServer / ChannelClient 释放。
        }
    }
}
```

- [ ] 2.2 实现 `TlsDuplexPipe` 握手与读泵

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/TlsDuplexPipe.cs`

```csharp
using System.IO.Pipelines;
using System.Net.Security;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>认证后的 TLS 双向管道；底层传输仍由框架释放。</summary>
    internal sealed class TlsDuplexPipe : IDuplexPipe, IAsyncDisposable
    {
        private readonly IDuplexPipe _inner;
        private readonly SslStream _ssl;
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly CancellationTokenSource _abort = new();
        private readonly Task _readPump;
        private readonly Task _writePump;
        private int _disposed;

        private TlsDuplexPipe(IDuplexPipe inner, SslStream ssl)
        {
            _inner = inner;
            _ssl = ssl;
            _readPump = ReadPumpAsync(_abort.Token);
            _writePump = WritePumpAsync(_abort.Token);
        }

        public PipeReader Input => _input.Reader;

        public PipeWriter Output => _output.Writer;

        public static async ValueTask<TlsDuplexPipe> CreateAsync(
            IDuplexPipe inner,
            ChannelTlsOptions options,
            bool isServer,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(options);

            if (isServer)
                options.ValidateServer();
            else
                options.ValidateClient();

            var ssl = new SslStream(new SslStreamDuplexPipe(inner), leaveInnerStreamOpen: false);

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCts.CancelAfter(options.HandshakeTimeout);

            try
            {
                if (isServer)
                {
                    await ssl.AuthenticateAsServerAsync(options.ServerOptions!, handshakeCts.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    await ssl.AuthenticateAsClientAsync(options.ClientOptions!, handshakeCts.Token)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return new TlsDuplexPipe(inner, ssl);
        }

        private async Task ReadPumpAsync(CancellationToken cancellationToken)
        {
            Exception? error = null;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var memory = _input.Writer.GetMemory(4096);
                    var read = await _ssl.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    _input.Writer.Advance(read);
                    var flush = await _input.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (flush.IsCompleted)
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                await _input.Writer.CompleteAsync(error).ConfigureAwait(false);
            }
        }

        private Task WritePumpAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public ValueTask DisposeAsync()
            => throw new NotImplementedException();
    }
}
```

- [ ] 2.3 实现写泵、`close_notify` 与释放

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/TlsDuplexPipe.cs`

把 `WritePumpAsync` 与 `DisposeAsync` 替换为：

```csharp
private async Task WritePumpAsync(CancellationToken cancellationToken)
{
    Exception? error = null;

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await _output.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            foreach (var segment in result.Buffer)
                await _ssl.WriteAsync(segment, cancellationToken).ConfigureAwait(false);

            _output.Reader.AdvanceTo(result.Buffer.End);
            await _ssl.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (result.IsCompleted || result.IsCanceled)
                break;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            await _ssl.ShutdownAsync().ConfigureAwait(false);
            _inner.Output.Complete();
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    catch (Exception ex)
    {
        error = ex;
        _inner.Output.Complete(ex);
    }
    finally
    {
        await _output.Reader.CompleteAsync(error).ConfigureAwait(false);
    }
}

public async ValueTask DisposeAsync()
{
    if (Interlocked.Exchange(ref _disposed, 1) != 0)
        return;

    try
    {
        await _output.Writer.CompleteAsync().ConfigureAwait(false);

        try
        {
            await _writePump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _abort.Cancel();
        }

        _abort.Cancel();
        await _ssl.DisposeAsync().ConfigureAwait(false);
        await _input.Writer.CompleteAsync().ConfigureAwait(false);
        await _output.Reader.CompleteAsync().ConfigureAwait(false);
        _input.Reader.Complete();
    }
    finally
    {
        _abort.Dispose();
    }
}
```

- [ ] 2.4 构建并跑配置/适配器测试

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelTlsOptionsTests"`
Expected: 全部通过。

## 3. 握手与关闭测试

- [ ] 3.1 增加服务端/客户端握手回显测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/TlsDuplexPipeTests.cs`

```csharp
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class TlsDuplexPipeTests
{
    [Fact]
    public async Task CreateAsync_ServerAndClient_HandshakeAndEcho()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();
        var serverOptions = CreateServerOptions(certificate);
        var clientOptions = CreateClientOptions(certificate);

        await using var pair = await TlsTestPair.ConnectAsync(serverOptions, clientOptions);

        await pair.Client.Output.WriteAsync("ping"u8.ToArray());
        await pair.Client.Output.FlushAsync();

        var payload = await ReadExactlyAsync(pair.Server.Input, 4);
        Assert.Equal("ping", Encoding.UTF8.GetString(payload));
    }

    private static ChannelTlsOptions CreateServerOptions(X509Certificate2 certificate)
        => new()
        {
            ServerOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
            },
        };

    private static ChannelTlsOptions CreateClientOptions(X509Certificate2 certificate)
        => new()
        {
            ClientOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = (_, remote, _, _) =>
                    remote is not null
                    && remote.GetCertHashString(HashAlgorithmName.SHA256)
                        == certificate.GetCertHashString(HashAlgorithmName.SHA256),
            },
        };

    private static async Task<byte[]> ReadExactlyAsync(PipeReader reader, int count)
    {
        var buffer = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var result = await reader.ReadAsync().AsTask().WaitAsync(TestWaits.DefaultTimeout);
            var slice = result.Buffer.Slice(0, Math.Min(result.Buffer.Length, count - offset));
            slice.CopyTo(buffer.AsSpan(offset));
            offset += (int)slice.Length;
            reader.AdvanceTo(slice.End);
        }

        return buffer;
    }
}
```

同时新增测试辅助 `TlsTestPair`（可放在 `TlsTestCertificates.cs` 末尾），用 `TcpListener`、`SocketChannelConnection` 和 `TlsDuplexPipe.CreateAsync` 建立真实 loopback 连接：

```csharp
internal sealed class TlsTestPair : IAsyncDisposable
{
    private TlsTestPair(TlsDuplexPipe server, TlsDuplexPipe client)
    {
        Server = server;
        Client = client;
    }

    public TlsDuplexPipe Server { get; }

    public TlsDuplexPipe Client { get; }

    public static async Task<TlsTestPair> ConnectAsync(
        ChannelTlsOptions serverOptions,
        ChannelTlsOptions clientOptions)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var acceptTask = listener.AcceptSocketAsync();
            using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await clientSocket.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            using var serverSocket = await acceptTask;

            var transportOptions = new ChannelServerOptions
            {
                EndPoint = (IPEndPoint)listener.LocalEndpoint,
            };
            await using var serverConnection = new SocketChannelConnection(serverSocket, transportOptions, logger: null);
            await using var clientConnection = new SocketChannelConnection(clientSocket, transportOptions, logger: null);

            var serverTask = TlsDuplexPipe.CreateAsync(
                serverConnection.Transport, serverOptions, isServer: true, CancellationToken.None).AsTask();
            var clientTask = TlsDuplexPipe.CreateAsync(
                clientConnection.Transport, clientOptions, isServer: false, CancellationToken.None).AsTask();

            await Task.WhenAll(serverTask, clientTask);
            return new TlsTestPair(await serverTask, await clientTask);
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
    }
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~TlsDuplexPipeTests"`
Expected: `CreateAsync_ServerAndClient_HandshakeAndEcho` 通过。

- [ ] 3.2 增加证书拒绝测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/TlsDuplexPipeTests.cs`

```csharp
[Fact]
public async Task CreateAsync_ClientRejectsCertificate_ThrowsAuthenticationException()
{
    using var certificate = TlsTestCertificates.CreateSelfSigned();
    var serverOptions = new ChannelTlsOptions
    {
        ServerOptions = new SslServerAuthenticationOptions { ServerCertificate = certificate },
    };
    var clientOptions = new ChannelTlsOptions
    {
        ClientOptions = new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            RemoteCertificateValidationCallback = (_, _, _, _) => false,
        },
    };

    await Assert.ThrowsAsync<AuthenticationException>(
        async () => await TlsTestPair.ConnectAsync(serverOptions, clientOptions));
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~CreateAsync_ClientRejectsCertificate"`
Expected: 测试通过；异常类型为 `AuthenticationException`。

- [ ] 3.3 增加明文连接拒绝测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/TlsDuplexPipeTests.cs`

```csharp
[Fact]
public async Task CreateAsync_ServerRejectsClearTextBytes()
{
    using var certificate = TlsTestCertificates.CreateSelfSigned();
    var serverOptions = new ChannelTlsOptions
    {
        ServerOptions = new SslServerAuthenticationOptions { ServerCertificate = certificate },
    };

    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var acceptTask = listener.AcceptTcpClientAsync();
    using var client = new TcpClient();
    await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
    using var accepted = await acceptTask;

    await client.GetStream().WriteAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray());

    var connection = new SocketChannelConnection(accepted.Client, new ChannelServerOptions(), logger: null);

    await Assert.ThrowsAnyAsync<Exception>(async () =>
        await TlsDuplexPipe.CreateAsync(
            connection.Transport, serverOptions, isServer: true, CancellationToken.None));
}
```

把 `Assert.ThrowsAnyAsync<Exception>` 替换为 `Assert.ThrowsAnyAsync<IOException>` 或 `AuthenticationException` 的具体断言，取决于本机 `SslStream` 的异常包装；测试必须至少断言异常不是“成功返回”。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~CreateAsync_ServerRejectsClearTextBytes"`
Expected: 测试通过；服务端不会返回可用 `TlsDuplexPipe`。

- [ ] 3.4 增加握手超时与外部取消测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/TlsDuplexPipeTests.cs`

```csharp
[Fact]
public async Task CreateAsync_HandshakeTimeout_ThrowsOperationCanceledException()
{
    using var certificate = TlsTestCertificates.CreateSelfSigned();
    var serverOptions = new ChannelTlsOptions
    {
        HandshakeTimeout = TimeSpan.FromMilliseconds(100),
        ServerOptions = new SslServerAuthenticationOptions { ServerCertificate = certificate },
    };

    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var acceptTask = listener.AcceptTcpClientAsync();
    using var client = new TcpClient();
    await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
    using var accepted = await acceptTask;
    var connection = new SocketChannelConnection(accepted.Client, new ChannelServerOptions(), logger: null);

    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        await TlsDuplexPipe.CreateAsync(
            connection.Transport, serverOptions, isServer: true, CancellationToken.None));
}

[Fact]
public async Task CreateAsync_ExternalCancellation_ThrowsOperationCanceledException()
{
    using var certificate = TlsTestCertificates.CreateSelfSigned();
    var serverOptions = new ChannelTlsOptions
    {
        ServerOptions = new SslServerAuthenticationOptions { ServerCertificate = certificate },
    };

    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var acceptTask = listener.AcceptTcpClientAsync();
    using var client = new TcpClient();
    await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
    using var accepted = await acceptTask;
    var connection = new SocketChannelConnection(accepted.Client, new ChannelServerOptions(), logger: null);
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        await TlsDuplexPipe.CreateAsync(
            connection.Transport, serverOptions, isServer: true, cts.Token));
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~CreateAsync_HandshakeTimeout|FullyQualifiedName~CreateAsync_ExternalCancellation"`
Expected: 两个测试通过。

- [ ] 3.5 增加优雅关闭、刷新与半关闭测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/TlsDuplexPipeTests.cs`

```csharp
[Fact]
public async Task DisposeAsync_DrainsCommittedOutput_AndPeerReadsEof()
{
    using var certificate = TlsTestCertificates.CreateSelfSigned();
    await using var pair = await TlsTestPair.ConnectAsync(
        CreateServerOptions(certificate),
        CreateClientOptions(certificate));

    var payload = new byte[1024 * 1024];
    Random.Shared.NextBytes(payload);

    await pair.Client.Output.WriteAsync(payload);
    await pair.Client.Output.FlushAsync();
    await pair.Client.Output.CompleteAsync();
    await pair.Client.DisposeAsync();

    var received = await ReadExactlyAsync(pair.Server.Input, payload.Length);
    Assert.Equal(payload, received);

    var eof = await pair.Server.Input.ReadAsync();
    Assert.True(eof.IsCompleted);
    pair.Server.Input.AdvanceTo(eof.Buffer.End);
}

[Fact]
public async Task ClientHalfClose_ServerCanStillRespond()
{
    using var certificate = TlsTestCertificates.CreateSelfSigned();
    await using var pair = await TlsTestPair.ConnectAsync(
        CreateServerOptions(certificate),
        CreateClientOptions(certificate));

    await pair.Client.Output.WriteAsync("bye"u8.ToArray());
    await pair.Client.Output.FlushAsync();
    await pair.Client.Output.CompleteAsync();

    var request = await ReadExactlyAsync(pair.Server.Input, 3);
    Assert.Equal("bye", Encoding.UTF8.GetString(request));

    await pair.Server.Output.WriteAsync("ok"u8.ToArray());
    await pair.Server.Output.FlushAsync();

    var response = await ReadExactlyAsync(pair.Client.Input, 2);
    Assert.Equal("ok", Encoding.UTF8.GetString(response));
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~DisposeAsync_DrainsCommittedOutput|FullyQualifiedName~ClientHalfClose_ServerCanStillRespond"`
Expected: 两个测试通过；1 MiB 数据顺序一致，客户端半关闭后仍能收到 `ok`。

## 4. 文档与验证

- [ ] 4.1 更新 Primitives README 的 TLS 章节

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

增加：

```markdown
## TLS 传输适配器（P3-1）

`ChannelTlsOptions`（`Microsoft.Extensions.Options`）复用 BCL 的
`SslServerAuthenticationOptions` / `SslClientAuthenticationOptions`，并提供 10 秒默认握手超时：

```csharp
var tls = new ChannelTlsOptions
{
    ServerOptions = new SslServerAuthenticationOptions
    {
        ServerCertificate = certificate,
    },
    HandshakeTimeout = TimeSpan.FromSeconds(10),
};
```

internal `TlsDuplexPipe.CreateAsync(inner, tls, isServer, ct)` 在现有 `IDuplexPipe` 上完成握手，
返回解密后的双向管道。适配器不拥有底层传输；关闭时先排空输出、发送 TLS `close_notify`，
再由 `ChannelServer` / `ChannelClient` 释放底层连接。

本阶段不提供 `UseTls`，也不做同端口 ALPN。管线接入见 `primitives-tls-transport-2`。
```

- [ ] 4.2 跑 Primitives 构建与全量测试

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 现有测试与新增 `ChannelTlsOptionsTests` / `TlsDuplexPipeTests` 全部通过。

- [ ] 4.3 校验 OpenSpec artifact

Run: `openspec validate "primitives-tls-transport-1" --strict --no-interactive`
Expected: `Change 'primitives-tls-transport-1' is valid`。
