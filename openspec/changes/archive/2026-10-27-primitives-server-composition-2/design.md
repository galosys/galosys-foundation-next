## Context

`primitives-server-composition-1` 已把生命周期抽到 `ChannelServerCore`，新增 sealed `ChannelServer`，并让 `ChannelServerBase` 委托同一个 core。此时基类只是兼容外观，`AddChannelServer<T>` 仍把全局唯一的 `ChannelServerOptions` 注册进容器；调用两次会串端口，且无法表达“同一个 host 多个 server”。

本 change 完成 P1.5a 的收尾：删除基类和泛型 DI 入口，sample/tests 全部改用 `ChannelServer`。多 server 生命周期（`ChannelServerGroup`）和 IoC 注册仍留给后续 change。

## Goals / Non-Goals

**Goals:**

- 删除 `ChannelServerBase`；`ChannelServer` 成为唯一公开服务器实现。
- 删除 `AddChannelServer<T>` / `ChannelServerHostedService<T>`，消除全局 options 注册 bug。
- 迁移 `NetServer.ConsoleApp`：`ChannelServer` + 示例内 `IHostedService` runner，保持现有行为。
- 迁移全部测试辅助类型、hosting 测试和 namespace/dependency 测试。
- 更新 Primitives 与 sample README，使文档只描述组合式入口。

**Non-Goals:**

- 不恢复 IoC 注册；不做 `AddChannelServer(definition)`。
- 不做 `ChannelServerGroup`、多 server 统一启停。
- 不做同端口多协议、TLS、消息层、客户端。
- 不改变 `ChannelServerCore` 的生命周期语义；本 change 只删外壳和迁移消费方。

## Decisions

### 1. 直接删除 `ChannelServerBase`，不保留 `[Obsolete]` 外观

`ChannelServerBase` 只有 sample/tests 使用，仓库内没有外部消费方。保留 obsolete 基类会继续暴露 `Configure`/`ChannelHandleAsync` 继承模型，也会让 README 和 intellisense 出现两套入口。P1.5a 的目标是组合优先，因此直接删除，breaking change 在 proposal 中标注。

`ChannelServerCore` 保持 internal，不成为新的“基类替代品”；调用方只看到 `ChannelServer` 和 `IChannelServer`。

### 2. 删除泛型 DI 入口，P1.5a 不做 IoC

`AddChannelServer<T>` 的注册模型无法支持同类型多实例，而且全局 `ChannelServerOptions` 会让多 server 串配置。本 change 不设计替代注册 API，只删除：

```text
ChannelServerServiceCollectionExtensions.cs
Hosting/ChannelServerHostedService.cs
```

宿主仍需 DI 时，由调用方显式注册 `ChannelServer` 工厂和一个小型 `IHostedService` runner。这个 runner 是 sample/test 的装配代码，不是框架 API；等 P1.5b 设计好多 server/IoC 后再考虑库内注册。

### 3. Sample runner 用 `IHostedService`，不用 `BackgroundService`

`ChannelServer.RunAsync` 会阻塞到停机；如果 runner 继承 `BackgroundService`，`ExecuteAsync` 启动后立即返回，`IHost.StartAsync` 可能在绑定完成前返回，`SenderBootstrapper` 会先连接而失败。runner 必须让宿主启动等待绑定完成：

```csharp
public sealed class ChannelServerRunner : IHostedService
{
    private readonly ChannelServer _server;

    public ChannelServerRunner(ChannelServer server) => _server = server;

    public Task StartAsync(CancellationToken ct) => _server.StartAsync(ct);

    public Task StopAsync(CancellationToken ct) => _server.StopAsync(CancellationToken.None);
}
```

- `StartAsync` 绑定成功才返回，端口占用会让宿主启动失败。
- `StopAsync` 传 `CancellationToken.None`，避免宿主超时把服务器留在半停机状态；服务器自己遵守 `ShutdownTimeout`。
- `ChannelServer` 注册为 singleton，宿主释放容器时由 DI 调用 `DisposeAsync`；runner 不负责释放。
- 注册顺序：runner 在 `SenderBootstrapper` 之前，保证 sender 连接时监听已就绪。

### 4. Sample 用 `SampleChannelHandler` 替代 `SampleChannelServer`

`SampleChannelHandler` 是普通类，持有所需的 `ILogger<SampleChannelHandler>`：

```csharp
public sealed class SampleChannelHandler
{
    private readonly ILogger<SampleChannelHandler> _logger;

    public SampleChannelHandler(ILogger<SampleChannelHandler> logger) => _logger = logger;

    public async Task HandleAsync(ChannelContext context)
    {
        // 保留原 SampleChannelServer.ChannelHandleAsync 的读取/UTF-8/打印逻辑
    }
}
```

`Program.cs` 注册：

```csharp
builder.Services.AddSingleton<SampleChannelHandler>();
builder.Services.AddSingleton(sp =>
{
    var options = new ChannelServerOptions
    {
        EndPoint = new IPEndPoint(IPAddress.Any, 8000),
    };
    var handler = sp.GetRequiredService<SampleChannelHandler>();

    return new ChannelServer(
        options,
        pipeline => pipeline.Run(handler.HandleAsync),
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<IServiceScopeFactory>());
});
builder.Services.AddHostedService<ChannelServerRunner>();
builder.Services.AddHostedService<SenderBootstrapper>();
```

