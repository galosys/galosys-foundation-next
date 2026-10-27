# Primitives P1.5：服务器入口组合化设计

> 一句话：**移除 `ChannelServerBase`，改为 sealed `ChannelServer` + internal `ChannelServerCore`；一个 host 可以创建多个 server，多端口/多协议用组合，不再用继承。**

## 路线图位置

| 阶段 | 状态 | 内容 | 验收标准 | 粗估 |
|---|---|---|---|---|
| **P1 服务端内核** | ✅ 已完成 | socket transport、Pipe、管线、Options、每连接 scope、优雅停机 | 60 个 loopback/单元测试通过 | — |
| **P1.5a 入口组合化** | **本设计** | 抽 `ChannelServerCore`；加 sealed `ChannelServer`；删除 `ChannelServerBase`；移除泛型 DI 入口 | 单 server 行为等价；旧测试迁移后全绿 | 2–3 天 |
| **P1.5b 多服务器** | 待做 | `ChannelServerGroup`；多 server 启停/失败回滚；多端口测试 | 一个 host 两个 server 同时监听、独立 options、统一停机 | 1–2 天 |
| **P2 消息层** | 待做 | `IMessageFramer` + `IMessageCodec<T>` + `IChannelSession<T>` | 半包/粘包/超限/EOF/取消测试通过 | 3–5 天 |
| **P2.5 协议分发** | 按需 | `IChannelProtocolSelector` + `ProtocolDispatcher`；同端口首字节分流，ALPN 预留 | 同端口两种协议可连接；TLS ALPN 在 P3 接入 | 2–4 天 |
| **P3 TLS** | 待做 | `ChannelTlsOptions` + `UseTls` | 握手/证书失败/关闭语义 | 2–3 天 |
| **P4 客户端** | 待做 | `IChannelClient` + 重连 + 连接池 + batch | 断线重连、退避、池耗尽 | 5–8 天 |
| **P5 收尾** | 待做 | NamedPipe、Diagnostics、示例与文档 | metrics/trace 可见 | 3–5 天 |

**建议顺序**：P1.5a → P1.5b → P2 → P2.5（按需）→ P3 → P4 → P5。

---

| 项 | 内容 |
|---|---|
| 状态 | 已定稿（待拆 OpenSpec 任务） |
| 日期 | 2026-10-22 |
| 范围 | `src/Galosys.Foundation.Primitives` 的服务器入口与多服务器装配 |
| 关联 | `docs/superpowers/specs/2026-10-20-primitives-socket-transport-design.md` §5.3；`docs/superpowers/specs/2026-10-22-primitives-message-frame-codec-design.md` |
| 结论 | 移除 `ChannelServerBase`；组合优先；builder 后置；IoC 暂不恢复 |

---

## 1. 背景与问题

P1 的 `ChannelServerBase` 把三件事压在继承里：

- accept loop、连接跟踪、优雅停机；
- `Configure(IChannelPipeline)` 管线配置；
- `ChannelHandleAsync(ChannelContext)` 业务处理。

单一 server 够用，但已经出现两个硬限制：

### 1.1 多服务器注册会串配置

`AddChannelServer<T>` 当前注册：

```csharp
services.AddSingleton(_ => options);          // 全局唯一
services.AddSingleton<T>();
services.AddSingleton<IChannelServer>(sp => sp.GetRequiredService<T>());
services.AddHostedService<ChannelServerHostedService<T>>();
```

调用两次：

```csharp
AddChannelServer<HttpServer>(o => o.EndPoint = new IPEndPoint(IPAddress.Any, 8080));
AddChannelServer<MqttServer>(o => o.EndPoint = new IPEndPoint(IPAddress.Any, 1883));
```

`ChannelServerOptions` 第二次注册覆盖第一次，两个服务器都拿到 1883，第二个 `StartAsync` 直接 `SocketException`。这是**注册层 bug**，不是内核问题。

### 1.2 继承不适合多实例/多协议

- 同一个 server 类跑两个端口，需要写两个子类；
- 每个协议一个子类，共享逻辑只能靠基类；
- `Configure` 是 `protected virtual`，测试和组合都不方便；
- 一个 host 跑多个 server 时，入口类型和生命周期都绑在继承上。

