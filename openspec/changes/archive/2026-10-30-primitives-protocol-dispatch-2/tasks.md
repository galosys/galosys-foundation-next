# P2.5a-2 前缀协议选择器 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。

**Goal:** 新增 `PrefixProtocolRule`、`PrefixProtocolSelectorOptions` 和 `PrefixProtocolSelector`，实现同端口 first-match 前缀路由，并保证半包等待、EOF/无匹配、`MaxPrefixSize` 和只窥探不消费。

**Architecture:** 规则按注册顺序保存；选择器用 `PipeReader.ReadAsync` 窥探。返回结果时 `AdvanceTo(buffer.Start, buffer.Start)`，等待半包时 `AdvanceTo(buffer.Start, buffer.End)`。完整匹配立即返回；当前字节是某条规则的严格前缀时等待更多数据；EOF、无匹配或达到 `MaxPrefixSize` 返回 null，由 `ProtocolDispatcher` 中止连接。选择器本身不依赖 P2 session/codec。

**Tech Stack:** .NET 10、`System.IO.Pipelines`、xunit 2.9.3、Microsoft.Extensions.Options Abstractions。

---

## 1. 测试先行：first-match 与半包

- [x] 1.1 写 `PrefixProtocolSelectorTests` 的 first-match 与半包测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/PrefixProtocolSelectorTests.cs`

**Step 1: 写入最小测试**

```csharp
using System.Text;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class PrefixProtocolSelectorTests
{
    [Fact]
    public async Task SelectAsync_FirstMatchWins_ByRegistrationOrder()
    {
        var first = new RecordingProtocol();
        var second = new RecordingProtocol();
        var selector = new PrefixProtocolSelector(new[]
        {
            new PrefixProtocolRule("A"u8.ToArray(), first),
            new PrefixProtocolRule("AB"u8.ToArray(), second),
        });

        var connection = new FakeChannelConnection();
        await WriteAsync(connection, "AB");
        await using var context = new ChannelContext(
            "prefix-first-match",
            connection,
            EmptyServiceProvider.Instance);

        var result = await selector.SelectAsync(context, context.ConnectionClosed);

        Assert.Same(first, result);
    }

    [Fact]
    public async Task SelectAsync_PartialPrefix_WaitsForMoreData()
    {
        var protocol = new RecordingProtocol();
        var selector = new PrefixProtocolSelector(new[]
        {
            new PrefixProtocolRule("ABCD"u8.ToArray(), protocol),
        });

        var connection = new FakeChannelConnection();
        await WriteAsync(connection, "AB");
        await using var context = new ChannelContext(
            "prefix-partial",
            connection,
            EmptyServiceProvider.Instance);

        var selection = selector.SelectAsync(context, context.ConnectionClosed).AsTask();
        await Task.Delay(100);
        Assert.False(selection.IsCompleted);

        await WriteAsync(connection, "CD");

        var result = await selection.WaitAsync(TestWaits.DefaultTimeout);
        Assert.Same(protocol, result);
    }

    private static async Task WriteAsync(FakeChannelConnection connection, string text)
    {
        await connection.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));
        await connection.Writer.FlushAsync();
    }
}
```

**Step 2: 运行测试，确认编译失败**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~PrefixProtocolSelectorTests`
Expected: 编译失败，提示 `PrefixProtocolSelector` 或 `PrefixProtocolRule` 不存在（这是本任务的“红”）。

## 2. 实现规则、选项与选择器

- [x] 2.1 创建 `PrefixProtocolRule`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Protocols/PrefixProtocolRule.cs`

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>把固定字节前缀绑定到一个协议。</summary>
    public sealed class PrefixProtocolRule
    {
        public PrefixProtocolRule(ReadOnlyMemory<byte> prefix, IChannelProtocol protocol)
        {
            if (prefix.IsEmpty)
                throw new ArgumentException("前缀不能为空。", nameof(prefix));

            ArgumentNullException.ThrowIfNull(protocol);

            Prefix = prefix.ToArray();
            Protocol = protocol;
        }

        /// <summary>从连接起始位置匹配的前缀。</summary>
        public ReadOnlyMemory<byte> Prefix { get; }

        /// <summary>匹配时使用的协议。</summary>
        public IChannelProtocol Protocol { get; }
    }
}
```

