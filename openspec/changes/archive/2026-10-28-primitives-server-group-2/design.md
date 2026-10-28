## Context

`primitives-server-group-1` 已经提供 `ChannelServerGroup`，但组本身只是纯组合类型：调用方要自己 `new ChannelServer`、自己把组接进宿主、自己处理启动失败。P1.5a 删除了旧的 `AddChannelServer<T>` 后，仓库没有库内多服务器注册入口；如果每个示例都手写工厂，宿主启动顺序、options 隔离和 scope 传播会重复实现。

本 change 在组之上增加一层很薄的宿主/IoC 装配：`ChannelServerDefinition` 描述一个 server，`AddChannelServer(definition)` 收集定义，`AddChannelServerGroup()` 用容器的 `ILoggerFactory` / `IServiceScopeFactory` 创建组，`ChannelServerGroupHostedService` 把组的启停映射到宿主生命周期。组的内核语义不变。

## Goals / Non-Goals

**Goals:**

- 提供 public `ChannelServerDefinition`，把名称、独立 options 和 pipeline 配置绑定为一个不可变描述。
- 提供 `AddChannelServer(ChannelServerDefinition)` 与 `AddChannelServerGroup()` 两个最小 DI 入口。
- 从所有已注册定义构建一个 `ChannelServerGroup`，为每个 `ChannelServer` 传入容器的 `ILoggerFactory` 和 `IServiceScopeFactory`。
- 明确注册顺序、重复注册、缺少组注册和启动失败的语义。
- 用宿主测试锁定双定义、独立 options、scope 传播、失败清理和注册行为。

**Non-Goals:**

- 不改变 `ChannelServerGroup` 的启动/停机/释放算法。
- 不恢复 `AddChannelServer<T>`、`ChannelServerHostedService<T>` 或泛型服务器基类。
- 不注册全局 `ChannelServerOptions`，不支持“默认 options + 命名覆盖”。
- 不做配置文件绑定、`IOptions<T>`、`ChannelServerBuilder` 或动态增删定义。
- 不迁移 `NetServer.ConsoleApp`；sample 仍可使用自定义 `IHostedService` runner。
- 不做同端口协议选择、TLS、ALPN、消息层或客户端。

## Decisions

### 1. `ChannelServerDefinition` 是不可变描述，不是服务器工厂

```csharp
public sealed class ChannelServerDefinition
{
    public ChannelServerDefinition(
        string name,
        ChannelServerOptions options,
        Action<IChannelPipeline> configurePipeline);

    public string Name { get; }
    public ChannelServerOptions Options { get; }
    public Action<IChannelPipeline> ConfigurePipeline { get; }
}
```

- 构造函数校验 `name` 非空白、`options` 非 null、`configurePipeline` 非 null。
- `Options` 在构造时调用 `Clone()` 快照，避免注册后外部继续修改同一个对象；每个 `ChannelServer` 构造时还会再克隆一次。
- `Name` 只用于诊断、日志和测试可读性，不参与端点选择，也不要求全局唯一。
- 定义不实现 `IChannelServer`，不持有 socket、logger 或 scope；它只是“如何创建一个 server”的数据。

**为什么不在定义里创建 `ChannelServer`：** 定义应能在 `IServiceCollection` 阶段安全注册，此时容器尚未构建；把创建推迟到组工厂，才能拿到 `ILoggerFactory` 和 `IServiceScopeFactory`。

### 2. `AddChannelServer` 只追加定义，不注册全局 options

```csharp
public static IServiceCollection AddChannelServer(
    this IServiceCollection services,
    ChannelServerDefinition definition)
{
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(definition);

    services.AddSingleton(definition);
    return services;
}
```

- 每个定义是独立的 `ChannelServerDefinition` 单例；多次调用保留注册顺序。
- 不调用 `services.AddSingleton<ChannelServerOptions>(...)`；这是 P1.5a 多服务器串配置的根因。
- 重复注册同一个定义实例会得到两个 server；这是调用方的选择，框架不做去重。需要去重时由调用方自己控制。
- 定义只在 `AddChannelServerGroup()` 的组工厂解析时被读取；没有组注册时，它们不会产生任何 socket。

### 3. `AddChannelServerGroup` 创建单个组和单个 hosted service

```csharp
public static IServiceCollection AddChannelServerGroup(this IServiceCollection services)
{
    ArgumentNullException.ThrowIfNull(services);

    services.TryAddSingleton(sp =>
    {
        var definitions = sp.GetServices<ChannelServerDefinition>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var servers = definitions
            .Select(definition => new ChannelServer(
                definition.Options,
                definition.ConfigurePipeline,
                loggerFactory,
                scopeFactory))
            .ToArray();

        return new ChannelServerGroup(servers);
    });

    services.TryAddEnumerable(
        ServiceDescriptor.Singleton<IHostedService, ChannelServerGroupHostedService>());

    return services;
}
```