### 1.3 目标

- 移除 `ChannelServerBase`；
- `ChannelServer` 用组合接收 options + pipeline 配置；
- 一个 host 可以 `new` 多个 `ChannelServer`；
- 多服务器启停用 `ChannelServerGroup`；
- IoC 暂不恢复，后面再加 `AddChannelServer(definition)`；
- 同端口多协议不在本设计内，单独做 P2.5。

### 1.4 非目标

- 不恢复 `AddChannelServer<T>` / `ChannelServerHostedService<T>`；
- 不做 `ChannelServerBuilder`（见 §3.6）；
- 不做同端口协议探测；
- 不改 P2 的 framer/codec/session 契约；
- 不改 `IChannelServer` 的公共成员。

---

## 2. 决策

1. **组合优先**：`ChannelServer` 是 sealed，不再提供可继承的服务器基类。
2. **内核复用**：accept loop、连接跟踪、停机逻辑抽到 `ChannelServerCore`，`ChannelServer` 只做公开包装。
3. **pipeline 用委托**：`Action<IChannelPipeline>` 替代 `protected virtual Configure`。
4. **handler 用委托**：`ChannelHandlerDelegate` 替代 `protected abstract ChannelHandleAsync`。
5. **builder 后置**：`Action<IChannelPipeline>` 已经是 pipeline builder；单 server 不再包一层。
6. **IoC 暂不恢复**：先用构造函数；以后 `AddChannelServer(definition)` 也是创建同一个 `ChannelServer`。
7. **多服务器用 group**：`ChannelServerGroup` 负责统一 start/stop/dispose 和失败回滚。
8. **同端口多协议**：P2.5 的 `IChannelProtocolSelector` + `ProtocolDispatcher`，不塞进 P1.5。

---

## 3. 目标 API

### 3.1 `IChannelServer` 不变

```csharp
public interface IChannelServer : IAsyncDisposable, IDisposable
{
    EndPoint? LocalEndPoint { get; }
    Task StartAsync(CancellationToken ct);
    Task RunAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
```

### 3.2 `ChannelServer`（sealed）

```csharp
public sealed class ChannelServer : IChannelServer
{
    public ChannelServer(
        ChannelServerOptions options,
        Action<IChannelPipeline> configurePipeline,
        ILoggerFactory? loggerFactory = null,
        IServiceScopeFactory? scopeFactory = null);

    public EndPoint? LocalEndPoint { get; }
    public Task StartAsync(CancellationToken ct);
    public Task RunAsync(CancellationToken ct);
    public Task StopAsync(CancellationToken ct);
    public ValueTask DisposeAsync();
    public void Dispose();
}
```

- `options` 由 core 克隆，构造后外部修改不影响运行中的 server。
- `configurePipeline` 在 `StartAsync` 时调用一次；必须调用 `Run`，否则启动失败。
- `loggerFactory` / `scopeFactory` 可空：不接 IoC 时也可以运行；为空时没有日志、每连接 `Services` 为空 scope。
- 不再提供 `protected virtual Configure` / `protected abstract ChannelHandleAsync`。

### 3.3 `ChannelServerGroup`

```csharp
public sealed class ChannelServerGroup : IAsyncDisposable, IDisposable
{
    public ChannelServerGroup(params IChannelServer[] servers);

    public IReadOnlyList<IChannelServer> Servers { get; }

    public Task StartAsync(CancellationToken ct);
    public Task StopAsync(CancellationToken ct);
    public ValueTask DisposeAsync();
    public void Dispose();
}
```

语义：

- `StartAsync` 按顺序启动；某个失败时，停止已启动的 server，再抛异常（内层保留原始异常）。
- `StopAsync` 并行停止所有 server，聚合异常；每个 server 仍受自己的 `ShutdownTimeout` 约束。
- `DisposeAsync` 幂等：停止并释放所有 server。
- `Servers` 暴露给测试/诊断读取 `LocalEndPoint`。

### 3.4 `ChannelServerCore`（internal）

