## Context

`src/Galosys.Foundation.Primitives` 目前只有 4 个类型：`IChannelServer`（8 行）、`ChannelServerBase`（113 行、自研 accept + 收/发循环）、`ChannelContext`（可变数据袋 + 死代码 `AbstractChannelHandler`）、`ChannelClientBase`（`internal` 占位）。唯一消费方是 `src/NetServer.ConsoleApp`。仓库无测试项目，验证手段只有编译。

本变更承接 `docs/superpowers/specs/2026-10-20-primitives-socket-transport-design.md` 的 P1 基架，只覆盖**服务端内核与契约**。设计文档已冻结：保留 4 个类型名与 `Microsoft.Extensions.Hosting.Net` 命名空间、复用 Kestrel public transport、Kestrel 类型不出现在 public 签名、连接级客户端语义（P4 再做）。

## Goals / Non-Goals

**Goals:**

- 用 Kestrel 公共 transport（`SocketTransportFactory` + `IConnectionListener`）替换自研 socket 内核，删除旧 accept/收/发循环。
- 冻结服务端最小公共契约：`ChannelContext` 只读视图、`IChannelServer` 监听器语义、`ChannelServerBase.Configure` 管线缝、`ChannelServerOptions`。
- 实现每连接独立 `IServiceScope`（`ChannelContext.Services`）、`ConnectionClosed` 恰好一次、优雅停机（停止 accept → 等在途 → 超时 abort）。
- 新建 `tests/Galosys.Foundation.Primitives.Tests`，用真实 loopback socket 锁定上述行为。
- `NetServer.ConsoleApp` 零业务改动（只把 `StartAsync` 换成 `RunAsync`），行为等价。

**Non-Goals:**

- 不做消息层（`IMessageFramer<T>` / `IChannelSession<T>`，P2）。
- 不做 TLS（P3）、客户端实现（P4）、NamedPipe、Diagnostics/metrics（P5）。
- 不改 `ChannelClientBase` 的占位状态。
- 不做 `AddChannelServer` DI 注册、`ChannelServerHostedService`、示例的完整迁移、NetServer 模块 README 与 `AGENTS.md` 更新（属于 `primitives-socket-p1-2`）；本变更只新增 `src/Galosys.Foundation.Primitives/README.md`。
- 不拆分 `Primitives.Kestrel` 独立包。

## Decisions

### D1 复用 Kestrel public transport，不自研、不复制源码

`SocketTransportFactory(IOptions<SocketTransportOptions>, ILoggerFactory)` 提供 `BindAsync(EndPoint, CancellationToken)` → `IConnectionListener`，`AcceptAsync` → `ConnectionContext`（`IDuplexPipe Transport`）。部分发送、`SocketError` 分类、背压、abort 语义由 Kestrel 承担。

替代方案：自研内核（~3000 行、重复踩坑）或直接暴露 `IConnectionListener` 作为公共契约（迫使使用方接受 Kestrel 命名）。均否决，理由同设计文档 D1/D3。

### D2 适配层 `KestrelChannelListener` 是 internal

新增 `Transports/KestrelChannelListener.cs`（internal），职责只有三件：

1. 把 `ChannelServerOptions` 映射为 `SocketTransportOptions`（`Backlog`、`NoDelay`、`MaxReadBufferSize`、`MaxWriteBufferSize`、`IOQueueCount`、`CreateBoundListenSocket`）。
2. 组合 `CreateBoundListenSocket`：先调用用户委托（若有），再应用 `KeepAlive`/`LingerState`/`ReuseAddress`/`DualMode`，最后交给 Kestrel `Listen`。
3. 暴露 `BindAsync` / `AcceptAsync` / `StopAsync` / `DisposeAsync`，返回 `ConnectionContext`。

public 签名中不出现任何 `Microsoft.AspNetCore.*` 类型。测试项目通过 `InternalsVisibleTo` 访问该适配层。

### D3 `ChannelContext` 包装 `ConnectionContext`，构造为 internal