- `TryAddSingleton` 保证重复调用只注册一个 `ChannelServerGroup`。
- `TryAddEnumerable` 按实现类型去重，重复调用只注册一个 `IHostedService`。
- `IEnumerable<ChannelServerDefinition>` 的顺序就是 `AddChannelServer` 的调用顺序，也就是组的启动顺序。
- 没有定义时仍会创建空组；宿主启动/停机是 no-op。这样“先注册组、后加定义”的代码顺序不会导致空引用，但所有定义仍必须在 `Build()` 前注册。
- 缺少 `ILoggerFactory` 或 `IServiceScopeFactory` 时，组工厂在解析阶段抛 DI 异常；正常 `Host.CreateApplicationBuilder` 两者都存在。

**为什么用 `TryAddSingleton` 而不是 `AddSingleton`：** 重复注册组会让宿主启动两个 hosted service、共享同一个组或创建两个组，行为取决于注册顺序。幂等注册更符合扩展方法的预期，也便于模块化装配。

### 4. `ChannelServerGroupHostedService` 只做生命周期映射

```csharp
public sealed class ChannelServerGroupHostedService : IHostedService, IAsyncDisposable
{
    private readonly ChannelServerGroup _group;

    public ChannelServerGroupHostedService(ChannelServerGroup group);

    public Task StartAsync(CancellationToken cancellationToken)
        => _group.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
        => _group.StopAsync(CancellationToken.None);

    public ValueTask DisposeAsync() => _group.DisposeAsync();
}
```

- `StartAsync` 直接传播组的异常；组已经负责回滚，宿主不会留下半启动的 server。
- `StopAsync` 传 `CancellationToken.None`，让每个 server 按自己的 `ShutdownTimeout` 完成停机；否则宿主停机超时会截断组的等待。
- `DisposeAsync` 转发到组；组自身幂等，容器即使同时释放 hosted service 和组单例也不会重复释放。
- hosted service 不持有 definitions，也不重新创建 server；所有创建逻辑集中在组工厂。

### 5. 注册顺序、缺失与重复的文档化行为

| 场景 | 行为 |
|---|---|
| `AddChannelServer(A)`、`AddChannelServer(B)`、`AddChannelServerGroup()` | 组内顺序为 A、B；启动顺序为 A、B |
| 只调用 `AddChannelServer(...)` | 定义保持惰性；没有 hosted service，宿主启动后不监听 |
| 调用两次 `AddChannelServerGroup()` | 只注册一个组和一个 hosted service；不会重复绑定 |
| `AddChannelServerGroup()` 在定义之前调用 | 组在解析时读取定义，最终仍按定义注册顺序构建；但所有定义必须在 `Build()` 前注册 |
| 组启动时第二个定义绑定失败 | 组停止第一个，`IHost.StartAsync` 抛出原始异常；第一个不再监听 |

### 6. 每个连接仍使用容器的 scope 工厂

组工厂把同一个 `IServiceScopeFactory` 传给每个 `ChannelServer`。`ChannelServerCore` 在每条连接上创建独立 scope，因此：

- 不同 server 的连接共享同一个根容器，但各自获得独立 scoped 实例；
- 同一 server 的不同连接也互不共享 scoped 实例；
- 连接关闭时 scope 释放顺序仍由 `ChannelServerCore` 保证，组和 hosted service 不介入。

### 7. 本 change 不迁移 sample

`NetServer.ConsoleApp` 目前使用自定义 runner 或组合式 `ChannelServer`。新 API 是可选装配方式；本 change 只增加库能力和 README，示例迁移留给后续 change，避免把“注册 API”与“示例行为回归”混在一起。

## Risks / Trade-offs

- **定义 options 被外部修改** → 定义构造时克隆，`ChannelServer` 再克隆；文档要求把定义视为不可变。
- **重复注册导致双绑定** → `AddChannelServerGroup` 用 `TryAddSingleton` / `TryAddEnumerable` 幂等；重复定义仍由调用方负责。
- **宿主停机超时截断** → hosted service 的 `StopAsync` 传 `None`，但宿主 `HostOptions.ShutdownTimeout` 仍可能先到期；README 要求宿主超时大于最大 server `ShutdownTimeout`。
- **组启动失败后宿主状态** → 组回滚并重抛原始异常；宿主不会报告启动成功。测试覆盖第二个绑定失败。
- **Scope 工厂共享** → 共享根容器是预期行为；每个连接仍有独立 scope。测试覆盖两个 server 的连接获得不同实例。
- **没有全局 options** → 调用方必须为每个定义创建 options；这是有意的显式性，避免串配置。

## Migration Plan

1. 新增 `ChannelServerDefinition.cs`，实现校验、options 快照和只读属性。
2. 新增 `ChannelServerGroupHostedService.cs`，只转发启动、停机和释放。
3. 新增 `ChannelServerGroupServiceCollectionExtensions.cs`，实现 `AddChannelServer`、组工厂和幂等 hosted service 注册。
4. 新增 `ServerGroupHostingTests.cs`，先覆盖双定义宿主启停，再补独立 options、缺失/重复注册、scope 和失败清理。
5. 更新 Primitives README 的“多服务器宿主注册”章节。
6. 运行 Primitives 构建、全量测试和 `openspec validate`。

回滚策略：删除三个新源文件、测试和 README 段落；`ChannelServerGroup` 与现有单服务器路径不受影响。

## Open Questions

无。动态定义、配置绑定、命名 options 和 sample 迁移明确不在本 change。
