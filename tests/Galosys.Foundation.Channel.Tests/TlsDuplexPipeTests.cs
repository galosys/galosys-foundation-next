using System;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Galosys.Foundation.Channel;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel.Tests;

/// <summary>
/// <see cref="TlsDuplexPipe"/> 的握手、失败、超时、关闭与半关闭语义测试。
/// </summary>
public sealed class TlsDuplexPipeTests
{
    [Fact]
    public async Task CreateAsync_ServerAndClient_HandshakeAndEcho()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();
        var serverOptions = CreateServerOptions(certificate);
        var clientOptions = CreateClientOptions(certificate);

        await using var pair = await TlsTestPair.ConnectAsync(serverOptions, clientOptions);

        // 客户端 -> 服务端。
        await pair.Client.Output.WriteAsync("ping"u8.ToArray());
        await pair.Client.Output.FlushAsync();

        var request = await ReadExactlyAsync(pair.Server.Input, 4);
        Assert.Equal("ping", Encoding.UTF8.GetString(request));

        // 服务端 -> 客户端。
        await pair.Server.Output.WriteAsync("pong"u8.ToArray());
        await pair.Server.Output.FlushAsync();

        var response = await ReadExactlyAsync(pair.Client.Input, 4);
        Assert.Equal("pong", Encoding.UTF8.GetString(response));
    }

    [Fact]
    public async Task CreateAsync_ClientRejectsCertificate_ThrowsAuthenticationException()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();
        var serverOptions = CreateServerOptions(certificate);
        var clientOptions = new ChannelTlsOptions
        {
            ClientOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                // 明确拒绝测试证书。
                RemoteCertificateValidationCallback = (_, _, _, _) => false,
            },
        };

        // 客户端拒绝证书时服务端也可能先收到客户端 alert 而失败，
        // 因此分别断言客户端任务，不使用 WhenAll 混判两端异常。
        var exception = await Assert.ThrowsAsync<AuthenticationException>(
            async () => await TlsTestPair.ConnectAsync(serverOptions, clientOptions));

        Assert.NotNull(exception);
    }

    [Fact]
    public async Task CreateAsync_ServerRejectsClearTextBytes()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();
        var serverOptions = CreateServerOptions(certificate);

        await using var scenario = await TlsTestPair.OpenRawAsync(serverOptions);

        // 客户端连上 TLS 端口后直接发送明文 HTTP 请求字节。
        await scenario.RawClient.WriteAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray());

        // 服务端握手必须失败；异常类型随平台不同（AuthenticationException / IOException）。
        var actual = await Record.ExceptionAsync(async () => await scenario.WaitForServerAsync());
        Assert.NotNull(actual);
        Assert.True(
            actual is AuthenticationException or IOException,
            $"明文连接的服务端异常类型为 {actual.GetType().FullName}，不符合 spec 预期。");

        // 失败后服务端不得暴露可用的 TLS 管道。
        Assert.False(scenario.ServerSucceeded);
    }

    [Fact]
    public async Task CreateAsync_HandshakeTimeout_ThrowsOperationCanceledException()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();
        var serverOptions = new ChannelTlsOptions
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(100),
            ServerOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
            },
        };

        await using var scenario = await TlsTestPair.OpenRawAsync(serverOptions);

        // 客户端连接后不发送任何握手字节，等待服务端握手超时。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await scenario.WaitForServerAsync());

        Assert.False(scenario.ServerSucceeded);
    }

    [Fact]
    public async Task CreateAsync_ExternalCancellation_ThrowsOperationCanceledException()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();
        var serverOptions = CreateServerOptions(certificate);

        await using var scenario = await TlsTestPair.OpenRawAsync(serverOptions);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // 取消握手可能表现为 TaskCanceledException（OperationCanceledException 的子类），
        // 因此这里断言“属于 OperationCanceledException 家族”，而不是精确类型。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await scenario.WaitForServerAsync(cts.Token));

        Assert.False(scenario.ServerSucceeded);
    }

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

        // 释放客户端：适配器应排空已提交字节并发送 close_notify。
        await pair.Client.DisposeAsync();

        var received = await ReadExactlyAsync(pair.Server.Input, payload.Length);
        Assert.Equal(payload, received);

        // 服务端随后应读到干净 EOF。
        var eof = await pair.Server.Input.ReadAsync().AsTask().WaitAsync(TestWaits.DefaultTimeout);
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

        // 半关闭：客户端完成输出（写方向结束），写泵发送 close_notify。
        await pair.Client.Output.CompleteAsync();

        var request = await ReadExactlyAsync(pair.Server.Input, 3);
        Assert.Equal("bye", Encoding.UTF8.GetString(request));

        // 服务端仍可写回响应。
        await pair.Server.Output.WriteAsync("ok"u8.ToArray());
        await pair.Server.Output.FlushAsync();

        var response = await pair.Client.Input.ReadAsync().AsTask().WaitAsync(TestWaits.DefaultTimeout);
        Assert.Equal("ok", Encoding.UTF8.GetString(response.Buffer));

        // 客户端在读到响应之后才观察到输入结束。
        Assert.False(response.IsCompleted);
        pair.Client.Input.AdvanceTo(response.Buffer.End);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotCompleteUnderlyingInput()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();

        await using var pair = await TlsTestPair.ConnectAsync(
            CreateServerOptions(certificate),
            CreateClientOptions(certificate));

        var underlying = pair.ClientConnection;
        Assert.NotNull(underlying);

        // 释放 TLS 适配器：适配器只释放自己的 SslStream 与内部管道。
        await pair.Client.DisposeAsync();

        // 底层输入未被完成（spec 禁止适配器调用底层 Input.Complete），
        // 因此读取不会立刻结束——说明底层传输没有被适配器释放。
        var readTask = underlying.Transport.Input.ReadAsync().AsTask();
        var finished = await Task.WhenAny(readTask, Task.Delay(200));

        Assert.NotSame(readTask, finished);

        // 底层连接随测试收尾而中止，此时挂起的读取会以异常结束；
        // 这里只需要观察任务，避免留下未观测异常。
        await underlying.Transport.Input.CompleteAsync();
        await ObserveAsync(readTask);
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TestWaits.DefaultTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 测试收尾路径：底层连接中止导致的读取异常无需断言。
        }
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();

        var pair = await TlsTestPair.ConnectAsync(
            CreateServerOptions(certificate),
            CreateClientOptions(certificate));

        // 第一次释放。
        await pair.Client.DisposeAsync();

        // 第二次释放必须正常完成，且不重复释放底层传输。
        await pair.Client.DisposeAsync();

        await pair.DisposeAsync();
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
    {
        var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);

        return new ChannelTlsOptions
        {
            ClientOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                // 按 SHA-256 指纹匹配测试证书，不使用“全接受”的危险回调。
                RemoteCertificateValidationCallback = (_, remote, _, _) =>
                    remote is not null
                    && remote.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint,
            },
        };
    }

    private static async Task<byte[]> ReadExactlyAsync(PipeReader reader, int count)
    {
        var buffer = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var result = await reader.ReadAsync().AsTask().WaitAsync(TestWaits.DefaultTimeout);
            var slice = result.Buffer.Slice(0, Math.Min(result.Buffer.Length, count - offset));

            // ReadOnlySequence 没有 CopyTo(Span) 重载，逐段拷贝。
            foreach (var segment in slice)
            {
                segment.CopyTo(buffer.AsMemory(offset));
                offset += segment.Length;
            }

            // 只消费实际拷贝的字节。
            reader.AdvanceTo(slice.End);
        }

        return buffer;
    }
}