从 `ChannelServerBase` 原样搬过去：

- `_options`、`_activeConnections`、`_activeTasks`、`_startLock`、`_listener`、`_pipeline`、`_stopping`、`_acceptLoop`、`_connectionCount`、`_stopStarted`、`_disposed`；
- `StartAsync` / `RunAsync` / `StopAsync` / `DisposeAsync`；
- `BuildPipeline`：创建 `ChannelPipeline`，调用 `configurePipeline`，`Build()`；
- `AcceptLoopAsync`：接受连接、`MaxConnections` 限制、连续失败阈值；
- `ProcessConnectionAsync`：每连接 scope、`ChannelContext`、连接跟踪、收尾；
- `StopCoreAsync` / `WaitForInflightAsync`：优雅停机、超时中止；
- `ScopeFactory` / `LoggerFactory` / `LocalEndPoint`。

`ChannelServer` 只做委托，不再复制这段逻辑。

### 3.5 为什么 `Action<IChannelPipeline>` 而不是 `ChannelHandlerDelegate`

`ChannelHandlerDelegate` 只能给终端 handler，无法配置 TLS/遥测/超时等 transport 中间件。`Action<IChannelPipeline>` 保留现有 `Use` + `Run` 能力，同时不需要继承：

```csharp
var server = new ChannelServer(options, pipeline =>
{
    pipeline.Use(TlsMiddleware);
    pipeline.Use(TelemetryMiddleware);
    pipeline.Run(HandleAsync);
});
```

### 3.6 为什么现在不做 `ChannelServerBuilder`

- `ChannelServerOptions` 用对象初始化器就够；
- `Action<IChannelPipeline>` 已经是 pipeline builder；
- 单 server builder 只会多一层状态、校验和 API 面积；
- 多服务器如果入口变啰嗦，再加 host 级 builder（`ChannelServerHostBuilder`），不是现在。

---

## 4. 使用方式

### 4.1 单个 server

```csharp
await using var server = new ChannelServer(
    new ChannelServerOptions
    {
        EndPoint = new IPEndPoint(IPAddress.Any, 8000),
    },
    pipeline => pipeline.Run(HandleAsync),
    loggerFactory);

await server.RunAsync(ct);
```

### 4.2 多个 server

```csharp
var http = new ChannelServer(
    new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Any, 8080) },
    pipeline => pipeline.Run(HttpHandler));

var mqtt = new ChannelServer(
    new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Any, 1883) },
    pipeline => pipeline.Run(MqttHandler));

await using var group = new ChannelServerGroup(http, mqtt);
await group.StartAsync(ct);

// group.Servers[0].LocalEndPoint / [1].LocalEndPoint
```

- 不同协议、不同端口：两个独立 `ChannelServer`。
- 同一个 handler 跑两个端口：两个 `ChannelServer`，options 不同。
- 一个启动失败：`ChannelServerGroup` 停掉已启动的 server。

### 4.3 示例迁移

`SampleChannelServer` 不再继承：

```csharp
internal static class SampleChannelHandler
{
    public static async Task HandleAsync(ChannelContext context)
    {
        await using var session = context.AsLineTextSession();

        while (true)
        {
            string line;
            try
            {
                line = await session.ReadAsync(context.ConnectionClosed);
            }
            catch (EndOfStreamException)
            {
                break;
            }

            // 处理一行
        }
    }
}
```

宿主侧保留 `Host` 也行，用一个很小的 runner 把 `ChannelServer` 接进去：

```csharp
public sealed class ChannelServerRunner : BackgroundService
{
    private readonly ChannelServer _server;

    public ChannelServerRunner(ChannelServer server) => _server = server;

    protected override Task ExecuteAsync(CancellationToken ct)
        => _server.RunAsync(ct);
}
```

### 4.4 不使用 IoC

```csharp
await using var server = new ChannelServer(options, pipeline => pipeline.Run(HandleAsync));
await server.StartAsync(ct);
```

`scopeFactory` 为空时，`ChannelContext.Services` 是空 scope；P2 的 `AsSession` 不依赖 DI，可以正常工作。

