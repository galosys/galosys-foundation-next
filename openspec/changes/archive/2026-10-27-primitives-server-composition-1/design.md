## Context

`ChannelServerBase` 是 P1 唯一的服务器实现，约 500 行，负责 accept loop、连接跟踪、每连接 scope、优雅停机和管线配置。它的 `protected virtual Configure` 与 `protected abstract ChannelHandleAsync` 把业务处理和生命周期绑在继承上：同一个服务器类无法直接跑两个端口，多个 server 只能靠派生多个子类。

P1.5 的目标是用组合替代继承，但直接删除基类会同时打断 sample、tests 和 `AddChannelServer<T>`。本 change 是过渡步骤：先把生命周期内核抽到 internal `ChannelServerCore`，新增 sealed `ChannelServer` 作为组合式入口，同时让 `ChannelServerBase` 变成薄适配器，保持现有消费方编译和行为不变。删除基类与 DI 入口留给 `primitives-server-composition-2`。

## Goals / Non-Goals

**Goals:**

- 把 accept loop、连接跟踪、每连接 scope、优雅停机和释放逻辑从 `ChannelServerBase` 抽到 `ChannelServerCore`，只保留一份实现。
- 新增 public sealed `ChannelServer`，通过构造函数接收 options、pipeline 配置、可选 logger/scope，不需要继承。
- 保持 `ChannelServerBase` 的公共 API、sample、tests、`AddChannelServer<T>` 在本 change 中继续可用，并且行为等价。
- 用新增测试锁定 `ChannelServer` 的生命周期、pipeline 配置和独立 options。

**Non-Goals:**

- 不删除 `ChannelServerBase`、`AddChannelServer<T>`、`ChannelServerHostedService<T>`。
- 不迁移 `NetServer.ConsoleApp`。
- 不添加 `ChannelServerGroup`、多服务器启停、IoC 注册或 builder。
- 不改 `IChannelServer`、`ChannelContext`、pipeline、transport 契约。
- 不改 P2 的 framer/codec/session。

## Decisions

### 1. 抽取 `ChannelServerCore`，而不是复制生命周期代码

`ChannelServerCore` 是 internal sealed 类，构造函数接收：

```csharp
internal ChannelServerCore(
    ChannelServerOptions options,
    Action<IChannelPipeline> configurePipeline,
    ILoggerFactory? loggerFactory,
    IServiceScopeFactory? scopeFactory)
```

它拥有原先 `ChannelServerBase` 的全部字段和方法：options 克隆/校验、start lock、listener、active connections/tasks、accept loop、connection processing、stop/dispose、shutdown timeout 和日志。`ChannelServer` 与 `ChannelServerBase` 都只持有 core 并转发。

**为什么不用两个实现：** 生命周期逻辑很细（停机重入、scope 释放顺序、accept 失败阈值），复制两份会让后续修复分叉。抽取后 `ChannelServerBase` 仅作为兼容外观，行为自然一致。

**替代方案：** 让 `ChannelServer` 继承 `ChannelServerBase` 并只暴露公共构造。这样不能移除继承，也解决不了基类占用 `Configure` 扩展点的问题，否决。

### 2. `ChannelServerBase` 降级为适配器

`ChannelServerBase` 保留现有构造函数、`Configure`、`ChannelHandleAsync`、`LocalEndPoint`、`StartAsync`/`RunAsync`/`StopAsync`/`Dispose` 签名，但内部字段全部移到 core。基类构造函数创建 core，并把 `Configure` 作为 `Action<IChannelPipeline>` 传入：

```csharp
_core = new ChannelServerCore(options, Configure, loggerFactory: null, scopeFactory: null);
```

`Configure` 仍然只在 `StartAsync` 时被调用；默认实现继续调用 `pipeline.Run(ChannelHandleAsync)`。`ScopeFactory` / `LoggerFactory` 这两个 internal 属性转发到 core，因此 `AddChannelServer<T>` 和 hosted service 无需修改。

