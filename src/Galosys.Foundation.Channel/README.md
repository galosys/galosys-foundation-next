# Galosys.Foundation.Channel

逻辑通道传输框架：socket 传输 + 分帧编解码 + 客户端/服务端。核心类型位于 `Galosys.Foundation.Channel`；`*Options` 位于 `Microsoft.Extensions.Options`。P1.5a 使用组合式 `ChannelServer`；P1.5b 增加 `ChannelServerGroup`；P4-1 增加单连接 `ChannelClient`；P4-2 增加重连与指数退避；P4-3 增加连接池与批量写入。

> 本库的 Channel 指网络通道（逻辑通道 + 消息边界），与 `System.Threading.Channels.Channel<T>` 的进程内生产者-消费者队列无关。

## 架构分层

本库按「上层不感知底层」组织，共四层。每一层只依赖它下面一层的契约，因此换传输、换分帧、换编解码都不会波及上层。

```
┌──────────────────────────────────────────────────────────┐
│ 组合层  ChannelServer / ChannelClient / ChannelClientPool │  生命周期、组合、连接池
│          ChannelServerGroup                               │
├──────────────────────────────────────────────────────────┤
│ 消息层  IChannelPipeline / IChannelSession<T>             │  中间件、消息边界、编解码
│          IMessageFramer / IMessageCodec<T>                │
├──────────────────────────────────────────────────────────┤
│ 传输契约 IChannelListener / IChannelConnection（internal）│  绑定、accept、字节管道
├──────────────────────┬───────────────────────────────────┤
│ 传输实现              │ 传输实现                          │
│ SocketChannel*（TCP） │ （NamedPipe 等，待建）            │
└──────────────────────┴───────────────────────────────────┘
```

### 各层职责边界

| 层 | 负责 | 不负责 |
|---|---|---|
| 组合层 | 启停、优雅停机、失败回滚、多服务器编排、连接池租借归还 | 字节怎么传 |
| 消息层 | 分帧边界、payload 编解码、消息会话、协议分发、TLS 握手 | socket 细节 |
| 传输契约 | 绑定端点、接受连接、提供 `IDuplexPipe`、半关闭与 abort 信号 | 消息边界 |
| 传输实现 | 把具体 IO 资源（socket / 管道流）接到 `PipeReader` / `PipeWriter` | 消息语义 |

### 关键抽象

- **传输契约**：`IChannelListener` / `IChannelConnection` 是 internal，输出只有一个 `IDuplexPipe`。任何新传输只要能产出字节管道，就能复用上层的每连接 scope、`MaxConnections`、优雅停机、分帧编解码与 TLS —— 不需要改上层代码。
- **当前传输实现**：只有 `SocketChannelListener` / `SocketChannelConnection`（TCP）。
- **换传输的前置条件**：目前 `ChannelServerCore` 直接调用 `SocketChannelListener.BindAsync`，没有按配置选择实现的工厂缝；新增第二种传输前需要先补该缝。
- **消息边界**：分帧（`IMessageFramer`）只回答「消息到哪结束」，编解码（`IMessageCodec<T>`）只处理一帧 payload，两者互不知晓。

### 构造入口

server、client、连接池的**唯一公开构造入口是构建器**，构造函数均非公开：

- `ChannelServerBuilder` → `ChannelServer`
- `ChannelClientBuilder` → `IChannelClient`；经 `AsPool()` 切换到 `ChannelClientPoolBuilder` → `ChannelClientPool`

本库**不提供** IoC 集成：没有 `IServiceCollection` 扩展、没有 `IHostedService` 适配器、不引用 `Microsoft.Extensions.Hosting.Abstractions`。需要挂到宿主时由调用方自行包装 runner（见「接入宿主」）。

## 能力

