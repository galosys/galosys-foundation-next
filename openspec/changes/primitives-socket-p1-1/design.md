## Context

`src/Galosys.Foundation.Primitives` 原先只有 4 个类型：`IChannelServer`、`ChannelServerBase`（自研 accept + 收/发循环）、`ChannelContext`（可变数据袋 + 死代码 `AbstractChannelHandler`）、`ChannelClientBase`（`internal` 占位）。唯一消费方是 `src/NetServer.ConsoleApp`。

本变更承接 `docs/superpowers/specs/2026-10-20-primitives-socket-transport-design.md` 的 P1 基架，只覆盖**服务端内核与契约**。设计过程中曾计划复用 Kestrel public transport，但该方案会给核心库引入 `Microsoft.AspNetCore.App` 共享框架依赖，使非 Web 宿主也被迫携带 ASP.NET Core。最终实现改为基于 `System.Net.Sockets` + `System.IO.Pipelines` 的自包含传输，同时保留 `Microsoft.Extensions.Hosting.Net` 的公共 API 与 internal transport 缝隙。

## Goals / Non-Goals

**Goals:**

- 用自包含 socket transport 替换旧 accept/收/发循环，核心程序集不引用任何 `Microsoft.AspNetCore.*`。
- 冻结服务端最小公共契约：`ChannelContext` 连接视图、`IChannelServer` 监听器语义、`ChannelServerBase.Configure` 管线缝、`ChannelServerOptions`。
- 实现每连接独立 `IServiceScope`（配置工厂时；`ChannelContext.Services`）、`ConnectionClosed` 恰好一次、优雅停机（停止 accept → 等在途 → 超时 abort）。
- 覆盖部分发送、输出排空、未读入站数据、半关闭、连接重置、背压、IPv4/IPv6 与自定义监听 socket。
- 新建 `tests/Galosys.Foundation.Primitives.Tests`，用真实 loopback socket 锁定上述行为。
- `NetServer.ConsoleApp` 零业务改动（只把 `StartAsync` 换成 `RunAsync`），行为等价。

**Non-Goals:**

- 不做消息层（`IMessageFramer<T>` / `IChannelSession<T>`，P2）。
- 不做 TLS（P3）、客户端实现（P4）、NamedPipe、Diagnostics/metrics（P5）。
- 不改 `ChannelClientBase` 的占位状态。
- 不做 `AddChannelServer` DI 注册、`ChannelServerHostedService`、示例的完整迁移、NetServer 模块 README 与 `AGENTS.md` 更新（属于 `primitives-socket-p1-2`）。
- 不新增 `Galosys.Foundation.Primitives.Kestrel` 适配包；如将来确有 Kestrel 需求，作为可选 transport 适配器另行评估，公共 API 不需要变化。

## Decisions

### D1 自包含 socket 内核，不依赖 ASP.NET Core

`SocketChannelListener` 负责创建/绑定/监听 socket（或调用 `CreateBoundListenSocket`）、应用监听器选项并接受连接；`SocketChannelConnection` 负责：

- 为每个连接创建输入/输出两个 `Pipe`，分别以 `MaxReadBufferSize` / `MaxWriteBufferSize` 作为暂停阈值，形成背压。
- 接收循环把 `NetworkStream` 数据写入输入 Pipe；发送循环从输出 Pipe 读取并用 `NetworkStream.WriteAsync` 完整写出，处理部分发送。
- 优雅关闭时完成输出 Pipe，等待发送循环排空（最长 5 秒）；随后停止接收循环并在 1 秒预算内读空内核接收缓冲区，避免带未读入站数据关闭触发 RST；最后关闭 socket。
- abort 路径取消循环、以异常完成 Pipe 并立即关闭 socket。
- 已接受连接应用 `NoDelay`、`LingerState`、TCP keep-alive；监听 socket 应用 `ReuseAddress`、IPv6 `DualMode`。

替代方案 Kestrel public transport 被否决：它会把 ASP.NET Core 共享框架带入所有引用方，违背核心库的宿主无关定位；直接暴露 `IConnectionListener` 也会污染公共契约。自包含实现的代价由传输测试承担。