---

## 5. 多服务器与多协议

### 5.1 不同协议、不同端口

直接用多个 `ChannelServer` + `ChannelServerGroup`。这是 P1.5 的主要场景。

### 5.2 同一个 server 类、多个端口

不需要子类：

```csharp
var publicServer = new ChannelServer(
    new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Any, 8080) },
    pipeline => pipeline.Run(HttpHandler));

var adminServer = new ChannelServer(
    new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Any, 8081) },
    pipeline => pipeline.Run(HttpHandler));

await using var group = new ChannelServerGroup(publicServer, adminServer);
```

### 5.3 同一个端口、多个协议

组合解决不了协议选择，仍然需要 selector。P2.5 的形态：

```csharp
public interface IChannelProtocol
{
    string Name { get; }
    ValueTask HandleAsync(ChannelContext context, CancellationToken ct);
}

public interface IChannelProtocolSelector
{
    ValueTask<IChannelProtocol> SelectAsync(ChannelContext context, CancellationToken ct);
}

public sealed class ProtocolDispatcher
{
    public ProtocolDispatcher(IChannelProtocolSelector selector);

    public async Task HandleAsync(ChannelContext context)
    {
        var ct = context.ConnectionClosed;
        var protocol = await selector.SelectAsync(context, ct);
        await protocol.HandleAsync(context, ct);
    }
}
```

`HandleAsync(ChannelContext)` 直接匹配 `ChannelHandlerDelegate`，挂到普通 server 上：

```csharp
var dispatcher = new ProtocolDispatcher(selector);
var server = new ChannelServer(options, pipeline => pipeline.Run(dispatcher.HandleAsync));
```

selector 只 peek、不消费：

```csharp
var result = await context.Reader.ReadAsync(context.ConnectionClosed);
var protocol = Match(result.Buffer); // TLS 0x16 / HTTP/2 preface / MQTT 0x10 / magic
context.Reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
```

TLS 同端口需要 ALPN 或先 sniff 再包 `SslStream`，依赖 P3；不要放进 P1.5/P2。

---

## 6. 迁移步骤

### 6.1 代码拆分

1. 新建 `ChannelServerCore.cs`，把 `ChannelServerBase` 的字段和方法原样搬入。
2. 新建 `ChannelServer.cs`，实现 `IChannelServer`，全部委托 core。
3. 新建 `ChannelServerGroup.cs`。
4. 删除 `ChannelServerBase.cs`。
5. 删除 `ChannelServerServiceCollectionExtensions.cs` 的 `AddChannelServer<T>`。
6. 删除 `ChannelServerHostedService<T>`（或改成非泛型，等 IoC 恢复）。
7. `SampleChannelServer.cs` 改成静态 handler。
8. `Program.cs` 用 `ChannelServer` + 示例内 `ChannelServerRunner`。

### 6.2 测试迁移

- `EchoHostedServer` / `HoldHostedServer` / `ScopedProbeServer` 不再继承，改成 handler 方法。
- `BuildHost<T>` 改成 `CreateServer(configure, handler, loggerFactory?, scopeFactory?)`。
- 需要 `GetRequiredService<EchoHostedServer>()` 的测试改为持有 `ChannelServer` 实例，或解析 `IChannelServer`。
- `AddChannelServer_RegistersSameServerInstance` 删除；P1.5 不测 IoC。
- 新增多 server 测试。

### 6.3 受影响文件

```text
src/Galosys.Foundation.Primitives/
  Microsoft/Extensions/Hosting/Net/
    ChannelServerBase.cs                 删除
    ChannelServerCore.cs                 新增（internal）
    ChannelServer.cs                     新增（public sealed）
    ChannelServerGroup.cs                新增
    Hosting/ChannelServerHostedService.cs 删除
  Microsoft/Extensions/DependencyInjection/
    ChannelServerServiceCollectionExtensions.cs 删除/重写

samples/NetServer.ConsoleApp/
  SampleChannelServer.cs                 改成 handler
  Program.cs                             改为 new ChannelServer
  ChannelServerRunner.cs                 新增（示例内 BackgroundService）

tests/Galosys.Foundation.Primitives.Tests/
  HostingTests.cs                        迁移
  ScopedHostingTests.cs                  迁移
  其他使用 ChannelServerBase 的测试       迁移
```