```csharp
public sealed class ChannelContext : IAsyncDisposable
{
    internal ChannelContext(
        string connectionId,
        ConnectionContext connection,
        IServiceProvider services,
        CancellationToken connectionClosed);
    public string ConnectionId { get; }
    public EndPoint? LocalEndPoint { get; }
    public EndPoint? RemoteEndPoint { get; }
    public IDuplexPipe Transport { get; }
    public PipeReader Reader => Transport.Input;
    public PipeWriter Writer => Transport.Output;
    public CancellationToken ConnectionClosed { get; }
    public IServiceProvider Services { get; }
    public IDictionary<object, object?> Items { get; }
    public void Abort(Exception? reason = null);
    public ValueTask DisposeAsync();
}
```

- `ConnectionClosed` 由框架自建 `CancellationTokenSource` 提供，在连接收尾路径中 `Cancel()` 一次，保证 INV-3；不直接暴露 Kestrel 的 token，避免与 `Abort` 时序耦合。
- `Abort(reason)` 记录首个异常并调用底层 `ConnectionContext.Abort(new ConnectionAbortedException(...))`，随后 `Cancel()`。
- `DisposeAsync` 幂等，内部 `Abort` + 释放 `ConnectionContext`。
- `Items` 保留为连接内杂项字典；服务只能从 `Services` 取（INV-5）。
- 不新增 `IChannelContext` 接口：测试用真实 loopback，不需要 mock 层。

### D4 管线是委托链，不是 DI 中间件

```csharp
public delegate Task ChannelHandlerDelegate(ChannelContext context);
public delegate Task ChannelMiddleware(ChannelContext context, ChannelHandlerDelegate next);
public interface IChannelPipeline
{
    IChannelPipeline Use(ChannelMiddleware middleware);
    void Run(ChannelHandlerDelegate handler);
}
```

- `ChannelPipeline`（internal）在 `Use` 时累积中间件，`Run` 是唯一终结点；构建委托链时 `Use(A), Use(B), Run(H)` 得到 `A(B(H))`。
- `Run` 重复调用抛 `InvalidOperationException`；未调用 `Run` 时 `Configure` 结束即抛 `InvalidOperationException`（不静默丢弃连接）。
- 中间件可短路（不调用 `next`），此时终端处理器不执行；框架照常收尾。
- 每次连接复用同一个不可变委托链，不为每连接分配闭包。

### D5 每连接 scope 通过 internal 工厂注入

`ChannelServerBase` 增加 `internal IServiceScopeFactory? ScopeFactory { get; set; }`。连接建立时：

1. `ScopeFactory?.CreateScope()`，否则使用 `EmptyServiceProvider.Instance`（无 DI 的手动构造场景）。
2. 用 scope 的 `ServiceProvider` 构造 `ChannelContext`。
3. 管线执行完毕 → `ConnectionClosed.Cancel()` → `await scope.DisposeAsync()` → 释放连接。

`primitives-socket-p1-2` 的 `AddChannelServer` 负责把容器里的 `IServiceScopeFactory`/`ILoggerFactory` 赋给这两个内部属性；本变更的测试用假工厂验证“每连接一个 scope、释放顺序正确”。

### D6 停机与在途连接跟踪

- 活动连接登记在 `ConcurrentDictionary<string, ConnectionContext>`，同时保留处理任务引用。
- `StopAsync(ct)`：`listener.StopAsync()` 停止 accept → 等待 accept 循环 → `Task.WhenAny(WhenAll(inflight), timeout(ShutdownTimeout))` → 超时对剩余连接 `Abort` 并等待短宽限期 → `listener.DisposeAsync()`。
- `StopAsync`/`DisposeAsync` 用 `Interlocked` + 状态机保证幂等；重复调用直接返回。
- `StartAsync` 只做“校验 Options → Bind → 启动 accept 循环 → 存 `LocalEndPoint`”；bind 失败异常原样抛给调用方，状态回到 `NotStarted`。
- `RunAsync(ct)` = `StartAsync` + 等待 accept 循环结束；`OperationCanceledException` 视为正常停机，不外抛。

### D7 Options 与校验

`ChannelServerOptions` 字段：`EndPoint`、`Backlog`(512)、`MaxConnections`(0=不限)、`NoDelay`(true)、`KeepAlive`、`LingerState`、`ReuseAddress`、`DualMode`、`ShutdownTimeout`(10s)、`MaxReadBufferSize`、`MaxWriteBufferSize`、`IOQueueCount`(0=内核决定)、`CreateBoundListenSocket`。