- [x] 2.2 创建 `PrefixProtocolSelectorOptions`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/PrefixProtocolSelectorOptions.cs`

```csharp
namespace Microsoft.Extensions.Options
{
    /// <summary>前缀选择器的扫描上限。</summary>
    public sealed class PrefixProtocolSelectorOptions
    {
        /// <summary>单条前缀和未知协议扫描的最大字节数，默认 64。</summary>
        public int MaxPrefixSize { get; set; } = 64;
    }
}
```

- [x] 2.3 创建选择器构造与规则校验

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Protocols/PrefixProtocolSelector.cs`

```csharp
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>按注册顺序做 first-match 的字节前缀协议选择器。</summary>
    public sealed class PrefixProtocolSelector : IChannelProtocolSelector
    {
        private readonly PrefixProtocolRule[] _rules;
        private readonly int _maxPrefixSize;

        public PrefixProtocolSelector(IEnumerable<PrefixProtocolRule> rules)
            : this(rules, new PrefixProtocolSelectorOptions())
        {
        }

        public PrefixProtocolSelector(
            IEnumerable<PrefixProtocolRule> rules,
            PrefixProtocolSelectorOptions options)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(options);

            if (options.MaxPrefixSize <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "MaxPrefixSize 必须大于 0。");

            _rules = rules.ToArray();
            _maxPrefixSize = options.MaxPrefixSize;

            foreach (var rule in _rules)
                ArgumentNullException.ThrowIfNull(rule);

            for (var i = 0; i < _rules.Length; i++)
            {
                if (_rules[i].Prefix.Length > _maxPrefixSize)
                    throw new ArgumentException(
                        "规则前缀超过 MaxPrefixSize。",
                        nameof(rules));

                for (var j = i + 1; j < _rules.Length; j++)
                {
                    if (_rules[i].Prefix.Span.SequenceEqual(_rules[j].Prefix.Span))
                        throw new ArgumentException(
                            "前缀不能重复。",
                            nameof(rules));
                }
            }
        }
    }
}
```

- [x] 2.4 实现 `SelectAsync` 的窥探、first-match、半包与上限

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Protocols/PrefixProtocolSelector.cs`

```csharp
public async ValueTask<IChannelProtocol?> SelectAsync(
    ChannelContext context,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(context);

    if (_rules.Length == 0)
        return null;

    var reader = context.Reader;

    while (true)
    {
        var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var buffer = result.Buffer;
        var isCompleted = result.IsCompleted;
        var length = buffer.Length;
        var needsMore = false;
        IChannelProtocol? selected = null;

        try
        {
            foreach (var rule in _rules)
            {
                if (StartsWith(buffer, rule.Prefix.Span))
                {
                    selected = rule.Protocol;
                    break;
                }

                if (IsPartialPrefix(buffer, rule.Prefix.Span))
                {
                    needsMore = true;
                    break;
                }
            }
        }
        finally
        {
            // 返回结果时 examined 留在 Start，后续协议可以立即读到同一批字节；
            // 等待半包时推进到 End，让下一次 ReadAsync 等待新数据。
            if (selected is null && needsMore)
                reader.AdvanceTo(buffer.Start, buffer.End);
            else
                reader.AdvanceTo(buffer.Start, buffer.Start);
        }

        if (selected is not null)
            return selected;

        if (isCompleted || length >= _maxPrefixSize || !needsMore)
            return null;
    }
}

private static bool StartsWith(
    in ReadOnlySequence<byte> buffer,
    ReadOnlySpan<byte> prefix)
{
    if (buffer.Length < prefix.Length)
        return false;

    var head = buffer.Slice(0, prefix.Length).ToArray();
    return head.AsSpan().SequenceEqual(prefix);
}

private static bool IsPartialPrefix(
    in ReadOnlySequence<byte> buffer,
    ReadOnlySpan<byte> prefix)
{
    if (buffer.Length >= prefix.Length)
        return false;

    var head = buffer.ToArray();
    return prefix[..head.Length].SequenceEqual(head);
}
```

需要在文件顶部补 `using System.Buffers;`。

- [x] 2.5 构建并跑首个前缀测试

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~PrefixProtocolSelectorTests`
Expected: `SelectAsync_FirstMatchWins_ByRegistrationOrder` 与 `SelectAsync_PartialPrefix_WaitsForMoreData` 通过。

## 3. 补齐边界与构造测试

