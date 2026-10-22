# Galosys.Foundation.Primitives

服务端 socket 传输层的最小基架，命名空间 `Microsoft.Extensions.Hosting.Net`。

## 能力

- 自包含 socket transport：基于 `System.Net.Sockets` + `System.IO.Pipelines`，核心程序集不依赖 `Microsoft.AspNetCore.*` 或 Kestrel。
- `ChannelContext`：连接视图（`ConnectionId`、端点、`Transport`、`ConnectionClosed`、`Services`）；公开属性无 setter，`Items` 为连接内可变字典。
- 每连接独立 `IServiceScope`（宿主集成注入 `IServiceScopeFactory` 后生效），连接关闭令牌先于作用域释放。
- `ChannelServerBase.Configure(IChannelPipeline)`：中间件只包装 `Transport`，业务数据由终端处理器消费。
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
public sealed class EchoServer : ChannelServerBase
{
    public EchoServer(EndPoint endPoint) : base(endPoint) { }

    protected override async Task ChannelHandleAsync(ChannelContext context)
    {
        while (true)
        {
            var result = await context.Reader.ReadAsync();
            if (result.IsCompleted && result.Buffer.IsEmpty) break;

            foreach (var segment in result.Buffer)
                await context.Writer.WriteAsync(segment);

            context.Reader.AdvanceTo(result.Buffer.End);
            await context.Writer.FlushAsync();
        }
    }
}

var server = new EchoServer(new IPEndPoint(IPAddress.Loopback, 8000));
await server.RunAsync(cancellationToken);
```

`StartAsync` 只负责绑定、监听并启动接受循环，返回即可连接；`RunAsync` 是“启动并阻塞到停机”的兼容入口。

## 宿主接入

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddChannelServer<MyServer>(options =>
{
    options.EndPoint = new IPEndPoint(IPAddress.Any, 8000);
    options.ShutdownTimeout = TimeSpan.FromSeconds(10);
});

var host = builder.Build();
host.Run();
```

- `MyServer` 的构造函数必须能解析 `ChannelServerOptions`，例如 `(ChannelServerOptions, ILogger<MyServer>)`。
- 每连接从容器创建独立 `IServiceScope`，可在处理器中通过 `context.Services` 解析 Scoped 服务。
- 宿主 `HostOptions.ShutdownTimeout` 应大于 `ChannelServerOptions.ShutdownTimeout`，否则宿主会先截断停机等待。
- 未配置 `EndPoint` 或配置非法时，`host.StartAsync()` 抛出 `ArgumentException`，不会进入监听状态。

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

P1（本阶段）：服务端内核、Options、管线、每连接 scope、优雅停机。消息层、TLS、客户端、NamedPipe/Diagnostics 在后续阶段。