- 自包含 socket transport：基于 `System.Net.Sockets` + `System.IO.Pipelines`，核心程序集不依赖 `Microsoft.AspNetCore.*` 或 Kestrel。
- `ChannelContext`：连接视图（`ConnectionId`、端点、`Transport`、`ConnectionClosed`、`Services`）；公开属性无 setter，`Items` 为连接内可变字典。
- `ChannelServerBuilder`：server 的唯一公开构建入口；配置快照、中间件注册顺序与终端处理器唯一性在构建器内保证。
- `ChannelClientBuilder` / `ChannelClientPoolBuilder`：client 与连接池的唯一公开构建入口；连接池经 `AsPool()` 作为 client 构建的一个选项进入。
- `ChannelServer`：sealed 组合入口，实现 `IChannelServer`。
- `ChannelServerGroup`：按构造顺序启动、失败时逆序回滚、并行停机并聚合异常、释放幂等。
- 每连接独立 `IServiceScope`（构建时传入 `IServiceScopeFactory` 后生效），连接关闭令牌先于作用域释放。
- `IChannelPipeline`：中间件只包装 `Transport`，业务数据由终端处理器消费。
- 优雅停机：停止 accept → 等待在途连接 → `ShutdownTimeout` 后强制 abort。
- 传输层处理部分发送、输入/输出 Pipe 背压、TCP keep-alive、半关闭与连接重置。
- 正常关闭会先排空已提交输出，并短暂读空内核接收缓冲区后再关闭，避免带未读入站数据触发 RST。
- TLS 传输适配器：`ChannelTlsOptions` 配置校验，`SslStream` 与 `IDuplexPipe` 的适配器，握手失败/超时与关闭语义。
- 服务端 TLS：`ChannelServerBuilder.UseTls(tls)` 在管线中完成握手并替换传输，握手先于后续中间件，失败不进入业务代码。

## 连接关闭语义

`ChannelContext.ConnectionClosed` 在以下情况触发一次：

- 处理器返回（框架开始收尾）；
- 对端重置连接（RST）或 socket 读写故障；
- 调用 `ChannelContext.Abort`。

客户端只关闭发送方向的半关闭（FIN）不会触发该令牌；处理器应通过 `Reader` 的 `IsCompleted`/读取结束来观察半关闭，并仍可继续写入响应。

## 消息分帧

`IMessageFramer` 只负责“消息到哪结束”，不负责 payload ↔ T。首批提供：

| Framer | 边界 | 说明 |
|---|---|---|
| `LengthPrefixedFramer` | 4 字节大端长度 + payload | 默认 `MaxFrameSize` 1 MiB，零长度帧合法 |
| `LineFramer` | `\n`（可裁剪 `\r`） | 纯字节分隔符，UTF-8 解码交给 codec |
| `PassThroughFramer` | 当前非空 buffer | 兼容原始字节；不保证多次写入不被合并 |

```csharp
var framer = new LengthPrefixedFramer(
    new LengthFieldFramerOptions { MaxFrameSize = 1024 * 1024 });

var result = framer.Parse(buffer);
if (result.IsComplete)
{
    // 必须在 Frame 被消费前完成解码或拷贝。
    var payload = result.Frame;
    reader.AdvanceTo(result.Consumed, result.Examined);
}
```

帧不完整时 `Consumed == buffer.Start`、`Examined == buffer.End`；完整帧只消费一帧；
非法长度或超限抛 `InvalidDataException`，由 session 中止连接。

## 消息编解码

`IMessageCodec<T>` 只处理一帧 payload，不处理长度头、分隔符、半包或粘包。

| Codec | T | 说明 |
|---|---|---|
| `JsonMessageCodec<T>` | `T` | `Utf8JsonReader`/`Utf8JsonWriter`，支持多段序列；空帧、`null`、无效 JSON、尾随内容抛 `JsonException` |
| `Utf8StringCodec` | `string` | 严格 UTF-8；空帧抛 `InvalidDataException`，非法字节抛 `DecoderFallbackException` |
| `RawMessageCodec` | `ReadOnlyMemory<byte>` | `Decode` 返回独立拷贝，可在 `AdvanceTo` 后继续使用 |

```csharp
IMessageCodec<MyMessage> codec = new JsonMessageCodec<MyMessage>();

// frame 来自 IMessageFramer.Parse；必须在 AdvanceTo 之前完成 Decode。
var message = codec.Decode(frame);

var writer = new ArrayBufferWriter<byte>();
codec.Encode(writer, message);
```

