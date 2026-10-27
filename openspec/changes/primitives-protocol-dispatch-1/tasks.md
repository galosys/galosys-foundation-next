# P2.5a-1 协议分发骨架 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。

**Goal:** 新增 `IChannelProtocol`、`IChannelProtocolSelector` 和 `ProtocolDispatcher`，让同一连接可以按选择器结果进入不同协议，并锁定选择顺序、取消和 `InvalidDataException` 中止语义。

**Architecture:** 选择器只窥探 `ChannelContext.Reader`，返回可空协议；分发器作为 `ChannelHandlerDelegate` 直接挂到 `ChannelServer`，调用选择器一次，再用 `context.ConnectionClosed` 调用协议。无匹配或选择器异常通过 `context.Abort(InvalidDataException)` 终止连接；协议异常原样传播。本 change 不实现前缀选择器、ALPN 或 TLS。

**Tech Stack:** .NET 10、`System.IO.Pipelines`、xunit 2.9.3、Microsoft.Extensions.Hosting Abstractions。

---

## 1. 测试先行：分发器调用顺序

- [ ] 1.1 写 `ProtocolDispatcherTests` 与 fake connection/selector/protocol

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/ProtocolDispatcherTests.cs`

**Step 1: 写入测试与 fake 类型**

```csharp
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ProtocolDispatcherTests
{
    [Fact]
    public async Task HandleAsync_CallsSelectorOnce_ThenProtocol()
    {
        var protocol = new RecordingProtocol();
        var selector = new RecordingSelector(protocol);
        var connection = new FakeChannelConnection();
        await using var context = new ChannelContext(
            "dispatcher-test",
            connection,
            EmptyServiceProvider.Instance);

        var dispatcher = new ProtocolDispatcher(selector);
        await dispatcher.HandleAsync(context);

        Assert.Equal(1, selector.Calls);
        Assert.Equal(1, protocol.Calls);
        Assert.Equal(
            new[] { "select", "handle" },
            selector.Events.Concat(protocol.Events));
        Assert.Equal(context.ConnectionClosed, protocol.Token);
    }
}

internal sealed class FakeChannelConnection : IChannelConnection
{
    private readonly Pipe _pipe = new();
    private readonly TaskCompletionSource<Exception?> _termination =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EndPoint? LocalEndPoint => new IPEndPoint(IPAddress.Loopback, 0);