- [x] 3.1 增加重叠规则、EOF 与未知前缀测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/PrefixProtocolSelectorTests.cs`

```csharp
[Fact]
public async Task SelectAsync_LongerRuleFirst_SelectsLongerProtocol()
{
    var longer = new RecordingProtocol();
    var shorter = new RecordingProtocol();
    var selector = new PrefixProtocolSelector(new[]
    {
        new PrefixProtocolRule("AB"u8.ToArray(), longer),
        new PrefixProtocolRule("A"u8.ToArray(), shorter),
    });

    var connection = new FakeChannelConnection();
    await WriteAsync(connection, "AB");
    await using var context = new ChannelContext(
        "prefix-overlap",
        connection,
        EmptyServiceProvider.Instance);

    var result = await selector.SelectAsync(context, context.ConnectionClosed);

    Assert.Same(longer, result);
}

[Fact]
public async Task SelectAsync_UnknownPrefix_ReturnsNull()
{
    var protocol = new RecordingProtocol();
    var selector = new PrefixProtocolSelector(new[]
    {
        new PrefixProtocolRule("AB"u8.ToArray(), protocol),
    });

    var connection = new FakeChannelConnection();
    await WriteAsync(connection, "ZZ");
    await using var context = new ChannelContext(
        "prefix-unknown",
        connection,
        EmptyServiceProvider.Instance);

    var result = await selector.SelectAsync(context, context.ConnectionClosed);

    Assert.Null(result);
}

[Fact]
public async Task SelectAsync_PartialPrefixAtEof_ReturnsNull()
{
    var protocol = new RecordingProtocol();
    var selector = new PrefixProtocolSelector(new[]
    {
        new PrefixProtocolRule("ABCD"u8.ToArray(), protocol),
    });

    var connection = new FakeChannelConnection();
    await WriteAsync(connection, "AB");
    connection.Writer.Complete();
    await using var context = new ChannelContext(
        "prefix-eof",
        connection,
        EmptyServiceProvider.Instance);

    var result = await selector.SelectAsync(context, context.ConnectionClosed);

    Assert.Null(result);
}
```

- [x] 3.2 增加取消、不消费与超限测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/PrefixProtocolSelectorTests.cs`

```csharp
[Fact]
public async Task SelectAsync_Cancellation_DoesNotConsumeInput()
{
    var protocol = new RecordingProtocol();
    var selector = new PrefixProtocolSelector(new[]
    {
        new PrefixProtocolRule("AB"u8.ToArray(), protocol),
    });

    var connection = new FakeChannelConnection();
    await using var context = new ChannelContext(
        "prefix-cancel",
        connection,
        EmptyServiceProvider.Instance);

    using var cts = new CancellationTokenSource();
    var selection = selector.SelectAsync(context, cts.Token).AsTask();
    await Task.Delay(50);
    cts.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);

    await WriteAsync(connection, "AB");
    var read = await context.Reader.ReadAsync();
    Assert.Equal("AB", Encoding.UTF8.GetString(read.Buffer.ToArray()));
    context.Reader.AdvanceTo(read.Buffer.End);
}

[Fact]
public async Task SelectAsync_AfterMatch_DoesNotConsumePrefix()
{
    var protocol = new RecordingProtocol();
    var selector = new PrefixProtocolSelector(new[]
    {
        new PrefixProtocolRule("AB"u8.ToArray(), protocol),
    });

    var connection = new FakeChannelConnection();
    await WriteAsync(connection, "ABpayload");
    await using var context = new ChannelContext(
        "prefix-peek",
        connection,
        EmptyServiceProvider.Instance);

    var result = await selector.SelectAsync(context, context.ConnectionClosed);
    Assert.Same(protocol, result);

    connection.Writer.Complete();
    var read = await context.Reader.ReadAsync();
    Assert.Equal("ABpayload", Encoding.UTF8.GetString(read.Buffer.ToArray()));
    context.Reader.AdvanceTo(read.Buffer.End);
}

[Fact]
public async Task SelectAsync_MaxPrefixSizeReached_ReturnsNull()
{
    var protocol = new RecordingProtocol();
    var selector = new PrefixProtocolSelector(
        new[] { new PrefixProtocolRule("ABCD"u8.ToArray(), protocol) },
        new PrefixProtocolSelectorOptions { MaxPrefixSize = 4 });

    var connection = new FakeChannelConnection();
    await WriteAsync(connection, "ABCE");
    await using var context = new ChannelContext(
        "prefix-max",
        connection,
        EmptyServiceProvider.Instance);

    var result = await selector.SelectAsync(context, context.ConnectionClosed);

    Assert.Null(result);
}
```