`ReadOnlySequence<byte>` 只在 `AdvanceTo` 之前有效；需要长期持有 payload 时使用
`RawMessageCodec` 或自行拷贝。

## 消息会话

`IChannelSession<T>` 是每连接一个的拉模型会话；只在终端 handler 创建，不放在
`IChannelPipeline.Use` 中间件里。

```csharp
static async Task HandleAsync(ChannelContext context)
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

        await session.WriteAsync(line, context.ConnectionClosed);
    }
}
```

示例 `NetServer.ConsoleApp` 只在终端 handler 创建行文本会话，并用同一个会话回包：

```csharp
public async Task HandleAsync(ChannelContext context)
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

        _logger.LogInformation(">>>>> {Line} <<<<<", line);

        // 回包；读和写复用同一个 session。
        await session.WriteAsync($"echo: {line}", context.ConnectionClosed);
    }
}
```

不要在 `IChannelPipeline.Use` 中间件里创建 session：中间件只包装 `Transport`，
读取业务数据会破坏管线契约。

- 一个连接只能创建一个 session；第二次 `AsSession`/`AsLineTextSession` 抛 `InvalidOperationException`。
- `DisposeAsync` 只释放 session 自己，不关闭连接；连接由 `ChannelServer` 收尾。
- `ReadAsync` 并发调用直接抛错；读取消不会消费缓冲区，下一次可以继续。
- `WriteAsync` 用信号量串行化；写取消可能已写出半个帧，会中止连接。
- `MessageChannelOptions.MaxMessageSize` 默认 1 MiB。
- `AsJsonSession<T>()` = 长度前缀 + JSON；`AsLineTextSession()` = 换行 + UTF-8。

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

- 校验先于握手：缺少服务端证书、`TargetHost` 为空或握手超时非法时直接抛 `ArgumentException`，不会进入 TLS 协商。
- 握手失败（证书被拒、明文连接）、握手超时与外部取消都以异常结束，不回退明文；失败时不暴露可用传输。
- 底层输入 EOF 只结束解密后的输入，写方向仍然可用，因此处理器仍能写回响应。
- 适配器不调用底层 `Input.Complete`，底层连接的收尾仍由框架负责。

本阶段不提供 `UseTls`，也不做同端口 ALPN。管线接入见 `primitives-tls-transport-2`。

## 服务端 TLS（P3-2）

```csharp
var tls = new ChannelTlsOptions
{
    ServerOptions = new SslServerAuthenticationOptions
    {
        ServerCertificate = certificate,
    },
};

var server = ChannelServerBuilder
    .Create(options => options.EndPoint = new IPEndPoint(IPAddress.Any, 8000))
    .UseTls(tls)
    .Use(telemetry)
    .Run(handler)
    .Build();
```

`UseTls` 在 `next` 之前完成服务端握手，并用 `context.UseTransport` 替换为解密后的管道；
握手失败不会调用后续中间件或 handler。中间件在管线结束后释放 TLS 包装层，底层连接仍由
`ChannelServer` 释放。

- TLS 中间件应尽量靠前；在它之前只放不读业务字节的观察型中间件。
- 同一管线只调用一次 `UseTls`；重复调用会导致二次握手失败。
- `UseTls` 只校验服务端一侧；`ClientOptions` 不完整不会阻塞服务端启动（客户端 TLS 另见后续阶段）。
- `ApplicationProtocols` 只透传给 `SslStream`，本阶段不做同端口 ALPN 协议分发。
- `NetServer.ConsoleApp` 保持明文示例，不在此 change 启用 TLS。

> 平台说明：Windows Schannel 默认协商 TLS 1.2。客户端拒绝服务端证书时，服务端握手会先于
> 客户端的拒绝告警返回成功，因此拒绝在业务读取时才暴露；TLS 端口始终不回退明文。

## 最小用法

```csharp
var server = ChannelServerBuilder
    .Create(options =>
    {
        options.EndPoint = new IPEndPoint(IPAddress.Loopback, 8000);
    })
    .Run(HandleAsync)
    .Build();

await server.RunAsync(cancellationToken);
```

