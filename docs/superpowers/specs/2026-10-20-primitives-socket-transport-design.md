# Primitives Socket 传输层：基架与抽象设计

| 项 | 内容 |
|---|---|
| 状态 | 待评审（抽象层已定稿，实现细节待拆任务） |
| 日期 | 2026-10-20 |
| 范围 | `src/Galosys.Foundation.Primitives` 的 socket 封装及其消费方 `NetServer.ConsoleApp` |
| 非目标 | 不涉及 HTTP/gRPC、不做消息总线、不改 `MafSample.*` 示例的逻辑 |
| 关联 | 后续由 `/opsx-propose-x` 拆解为 `openspec/changes/<name>/` |

---

## 1. 背景

`Galosys.Foundation.Primitives` 目前只有一套基于 TCP + `System.IO.Pipelines` 的 socket 封装，
被 `NetServer.ConsoleApp` 用作示例。封装本身能跑通"收字节、打印、发字节"，但**抽象层次是错的**：
把"监听器、连接、字节流、消息、服务容器"压进了 4 个类型里。随着它要被当作**框架级公共传输层**
（TLS、自定义帧协议、客户端重连、Unix socket/NamedPipe、可观测性）复用，现有抽象无法承载。

本设计只讨论**基架与抽象**：
- 分层与职责边界
- 公共契约与不变量
- 所有权与生命周期
- 客户端模型
- 命名与依赖

实现细节（部分发送、错误码分类、背压阈值调参等）由底层内核承担，见 §4.1。

### 1.1 现状代码范围

| 文件 | 行数 | 角色 |
|---|---|---|
| `IChannelServer.cs` | 8 | 服务器生命周期契约 |
| `ChannelServerBase.cs` | 113 | 监听 + accept + 每连接收/发循环 |
| `ChannelContext.cs` | 29 | 连接数据袋（另含死代码 `AbstractChannelHandler`） |
| `ChannelClientBase.cs` | 21 | `internal` 占位，全部 `NotImplementedException` |

消费方仅 `src/NetServer.ConsoleApp`（`ListenerBootstrapper.cs` + `SenderBootstrapper.cs`）。
`MafSample.API`、`MafSample.WpfApp` 均不引用本库，**影响面为 1 个项目**。

---

## 2. 现状诊断

### 2.1 抽象层的结构性错位

| # | 现状 | 本质 |
|---|---|---|
| A1 | `IChannelServer` 同时承担监听生命周期、连接处理、资源释放 | 一个接口三种职责 |
| A2 | `ChannelContext` 是可变的 sealed 数据袋（可 set 的 `Reader`/`Writer` + `Items`） | 用数据结构代替契约：无身份、无生命周期、无所有权 |
| A3 | `ChannelClientBase : IChannelServer` | 范畴错误：客户端不是服务器 |

派生的抽象缺陷：

- **无组合缝**：`ChannelServerBase.ChannelHandleAsync` 是唯一扩展点，TLS/日志/超时/限流/帧解码只能塞进子类。
- **无所有权定义**：Socket/Pipe 谁创建、谁释放、handler 异常谁观察，均未定义。
- **配置泄漏**：无 Options 类型，backlog/Pipe 阈值/socket 选项只能硬编码。
- **同名反义**：`IChannelServer.StartAsync` 阻塞到停机，`IHostedService.StartAsync` 启动即返回。
- **死抽象**：`AbstractChannelHandler` 从未接线；`ChannelClientBase` 是空壳。

### 2.2 实现层缺陷（由内核替换解决，此处仅登记）

> 这些**不是本次要逐个修的代码**，而是"为什么不能继续在现有内核上叠功能"的证据。
> 复用 Kestrel transport 后，绝大部分随旧代码一起删除。