### D2 传输契约保持 internal

新增 `Transports/IChannelListener.cs` 与 `Transports/IChannelConnection.cs`：

```csharp
internal interface IChannelListener : IAsyncDisposable
{
    EndPoint? LocalEndPoint { get; }
    ValueTask<IChannelConnection?> AcceptAsync(CancellationToken ct);
    ValueTask UnbindAsync(CancellationToken ct);
}

internal interface IChannelConnection : IAsyncDisposable
{
    EndPoint? LocalEndPoint { get; }
    EndPoint? RemoteEndPoint { get; }
    IDuplexPipe Transport { get; }
    Task<Exception?> Termination { get; }
    void Abort(Exception? reason);
}
```

公共签名中不出现传输实现类型（`IChannelListener`/`IChannelConnection`/`SocketChannelListener`/`SocketChannelConnection`）；`ChannelServerOptions` 有意使用 BCL 的 `EndPoint`、`Socket`、`LingerOption` 等配置类型。测试项目通过 `InternalsVisibleTo` 访问内部类型；另有依赖边界测试断言核心程序集的引用程序集列表中没有 `Microsoft.AspNetCore.*`。

### D3 `ChannelContext` 包装 `IChannelConnection`，构造为 internal

```csharp
public sealed class ChannelContext : IAsyncDisposable
{
    internal ChannelContext(string connectionId, IChannelConnection connection, IServiceProvider services);
    public string ConnectionId { get; }
    public EndPoint? LocalEndPoint { get; }
    public EndPoint? RemoteEndPoint { get; }
    public IDuplexPipe Transport { get; }
    public PipeReader Reader => Transport.Input;
    public PipeWriter Writer => Transport.Output;
    public CancellationToken ConnectionClosed { get; }
    public IServiceProvider Services { get; }
    public IDictionary<object, object?> Items { get; }
    public Exception? AbortReason { get; }
    public void UseTransport(IDuplexPipe transport);
    public void Abort(Exception? reason = null);
    public ValueTask DisposeAsync();
}
```

- `Transport` 默认指向底层连接的 `IDuplexPipe`；管线中间件可在调用 `next` 前通过 `UseTransport` 包装或替换（TLS、指标等场景）。替换后的传输由中间件负责释放，框架只完成当前传输并释放底层连接。
- `ConnectionClosed` 由框架自建 `CancellationTokenSource` 提供，在处理器返回、对端重置（RST）或 socket 故障、`Abort` 时 `Cancel()` 一次，保证 INV-3；不直接暴露底层 token，避免与 `Abort` 时序耦合。客户端正常半关闭（FIN）不触发该令牌，处理器通过 `Reader` 观察输入结束。
- `Abort(reason)` 记录首个异常、触发 `ConnectionClosed`，并把原始异常传给 `IChannelConnection.Abort`。连接实现用 `IOException` 包装后完成管道，但 `AbortReason` 始终保留原始异常。
- `DisposeAsync` 幂等，内部 `BeginCloseAsync` + 释放底层连接；正常关闭先完成当前传输的输出，再等待连接排空。
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

1. `ScopeFactory?.CreateScope()`，否则使用 `EmptyServiceScope.Instance`（无 DI 的手工构造场景）。
2. 用 scope 的 `ServiceProvider` 构造 `ChannelContext`。
3. 管线执行完毕 → `ConnectionClosed.Cancel()` → `await scope.DisposeAsync()` → 释放连接。

`primitives-socket-p1-2` 的 `AddChannelServer` 负责把容器里的 `IServiceScopeFactory`/`ILoggerFactory` 赋给这两个内部属性；本变更的测试用假工厂验证“每连接一个 scope、释放顺序正确”。未配置工厂时使用共享空作用域，仅用于测试或无容器宿主。

### D6 停机与在途连接跟踪

