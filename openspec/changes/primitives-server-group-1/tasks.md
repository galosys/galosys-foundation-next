# P1.5b-1 多服务器组合生命周期 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。

**Goal:** 新增 public sealed `ChannelServerGroup`，为多个 `IChannelServer` 提供顺序启动、失败回滚、并行停机、异常聚合和幂等释放。

**Architecture:** 组只持有 `IChannelServer` 列表并编排生命周期，不复制 server 状态、不引入组级 options。启动串行并记录本次已启动项，失败时逆序停止；停机并行并收集每个 server 的异常；释放用 `Interlocked` + `TaskCompletionSource` 保证幂等。本 change 不接 DI、不接宿主、不迁移 sample。

**Tech Stack:** .NET 10、`System.Net.Sockets`、xunit 2.9.3、Microsoft.Extensions.Hosting/Options Abstractions。

---

## 1. 测试先行：双服务器绑定

- [ ] 1.1 写 `ChannelServerGroup` 双服务器绑定测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/ChannelServerGroupTests.cs`

**Step 1: 写入最小测试**

```csharp
using System.Net;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ChannelServerGroupTests
{
    [Fact]
    public async Task StartAsync_StartsTwoServers_AndBindsIndependentEndPoints()
    {
        var first = CreateServer();
        var second = CreateServer();
        await using var group = new ChannelServerGroup(first, second);

        await group.StartAsync(CancellationToken.None);

        try
        {
            Assert.Equal(2, group.Servers.Count);
            Assert.NotNull(first.LocalEndPoint);
            Assert.NotNull(second.LocalEndPoint);
            Assert.NotEqual(
                ((IPEndPoint)first.LocalEndPoint!).Port,
                ((IPEndPoint)second.LocalEndPoint!).Port);
        }
        finally
        {
            await group.StopAsync(CancellationToken.None);
        }
    }

    private static ChannelServer CreateServer()
        => new(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(2),
            },
            pipeline => pipeline.Run(TestHandlers.EchoAsync));
}
```

**Step 2: 运行测试，确认编译失败**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~ChannelServerGroupTests`
Expected: 编译失败，提示 `ChannelServerGroup` 不存在（这是本任务的“红”）。

## 2. 实现组类型

- [ ] 2.1 创建 `ChannelServerGroup` 的构造与 `Servers`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerGroup.cs`

```csharp
using System.Collections.ObjectModel;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>按组合方式管理多个服务器的生命周期。</summary>
    public sealed class ChannelServerGroup : IAsyncDisposable, IDisposable
    {
        private readonly IChannelServer[] _servers;

        /// <summary>创建服务器组；空数组合法。</summary>
        public ChannelServerGroup(params IChannelServer[] servers)
        {
            ArgumentNullException.ThrowIfNull(servers);

            foreach (var server in servers)
                ArgumentNullException.ThrowIfNull(server);

            _servers = (IChannelServer[])servers.Clone();
            Servers = new ReadOnlyCollection<IChannelServer>(_servers);
        }

        /// <summary>构造时传入的服务器，顺序即启动顺序。</summary>
        public IReadOnlyList<IChannelServer> Servers { get; }
    }
}
```

- [ ] 2.2 实现 `StartAsync` 的顺序启动与回滚

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerGroup.cs`

在类中加入：

```csharp
public async Task StartAsync(CancellationToken ct)
{
    var started = new List<IChannelServer>(_servers.Length);

    try
    {
        foreach (var server in _servers)
        {
            ct.ThrowIfCancellationRequested();
            await server.StartAsync(ct).ConfigureAwait(false);
            started.Add(server);
        }
    }
    catch (Exception original)
    {
        var rollbackFailures = new List<Exception>();

        for (var i = started.Count - 1; i >= 0; i--)
        {
            try
            {
                await started[i].StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                rollbackFailures.Add(rollbackError);
            }
        }

        if (rollbackFailures.Count > 0)
            original.Data["ChannelServerGroup.RollbackFailures"] = new AggregateException(rollbackFailures);

        throw;
    }
}
```

- [ ] 2.3 实现 `StopAsync` 的并行停机与 `AggregateException`

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerGroup.cs`

```csharp
public async Task StopAsync(CancellationToken ct)
{
    var results = await Task.WhenAll(
        _servers.Select(server => StopOneAsync(server, ct))).ConfigureAwait(false);

    var failures = results
        .Where(error => error is not null)
        .Cast<Exception>()
        .ToArray();

    if (failures.Length > 0)
        throw new AggregateException("一个或多个服务器停止失败。", failures);
}

private static async Task<Exception?> StopOneAsync(IChannelServer server, CancellationToken ct)
{
    try
    {
        await server.StopAsync(ct).ConfigureAwait(false);
        return null;
    }
    catch (Exception error)
    {
        return error;
    }
}
```

- [ ] 2.4 实现幂等 `DisposeAsync` / `Dispose`

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerGroup.cs`

```csharp
private readonly TaskCompletionSource _disposeCompleted =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

private int _disposeStarted;

public async ValueTask DisposeAsync()
{
    if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
    {
        var failures = new List<Exception>();

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (AggregateException error)
        {
            failures.AddRange(error.InnerExceptions);
        }

        foreach (var server in _servers)
        {
            try
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }

        if (failures.Count > 0)
        {
            var aggregate = new AggregateException("一个或多个服务器释放失败。", failures);
            _disposeCompleted.TrySetException(aggregate);
            throw aggregate;
        }

        _disposeCompleted.TrySetResult();
    }

    await _disposeCompleted.Task.ConfigureAwait(false);
}

public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
```