    public EndPoint? RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 0);

    public IDuplexPipe Transport => new DuplexPipe(_pipe.Reader, _pipe.Writer);

    public Task<Exception?> Termination => _termination.Task;

    public PipeWriter Writer => _pipe.Writer;

    public void Abort(Exception? reason)
    {
        _termination.TrySetResult(reason);
        _pipe.Writer.Complete();
    }

    public ValueTask DisposeAsync()
    {
        _termination.TrySetResult(null);
        _pipe.Writer.Complete();
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingProtocol : IChannelProtocol
{
    public string Name => "recording";

    public int Calls { get; private set; }

    public CancellationToken Token { get; private set; }

    public List<string> Events { get; } = new();

    public ValueTask HandleAsync(ChannelContext context, CancellationToken cancellationToken)
    {
        Calls++;
        Token = cancellationToken;
        Events.Add("handle");
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingSelector : IChannelProtocolSelector
{
    private readonly IChannelProtocol? _protocol;
    private readonly Exception? _error;

    public RecordingSelector(IChannelProtocol? protocol = null, Exception? error = null)
    {
        _protocol = protocol;
        _error = error;
    }

    public int Calls { get; private set; }

    public List<string> Events { get; } = new();

    public ValueTask<IChannelProtocol?> SelectAsync(
        ChannelContext context,
        CancellationToken cancellationToken)
    {
        Calls++;
        Events.Add("select");

        if (_error is not null)
            throw _error;

        return ValueTask.FromResult<IChannelProtocol?>(_protocol);
    }
}
```

**Step 2: 运行测试，确认编译失败**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~ProtocolDispatcherTests`
Expected: 编译失败，提示 `IChannelProtocol`、`IChannelProtocolSelector` 或 `ProtocolDispatcher` 不存在（这是本任务的“红”）。

## 2. 实现契约与分发器

- [ ] 2.1 创建 `IChannelProtocol`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Protocols/IChannelProtocol.cs`

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>同一端口上可被选择的一个协议。</summary>
    public interface IChannelProtocol
    {
        /// <summary>诊断名称，不参与匹配。</summary>
        string Name { get; }

        /// <summary>处理一个已经选择了本协议的连接。</summary>
        ValueTask HandleAsync(ChannelContext context, CancellationToken cancellationToken);
    }
}
```

- [ ] 2.2 创建 `IChannelProtocolSelector`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Protocols/IChannelProtocolSelector.cs`

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>只窥探连接并选择协议；不得消费输入。</summary>
    public interface IChannelProtocolSelector
    {
        /// <summary>返回匹配协议；返回 null 表示无匹配。</summary>
        ValueTask<IChannelProtocol?> SelectAsync(
            ChannelContext context,
            CancellationToken cancellationToken);
    }
}
```

- [ ] 2.3 创建 `ProtocolDispatcher` 的构造与选择调用

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Protocols/ProtocolDispatcher.cs`

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>把连接分发给选择器选中的协议。</summary>
    public sealed class ProtocolDispatcher
    {
        private readonly IChannelProtocolSelector _selector;

        public ProtocolDispatcher(IChannelProtocolSelector selector)
        {
            ArgumentNullException.ThrowIfNull(selector);
            _selector = selector;
        }

        /// <summary>匹配 <see cref="ChannelHandlerDelegate"/>，可直接传给 pipeline.Run。</summary>
        public async Task HandleAsync(ChannelContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var cancellationToken = context.ConnectionClosed;

            if (cancellationToken.IsCancellationRequested)
                return;

            var protocol = await _selector
                .SelectAsync(context, cancellationToken)
                .ConfigureAwait(false);

            if (protocol is null)
            {
                context.Abort(new InvalidDataException("没有匹配的协议。"));
                return;
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            await protocol.HandleAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }
}
```

- [ ] 2.4 加入选择器异常包装与中止分支

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Protocols/ProtocolDispatcher.cs`

把选择器调用包在 `try` / `catch` 中：

```csharp
IChannelProtocol? protocol;

try
{
    protocol = await _selector
        .SelectAsync(context, cancellationToken)
        .ConfigureAwait(false);
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    return;
}
catch (Exception error)
{
    context.Abort(error as InvalidDataException
        ?? new InvalidDataException("协议选择失败。", error));
    return;
}
```

保留后续的 null 判断、二次取消检查和协议调用。

- [ ] 2.5 构建并跑首个分发测试

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~ProtocolDispatcherTests`
Expected: `HandleAsync_CallsSelectorOnce_ThenProtocol` 通过。

## 3. 补齐失败、取消与不消费测试

- [ ] 3.1 增加无匹配与选择器异常测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ProtocolDispatcherTests.cs`

```csharp
[Fact]
public async Task HandleAsync_NoMatch_AbortsWithInvalidDataException()
{
    var selector = new RecordingSelector();
    var connection = new FakeChannelConnection();
    await using var context = new ChannelContext(
        "no-match",
        connection,
        EmptyServiceProvider.Instance);

    var dispatcher = new ProtocolDispatcher(selector);
    await dispatcher.HandleAsync(context);

    Assert.Equal(1, selector.Calls);
    Assert.IsType<InvalidDataException>(context.AbortReason);
}

[Fact]
public async Task HandleAsync_SelectorThrows_WrapsInnerException()
{
    var inner = new IOException("selector-boom");
    var selector = new RecordingSelector(error: inner);
    var connection = new FakeChannelConnection();
    await using var context = new ChannelContext(
        "selector-error",
        connection,
        EmptyServiceProvider.Instance);

    var dispatcher = new ProtocolDispatcher(selector);
    await dispatcher.HandleAsync(context);

    var error = Assert.IsType<InvalidDataException>(context.AbortReason);
    Assert.Same(inner, error.InnerException);
}

[Fact]
public async Task HandleAsync_SelectorThrowsInvalidData_DoesNotDoubleWrap()
{
    var original = new InvalidDataException("bad-prefix");
    var selector = new RecordingSelector(error: original);
    var connection = new FakeChannelConnection();
    await using var context = new ChannelContext(
        "selector-invalid-data",
        connection,
        EmptyServiceProvider.Instance);

    var dispatcher = new ProtocolDispatcher(selector);
    await dispatcher.HandleAsync(context);

    Assert.Same(original, context.AbortReason);
}
```

- [ ] 3.2 增加取消语义测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ProtocolDispatcherTests.cs`

```csharp
[Fact]
public async Task HandleAsync_ConnectionClosedBeforeDispatch_SkipsSelector()
{
    var selector = new RecordingSelector();
    var connection = new FakeChannelConnection();
    await using var context = new ChannelContext(
        "already-closed",
        connection,
        EmptyServiceProvider.Instance);

    context.Abort();

    var dispatcher = new ProtocolDispatcher(selector);
    await dispatcher.HandleAsync(context);

    Assert.Equal(0, selector.Calls);
    Assert.Null(context.AbortReason);
}

[Fact]
public async Task HandleAsync_SelectorObservesCancellation_DoesNotAbort()
{
    var selector = new AbortingSelector();
    var connection = new FakeChannelConnection();
    await using var context = new ChannelContext(
        "cancel-during-select",
        connection,
        EmptyServiceProvider.Instance);

    var dispatcher = new ProtocolDispatcher(selector);
    await dispatcher.HandleAsync(context);

    Assert.Equal(1, selector.Calls);
    Assert.Null(context.AbortReason);
}
```

在文件底部加入：

```csharp
internal sealed class AbortingSelector : IChannelProtocolSelector
{
    public int Calls { get; private set; }

    public ValueTask<IChannelProtocol?> SelectAsync(
        ChannelContext context,
        CancellationToken cancellationToken)
    {
        Calls++;
        context.Abort();
        throw new OperationCanceledException(context.ConnectionClosed);
    }
}
```

- [ ] 3.3 增加未知协议不消费输入测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ProtocolDispatcherTests.cs`

```csharp
[Fact]
public async Task HandleAsync_UnknownProtocol_LeavesInputUnconsumed()
{
    var connection = new FakeChannelConnection();
    await connection.Writer.WriteAsync(Encoding.UTF8.GetBytes("XYZ"));
    await connection.Writer.FlushAsync();

    await using var context = new ChannelContext(
        "unknown-protocol",
        connection,
        EmptyServiceProvider.Instance);

    var dispatcher = new ProtocolDispatcher(new PeekThenNullSelector());
    await dispatcher.HandleAsync(context);

    Assert.IsType<InvalidDataException>(context.AbortReason);

    var result = await context.Reader.ReadAsync();
    Assert.Equal("XYZ", Encoding.UTF8.GetString(result.Buffer.ToArray()));
    context.Reader.AdvanceTo(result.Buffer.End);
}

private sealed class PeekThenNullSelector : IChannelProtocolSelector
{
    public async ValueTask<IChannelProtocol?> SelectAsync(
        ChannelContext context,
        CancellationToken cancellationToken)
    {
        var result = await context.Reader.ReadAsync(cancellationToken);
        context.Reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        return null;
    }
}
```

`PeekThenNullSelector` 必须是 `private sealed class` 且放在 `ProtocolDispatcherTests` 内部；如果放在文件底部，改为 `internal sealed class`。

- [ ] 3.4 跑分发测试全集

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~ProtocolDispatcherTests`
Expected: 全部 `ProtocolDispatcherTests` 通过。

## 4. README 与最终验证

- [ ] 4.1 更新 Primitives README 的协议分发章节

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

在“宿主接入”之后增加：

    ## 协议分发

    ```csharp
    var dispatcher = new ProtocolDispatcher(selector);

    var server = new ChannelServer(
        new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Any, 8080) },
        pipeline => pipeline.Run(dispatcher.HandleAsync));
    ```

    `ProtocolDispatcher.HandleAsync` 与 `ChannelHandlerDelegate` 签名一致，可以直接作为终端处理器。
    选择器只调用一次，必须只窥探 `context.Reader`（读取后 `AdvanceTo(buffer.Start, buffer.End)`），不得消费字节。
    选择器返回 null 或抛异常时，分发器用 `InvalidDataException` 中止连接；连接关闭导致的取消不会产生协议错误。
    具体的前缀选择器和 ALPN 在后续 change 提供。

- [ ] 4.2 构建并跑全量测试

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 现有测试与新增 `ProtocolDispatcherTests` 全部通过。

- [ ] 4.3 校验 OpenSpec artifact

Run: `openspec validate "primitives-protocol-dispatch-1" --strict --no-interactive`
Expected: `Change 'primitives-protocol-dispatch-1' is valid`。