- [x] 3.3 增加构造校验测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/PrefixProtocolSelectorTests.cs`

```csharp
[Fact]
public void Constructor_RejectsInvalidRules()
{
    var protocol = new RecordingProtocol();

    Assert.Throws<ArgumentException>(
        () => new PrefixProtocolRule(ReadOnlyMemory<byte>.Empty, protocol));

    Assert.Throws<ArgumentException>(() => new PrefixProtocolSelector(new[]
    {
        new PrefixProtocolRule("ABCD"u8.ToArray(), protocol),
    }, new PrefixProtocolSelectorOptions { MaxPrefixSize = 3 }));

    Assert.Throws<ArgumentException>(() => new PrefixProtocolSelector(new[]
    {
        new PrefixProtocolRule("AB"u8.ToArray(), protocol),
        new PrefixProtocolRule("AB"u8.ToArray(), new RecordingProtocol()),
    }));

    Assert.Throws<ArgumentOutOfRangeException>(() => new PrefixProtocolSelector(
        Array.Empty<PrefixProtocolRule>(),
        new PrefixProtocolSelectorOptions { MaxPrefixSize = 0 }));
}
```

- [x] 3.4 跑前缀单元测试全集

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~PrefixProtocolSelectorTests`
Expected: 全部 `PrefixProtocolSelectorTests` 通过。

## 4. 同端口集成测试