`StartAsync` 只负责绑定、监听并启动接受循环，返回即可连接；`RunAsync` 是“启动并阻塞到停机”的兼容入口。构建器在 `Build()` 时把中间件与终端处理器物化成管线，每次启动尝试都会重新物化。

- `Create` 立即校验配置，非法配置在构建期就抛 `ArgumentException`，不会进入监听状态。
- `Run` 只能调用一次，重复调用抛 `InvalidOperationException`；未调用 `Run` 就 `Build()` 也抛 `InvalidOperationException`。
- `Use` 的注册顺序即中间件由外向内的执行顺序。
- `UseTls` 在**注册时**校验服务端 TLS 配置（缺少服务端证书等立即抛错），握手在管线最前完成。

## 单连接客户端（P4-1）

```csharp
await using var client = ChannelClientBuilder
    .Create(options =>
    {
        options.EndPoint = new IPEndPoint(IPAddress.Loopback, 8000);
        options.ConnectTimeout = TimeSpan.FromSeconds(5);
    })
    .Build();

await client.ConnectAsync(cancellationToken);

await client.Connection!.Writer.WriteAsync(bytes);
var read = await client.Connection.Reader.ReadAsync();
```

- `ConnectAsync` 幂等，并发调用共享一次连接尝试；`CloseAsync` / `DisposeAsync` 进入终态 `Closed`。
- 客户端只暴露字节 `Connection`，消息收发通过 P2 的 `Connection.AsSession<T>()`。
- `ChannelClientOptions.Tls` 目前只做配置校验；TLS 握手尚未接入，`Tls` 非空时 `ConnectAsync` 抛 `NotSupportedException`，不会回退明文。
- 连接池与 batch 见 P4-3 章节。

## 重连与退避（P4-2）

```csharp
var client = ChannelClientBuilder
    .Create(options =>
    {
        options.EndPoint = endPoint;
        options.Reconnect = new ChannelReconnectPolicy
        {
            MaxAttempts = 0,                 // 0 = 无限
            InitialDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(30),
            BackoffMultiplier = 2.0,
            JitterFactor = 0.2,
        };
    })
    .Build();
```

也可以用 `.WithReconnect(policy)` 单独设置重连策略（等价于在 `Create` 回调里赋值）。

- 首次连接失败与已连接断线共用同一套退避。
- 重连成功产生新的 `ChannelContext` 与新 `ConnectionId`；断开期间不缓冲、不重放消息。
- `MaxAttempts = 0` 时 `ConnectAsync` 可能一直等待，调用方必须传入可取消 token。
- 配置 TLS 时直接抛 `NotSupportedException`，调用方取消与配置错误不会重试。
- 连接池与 batch 见 P4-3 章节。

## 连接池（P4-3）

```csharp
await using var pool = ChannelClientBuilder
    .Create(options =>
    {
        options.EndPoint = new IPEndPoint(IPAddress.Loopback, 8000);
        options.ConnectTimeout = TimeSpan.FromSeconds(5);
    })
    .AsPool()
    .WithMaxSize(8)
    .WithRentTimeout(TimeSpan.FromSeconds(30))
    .Build();

var client = await pool.RentAsync(cancellationToken);
try
{
    // 使用 client.Connection
}
finally
{
    await pool.ReturnAsync(client);
}
```

`AsPool()` 把构建目标从单连接切换为连接池，客户端配置被继承到池配置。`WithMaxSize` 与 `WithRentTimeout` 在调用时校验参数（非正数或超过 1 天抛 `ArgumentOutOfRangeException`）。

- `MaxSize` 是已创建 + 正在创建 + 已租出的硬上限；池满时不再新建连接。
- 池满时 `RentAsync` 最多等待 `RentTimeout`，超时抛 `TimeoutException`；调用方取消抛 `OperationCanceledException`。
- 归还即转移所有权：重复归还、归还其他池的客户端都抛 `InvalidOperationException`。
- 断线的空闲客户端在归还或再次租借时被释放并替换；池不做空闲淘汰、负载均衡与消息重放。
- 池释放后 `RentAsync` 抛 `ObjectDisposedException`；已租出的客户端不被强制中断，归还时才释放。
- `IChannelClientFactory` 可替换创建方式，便于测试与后续 DI 接入。