- [ ] 2.5 构建并跑首个组测试

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~ChannelServerGroupTests`
Expected: `StartAsync_StartsTwoServers_AndBindsIndependentEndPoints` 通过。

## 3. 补齐生命周期测试

- [ ] 3.1 增加独立 options 与统一停机测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelServerGroupTests.cs`

```csharp
[Fact]
public async Task StartAsync_UsesIndependentOptions_AndStopAll_ClosesBoth()
{
    var first = new ChannelServer(
        new ChannelServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ShutdownTimeout = TimeSpan.FromSeconds(1),
            MaxConnections = 1,
        },
        pipeline => pipeline.Run(TestHandlers.EchoAsync));

    var second = new ChannelServer(
        new ChannelServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ShutdownTimeout = TimeSpan.FromSeconds(3),
            MaxConnections = 0,
        },
        pipeline => pipeline.Run(TestHandlers.EchoAsync));

    await using var group = new ChannelServerGroup(first, second);
    await group.StartAsync(CancellationToken.None);

    var firstEndPoint = (IPEndPoint)first.LocalEndPoint!;
    var secondEndPoint = (IPEndPoint)second.LocalEndPoint!;
    Assert.NotEqual(firstEndPoint.Port, secondEndPoint.Port);

    await group.StopAsync(CancellationToken.None);

    Assert.Null(first.LocalEndPoint);
    Assert.Null(second.LocalEndPoint);
    Assert.False(await TestClient.TryConnectAsync(firstEndPoint));
    Assert.False(await TestClient.TryConnectAsync(secondEndPoint));
}
```

- [ ] 3.2 增加第二个绑定失败的 rollback 测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelServerGroupTests.cs`

```csharp
[Fact]
public async Task StartAsync_SecondBindFails_StopsFirstAndRethrowsOriginal()
{
    using var blocker = new TcpListener(IPAddress.Loopback, 0);
    blocker.Start();
    var busyPort = ((IPEndPoint)blocker.LocalEndpoint).Port;

    var first = CreateServer();
    var second = new ChannelServer(
        new ChannelServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, busyPort),
        },
        pipeline => pipeline.Run(TestHandlers.EchoAsync));

    await using var group = new ChannelServerGroup(first, second);

    var error = await Assert.ThrowsAsync<SocketException>(
        () => group.StartAsync(CancellationToken.None));

    Assert.Null(first.LocalEndPoint);
    Assert.Null(second.LocalEndPoint);
    Assert.IsType<SocketException>(error);
}
```

需要在文件顶部补 `using System.Net.Sockets;`。

- [ ] 3.3 增加释放幂等、空组与取消测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelServerGroupTests.cs`

```csharp
[Fact]
public async Task DisposeAsync_IsIdempotent_AndStopsBothServers()
{
    var first = CreateServer();
    var second = CreateServer();
    var group = new ChannelServerGroup(first, second);

    await group.StartAsync(CancellationToken.None);
    await group.DisposeAsync();
    await group.DisposeAsync();
    group.Dispose();

    Assert.Null(first.LocalEndPoint);
    Assert.Null(second.LocalEndPoint);
}

[Fact]
public async Task EmptyGroup_AllOperationsAreNoOp()
{
    await using var group = new ChannelServerGroup();

    await group.StartAsync(CancellationToken.None);
    await group.StopAsync(CancellationToken.None);
    await group.DisposeAsync();

    Assert.Empty(group.Servers);
}

[Fact]
public async Task StartAsync_CanceledToken_ThrowsAndLeavesServersUnbound()
{
    var first = CreateServer();
    var second = CreateServer();
    await using var group = new ChannelServerGroup(first, second);
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => group.StartAsync(cts.Token));

    Assert.Null(first.LocalEndPoint);
    Assert.Null(second.LocalEndPoint);
}
```

- [ ] 3.4 跑组测试全集

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~ChannelServerGroupTests`
Expected: 全部 `ChannelServerGroupTests` 通过。

## 4. README 与验证

- [ ] 4.1 更新 Primitives README 的多服务器用法

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

在“最小用法”之后增加：

    ## 多服务器组合

    ```csharp
    var http = new ChannelServer(
        new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Any, 8080) },
        pipeline => pipeline.Run(HttpHandler));

    var mqtt = new ChannelServer(
        new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Any, 1883) },
        pipeline => pipeline.Run(MqttHandler));

    await using var group = new ChannelServerGroup(http, mqtt);
    await group.StartAsync(cancellationToken);

    // group.Servers[0].LocalEndPoint / group.Servers[1].LocalEndPoint

    await group.StopAsync(cancellationToken);
    ```

    组按构造顺序串行启动；某个 server 启动失败时，已启动的 server 会按相反顺序停止，并重新抛出原始异常。
    停机并行执行，所有失败汇总为 `AggregateException`；`DisposeAsync` 可重复调用。

- [ ] 4.2 构建并跑全量测试

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 现有测试与新增 `ChannelServerGroupTests` 全部通过。

- [ ] 4.3 校验 OpenSpec artifact

Run: `openspec validate "primitives-server-group-1" --strict --no-interactive`
Expected: `Change 'primitives-server-group-1' is valid`。