- [x] 4.1 写两个前缀协议共用一个 `ChannelServer` 的集成测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/PrefixProtocolIntegrationTests.cs`

```csharp
using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class PrefixProtocolIntegrationTests
{
    [Fact]
    public async Task SamePort_TwoPrefixes_DispatchToDistinctHandlers()
    {
        var alpha = new PrefixEchoProtocol("alpha", new byte[] { 0x01 }, "alpha");
        var beta = new PrefixEchoProtocol("beta", new byte[] { 0x02 }, "beta");
        var selector = new PrefixProtocolSelector(new[]
        {
            new PrefixProtocolRule(alpha.Prefix, alpha),
            new PrefixProtocolRule(beta.Prefix, beta),
        });
        var dispatcher = new ProtocolDispatcher(selector);

        await using var server = new ChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(2),
            },
            pipeline => pipeline.Run(dispatcher.HandleAsync));

        await server.StartAsync(CancellationToken.None);

        try
        {
            var endPoint = (IPEndPoint)server.LocalEndPoint!;

            using (var client = await TestClient.ConnectAsync(endPoint))
            {
                var stream = client.GetStream();
                await stream.WriteAsync(new byte[] { 0x01, (byte)'x' });
                var response = await TestClient.ReadExactlyAsync(
                    stream,
                    alpha.Response.Length);

                Assert.Equal("alpha", Encoding.UTF8.GetString(response));
            }

            using (var client = await TestClient.ConnectAsync(endPoint))
            {
                var stream = client.GetStream();
                await stream.WriteAsync(new byte[] { 0x02, (byte)'y' });
                var response = await TestClient.ReadExactlyAsync(
                    stream,
                    beta.Response.Length);

                Assert.Equal("beta", Encoding.UTF8.GetString(response));
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private sealed class PrefixEchoProtocol : IChannelProtocol
    {
        private readonly string _response;

        public PrefixEchoProtocol(string name, byte[] prefix, string response)
        {
            Name = name;
            Prefix = prefix;
            _response = response;
        }

        public string Name { get; }

        public ReadOnlyMemory<byte> Prefix { get; }

        public string Response => _response;

        public async ValueTask HandleAsync(
            ChannelContext context,
            CancellationToken cancellationToken)
        {
            var read = await context.Reader.ReadAsync(cancellationToken);
            var buffer = read.Buffer;

            if (buffer.Length < Prefix.Length)
                return;

            context.Reader.AdvanceTo(buffer.GetPosition(Prefix.Length));

            await context.Writer.WriteAsync(
                Encoding.UTF8.GetBytes(_response),
                cancellationToken);
            await context.Writer.FlushAsync(cancellationToken);
        }
    }
}
```

- [x] 4.2 增加半包前缀与未知前缀集成测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/PrefixProtocolIntegrationTests.cs`

```csharp
[Fact]
public async Task SamePort_PartialPrefix_WaitsForRemainingBytes()
{
    var protocol = new PrefixEchoProtocol(
        "partial",
        new byte[] { 0x10, 0x20 },
        "partial");

    await using var server = StartServer(protocol);
    await server.StartAsync(CancellationToken.None);

    try
    {
        var endPoint = (IPEndPoint)server.LocalEndPoint!;
        using var client = await TestClient.ConnectAsync(endPoint);
        var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { 0x10 });
        await stream.FlushAsync();
        await Task.Delay(100);
        await stream.WriteAsync(new byte[] { 0x20, (byte)'x' });
        await stream.FlushAsync();

        var response = await TestClient.ReadExactlyAsync(
            stream,
            protocol.Response.Length);

        Assert.Equal("partial", Encoding.UTF8.GetString(response));
    }
    finally
    {
        await server.DisposeAsync();
    }
}

[Fact]
public async Task SamePort_UnknownPrefix_AbortsConnection_AndServerKeepsAccepting()
{
    var alpha = new PrefixEchoProtocol("alpha", new byte[] { 0x01 }, "alpha");

    await using var server = StartServer(alpha);
    await server.StartAsync(CancellationToken.None);

    try
    {
        var endPoint = (IPEndPoint)server.LocalEndPoint!;

        using (var unknown = await TestClient.ConnectAsync(endPoint))
        {
            var stream = unknown.GetStream();
            await stream.WriteAsync(new byte[] { 0xFF });
            await stream.FlushAsync();
            await TestClient.AssertConnectionClosedAsync(stream);
        }

        using (var valid = await TestClient.ConnectAsync(endPoint))
        {
            var stream = valid.GetStream();
            await stream.WriteAsync(new byte[] { 0x01, (byte)'x' });
            var response = await TestClient.ReadExactlyAsync(
                stream,
                alpha.Response.Length);

            Assert.Equal("alpha", Encoding.UTF8.GetString(response));
        }
    }
    finally
    {
        await server.DisposeAsync();
    }
}

private static ChannelServer StartServer(params PrefixEchoProtocol[] protocols)
{
    var rules = protocols
        .Select(protocol => new PrefixProtocolRule(protocol.Prefix, protocol))
        .ToArray();
    var dispatcher = new ProtocolDispatcher(new PrefixProtocolSelector(rules));

    return new ChannelServer(
        new ChannelServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        },
        pipeline => pipeline.Run(dispatcher.HandleAsync));
}
```

- [x] 4.3 跑同端口集成测试

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~PrefixProtocolIntegrationTests`
Expected: 全部 `PrefixProtocolIntegrationTests` 通过。

## 5. README 与最终验证

- [x] 5.1 更新 Primitives README 的同端口前缀路由章节

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

在“协议分发”之后增加：

    ## 同端口前缀路由

    ```csharp
    var selector = new PrefixProtocolSelector(new[]
    {
        new PrefixProtocolRule("HTTP/1."u8.ToArray(), httpProtocol),
        new PrefixProtocolRule(new byte[] { 0x10 }, mqttProtocol),
    });

    var server = new ChannelServer(
        new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Any, 8080) },
        pipeline => pipeline.Run(new ProtocolDispatcher(selector).HandleAsync));
    ```

    规则按注册顺序 first-match；重叠前缀时先注册者优先，需要更长前缀优先时把它放在前面。
    只收到半个前缀时选择器等待更多数据；EOF、无匹配或达到 `MaxPrefixSize` 时返回 null，由 `ProtocolDispatcher` 以 `InvalidDataException` 中止连接。
    选择器只窥探不消费，协议 handler 仍能读到包括前缀在内的完整字节流。
    `MaxPrefixSize` 默认 64，必须大于 0；规则前缀不能超过该值。

- [x] 5.2 构建并跑全量测试

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 现有测试与新增前缀测试全部通过。

- [x] 5.3 校验 OpenSpec artifact

Run: `openspec validate "primitives-protocol-dispatch-2" --strict --no-interactive`
Expected: `Change 'primitives-protocol-dispatch-2' is valid`。