- 活动连接登记在 `ConcurrentDictionary<string, ChannelContext>`，同时保留处理任务引用。
- 接受循环只在监听器级错误时退出；单个连接的 scope/上下文初始化失败会中止并释放该连接，然后继续接受新连接。瞬时 accept 失败重试，连续失败 10 次才记录错误并退出。监听器级错误记录并在 `RunAsync` 中抛给宿主；后续 `StartAsync` 若发现接受循环已终止会抛 `InvalidOperationException`。
- `StopAsync(ct)`：`listener.UnbindAsync()` 停止 accept → 等待 accept 循环 → `Task.WhenAny(WhenAll(inflight), timeout(ShutdownTimeout))` → 超时对剩余连接 `Abort(TimeoutException)` 并等待短宽限期 → `listener.DisposeAsync()`。`ct` 只取消对最终停机完成的等待，不取消停机流程本身。
- `StopCoreAsync` 用 `try/finally` 保证即使取消回调或解绑抛异常，监听器仍会被释放，`LocalEndPoint` 最终置空。
- `StopAsync`/`DisposeAsync` 用 `Interlocked` + 状态机保证幂等；重复调用直接返回。
- `StartAsync` 只做“校验 Options → Bind/Listen → 启动 accept 循环 → 存 `LocalEndPoint`”；bind 失败异常原样抛给调用方，状态回到 `NotStarted`，允许重试。已停止或已释放的服务器再次 `StartAsync` 抛 `InvalidOperationException`，避免返回成功但无法 accept 的假状态。
- `RunAsync(ct)` = `StartAsync` + 等待 accept 循环结束；`OperationCanceledException` 视为正常停机，不外抛。

### D7 Options 与校验

`ChannelServerOptions` 字段：`EndPoint`、`Backlog`(512)、`MaxConnections`(0=不限)、`NoDelay`(true)、`KeepAlive`、`LingerState`、`ReuseAddress`、`DualMode`、`ShutdownTimeout`(10s)、`MaxReadBufferSize`(1 MiB)、`MaxWriteBufferSize`(64 KiB)、`CreateBoundListenSocket`。

- `MaxReadBufferSize` / `MaxWriteBufferSize` 是输入/输出 Pipe 的暂停阈值，必须大于 0；默认值不为 null/0，避免无限缓冲导致内存风险。
- `CreateBoundListenSocket` 由调用方负责创建、绑定和套接字选项；框架随后调用 `Listen`，失败时负责释放返回的 socket。返回 null 视为配置错误并抛出异常，不回退默认 socket。
- `NoDelay`、`LingerState`、`KeepAlive` 应用于已接受的 TCP 连接；`ReuseAddress`、`DualMode` 在监听 socket 绑定前应用。
- 校验在 `StartAsync` 前执行（`ValidateOptions` 风格，不依赖 `Microsoft.Extensions.Options`）：`EndPoint != null`、`Backlog > 0`、`ShutdownTimeout` 在 `(0, 1 天]`、`MaxConnections >= 0`、读写缓冲大于 0、启用 keep-alive 时各时间值至少 1 秒且重试次数非负。失败抛 `ArgumentException`。
- `IOQueueCount` 已删除：它是 Kestrel 的 IO 队列概念，自包含实现没有对应配置，保留会造成误导。
- `IdleTimeout` 从 P1 移除：目前没有消费它的中间件，保留死配置会误导使用者；P5 的超时中间件再引入。
- `MaxConnections` 用计数器实现：超过上限时立即 `Abort` 新连接（不排队、不让 accept 停顿），并记录日志。

### D8 兼容外观与消费方迁移

`ChannelServerBase` 保留 `protected ChannelServerBase(EndPoint)` 与 `protected abstract Task ChannelHandleAsync(ChannelContext)`，现有子类（`SampleChannelServer`）零改动。旧的自研循环、`FillAsync`、`DrainAsync`、`AbstractChannelHandler` 全部删除，不做 `[Obsolete]` 过渡（仓库内只有 1 个消费方）。

`ListenerBootstrapper` 的 `await srv.StartAsync(stoppingToken)` 改为 `await srv.RunAsync(stoppingToken)`，保持“阻塞到停机”的行为；完整的 `AddChannelServer`/`HostedService` 迁移放 p1-2。

### D9 测试策略