| # | 位置 | 问题 |
|---|---|---|
| P0-1 | `ChannelServerBase.cs:27-30` | `StopAsync` 直接 `throw`，无法优雅停机；`Dispose` 不关闭活动连接 |
| P0-2 | `ChannelServerBase.cs:23` | `_ = HandleAsync(socket)` 即发即弃：异常被吞、无法等待在途连接、无并发上限 |
| P0-3 | `ChannelServerBase.cs:53-59` | `WhenAll(fill, process)` 在 `fill` 抛异常时跳过 `@out.Writer.Complete()` 与 `await drain`，任务泄漏 |
| P0-4 | `ChannelServerBase.cs:99` | `SendAsync` 返回值被忽略，**部分发送会静默丢字节** |
| P0-5 | `ChannelServerBase.cs:74` | `ReceiveAsync` 不传 `CancellationToken`，不区分 `ConnectionReset`/`OperationAborted`/`ObjectDisposed` |
| P0-6 | `ChannelServerBase.cs:22` | `AcceptAsync(ct)` 取消异常冒泡到 `BackgroundService`，正常停机走成宿主故障 |
| P0-7 | `ListenerBootstrapper.cs:18-23` | 一个 DI scope 覆盖服务器整个生命期，处理器解析 Scoped 服务会拿到长生命周期实例 |
| P1-1 | `ChannelServerBase.cs` | 无中间件缝隙，无法插入 TLS/帧协议/认证 |
| P1-2 | `ChannelContext.cs` | 无 `ConnectionId`/端点/`ConnectionClosed`/per-connection `IServiceProvider` |
| P1-3 | `ChannelClientBase.cs` | 客户端不存在（无 connect/重连/心跳） |
| P1-4 | `ChannelServerBase.cs:11-16` | 写死 IPv4、backlog=128、无 `PipeOptions`、无 socket 选项、无超时、构造即 Bind |
| P1-5 | 全部 | 无 `ILogger`、无 metrics、无 `ActivitySource`、无连接 ID |
| P2-1 | 全部 | 库代码无 `ConfigureAwait(false)`（同仓库有 WPF，存在上下文捕获风险） |
| P2-2 | 仓库 | 无任何测试项目，并发 + IO 代码只能手工验证 |

---

## 3. 关键决策

| ID | 决策 | 理由 |
|---|---|---|
| D1 | **不自研 socket 内核，也不复制 Kestrel 源码**；复用 Kestrel 的 **public** transport | Kestrel 的 `SocketConnection` 是 `internal`，复制即接管维护；而接受连接/收发内核以 public API 形式可用（§3.1） |
| D2 | **保留现有 4 个类型名与命名空间** `Microsoft.Extensions.Hosting.Net` | 使用方零迁移；且 ASP.NET Core/.NET 中不存在同名 public 类型，不会碰撞 |
| D3 | **Kestrel 类型只出现在 private 字段/局部变量，绝不进入 public 签名** | 使用方全程 `using Microsoft.AspNetCore.*` 也不会二义；升级依赖不破坏 API |
| D4 | **客户端采用"连接级"语义**：重连产生新 `ChannelContext`，断开期间不缓冲、不重放 | 不隐瞒"消息是否送达"；透明续传是另一个复杂度量级，另立抽象 |
| D5 | **连接 ≠ 消息 ≠ 传输**，三层分离，消息层可选 | 现在 90% 的抽象问题来自三者混在一起 |

### 3.1 D1 的事实依据

`Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets` 的 `PublicAPI.Shipped.txt`（release/10.0）确认：

| public 类型 | 能力 |
|---|---|
| `SocketTransportFactory(IOptions<SocketTransportOptions>, ILoggerFactory)` | `BindAsync(EndPoint, CancellationToken)` → `IConnectionListener`、`CanBind(EndPoint)` |
| `SocketConnectionContextFactory` | `Create(Socket)` → `ConnectionContext` |
| `SocketTransportOptions` | `Backlog`、`NoDelay`、`MaxRead/WriteBufferSize`、`IOQueueCount`、`CreateBoundListenSocket` |

`ConnectionContext` / `IConnectionListener` / `ConnectionDelegate` 位于 `Microsoft.AspNetCore.Connections`，
与 HTTP 无关。**结论：内核不用写。**

> 曾被考虑并否决的方案：
> - 自研 `SocketConnection` 内核（~3000 行，重复 Kestrel 已踩过的坑）。
> - 直接改用 ASP.NET Core 的 `IConnectionListener` 作为公共契约（会迫使使用方接受 Kestrel 命名与依赖）。
> - 改名对齐 Kestrel（与 D2 冲突，且无收益）。

