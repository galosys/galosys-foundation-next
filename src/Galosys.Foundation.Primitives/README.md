# Galosys.Foundation.Primitives

服务端 socket 传输层的最小基架：核心类型位于 `Microsoft.Extensions.Hosting.Net`；`*Options` 位于 `Microsoft.Extensions.Options`。P1.5a 使用组合式 `ChannelServer`；P1.5b 增加 `ChannelServerGroup` 与多服务器宿主注册。

## 能力

- 自包含 socket transport：基于 `System.Net.Sockets` + `System.IO.Pipelines`，核心程序集不依赖 `Microsoft.AspNetCore.*` 或 Kestrel。
- `ChannelContext`：连接视图（`ConnectionId`、端点、`Transport`、`ConnectionClosed`、`Services`）；公开属性无 setter，`Items` 为连接内可变字典。
- `ChannelServer`：sealed 组合入口，通过构造函数接收 `ChannelServerOptions`、pipeline 配置回调和可选的 `ILoggerFactory` / `IServiceScopeFactory`。
- `ChannelServerGroup`：按构造顺序启动、失败时逆序回滚、并行停机并聚合异常、释放幂等。
- `ChannelServerDefinition` + `AddChannelServer` / `AddChannelServerGroup()`：把多个独立配置的 server 注册到宿主。
- 每连接独立 `IServiceScope`（构造 `ChannelServer` 时传入 `IServiceScopeFactory` 后生效），连接关闭令牌先于作用域释放。
- `Action<IChannelPipeline>`：中间件只包装 `Transport`，业务数据由终端处理器消费。
- 优雅停机：停止 accept → 等待在途连接 → `ShutdownTimeout` 后强制 abort。
- 传输层处理部分发送、输入/输出 Pipe 背压、TCP keep-alive、半关闭与连接重置。
- 正常关闭会先排空已提交输出，并短暂读空内核接收缓冲区后再关闭，避免带未读入站数据触发 RST。

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

示例 `NetServer.ConsoleApp` 只在终端 handler 创建行文本会话：

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

## 最小用法

```csharp
var server = new ChannelServer(
    new ChannelServerOptions
    {
        EndPoint = new IPEndPoint(IPAddress.Loopback, 8000),
    },
    pipeline => pipeline.Run(HandleAsync),
    loggerFactory);

await server.RunAsync(cancellationToken);
```

`StartAsync` 只负责绑定、监听并启动接受循环，返回即可连接；`RunAsync` 是“启动并阻塞到停机”的兼容入口。`pipeline` 回调在启动时执行一次，必须调用 `Run`。

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

## 多服务器宿主注册

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddChannelServer(new ChannelServerDefinition(
    "http",
    new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Any, 8080) },
    pipeline => pipeline.Run(HttpHandler)));

builder.Services.AddChannelServer(new ChannelServerDefinition(
    "mqtt",
    new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Any, 1883) },
    pipeline => pipeline.Run(MqttHandler)));

builder.Services.AddChannelServerGroup();

var host = builder.Build();
host.Run();
```

注册顺序就是启动顺序；`ChannelServerGroupHostedService` 在宿主启动时按该顺序启动组，停机时并行停止。
重复调用 `AddChannelServerGroup()` 是幂等的；只注册定义而不调用它时，定义不会监听。
每个定义保留自己的 `ChannelServerOptions`，不存在全局 `ChannelServerOptions` 注册。
每连接作用域来自容器的 `IServiceScopeFactory`；宿主 `ShutdownTimeout` 应大于最大的 server `ShutdownTimeout`。

## 宿主接入

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<SampleHandler>();
builder.Services.AddSingleton(sp =>
{
    var options = new ChannelServerOptions
    {
        EndPoint = new IPEndPoint(IPAddress.Any, 8000),
        ShutdownTimeout = TimeSpan.FromSeconds(10),
    };
    var handler = sp.GetRequiredService<SampleHandler>();

    return new ChannelServer(
        options,
        pipeline => pipeline.Run(handler.HandleAsync),
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<IServiceScopeFactory>());
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

- 不提供泛型 `AddChannelServer<T>`；多服务器宿主托管使用 `AddChannelServer(definition)` + `AddChannelServerGroup()`，options 仍由调用方为每个定义单独创建。
- runner 必须先于会立即连接服务器的 hosted service 注册，保证绑定先完成。
- 每连接从传入的 `IServiceScopeFactory` 创建独立作用域，可在处理器中通过 `context.Services` 解析 Scoped 服务。
- 宿主 `HostOptions.ShutdownTimeout` 应大于 `ChannelServerOptions.ShutdownTimeout`，否则宿主会先截断停机等待。
- 未配置 `EndPoint` 或配置非法时，`StartAsync()` 抛出 `ArgumentException`，不会进入监听状态。

## 配置

`ChannelServerOptions` 覆盖 `EndPoint`、`Backlog`、`MaxConnections`、`NoDelay`、`KeepAlive`、`LingerState`、`ReuseAddress`、`DualMode`、`ShutdownTimeout`、`MaxReadBufferSize`、`MaxWriteBufferSize`、`CreateBoundListenSocket`。

- `MaxReadBufferSize` / `MaxWriteBufferSize` 是输入/输出 Pipe 的暂停阈值，默认分别为 1 MiB / 64 KiB，必须大于 0。
- `CreateBoundListenSocket` 由调用方负责创建、绑定和套接字选项；框架随后调用 `Listen`，失败时负责释放返回的 socket。
- 非法配置在绑定前抛出 `ArgumentException`。

## 约束

- 一个连接一个 session；session 只在终端 handler 创建。
- 行文本示例的线格式是 `hello\n`；`Utf8StringCodec` 拒绝空行。
- 多字节 UTF-8 只在完整帧上解码，不会跨 `ReadAsync` 截断。
- 处理器不得 `Complete` 或释放 `Transport`，收尾由框架完成。
- 中间件只能包装 `Transport`，不得读取业务字节。
- 一个连接只有一个 Reader 消费者、一个 Writer 生产者。
- `ConnectionClosed` 令牌的取消发生在 scope 释放之前；通过 `Register` 注册的回调由 `CancellationTokenSource` 调度，不保证全部在 scope 释放前执行完毕。
- 核心库不得引入 `Microsoft.AspNetCore.App` FrameworkReference 或任何 `Microsoft.AspNetCore.*` 程序集。

## 构建与测试

```powershell
dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj
dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj
```

`Galosys.Foundation.Next.slnx` 因包含无法编译的 `Galosys.Foundation.Nats.Net`，不参与本项目验证。

## 阶段

P1.5a：组合式服务器入口，删除继承与泛型 DI 入口。P1.5b（本阶段）：`ChannelServerGroup` 与多服务器 IoC 注册。消息层、TLS、客户端、NamedPipe/Diagnostics 在后续阶段。