- `ChannelServerOptions` 不再注册到容器；每个 `ChannelServer` 工厂自己创建 options。
- `IServiceScopeFactory` 显式传入，保持每连接 scope 能力。
- 监听地址、日志文本、`hello` 频率、Ctrl+C 行为不变。

### 5. 测试辅助类型保留名称，内部改为组合包装

`TestSupport` 的 `TestChannelServer` / `ConfiguredTestServer` 保留类名和构造签名，但不再继承基类，而是内部持有 `ChannelServer`：

```csharp
internal sealed class TestChannelServer : IChannelServer
{
    private readonly ChannelServer _server;

    public TestChannelServer(
        ChannelServerOptions options,
        Func<ChannelContext, Task> handler,
        IServiceScopeFactory? scopeFactory = null)
    {
        _server = new ChannelServer(
            options,
            pipeline => pipeline.Run(handler),
            loggerFactory: null,
            scopeFactory: scopeFactory);
    }

    // IChannelServer 成员全部转发到 _server
}
```

这样 `OptionsValidationTests`、`ServerLifecycleTests`、`PipelineIntegrationTests`、`ShutdownTests` 的代码基本不用改。`ConfiguredTestServer` 保留 `(options, configure, handler)` 构造签名，忽略 `handler`，由 `configure` 自己调用 `Run`；`TestServerHandle.Server` 改为 `IChannelServer`，`WaitUntilListeningAsync` 接收 `IChannelServer`。

`HostingTests` 用测试本地 runner 注册 `ChannelServer` 工厂；`ScopedHostingTests` 通过工厂传入 `IServiceScopeFactory`；`ShutdownTests` 的 `ChannelServerBase?` 改为 `IChannelServer?`；`DependencyBoundaryTests` / `NamespaceConventionTests` 的 `typeof(ChannelServerBase)` 改为 `typeof(ChannelServer)`。

### 6. 规格同步删除基类/DI 入口

- `channel-server-transport`：新增“唯一公开服务器实现”要求，删除 `ChannelServerBase`；连接 scope 改为“传入 `IServiceScopeFactory`”。
- `channel-hosting`：删除“服务注册”；宿主生命周期改为调用方 runner；每连接 scope 改为传入工厂；示例场景改为 `ChannelServer`。
- `channel-pipeline`：pipeline 入口从 `ChannelServerBase.Configure` 改为 `ChannelServer` 构造回调。
- `foundation-namespace-conventions`：删除 `AddChannelServer` 专属场景；保留 Options 与其他 DI 辅助类型的约定。

## Risks / Trade-offs

- **破坏性 API 删除** → 仓库内消费方只有 sample/tests；一次性迁移，不保留 obsolete 外观。
- **宿主启动时序** → runner 用 `IHostedService`，`StartAsync` 绑定后才返回；注册顺序在 sender 之前。
- **DI 释放顺序** → `ChannelServer` 作为 singleton 由容器释放；runner 只映射生命周期，不额外持有所有权。
- **测试辅助类型大改** → 先改 `TestSupport`，让大部分测试只改类型名；再单独迁移 `HostingTests`/`ScopedHostingTests`。
- **规格与实现不一致** → 本 change 同步更新四个 capability；归档前跑 `openspec validate`。
- **IoC 空窗期** → 明确 P1.5a 不提供库内注册；sample 用手写工厂，P1.5b 再设计。

## Migration Plan

1. 先把 `TestSupport.cs` 的 `TestChannelServer` / `ConfiguredTestServer` 改为 `ChannelServer` 包装，更新 `TestServerHandle` 与 `WaitUntilListeningAsync` 的接口类型。
2. 迁移 `HostingTests`、`ScopedHostingTests`、`ShutdownTests`、`DependencyBoundaryTests`、`NamespaceConventionTests`；确认 `OptionsValidationTests`、`ServerLifecycleTests`、`PipelineIntegrationTests` 通过辅助包装无需改动。
3. 删除 `ChannelServerBase.cs`、`ChannelServerServiceCollectionExtensions.cs`、`Hosting/ChannelServerHostedService.cs`。
4. 新增 sample `ChannelServerRunner.cs`，把 `SampleChannelServer.cs` 改为 `SampleChannelHandler.cs`，更新 `Program.cs`。
5. 更新 Primitives README 与 sample README。
6. 更新四个 spec delta，运行 `openspec validate`。
7. 构建 Primitives、构建 NetServer、跑全量测试；运行 sample 验证 8000 端口、hello、Ctrl+C。

回滚策略：本 change 依赖 change 1；若迁移失败，回滚到 change 1 的提交，保留 `ChannelServer` + 基类兼容层，待修复后再重做删除。

## Open Questions

无。IoC、`ChannelServerGroup`、多服务器和同端口多协议明确不在本 change。