---

## 4. 目标分层

```
┌──────────────────────────────────────────────────────────────┐
│ L5 宿主/DI      AddChannelServer / ChannelServerHostedService  │ 生命周期与注册
├──────────────────────────────────────────────────────────────┤
│ L4 中间件       UseTls → UseTelemetry → UseTimeout → ...       │ 只包 Transport，不碰业务
├──────────────────────────────────────────────────────────────┤
│ L3 消息         IChannelSession<T> + IMessageFramer<T>         │ 可选：byte → message
├──────────────────────────────────────────────────────────────┤
│ L2 连接         ChannelContext（身份 + 生命周期 + Transport）  │ 唯一连接视图
├──────────────────────────────────────────────────────────────┤
│ L1 传输         Kestrel SocketTransportFactory / NamedPipe     │ internal，不出现在 public 签名
└──────────────────────────────────────────────────────────────┘
```

### 4.1 内核职责边界

L1 由以下实现承担，**我们不写**：

| 能力 | 提供方 |
|---|---|
| accept 循环、每连接收/发双循环 | Kestrel `SocketTransportFactory` |
| 部分发送循环、`SocketError` 分类、`ObjectDisposedException` 处理 | Kestrel `SocketSender`/`SocketReceiver` |
| 背压、`PipeOptions`、`MemoryPool`、`PipeScheduler` | Kestrel + `System.IO.Pipelines` |
| abort/graceful shutdown、`ConnectionClosed` 恰好一次 | Kestrel `SocketConnection.Shutdown` |
| 客户端 connect + 双循环 | `Pipelines.Sockets.Unofficial` 的 `SocketConnection.ConnectAsync` |
| NamedPipe 服务端 | `NamedPipeServerStream` + `PipeReader/PipeWriter.Create`（自写，~100 行） |

---

## 5. 抽象契约

### 5.1 `ChannelContext`（保留类名，改为只读连接视图）

```csharp
public sealed class ChannelContext : IAsyncDisposable
{
    internal ChannelContext(/* internal ctor: 外部不可伪造 */);

    public string ConnectionId { get; }                 // 日志/追踪关联
    public EndPoint? LocalEndPoint { get; }
    public EndPoint? RemoteEndPoint { get; }
    public IDuplexPipe Transport { get; }               // 原始字节
    public PipeReader Reader => Transport.Input;        // 保留，源码兼容
    public PipeWriter Writer => Transport.Output;       // 保留
    public CancellationToken ConnectionClosed { get; }  // 恰好触发一次
    public IServiceProvider Services { get; }           // 每连接 scope
    public IDictionary<object, object?> Items { get; }  // 保留：仅杂项状态，不放服务

    public void Abort(Exception? reason = null);        // 与"优雅完成"分开
    public ValueTask DisposeAsync();                    // 幂等；内部 abort
}
```

**不新增 `IChannelContext` 接口**：测试用一对内存 `Pipe` 即可，不值得为 mock 增加一层抽象。

### 5.2 `IChannelServer`（保留名字，职责收窄为监听器）

```csharp
public interface IChannelServer : IAsyncDisposable
{
    EndPoint? LocalEndPoint { get; }          // 绑定成功后非 null
    Task StartAsync(CancellationToken ct);    // 返回 ⇔ 已可 accept
    Task RunAsync(CancellationToken ct);      // 新增：阻塞到停机的旧用法
    Task StopAsync(CancellationToken ct);     // 停止 accept → 等在途 → 超时 Abort
}
```

- `StartAsync` 语义变更见 §13-Q1；`RunAsync` 保证旧消费方零改动迁移。
- `IDisposable` 保留为兜底（同步释放），`IAsyncDisposable` 为主路径。

### 5.3 `ChannelServerBase`（保留，降级为兼容外观 + 组合入口）

```csharp
public abstract class ChannelServerBase : IChannelServer
{
    protected ChannelServerBase(EndPoint endPoint);                    // 保留
    protected ChannelServerBase(ChannelServerOptions options);         // 新增

    protected virtual void Configure(IChannelPipeline pipeline)
        => pipeline.Run(ChannelHandleAsync);                           // 默认行为不变

    protected abstract Task ChannelHandleAsync(ChannelContext context);
}
```