**为什么不在本 change 删除基类：** 本 change 的验收标准是“新增入口 + 旧路径不变”。把破坏性删除拆到下一个 change，可以让 core 抽取的回归和 API 删除的回归分别定位。

### 3. `ChannelServer` 是 sealed 组合入口

```csharp
public sealed class ChannelServer : IChannelServer
{
    public ChannelServer(
        ChannelServerOptions options,
        Action<IChannelPipeline> configurePipeline,
        ILoggerFactory? loggerFactory = null,
        IServiceScopeFactory? scopeFactory = null);
}
```

- `configurePipeline` 是完整的 pipeline 配置回调，可以调用 `Use` 和 `Run`；不能只传终端 handler，否则 TLS/遥测/超时中间件没有挂载点。
- 不提供 `(options, ChannelHandlerDelegate)` 重载，避免 lambda 在 `IChannelPipeline` 与 `ChannelContext` 之间产生重载歧义；调用方写 `pipeline => pipeline.Run(handler)` 即可。
- `loggerFactory` / `scopeFactory` 可空；为空时 core 使用无日志和 `EmptyServiceScope`，与手工构造基类的行为一致。
- options 在 core 构造时克隆；两个 `ChannelServer` 实例不会共享同一个 options 对象。

### 4. pipeline 配置与失败语义

- `configurePipeline` 在 `StartAsync` 内、绑定 listener 之前执行。
- 回调必须调用 `Run`；`ChannelPipeline.Build()` 会在缺少终端处理器时抛 `InvalidOperationException`，启动失败且不留下 listener。
- pipeline 配置抛异常时，本次启动失败；core 保持未启动状态，允许调用方修正后重试。
- 回调只在每次实际启动尝试时执行一次；已启动的 server 重复调用 `StartAsync` 直接返回，不重复构建。

### 5. 本 change 不动 DI 与示例

`AddChannelServer<T>` 仍要求 `TChannelServer : ChannelServerBase`，`ChannelServerHostedService<T>` 仍通过基类的 internal 属性注入 scope/logger。这样 change 1 合入后，现有宿主测试、sample 和 README 的宿主章节保持有效；change 2 再统一迁移。

## Risks / Trade-offs

- **core 抽取引入回归** → 机械搬运现有字段和方法，不改变控制流；先跑现有 67 个测试，再加新测试。
- **基类适配器的虚方法时序** → `Configure` 只在 `StartAsync` 调用，不在构造函数调用；基类构造函数只保存委托，不触发虚方法。
- **`ChannelServer` 与基类行为分叉** → 两者共享同一个 core；测试同时覆盖新入口和旧入口的 echo/stop。
- **pipeline 配置重试语义** → 明确“每次启动尝试构建一次”；用缺少 `Run` 的测试锁定启动失败且 `LocalEndPoint` 仍为 null。
- **本 change 暂时保留两套公开入口** → 接受短期重复；change 2 删除基类，避免长期双轨。

## Migration Plan

1. 新增 `ChannelServerCore.cs`，把 `ChannelServerBase` 的生命周期字段和方法搬入；保持方法名、日志文本和异常类型不变。
2. 新增 `ChannelServer.cs`，实现 `IChannelServer` 并全部转发 core。
3. 修改 `ChannelServerBase.cs`：保留公共/受保护签名，删除重复字段和实现，改为持有 core 并转发；internal scope/logger 属性转发到 core。
4. 新增 `ChannelServerTests.cs`，覆盖组合入口；现有测试不改。
5. 更新 `src/Galosys.Foundation.Primitives/README.md`，增加组合式用法，保留继承式用法。
6. 运行 Primitives 构建、NetServer 构建和全量测试。若回归，回滚本 change 的三个源文件和测试/README 修改即可；基类旧实现仍在 git 历史中。

## Open Questions

无。`ChannelServerGroup`、多服务器和 IoC 注册明确留给后续 change。
