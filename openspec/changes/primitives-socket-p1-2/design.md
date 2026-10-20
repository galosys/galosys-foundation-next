## Context

`primitives-socket-p1-1` 交付了 `ChannelServerBase` + `KestrelChannelListener` + 管线 + Options + 每连接 scope 的内部注入点（`internal IServiceScopeFactory? ScopeFactory`、`internal ILoggerFactory? LoggerFactory`）。但宿主仍要手工完成：

- 在 `BackgroundService` 中创建 scope 并调用 `RunAsync`（`NetServer.ConsoleApp/ListenerBootstrapper.cs` 当前就是这样，且一个 scope 覆盖服务器整个生命周期）。
- 没有 `AddChannelServer`，服务器与容器生命周期脱节，停机依赖 hosted service 的取消令牌。
- `SampleChannelServer` 构造函数接收 `EndPoint`，无法直接使用 `ChannelServerOptions`。

本变更补齐 P1 的宿主层，并把示例迁移到新 API。

## Goals / Non-Goals

**Goals:**

- 提供 `AddChannelServer<TChannelServer>`，把 Options、服务器单例、hosted service 一次注册。
- 宿主启动完成前完成绑定；宿主停机遵守服务器 `ShutdownTimeout`。
- 每连接 scope 来自宿主容器，Scoped 服务在连接内可解析、连接结束即释放。
- `NetServer.ConsoleApp` 行为等价迁移，删除手工 scope 包裹。
- 更新 Primitives/NetServer 文档与根 `AGENTS.md`。

**Non-Goals:**

- 不做客户端、消息层、TLS、NamedPipe、Diagnostics。
- 不改变 `channel-server-transport` / `channel-pipeline` 的既有规范。
- 不引入 `Microsoft.Extensions.Options` 的 `IValidateOptions` 体系；Options 仍是普通 POCO + 显式校验。
- 不做多服务器实例托管（一个 `AddChannelServer` 调用对应一个服务器）。

## Decisions

### D1 泛型注册，要求服务器构造函数接收 ChannelServerOptions

```csharp
public static IServiceCollection AddChannelServer<TChannelServer>(
    this IServiceCollection services,
    Action<ChannelServerOptions>? configure = null)
    where TChannelServer : ChannelServerBase
```

注册内容：

```csharp
services.AddSingleton(sp =>
{
    var options = new ChannelServerOptions();
    configure?.Invoke(options);
    options.Validate();                       // 在解析（宿主启动）时 fail-fast
    return options;
});
services.AddSingleton<TChannelServer>();
services.AddSingleton<IChannelServer>(sp => sp.GetRequiredService<TChannelServer>());
services.AddHostedService<ChannelServerHostedService<TChannelServer>>();
```

约束：`TChannelServer` 必须有一个可被 DI 解析的构造函数，且参数包含 `ChannelServerOptions`。示例的 `SampleChannelServer` 由 `(EndPoint, ILogger<T>)` 改为 `(ChannelServerOptions, ILogger<T>)`。

替代方案：非泛型 `AddChannelServer(Type, ...)`（反射、可读性差）或要求传入工厂（调用方仍要理解 scope 注入点）。泛型 + 约定构造最简单。

校验放在 Options 工厂内而不是 `AddChannelServer` 调用时：满足“注册不失败、启动失败”，也让 `Host.Build()` 不因宿主尚未启动就抛异常。

### D2 Hosted service 只做生命周期映射与内部注入

```csharp
internal sealed class ChannelServerHostedService<TChannelServer> : IHostedService
    where TChannelServer : ChannelServerBase
{
    private readonly TChannelServer _server;

    public ChannelServerHostedService(
        TChannelServer server,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory)
    {
        _server = server;
        server.ScopeFactory = scopeFactory;
        server.LoggerFactory = loggerFactory;
    }

    public Task StartAsync(CancellationToken ct) => _server.StartAsync(ct);
    public Task StopAsync(CancellationToken ct) => _server.StopAsync(ct);
}
```

- `StartAsync` 抛出绑定异常 → 宿主启动失败，符合 fail-fast。
- `StopAsync` 交给服务器的 `StopCoreAsync` 处理“停止 accept → 等待 → 超时 abort”。宿主自身的 `HostOptions.ShutdownTimeout`（默认 30s）应大于服务器 `ShutdownTimeout`（默认 10s），文档写明。
- 服务器作为单例由容器释放；`ChannelServerBase.DisposeAsync` 已幂等。
- 不缓存 `ct`：`IHostedService.StopAsync` 的令牌只用于取消等待，不传给服务器内部停机逻辑（避免宿主超时把服务器置于半停机状态）。

### D3 每连接 scope 由容器工厂创建

`ChannelServerHostedService` 在构造时把容器的 `IServiceScopeFactory` 写入 `_server.ScopeFactory`。p1-1 的 `AcceptLoopAsync` 已实现：