- 现有子类一行不改。
- 新代码通过 `Configure` 插入 TLS/遥测/超时，不再重写 accept 循环。
- **删除** `AbstractChannelHandler`（死代码）。

### 5.4 管线与中间件

```csharp
public delegate Task ChannelHandlerDelegate(ChannelContext context);
public delegate Task ChannelMiddleware(ChannelContext context, ChannelHandlerDelegate next);

public interface IChannelPipeline
{
    IChannelPipeline Use(ChannelMiddleware middleware);
    void Run(ChannelHandlerDelegate handler);
}
```

顺序固定为：`scope → TLS → telemetry → timeout → 业务 handler`。
中间件**只允许包装 `Transport`**，不得读取业务数据（避免抢走 handler 的字节流）。

### 5.5 Options

```csharp
public sealed class ChannelServerOptions
{
    public EndPoint EndPoint { get; set; } = default!;
    public int Backlog { get; set; } = 512;
    public int MaxConnections { get; set; } = 0;                 // 0 = 不限
    public bool NoDelay { get; set; } = true;
    public ChannelKeepAliveOptions? KeepAlive { get; set; }
    public LingerOption? LingerState { get; set; }
    public bool ReuseAddress { get; set; }
    public bool DualMode { get; set; }
    public TimeSpan? IdleTimeout { get; set; }
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public long? MaxReadBufferSize { get; set; }
    public long? MaxWriteBufferSize { get; set; }
    public int IOQueueCount { get; set; } = 0;                   // 0 = 由内核决定
    public Func<EndPoint, Socket>? CreateBoundListenSocket { get; set; }
}
```

消息层独立配置：`MessageChannelOptions { MaxMessageSize, MaxBufferedMessages, IdleTimeout }`。

### 5.6 消息层（L3）

```csharp
public interface IMessageFramer<T>
{
    bool TryParseMessage(ref ReadOnlySequence<byte> buffer, out T message);
    void WriteMessage(IBufferWriter<byte> writer, T message);
}

public interface IChannelSession<T> : IAsyncDisposable
{
    ValueTask<T> ReadAsync(CancellationToken ct);
    ValueTask WriteAsync(T message, CancellationToken ct);
    Task Completion { get; }
}

// 在连接之上叠加，连接本身仍是字节视图
public static class ChannelContextMessageExtensions
{
    public static IChannelSession<T> AsSession<T>(
        this ChannelContext context,
        IMessageFramer<T> framer,
        MessageChannelOptions? options = null);
}
```

内置 framer：`LengthPrefixedFramer`（4 字节大端）、`LineFramer`（UTF-8 + `\n`）、`RawFramer`（兼容现状）。
序列化复用 `Galosys.Foundation.Core` 的 `IObjectSerializer`，**不自造 `IMessageSerializer`**。
`MaxMessageSize` 超限 → 抛 `InvalidDataException` 并 abort 连接（防止文档点名的无限缓冲 OOM）。

### 5.7 客户端（L1 + L4）

```csharp
public interface IChannelClient : IAsyncDisposable
{
    ChannelConnectionState State { get; }
    ChannelContext? Connection { get; }               // 仅 Connected 时非 null
    event EventHandler<ChannelStateChangedEventArgs>? StateChanged;

    Task ConnectAsync(CancellationToken ct);           // 幂等
    Task CloseAsync(CancellationToken ct);             // 优雅关闭，不再重连
}

public abstract class ChannelClientBase : IChannelClient
{
    protected ChannelClientBase(ChannelClientOptions options,
                                ChannelHandlerDelegate? handler = null);
}

public sealed class ChannelClient : ChannelClientBase { /* 默认实现 */ }
```

**`SendAsync/ReceiveAsync` 不放在 `IChannelClient` 上**：发消息走 `Connection.AsSession<T>()`，
与服务端同一套 API。

#### 5.7.1 连接级 vs 会话级