## 批量写入（P4-3）

```csharp
await using var session = client.Connection!.AsLineTextSession();
await session.WriteBatchAsync(new[] { "alpha", "beta", "gamma" });
```

- 非空批次持有同一个写锁，按顺序编码并写出多帧，只调用一次 `FlushAsync`。
- 空批次不写、不 flush。
- 内置 session 走单次 flush 快路径；自定义 `IChannelSession<T>` 退化为顺序 `WriteAsync`。
- 批次失败会中止连接，不保证原子送达，接收方可能只看到前缀。

## 多服务器组合

```csharp
var http = ChannelServerBuilder
    .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Any, 8080))
    .Run(HttpHandler)
    .Build();

var mqtt = ChannelServerBuilder
    .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Any, 1883))
    .Run(MqttHandler)
    .Build();

await using var group = new ChannelServerGroup(http, mqtt);
await group.StartAsync(cancellationToken);

// group.Servers[0].LocalEndPoint / group.Servers[1].LocalEndPoint

await group.StopAsync(cancellationToken);
```

组按构造顺序串行启动；某个 server 启动失败时，已启动的 server 会按相反顺序停止，并重新抛出原始异常。
停机并行执行，所有失败汇总为 `AggregateException`；`DisposeAsync` 可重复调用。
`ChannelServerGroup` 是纯运行时组合类型，不依赖容器；组的启停由调用方直接驱动。

## 接入宿主

本库不提供 IoC 集成，也没有 `IHostedService` 适配器。需要挂到宿主时由调用方自行包装 runner：

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<SampleHandler>();
builder.Services.AddSingleton(sp =>
{
    var handler = sp.GetRequiredService<SampleHandler>();

    return ChannelServerBuilder
        .Create(o =>
        {
            o.EndPoint = new IPEndPoint(IPAddress.Any, 8000);
            o.ShutdownTimeout = TimeSpan.FromSeconds(10);
        })
        .Run(handler.HandleAsync)
        .WithLoggerFactory(sp.GetRequiredService<ILoggerFactory>())
        .WithServiceScopeFactory(sp.GetRequiredService<IServiceScopeFactory>())
        .Build();
});
builder.Services.AddHostedService<ChannelServerRunner>();

var host = builder.Build();
host.Run();
```

`ChannelServerRunner` 用 `IHostedService` 把启动/停机映射到 `ChannelServer`：

```csharp
internal sealed class ChannelServerRunner : IHostedService
{
    private readonly ChannelServer _server;

    public ChannelServerRunner(ChannelServer server) => _server = server;

    public Task StartAsync(CancellationToken ct) => _server.StartAsync(ct);

    public Task StopAsync(CancellationToken ct) => _server.StopAsync(CancellationToken.None);
}
```

- runner 必须先于会立即连接服务器的 hosted service 注册，保证绑定先完成。
- 每连接从传入的 `IServiceScopeFactory` 创建独立作用域，可在处理器中通过 `context.Services` 解析 Scoped 服务。
- 宿主 `HostOptions.ShutdownTimeout` 应大于 `ChannelServerOptions.ShutdownTimeout`，否则宿主会先截断停机等待。
- 停机时 runner 必须传 `CancellationToken.None`，否则宿主的停机截止时间会截断服务器自身的排空预算。

## 协议分发

```csharp
var dispatcher = new ProtocolDispatcher(selector);

var server = ChannelServerBuilder
    .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Any, 8080))
    .Run(dispatcher.HandleAsync)
    .Build();
