# Galosys.Foundation.Primitives

服务端 socket 传输层的最小基架：核心类型位于 `Microsoft.Extensions.Hosting.Net`；`*Options` 位于 `Microsoft.Extensions.Options`。P1.5a 使用组合式 `ChannelServer`，不提供库内 DI 注册。

## 能力

- 自包含 socket transport：基于 `System.Net.Sockets` + `System.IO.Pipelines`，核心程序集不依赖 `Microsoft.AspNetCore.*` 或 Kestrel。
- `ChannelContext`：连接视图（`ConnectionId`、端点、`Transport`、`ConnectionClosed`、`Services`）；公开属性无 setter，`Items` 为连接内可变字典。
- `ChannelServer`：sealed 组合入口，通过构造函数接收 `ChannelServerOptions`、pipeline 配置回调和可选的 `ILoggerFactory` / `IServiceScopeFactory`。
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

- 不提供 `AddChannelServer<T>`；options 由调用方为每个 `ChannelServer` 单独创建。
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

P1.5a（本阶段）：组合式服务器入口，删除继承与泛型 DI 入口。多服务器托管、IoC 注册在 P1.5b；消息层、TLS、客户端、NamedPipe/Diagnostics 在后续阶段。