| | A. 连接级（本设计） | B. 会话级（不做） |
|---|---|---|
| 重连后 `Connection` | 换新实例，旧连接 `ConnectionClosed` 已触发 | 保持不变，透明续传 |
| 断开期间发消息 | **失败并报错** | 排队缓冲 |
| 重放/去重 | 不做，业务决定 | 框架必须实现（序号/ack/幂等键） |
| 语义 | 诚实 | 易被误解为不丢 |
| 复杂度 | 低 | 高 |

**v1 只做 A，并把"不缓冲、不重放"写进文档。** B 将来作为独立的 `IResumableSession<T>` 提供，
绝不藏在客户端里静默实现。

#### 5.7.2 状态机

```
Disconnected ──ConnectAsync──▶ Connecting ──成功──▶ Connected
      ▲                            │                  │
      │                       失败(可重试)        断开/CloseAsync
      │                            ▼                  ▼
      └────────── 达到上限 ──── Reconnecting ◀────────┘
                                   │
                              CloseAsync/Dispose
                                   ▼
                                 Closed（终态，不可逆）
```

#### 5.7.3 重连策略

```csharp
public sealed class ChannelReconnectPolicy
{
    public int MaxAttempts { get; set; } = 0;                   // 0 = 无限
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);
    public double BackoffMultiplier { get; set; } = 2.0;
    public double JitterFactor { get; set; } = 0.2;
    public Func<Exception, bool>? ShouldRetry { get; set; }     // 默认见下表
}
```

| 失败类型 | 是否重试 |
|---|---|
| `ConnectionRefused` / `TimedOut` / `HostUnreachable` | 是 |
| TLS 证书/认证失败 | **否**（配置问题，重试无意义） |
| 调用方取消（`OperationCanceledException`） | 否 |
| 配置错误（无 EndPoint 等） | 否 |

- 退避使用 `TimeProvider`（测试注入假时钟），**代码中不得出现裸 `Task.Delay`**。
- 指数退避 + 抖动，避免集群同时重连。
- 不引入 Polly 的 retry：那是"操作级"重试；连接生命周期用状态机表达。`IResiliencePipeline` 仅可选用于握手后的单次请求。

#### 5.7.4 客户端独有配置

```csharp
public sealed class ChannelClientOptions
{
    public EndPoint EndPoint { get; set; } = default!;
    public IReadOnlyList<EndPoint>? EndPoints { get; set; }     // 预留：故障转移
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public ChannelReconnectPolicy? Reconnect { get; set; }
    public ChannelTlsOptions? Tls { get; set; }
    public ChannelKeepAliveOptions? KeepAlive { get; set; }
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
```

---

## 6. 不变量（实现必须遵守）

| ID | 不变量 |
|---|---|
| INV-1 | 一个连接只有**一个 Reader 消费者**、**一个 Writer 生产者**；跨中间件不得重复消费 |
| INV-2 | handler **不得** `Dispose`/`Complete` `Transport`，只负责返回；收尾由框架完成 |
| INV-3 | `ConnectionClosed` **恰好触发一次**，且在 per-connection scope 释放之前 |
| INV-4 | `Abort`（立即中止）与"优雅完成"（flush + FIN）是两条路径，不合并 |
| INV-5 | `ChannelContext` 所有属性只读；服务从 `Services` 取，不从 `Items` 取 |
| INV-6 | `StartAsync` 返回 ⇔ 已能 accept（不是因为停机返回） |
| INV-7 | Kestrel 类型不出现在任何 public 签名 |
| INV-8 | 客户端每个连接对应一个 `ChannelContext`；重连 = 新连接 + 新 `ConnectionId` |
| INV-9 | 消息层超限必须 fail-fast，不得无限缓冲 |
| INV-10 | 库代码一律 `ConfigureAwait(false)` |

---

## 7. 命名与冲突自检