校验在 `StartAsync` 前执行（`ValidateOptions` 风格，不依赖 `Microsoft.Extensions.Options`）：`EndPoint != null`、`Backlog > 0`、`ShutdownTimeout > 0`、`MaxConnections >= 0`、缓冲/队列值非负。失败抛 `ArgumentException`。

`IdleTimeout` 从 P1 移除：目前没有消费它的中间件，保留死配置会误导使用者；P5 的超时中间件再引入。

`MaxConnections` 用计数器实现：超过上限时立即 `Abort` 新连接（不排队、不让 accept 停顿），并记录日志。

### D8 兼容外观与消费方迁移

`ChannelServerBase` 保留 `protected ChannelServerBase(EndPoint)` 与 `protected abstract Task ChannelHandleAsync(ChannelContext)`，现有子类（`SampleChannelServer`）零改动。旧的自研循环、`FillAsync`、`DrainAsync`、`AbstractChannelHandler` 全部删除，不做 `[Obsolete]` 过渡（仓库内只有 1 个消费方）。

`ListenerBootstrapper` 的 `await srv.StartAsync(stoppingToken)` 改为 `await srv.RunAsync(stoppingToken)`，保持“阻塞到停机”的行为；完整的 `AddChannelServer`/`HostedService` 迁移放 p1-2。

### D9 测试策略

新建 `tests/Galosys.Foundation.Primitives.Tests`（xunit 2.9.3 + `Microsoft.NET.Test.Sdk` 17.14.1 + `xunit.runner.visualstudio` 3.1.4，与 `dotnet new xunit` 模板一致）：

- 单元：Options 校验、管线顺序/重复 `Run`/短路。
- 集成：真实 loopback，覆盖 `StartAsync` 立即返回、端口占用、大消息（1 MiB，触发多次 socket 写）、处理器异常不影响其他连接、`ConnectionClosed` 恰好一次、`StopAsync` 等待在途、超时强制 abort、每连接 scope 独立与释放顺序。
- 每个测试使用 `127.0.0.1:0` 动态端口，避免端口冲突；等待使用 `TaskCompletionSource` + 超时（`WaitAsync`），不使用真实 `Task.Delay` 轮询。

## Risks / Trade-offs

- **Kestrel 共享框架依赖**：`<FrameworkReference Include="Microsoft.AspNetCore.App" />` 会进入所有引用方（当前只有 `NetServer.ConsoleApp`）→ 影响面 1 个项目，后续出现非 ASP.NET 宿主再拆包。
- **`StartAsync` 语义变更破坏外部调用方** → 仓库内仅 1 个消费方，同步改为 `RunAsync`；变更写入 proposal。
- **`ConnectionClosed` 由自建 CTS 提供，可能与底层 abort 时序不同** → 只在收尾路径 `Cancel` 一次，测试锁定“恰好一次且先于 scope 释放”。
- **Kestrel 监听器的 `StopAsync` 行为随版本变化** → 版本随 .NET 10 共享框架；停机超时与 abort 由我们自己兜底。
- **loopback 测试在 CI/本机的时序抖动** → 禁用真实 sleep，统一 `WaitAsync(TimeSpan)` 超时；端口用 `:0`。
- **`MaxConnections` 直接 abort 新连接可能让客户端看到连接重置** → 这是刻意语义（不排队），文档写明。
- **无日志时异常不可观察** → 保留 internal `ILoggerFactory` 注入点，p1-2 接入宿主日志；无注入时用 `NullLoggerFactory`。

## Migration Plan

1. 新增测试项目与基础测试（先红）。
2. 引入 `FrameworkReference` 与 internal `KestrelChannelListener`，替换 `ChannelServerBase` 内核，删除旧循环与 `AbstractChannelHandler`。
3. 改造 `ChannelContext` / `IChannelServer` / 新增 Options 与管线。
4. 修改 `NetServer.ConsoleApp/ListenerBootstrapper.cs` 为 `RunAsync`。
5. 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet build src/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`。

回滚：本变更未提交 git 历史（仓库无 git），回滚即恢复 4 个源文件并删除测试项目；无数据/持久化影响。

## Open Questions

无。设计文档 §12 的 Q1–Q8 已给出结论，本变更按 Q1（`StartAsync` 返回即监听）、Q3（保留继承 + `Configure`）、Q4（删除死代码）、Q7（不做 `[Obsolete]` 过渡）执行。
