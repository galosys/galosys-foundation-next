## Context

P1 的传输层由 `SocketChannelListener` 与 `SocketChannelConnection` 组成，两者都实现 internal 的 `IChannelListener` / `IChannelConnection`。P1.5a 把 accept loop、连接跟踪、每连接 scope、优雅停机和释放逻辑集中到 internal `ChannelServerCore`，但 `StartAsync` 仍然直接调用 `SocketChannelListener.BindAsync`：

```csharp
var listener = await SocketChannelListener.BindAsync(_options, LoggerFactory, ct).ConfigureAwait(false);
```

这带来两个限制：

1. 新增 transport 必须修改核心类，无法只替换 listener；
2. `ChannelServerOptions.EndPoint` 没有表达“这是 TCP 还是命名管道”的位置，`NamedPipeServerStream` 需要的是管道名而不是 socket 地址。

P5 要补上 NamedPipe 服务端。NamedPipe 与 TCP 的差异集中在绑定和连接创建：accept 循环、`ChannelContext`、每连接 scope、`MaxConnections`、停机与取消都应复用 `ChannelServerCore`，不应复制第二套生命周期代码。

## Goals / Non-Goals

**Goals:**

- 提供 public 的传输选择表面：`ChannelTransportKind` 与 `NamedPipeEndPoint`，并让 `Tcp` 保持默认行为。
- 用 internal `IChannelListenerFactory` 把 `ChannelServerCore` 与具体 listener 解耦；TCP 与 NamedPipe 各自提供工厂。
- 用 `NamedPipeServerStream` + `PipeReader.Create` / `PipeWriter.Create` 实现 NamedPipe listener 与 connection，复用现有 `DuplexPipe`、`IChannelConnection` 和 `ChannelContext`。
- 保持与 TCP 一致的 accept、每连接 scope、`MaxConnections`、优雅停机、取消和释放语义。
- 用 Windows 上可重复的测试锁定回显、并发连接、管道占用、停机和校验行为。
- 在 Primitives README 中给出可直接照抄的 NamedPipe 服务端与客户端用法。

**Non-Goals:**

- 不修改 public `IChannelServer`、`ChannelContext`、`ChannelServer` 的成员或生命周期契约。
- 不增加公共的 listener/connection 抽象；`IChannelListenerFactory`、`IChannelListener`、`IChannelConnection` 及具体实现都保持 internal。
- 不实现 NamedPipe 客户端封装、连接池、重连或 ACL 配置；示例直接使用 `NamedPipeClientStream`。
- 不实现 Unix domain socket；`UnixDomainSocketEndPoint` 仍走现有 socket 路径，不在本 change 的验收范围。
- 不改变 TCP 的绑定、socket 选项、收发循环、停机或 `LocalEndPoint` 行为。
- 不接入 ActivitySource/Meter；诊断由 `primitives-diagnostics` 单独完成。

## Decisions

### 1. 传输选择使用显式 `Transport`，默认 `Tcp`

```csharp
namespace Microsoft.Extensions.Options
{
    public enum ChannelTransportKind
    {
        Tcp = 0,
        NamedPipe = 1,
    }
}
```

`ChannelServerOptions` 增加：

```csharp
public ChannelTransportKind Transport { get; set; } = ChannelTransportKind.Tcp;
```

NamedPipe 端点单独建模，放在 `Microsoft.Extensions.Hosting.Net`：

```csharp
public sealed class NamedPipeEndPoint : EndPoint
{
    public NamedPipeEndPoint(string pipeName);
    public string PipeName { get; }
    public override AddressFamily AddressFamily => AddressFamily.Unknown;
    public override string ToString() => $"pipe:{PipeName}";
}
```

不采用“根据 `EndPoint` 类型自动推断 transport”的方案。显式 `Transport` 让默认值就是 TCP，不会因为自定义 `EndPoint` 派生类型而意外切换传输；同时 `Transport = NamedPipe` 与 `NamedPipeEndPoint` 的配对可以在绑定前校验，错误信息明确。代价是调用方要写两行配置，README 会给出完整示例。

`NamedPipeEndPoint` 在构造时校验管道名：非空、非纯空白、不含 `\` 或 `/`、长度不超过 Windows 命名管道的实际上限。`EndPoint` 的 `AddressFamily` 返回 `AddressFamily.Unknown`，因为命名管道不属于 IP 地址族；`Serialize`/`Create` 不提供有意义实现，调用时抛 `NotSupportedException`。

### 2. `IChannelListenerFactory` 是 internal 缝，listener/connection 类型继续 internal

```csharp
internal interface IChannelListenerFactory
{
    ValueTask<IChannelListener> BindAsync(
        ChannelServerOptions options,
        ILoggerFactory? loggerFactory,
        CancellationToken ct);
}
```

解析器按 `ChannelServerOptions.Transport` 选择工厂：

```csharp
internal static class ChannelListenerFactory
{
    public static IChannelListenerFactory Create(ChannelServerOptions options)
        => options.Transport switch
        {
            ChannelTransportKind.Tcp => SocketChannelListenerFactory.Instance,
            ChannelTransportKind.NamedPipe => NamedPipeChannelListenerFactory.Instance,
            _ => throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Transport,
                "不支持的传输类型。"),
        };
}
```

`ChannelServerCore` 在构造时解析一次工厂，`StartAsync` 改为：

```csharp
var listener = await _listenerFactory
    .BindAsync(_options, LoggerFactory, ct)
    .ConfigureAwait(false);