| 候选名 | 冲突 | 决定 |
|---|---|---|
| `ChannelContext` / `IChannelServer` / `ChannelServerBase` / `ChannelClientBase` | 检索未发现 ASP.NET Core/.NET 同名 public 类型 | ✅ 保留 |
| `ConnectionContext` / `IConnectionListener` / `ConnectionDelegate` | 与 Kestrel 全名相同 | ❌ 不定义、不暴露 |
| `IConnectionFactory` | `Microsoft.AspNetCore.Connections` 已有 | ❌ 用 `IChannelClientFactory` |
| `ChannelOptions` | 与 `System.Threading.Channels.ChannelOptions` 冲突 | ❌ 用 `ChannelServerOptions` |
| `IMessageChannel` | 可能与 Galosys Messaging 抽象冲突 | ❌ 用 `IChannelSession<T>` |
| `IMessageSerializer` | 已有 `IObjectSerializer` | ❌ 复用现有 |
| `ConnectionState` | 通用名，易混 | ❌ 用 `ChannelConnectionState` |
| `ReconnectPolicy` | 与 SignalR `IRetryPolicy` 概念相近 | ⚠️ 用 `ChannelReconnectPolicy` |
| `KeepAliveOptions` | 通用名 | ⚠️ 用 `ChannelKeepAliveOptions` |

> 冻结公共 API 前，应做一次全仓库 + 依赖包的类型名检索（含 `galosys.foundation.*`）。

---

## 8. 工程骨架与依赖

保持**单项目**，内部按层分文件夹：

```
src/Galosys.Foundation.Primitives/
  Net/
    ChannelContext.cs
    IChannelServer.cs
    ChannelServerBase.cs
    ChannelClientBase.cs
    ChannelClient.cs
    Options/
      ChannelServerOptions.cs
      ChannelClientOptions.cs
      MessageChannelOptions.cs
    Pipeline/
      ChannelHandlerDelegate.cs
      ChannelMiddleware.cs
      IChannelPipeline.cs
    Hosting/
      ChannelServerHostedService.cs
      ServiceCollectionExtensions.cs
    Framing/
      IMessageFramer.cs
      IChannelSession.cs
      LengthPrefixedFramer.cs
      LineFramer.cs
      RawFramer.cs
    Security/
      ChannelTlsOptions.cs
      TlsMiddleware.cs
    Transports/            // internal
      KestrelChannelListener.cs
      NamedPipeChannelListener.cs
      InMemoryConnection.cs   // 测试用，InternalsVisibleTo
    Diagnostics/
      ChannelDiagnostics.cs   // ActivitySource + Meter + 日志事件 ID
```

依赖：

| 依赖 | 用途 | 说明 |
|---|---|---|
| `<FrameworkReference Include="Microsoft.AspNetCore.App" />` | Kestrel transport | 只加在本项目；当前仅 `NetServer.ConsoleApp` 引用本库 |
| `Pipelines.Sockets.Unofficial`（MIT，2.2.16） | 客户端 `SocketConnection.ConnectAsync` | 通过 internal `IConnectionSource` 包裹，可替换 |
| `Microsoft.Extensions.*` | Hosting/Options/Logging | 已有 |

> 若将来出现非 ASP.NET Core 宿主，再把 `Transports/` 拆成独立包 `Primitives.Kestrel`。
> **现在不拆**，避免过早分包。

---

## 9. 分阶段计划

| 阶段 | 内容 | 验收标准 |
|---|---|---|
| P1 基架 | Abstractions + Kestrel 适配 + 管线 + Options + 每连接 scope + 优雅停机 + `ChannelServerHostedService`；**删除**旧收/发循环 | `NetServer.ConsoleApp` 改用新 API 后行为等价；loopback 测试通过 |
| P2 消息层 | `IMessageFramer<T>` + 三种内置 framer + `IChannelSession<T>` + `MaxMessageSize` | 半包/粘包/超限拒绝测试通过 |
| P3 TLS | `ChannelTlsOptions` + `UseTls`（服务端/客户端） | 双向 TLS 握手、证书校验失败路径测试通过 |
| P4 客户端 | `ChannelClientBase`/`ChannelClient` + 状态机 + `ChannelReconnectPolicy` + 心跳接入点 | 断线重连、退避、取消、终态测试通过（假时钟） |
| P5 收尾 | NamedPipe transport + Diagnostics + 文档 + 示例迁移 | metrics/trace 可见；`AGENTS.md` 更新（新增测试命令） |

P1 内即完成一次"旧代码删除"，避免两套内核长期并存。

---