```
scope = ScopeFactory?.CreateScope() ?? EmptyServiceScope.Instance
context = new ChannelContext(id, connection, scope.ServiceProvider)
```

因此无需改内核；本变更只验证“容器注册 → 连接作用域”链路，并锁定 Scoped 服务在不同连接间隔离、连接结束被释放。

注意：hosted service 构造函数在宿主启动时执行，早于 `StartAsync`，因此 `ScopeFactory` 在 accept 循环启动前已就位。

### D4 示例迁移

`Program.cs`：

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddChannelServer<SampleChannelServer>(options =>
{
    options.EndPoint = new IPEndPoint(IPAddress.Any, 8000);
});
builder.Services.AddHostedService<SenderBootstrapper>();

var host = builder.Build();
host.Run();
```

- 删除 `ListenerBootstrapper.cs`（其 `SampleChannelServer` 迁到独立文件）。
- `SampleChannelServer` 的处理器逻辑（读取、UTF-8 解码、打印）保持不变。
- `SenderBootstrapper` 保持裸 `Socket` 客户端（P4 才引入客户端抽象）。
- 停机由宿主触发 `ChannelServerHostedService.StopAsync`，`RunAsync` 的取消路径不再由示例手工处理。

### D5 测试策略

测试项目在 p1-1 基础上增加 `<FrameworkReference Include="Microsoft.AspNetCore.App" />`，以便使用 `Host.CreateApplicationBuilder`、`IServiceCollection`。覆盖：

- 注册：同一实例、`IChannelServer` 指向同一对象、注册阶段不校验。
- 生命周期：`Host.StartAsync` 后立即可连接；`Host.StopAsync` 等待在途；端口占用与缺 EndPoint 使宿主启动失败。
- 作用域：两个连接解析到不同 `ScopedProbe` 实例；连接结束后 `Dispose` 被调用。
- 容器释放：`host.Dispose()` 后服务器停止，端口可被重新绑定。

端口统一用 `127.0.0.1:0`；等待使用 `TaskCompletionSource` + `WaitAsync`，不使用轮询式 `Task.Delay`（除 p1-1 已有的短等待辅助）。

### D6 文档与 AGENTS

- `src/Galosys.Foundation.Primitives/README.md`：补“宿主接入”章节（`AddChannelServer`、ShutdownTimeout 与 HostOptions 的关系、每连接 scope）。
- `src/NetServer.ConsoleApp/README.md`：说明示例行为、如何运行、Ctrl+C 停机。
- 根 `AGENTS.md`：
  - 删除“仓库没有测试”的表述，新增 `tests/Galosys.Foundation.Primitives.Tests` 与 `dotnet test` 命令。
  - 补充 Primitives 现在依赖 `Microsoft.AspNetCore.App`、NetServer 通过 `AddChannelServer` 托管。
  - 保留 `MafSample.slnx` 不可构建、API 需外部基础设施等既有约束。

## Risks / Trade-offs

- **`TChannelServer` 构造函数约定是隐式的** → 用 XML 文档与示例固定；DI 构造失败会在宿主启动时报“无法解析服务”，信息足够定位。
- **宿主 `ShutdownTimeout` 小于服务器 `ShutdownTimeout` 会截断等待** → README 明确要求 `HostOptions.ShutdownTimeout > ChannelServerOptions.ShutdownTimeout`；示例保持默认 30s > 10s。
- **`AddHostedService` 注册 internal 类型** → 同一程序集内注册，DI 通过公共构造函数实例化；如遇可见性问题改为 public sealed（测试会暴露）。
- **Options 工厂在解析时校验，可能被非 hosted service 提前解析** → 校验是幂等的；提前解析后仍会在启动时抛。
- **示例迁移改变启动路径** → 通过手工运行冒烟验证（每秒 `hello`、Ctrl+C 退出）；行为断言写在 `src/NetServer.ConsoleApp/README.md`。

## Migration Plan

1. 新增加宿主层（`Hosting/ChannelServerHostedService.cs`、`Hosting/ServiceCollectionExtensions.cs`）。
2. 测试先红后绿：注册、生命周期、作用域、容器释放。
3. 迁移 `NetServer.ConsoleApp`：拆出 `SampleChannelServer.cs`、删除 `ListenerBootstrapper.cs`、改写 `Program.cs`。
4. 更新 Primitives README、新建 NetServer README、更新 `AGENTS.md`。
5. 验证命令：三个 `dotnet build`/`dotnet test`，再加一次 `dotnet run --project src/NetServer.ConsoleApp` 手工冒烟。

回滚：删除新增 `Hosting/` 两个文件与测试，恢复 `Program.cs`/`ListenerBootstrapper.cs`，文档回退；无数据影响。

## Open Questions

无。依赖 p1-1 已冻结的 `ChannelServerBase` 内部注入点，不需要再改内核。