```

**为什么 listener/connection 类型不公开：** 它们描述的是 accept loop 与 Pipe 的内部协作方式，不是使用方需要实现的扩展点。公开 `IChannelListener` 会把“accept 返回什么、谁来释放、`Termination` 何时完成”冻结成公共契约，未来替换 Kestrel/IOQueue 或调整 NamedPipe 缓冲策略都会变成 breaking change。使用方只需要配置 `Transport` 与端点；测试通过 `InternalsVisibleTo` 观察内部类型。

**为什么工厂也保持 internal：** 本 change 不承诺第三方 transport 插件模型。工厂的作用是让核心类可替换、可测试，而不是新增公共扩展 API。若未来需要插件，再单独设计公共工厂契约。

### 3. NamedPipe 监听器预创建两个 pending 实例

`NamedPipeChannelListener` 持有一个 pending 队列：

```csharp
private readonly Queue<NamedPipeServerStream> _pending = new();
private readonly object _gate = new();
```

`BindAsync` 先创建两个 `NamedPipeServerStream` 实例，再返回 listener。`AcceptAsync` 从队列取出一个实例并 `WaitForConnectionAsync(ct)`；连接建立后立即补充一个 pending 实例，保证下一个客户端可以马上连接。两个实例覆盖“两个客户端同时连接”的测试，也避免 accept 循环补实例的短暂窗口让第二个客户端等待。

每个实例使用：

```csharp
new NamedPipeServerStream(
    pipeName,
    PipeDirection.InOut,
    NamedPipeServerStream.MaxAllowedServerInstances,
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous);
```

`MaxConnections` 仍由 `ChannelServerCore` 在 accept 之后统一执行；命名管道的 OS 实例上限与应用的 `MaxConnections` 是两层限制，不能互相替代。

`UnbindAsync` 设置 unbound 标志并释放所有 pending 实例；正在 `WaitForConnectionAsync` 的实例由 `ChannelServerCore` 先取消的停机 token 唤醒。`AcceptAsync` 在停机期间返回 null 或抛出取消异常，由现有 accept loop 处理。

### 4. NamedPipe 连接直接使用 `PipeReader.Create` / `PipeWriter.Create`

`NamedPipeChannelConnection` 在构造时创建：

```csharp
_reader = PipeReader.Create(stream, new StreamPipeReaderOptions(
    pool: MemoryPool<byte>.Shared,
    bufferSize: readBufferSize,
    minimumReadSize: 4096,
    leaveOpen: true));

_writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(
    pool: MemoryPool<byte>.Shared,
    minimumBufferSize: 4096,
    leaveOpen: true));

_transport = new DuplexPipe(_reader, _writer);
```

`PipeReader` / `PipeWriter` 自己处理部分读、缓冲和背压，不需要再写一套 socket 风格的接收/发送循环。`leaveOpen: true` 让连接统一负责 `NamedPipeServerStream` 的释放；`DuplexPipe` 复用 P1 已有的传输组合。

端点语义：

- `LocalEndPoint` 返回 `new NamedPipeEndPoint(pipeName)`；
- `RemoteEndPoint` 返回 null，因为 Windows 命名管道不暴露对端地址；README 明确说明；
- `Termination` 在 `Abort` 或本地 `DisposeAsync` 时完成。对端正常关闭表现为 `PipeReader` 读到 EOF，由 handler 观察，不在空闲时主动轮询；这与 TCP 干净 FIN 不触发 `Termination` 的现有语义一致。读写故障会通过 `PipeReader`/`PipeWriter` 抛给 handler，随后由 `ChannelContext` 的关闭路径处理。

`Abort` 取消 pending 读写、释放 `NamedPipeServerStream`，并把原因写入 `Termination`。`DisposeAsync` 先完成 writer（把已提交输出刷出），再完成 reader 并释放流；重复调用幂等。

### 5. 校验放在 `ChannelServerOptions.Validate`，绑定前失败

启动前校验：

- `Transport = NamedPipe` 时 `EndPoint` 必须是 `NamedPipeEndPoint`；
- `Transport = Tcp` 时 `EndPoint` 不能是 `NamedPipeEndPoint`；
- `Transport = NamedPipe` 时 `CreateBoundListenSocket` 必须为 null；
- 未定义的枚举值抛 `ArgumentOutOfRangeException`；
- 管道名为空或包含路径分隔符时由 `NamedPipeEndPoint` 构造函数抛 `ArgumentException`。

TCP 专属选项在 NamedPipe 下被忽略并在 README 说明：`Backlog`（命名管道没有 backlog 概念）、`NoDelay`、`KeepAlive`、`LingerState`、`ReuseAddress`、`DualMode`、`CreateBoundListenSocket`。不因为调用方保留默认 `NoDelay = true` 而拒绝启动。

### 6. 测试使用唯一管道名，Windows 限定

每个测试通过 `NamedPipeTestSupport.CreatePipeName()` 生成：

```csharp
"gf-primitives-" + Guid.NewGuid().ToString("N")
```

不使用固定管道名，避免并行测试或多个开发会话互相占用。客户端统一用：

```csharp
var client = new NamedPipeClientStream(
    ".",
    pipeName,
    PipeDirection.InOut,
    PipeOptions.Asynchronous);