## 10. 测试策略

仓库当前**没有任何测试项目**，P1 必须新建 `Galosys.Foundation.Primitives.Tests`。

| 类型 | 内容 |
|---|---|
| 单元 | framer 解析（半包、粘包、超限、空消息）、options 校验、状态机迁移 |
| 集成（真实 loopback socket） | 大消息触发部分发送、连接重置、`MaxMessageSize` 拒绝、优雅停机等待在途连接、端口占用 |
| 集成（TLS） | 握手成功、证书校验失败、TLS 下关闭语义 |
| 客户端 | 连接失败分类、重连上限、`CloseAsync` 打断重连、`Dispose` 后不再重连、旧 `Connection` 失效后发消息报错 |
| 传输无关 | `InMemoryConnection` + `InternalsVisibleTo`，客户端/服务端逻辑不依赖 socket |
| 时间 | 一律 `TimeProvider` 假时钟，禁止真实等待 |

---

## 11. 风险与缓解

| 风险 | 影响 | 缓解 |
|---|---|---|
| 引入 `Microsoft.AspNetCore.App` 依赖 | 非 Web 宿主也被迫携带 ASP.NET Core 共享框架 | 只加在 `Primitives`；影响面 1 个项目；预留拆包方案 |
| TLS 无真正半关闭 | 关闭语义含糊、可能丢尾部数据 | §6 INV-4 明确定义 `Abort` vs 优雅完成；TLS 下以 `SslStream` 关闭流程为准，写测试锁定 |
| `Pipelines.Sockets.Unofficial` 自述 API "very preliminary" | 上游变更 | 固定版本，内部 `IConnectionSource` 隔离，可替换为自写客户端 |
| `Microsoft.Extensions.Hosting.Net` 是微软保留前缀 | 未来同名类型冲突（CS0433） | 本次不改（D2）；记为后续可选迁移项 |
| 行为变更（Bind 时机、`StartAsync` 返回值） | 现有消费方受影响 | 提供 `RunAsync`；`NetServer.ConsoleApp` 同步迁移；变更写入发布说明 |
| 框架级 API 过早冻结 | 后续改造成本高 | P1 仅冻结最小集（§5.1–5.5）；消息层/客户端在 P2/P4 再冻结 |

---

## 12. 待决事项

| ID | 问题 | 推荐 | 备注 |
|---|---|---|---|
| Q1 | `StartAsync` 是否改为"返回即监听"，另加 `RunAsync` | 是 | 符合 `IHostedService` 约定；旧消费方用 `RunAsync` |
| Q2 | `ChannelClientBase` 保持 abstract + 新增 `ChannelClient` | 是 | 保留扩展性，默认实现开箱可用 |
| Q3 | `ChannelServerBase` 保留继承 + `Configure` 组合缝 | 是 | 现有子类零改动 |
| Q4 | `AbstractChannelHandler` 死代码 | 直接删 | 无引用 |
| Q5 | 客户端首版是否支持多 EndPoint 故障转移 | 否，仅预留字段 | 重连先做单地址 |
| Q6 | 客户端是否进本轮（P4） | 是 | 但排在服务端之后 |
| Q7 | 旧 `Primitives` socket 类型是否保留 `[Obsolete]` 过渡 | 不保留，直接替换（仅 1 个消费方） | 如外部已有使用方则改为保留一版 |
| Q8 | 是否现在拆 `Primitives.Kestrel` 独立包 | 否 | 等第二个宿主出现 |

---

## 13. 参考

- [System.IO.Pipelines - .NET](https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines)（背压、`AdvanceTo`、半包/粘包、取消）
- Kestrel `SocketConnection`（`dotnet/aspnetcore`，`release/10.0`，`Transport.Sockets/src/Internal/`）
- Kestrel `Transport.Sockets` `PublicAPI.Shipped.txt`（确认可复用的 public API）
- [Pipelines.Sockets.Unofficial](https://github.com/mgravell/Pipelines.Sockets.Unofficial)（MIT，客户端 `SocketConnection`）
- `Microsoft.AspNetCore.Connections` 命名空间（`ConnectionContext`/`IConnectionListener`/`IConnectionFactory`）