新建 `tests/Galosys.Foundation.Primitives.Tests`（xunit 2.9.3 + `Microsoft.NET.Test.Sdk` 17.14.1 + `xunit.runner.visualstudio` 3.1.4，与 `dotnet new xunit` 模板一致）：

- 单元：Options 校验、管线顺序/重复 `Run`/短路、依赖边界。
- 集成：真实 loopback，覆盖 `StartAsync` 立即返回、端口占用、大消息（1 MiB，触发多次 socket 写）、处理器异常不影响其他连接、`ConnectionClosed` 恰好一次、`StopAsync` 等待在途、超时强制 abort、每连接 scope 独立与释放顺序。
- 传输：优雅关闭排空已提交输出、带未读入站数据的优雅关闭（避免 RST）、客户端半关闭后仍可回显、RST 唤醒空闲处理器、`MaxConnections` 拒绝、极小 Pipe 阈值、abort。
- 停机：`ConnectionClosed` 回调重入 `StopAsync` 不死锁。
- 每个测试使用 `127.0.0.1:0` 动态端口，避免端口冲突；等待优先使用 `TaskCompletionSource` + `WaitAsync`，计数类断言使用带 5 秒截止时间的短轮询，不依赖固定 `sleep` 猜测时序。
- 当前测试总数为 60，连续多次运行稳定通过。

## Risks / Trade-offs

- **自维护 socket 传输出错**（部分发送、RST、背压、半关闭）→ 发送循环完整写、输入/输出 Pipe 背压、优雅关闭排空测试与半关闭测试锁定；后续若出现边缘平台问题，可在 internal 契约后替换实现。
- **引入 `Microsoft.AspNetCore.App` 的计划被撤销** → 核心程序集零 ASP.NET 依赖，由 `DependencyBoundaryTests` 防回归；代价是核心库自行维护少量 socket 代码。
- **`ChannelContext` 自建 `ConnectionClosed` 可能与底层 abort 时序不同** → 只在收尾路径、对端重置或 `Abort` 时 `Cancel` 一次，测试锁定“恰好一次且先于 scope 释放”；FIN 半关闭不触发。
- **优雅关闭等待发送排空有 5 秒上限，入站排空有 1 秒上限** → 超时走 abort；停机总时长仍由 `ShutdownTimeout` 兜底。
- **`StartAsync` 语义变更破坏外部调用方** → 仓库内仅 1 个消费方，同步改为 `RunAsync`；变更写入 proposal。
- **loopback 测试在 CI/本机的时序抖动** → 禁用真实 sleep，统一 `WaitAsync(TimeSpan)` 超时；端口用 `:0`。
- **`MaxConnections` 直接 abort 新连接可能让客户端看到连接重置** → 这是刻意语义（不排队），文档写明。
- **无日志时异常不可观察** → 保留 internal `ILoggerFactory` 注入点，p1-2 接入宿主日志；无注入时使用 `NullLoggerFactory`。

## Migration Plan

1. 新增测试项目与基础测试（先红）。
2. 新增 internal transport 契约与自包含 `SocketChannelListener` / `SocketChannelConnection`，删除 `KestrelChannelListener`。
3. 改造 `ChannelContext` / `IChannelServer` / `ChannelServerBase` / `ChannelServerOptions`，删除旧循环、`AbstractChannelHandler` 与 `IOQueueCount`。
4. 修改 `NetServer.ConsoleApp/ListenerBootstrapper.cs` 为 `RunAsync`。
5. 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet build src/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`，并检查 Primitives 的 csproj、`project.assets.json` 与程序集引用中不存在 `Microsoft.AspNetCore.*`。

回滚：恢复 4 个源文件、删除新增的 Transports/测试项目即可；本变更不涉及持久化数据。

## Open Questions

无。设计文档 §12 的 Q1–Q8 已给出结论，本变更按 Q1（`StartAsync` 返回即监听）、Q3（保留继承 + `Configure`）、Q4（删除死代码）、Q7（不做 `[Obsolete]` 过渡）、Q8（暂不做 Kestrel 适配包）执行。