```

`ProtocolDispatcher.HandleAsync` 与 `ChannelHandlerDelegate` 签名一致，可以直接作为终端处理器。
选择器只调用一次，必须只窥探 `context.Reader`，不得消费字节：返回结果时用 `AdvanceTo(buffer.Start, buffer.Start)`，等待半包时用 `AdvanceTo(buffer.Start, buffer.End)`。
选择器返回 null 或抛异常时，分发器用 `InvalidDataException` 中止连接；连接关闭导致的取消不会产生协议错误。
ALPN 在后续 change 提供。

## 同端口前缀路由

```csharp
var selector = new PrefixProtocolSelector(new[]
{
    new PrefixProtocolRule("HTTP/1."u8.ToArray(), httpProtocol),
    new PrefixProtocolRule(new byte[] { 0x10 }, mqttProtocol),
});

var server = ChannelServerBuilder
    .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Any, 8080))
    .Run(new ProtocolDispatcher(selector).HandleAsync)
    .Build();
```

规则按注册顺序 first-match；重叠前缀时先注册者优先，需要更长前缀优先时把它放在前面。
只收到半个前缀时选择器等待更多数据；EOF、无匹配或达到 `MaxPrefixSize` 时返回 null，由 `ProtocolDispatcher` 以 `InvalidDataException` 中止连接。
选择器只窥探不消费，协议 handler 仍能读到包括前缀在内的完整字节流。
`MaxPrefixSize` 默认 64，必须大于 0；规则前缀不能超过该值。

## 配置

`ChannelServerOptions` 覆盖 `EndPoint`、`Backlog`、`MaxConnections`、`NoDelay`、`KeepAlive`、`LingerState`、`ReuseAddress`、`DualMode`、`ShutdownTimeout`、`MaxReadBufferSize`、`MaxWriteBufferSize`、`CreateBoundListenSocket`。

- `MaxReadBufferSize` / `MaxWriteBufferSize` 是输入/输出 Pipe 的暂停阈值，默认分别为 1 MiB / 64 KiB，必须大于 0。
- `CreateBoundListenSocket` 由调用方负责创建、绑定和套接字选项；框架随后调用 `Listen`，失败时负责释放返回的 socket。
- 非法配置在**构建期**抛出 `ArgumentException`（`ChannelServerBuilder.Create` 立即校验），不会进入绑定阶段。

## 约束

- 一个连接一个 session；session 只在终端 handler 创建。
- 行文本示例的线格式是 `hello\n`；`Utf8StringCodec` 拒绝空行。
- 多字节 UTF-8 只在完整帧上解码，不会跨 `ReadAsync` 截断。
- 处理器不得 `Complete` 或释放 `Transport`，收尾由框架完成。
- 中间件只能包装 `Transport`，不得读取业务字节。
- 一个连接只有一个 Reader 消费者、一个 Writer 生产者。
- `ConnectionClosed` 令牌的取消发生在 scope 释放之前；通过 `Register` 注册的回调由 `CancellationTokenSource` 调度，不保证全部在 scope 释放前执行完毕。
- 核心库不得引入 `Microsoft.AspNetCore.App` FrameworkReference 或任何 `Microsoft.AspNetCore.*` 程序集。
- server / client / 连接池的构造函数均非公开，构建器是唯一公开构造入口。

## 构建与测试

```powershell
dotnet build src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj
dotnet test tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj
```

`Galosys.Foundation.Next.slnx` 因包含无法编译的 `Galosys.Foundation.Nats.Net`，不参与本项目验证。

## 阶段

P1.5a：组合式服务器入口，删除继承与泛型 DI 入口。P1.5b：`ChannelServerGroup` 与多服务器组合。P4-1（本阶段）：单连接 `ChannelClient`。P4-2（本阶段）：重连与指数退避。P4-3（本阶段）：连接池与批量写入。P3-1（本阶段）：TLS 传输适配器。P3-2（本阶段）：服务端 TLS `UseTls`。

本阶段：移除 IoC/Hosting 集成（`AddChannelServer` / `AddChannelServerGroup()` / `ChannelServerGroupHostedService` / `ChannelServerDefinition`），改用 `ChannelServerBuilder` / `ChannelClientBuilder` / `ChannelClientPoolBuilder` 作为唯一公开构造入口。

客户端 TLS、NamedPipe/Diagnostics 在后续阶段。