await client.ConnectAsync(TestWaits.DefaultTimeout, ct);
```

测试只在 Windows 上验证；仓库的构建与运行环境本身也限定 Windows。非 Windows 上 `NamedPipeServerStream` 的行为不在验收范围。

## Risks / Trade-offs

- **命名管道是机器级命名空间** → 固定名称会与其它进程冲突；测试与 README 都要求唯一名称，生产部署由调用方负责命名空间规划。
- **NamedPipe 没有对端端点** → `ChannelContext.RemoteEndPoint` 为 null；文档明确说明，依赖远端地址的中间件需要判空。
- **预创建两个实例增加句柄数** → 每个 server 多一个 pending 管道句柄；换来两个客户端并发连接时无需等待 accept 补位。`UnbindAsync`/`DisposeAsync` 统一释放。
- **补充 pending 实例失败** → 记录警告并继续服务当前连接；下一次 `AcceptAsync` 会再次尝试创建。连续失败会由现有 accept loop 的阈值终止，不会静默丢失监听能力。
- **`PipeReader.Create` 不提供主动断连监测** → 空闲客户端的干净关闭只能在下一次读取时观察到；这与 TCP 干净 FIN 的现有语义一致。需要主动断连检测的调用方应使用读超时或业务心跳。
- **`Backlog` 在 NamedPipe 下无效** → 保留属性以免破坏 TCP 配置；README 与 XML 文档标注仅 TCP 生效。
- **依赖 P1.5a** → `ChannelServerCore` 尚未在当前分支落地时无法直接应用；迁移计划要求先完成 composition change。
- **Windows API 差异** → 不同 Windows 版本的管道 ACL/实例行为可能不同；测试只覆盖本机回显和占用，不做跨机器或 ACL 验收。

## Migration Plan

1. 在 `Microsoft/Extensions/Options/` 增加 `ChannelTransportKind`，在 `Microsoft/Extensions/Hosting/Net/` 增加 `NamedPipeEndPoint`；补 XML 文档与构造校验。
2. 修改 `ChannelServerOptions`：增加 `Transport`，在 `Validate` 中加入传输/端点匹配与 `CreateBoundListenSocket` 校验；`Clone` 复制新属性。
3. 增加 `IChannelListenerFactory`、`ChannelListenerFactory`、`SocketChannelListenerFactory`，把现有 `SocketChannelListener.BindAsync` 包装成工厂。
4. 修改 `ChannelServerCore`：构造时解析工厂，`StartAsync` 通过工厂绑定；TCP 路径行为不变。
5. 增加 `NamedPipeChannelConnection` 与 `NamedPipeChannelListener`，先完成连接属性和 Pipe 组合，再补 abort/dispose 与 accept/unbind。
6. 增加 `NamedPipeTestSupport` 与 `NamedPipeTransportTests`，按回显、并发、占用、停机、取消、校验的顺序补齐。
7. 在 `NamespaceConventionTests` 中断言 `IChannelListener`、`IChannelConnection` 及具体 NamedPipe 类型不是 public。
8. 更新 Primitives README，加入 NamedPipe 配置、客户端示例、Windows/唯一名称/RemoteEndPoint 说明。
9. 运行 Primitives 构建、NamedPipe 过滤测试、全量测试与 `openspec validate`。

回滚策略：删除 NamedPipe 文件、工厂与新增 options 属性，恢复 `ChannelServerCore` 直接调用 `SocketChannelListener.BindAsync`。TCP 的 listener/connection 未被修改，回滚不影响现有 socket 行为；NamedPipe 不涉及持久化状态。

## Open Questions

无。NamedPipe 客户端封装、ACL 配置、Unix domain socket 与 diagnostics 明确不在本 change。