---

## 7. 行为保持清单

抽取 core 时必须保留：

- `ChannelServerOptions.Clone()` 与 `Validate()`；
- `StartAsync` 的启动锁、绑定成功后返回；
- 停机后不能重启（`_stopStarted`）；
- `StopAsync` 幂等，`ct` 只取消等待，不取消停机流程；
- accept loop 连续失败阈值（10 次）与日志；
- `MaxConnections` 拒绝路径；
- 每连接 `IServiceScope` 创建、释放、异常兜底；
- `ChannelContext`、`ConnectionClosed`、`Abort`、`DisposeConnectionAsync`；
- 优雅停机等待在途连接，超时后 abort；
- `LocalEndPoint`；
- `IChannelServer` 公共成员不变。

---

## 8. 测试计划

### 8.1 单 server

- `ChannelServer_StartAsync_BindsAndEchoes`：现有 echo 测试迁移。
- `ChannelServer_StartAsync_Throws_WhenPortInUse`。
- `ChannelServer_StartAsync_Throws_WhenEndPointMissing`。
- `ChannelServer_StopAsync_WaitsForInflightConnection`。
- `ChannelServer_RunAsync_Cancel_Stops`。
- `ChannelServer_NoLoggerNoScope_Works`。

### 8.2 多 server

- `ChannelServerGroup_TwoServers_BindDifferentPorts`：两个 `loopback:0`，各自 `LocalEndPoint` 不同。
- `ChannelServerGroup_IndependentOptions`：两个 server 用不同 `MaxConnections`/`ShutdownTimeout`，行为不串。
- `ChannelServerGroup_StartFailure_StopsStarted`：第二个端口被占用，第一个被停掉。
- `ChannelServerGroup_Stop_StopsAll`。
- `ChannelServerGroup_Dispose_Idempotent`。

### 8.3 迁移后回归

- 现有 60 个测试全部通过；
- 每连接 scope 测试仍通过（传入 `IServiceScopeFactory`）；
- `NetServer.ConsoleApp` 能启动并回显。

---

## 9. 风险与取舍

| 风险/取舍 | 说明 | 缓解 |
|---|---|---|
| 破坏性变更 | `ChannelServerBase`、`AddChannelServer<T>` 删除 | 仓库内只有 sample/tests 使用；一次性迁移 |
| 抽取 core 引入回归 | 500+ 行生命周期代码搬家 | 机械搬运，不改逻辑；先跑全量测试 |
| 没有 `protected virtual` 扩展点 | 不能通过子类改行为 | 用 `Action<IChannelPipeline>` + handler 组合；确需扩展再加 |
| `ChannelServerGroup` 失败语义 | 部分启动失败的回滚 | 明确“停已启动、抛原始异常”，加测试 |
| IoC 暂缺 | `AddChannelServer` 删除 | 先用构造函数；IoC 恢复时加 `AddChannelServer(definition)` |
| 同端口多协议未解决 | 组合不是 selector | P2.5 单独做，不混进 P1.5 |

---

## 10. 决议

1. **移除 `ChannelServerBase`**，不再保留兼容外观。
2. **`ChannelServer` sealed + `ChannelServerCore` internal**，组合替代继承。
3. **pipeline 配置用 `Action<IChannelPipeline>`**，handler 用 `ChannelHandlerDelegate`。
4. **`ChannelServerGroup` 纳入 P1.5b**，负责多 server 生命周期。
5. **builder 后置**：单 server 不引入 `ChannelServerBuilder`；多 server 入口真啰嗦时再加 host builder。
6. **IoC 后置**：P1.5 不恢复 `AddChannelServer<T>`；以后用 `ChannelServerDefinition` 接入。
7. **同端口多协议单独 P2.5**：`IChannelProtocolSelector` + `ProtocolDispatcher` + ALPN。
8. **`IChannelServer` 保持不变**，P4 客户端继续实现同一契约。
