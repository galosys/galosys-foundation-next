## Context

P2 已经有了 `IMessageFramer` 和 `IMessageCodec<T>`，但没有任何类型负责把两者接到连接上。P1 的 handler 只能直接读 `context.Reader`，因此每个业务处理器都要重复处理半包、粘包、EOF、取消、最大消息和并发写。设计文档要求 P2 提供拉模型 `IChannelSession<T>`，并且明确“一个连接一个 session、session 不拥有连接、只在终端 handler 创建”。

本 change 只组合已有 framer/codec 与 `ChannelContext`，不修改传输、管线和服务器生命周期。`ChannelSession<T>` 是 internal 实现，公开面是 `IChannelSession<T>` 与扩展方法。

## Goals / Non-Goals

**Goals:**

- 提供 `IChannelSession<T>` 与 internal `ChannelSession<T>`，实现拉模型 `ReadAsync`/`WriteAsync`。
- 用 `ChannelContext` 的 internal 槽位保证一个连接最多一个 session。
- 明确干净 EOF、帧中途 EOF、读取消、并发读、超限和写取消的语义。
- 用 `SemaphoreSlim` 串行化写入，保证帧不交错，并检查 `FlushResult.IsCompleted`。
- 提供 `AsSession<T>`、`AsJsonSession<T>`、`AsLineTextSession` 三个组合入口。
- 用 loopback 测试覆盖半包/粘包、1 MiB 背压和所有权。

**Non-Goals:**

- 不实现后台读循环、`Channel<T>` 队列或 `Completion` 属性。
- 不实现客户端、连接池、重连、请求/响应关联或批处理。
- 不实现 TLS、压缩、加密或同端口协议分发。
- 不改变 `ChannelContext` 的公开成员，不改变服务器关闭连接的职责。
- 不把 session 做成 `IChannelPipeline` 中间件。

## Decisions

### 1. 拉模型，不做后台读循环

`ReadAsync` 由 handler 主动调用，session 不在后台预读，也不维护消息队列。这样异常、背压和 `ConnectionClosed` 的生命周期都保持简单：handler 不读，数据就留在 Pipe 里形成背压；handler 返回，服务器照常收尾。

### 2. 一个连接一个 session，用 `ChannelContext` 槽位防重复

`ChannelContext` 增加：

```csharp
private object? _session;

internal bool TrySetSession(object session)
    => Interlocked.CompareExchange(ref _session, session, null) is null;

internal object? Session => Volatile.Read(ref _session);
```

`ChannelSession<T>` 构造函数在字段赋值完成后调用 `TrySetSession(this)`；返回 false 时释放自己的信号量并抛 `InvalidOperationException`。槽位在 session 释放后也不清空：一个连接的生命周期内不允许再创建第二个 session，避免“先释放再抢 Reader”的隐蔽错误。

`AsSession` 是唯一底层入口；`AsJsonSession<T>` 和 `AsLineTextSession` 只负责构造 framer/codec 再调用 `AsSession`，不引入第三个合并接口。

### 3. session 借用连接，不拥有连接

- `DisposeAsync` 只标记 disposed、等待在途写、释放 session 自己的状态；不调用 `context.Abort`，不 `Complete` 输入/输出，不释放 `Transport`。
- handler 返回后由 `ChannelServer` 关闭连接；需要提前中止时调用 `context.Abort(reason)`。
- 不暴露 `Completion`；等待连接关闭统一使用 `context.ConnectionClosed`。
- 第二个 session 的创建尝试永远失败，即使第一个已经释放。

### 4. 读取循环与 EOF 语义

`ReadAsync` 用 `Interlocked.CompareExchange` 实现单读者：

- 已有读在进行：立即抛 `InvalidOperationException`，不排队，避免打乱消息顺序。
- 每次循环调用 `context.Reader.ReadAsync(ct)`，把 `result.Buffer` 交给 framer。
- framer 返回完整帧：检查 `Frame.Length <= MaxMessageSize`，调用 codec `Decode`，然后 `AdvanceTo(parse.Consumed, parse.Examined)` 并返回消息。
- framer 返回不完整：`AdvanceTo(parse.Consumed, parse.Examined)` 后继续循环；如果 `result.IsCompleted`，说明帧中途 EOF，抛 `InvalidDataException`。
- `buffer.IsEmpty && result.IsCompleted`：干净 EOF，抛 `EndOfStreamException`。
- framer 抛 `InvalidDataException`、codec 抛 `JsonException`/`DecoderFallbackException` 或其他解码异常：调用 `context.Abort(ex)` 后原样抛出；坏帧后流通常已不可信，不尝试跳过。
- `MaxMessageSize` 是 session 层兜底：framer 的 `MaxFrameSize` 负责在等待 payload 前拒绝明显非法长度；session 再检查实际 payload，防止自定义 framer 或 codec 绕过。

### 5. 读取消是安全的

`PipeReader.ReadAsync(ct)` 取消时不会返回 `ReadResult`，session 直接让 `OperationCanceledException` 传播，不调用 `AdvanceTo`；下一次 `ReadAsync` 可以继续读取同一段数据。取消发生在已拿到 `ReadResult` 之后时，当前实现会继续完成这一帧（同步的 framer/codec 很快），不会留下半消费状态。

### 6. 写入串行化、池化编码与取消

`WriteAsync` 的步骤：

1. 检查 disposed 和 `ct`。
2. `await _writeLock.WaitAsync(ct)`；等待期间取消是安全的（还没写任何字节）。
3. 用 internal `PooledBufferWriter`（基于 `ArrayPool<byte>`）调用 codec `Encode`；codec 失败时还没写连接，直接释放锁并抛出。
4. 调用 framer `WriteFrame(context.Writer, buffer.WrittenSequence)`；长度头在 payload 之前写入。
5. `await context.Writer.FlushAsync(ct)`；若 `FlushResult.IsCompleted` 为 true，抛 `IOException`，表示对端已关闭。
6. `finally` 释放信号量，`PooledBufferWriter.Dispose` 归还数组。

写取消是不安全的：如果取消发生在 `WriteFrame`/`FlushAsync` 期间，可能已经写出半个帧，必须 `context.Abort(ex)` 后原样抛出。写侧不做批量“只 flush 一次”；那属于 P4 的 batch。

### 7. `MessageChannelOptions` 只做 session 兜底

```csharp
public sealed class MessageChannelOptions
{
    /// <summary>单条消息 payload 的硬上限，默认 1 MiB。</summary>
    public int MaxMessageSize { get; set; } = 1024 * 1024;
}
```

`MaxMessageSize <= 0` 时 session 构造函数抛 `ArgumentOutOfRangeException`。framer 自己的 `MaxFrameSize` 仍然独立生效；两者都配置时取更早失败的那个。

### 8. 便捷工厂的默认组合

```csharp
public static IChannelSession<T> AsSession<T>(
    this ChannelContext context,
    IMessageFramer framer,
    IMessageCodec<T> codec,
    MessageChannelOptions? options = null);

public static IChannelSession<T> AsJsonSession<T>(
    this ChannelContext context,
    LengthFieldFramerOptions? framer = null,
    JsonSerializerOptions? json = null,
    MessageChannelOptions? options = null);

public static IChannelSession<string> AsLineTextSession(
    this ChannelContext context,
    LineFramerOptions? framer = null,
    MessageChannelOptions? options = null);
```

- `AsJsonSession<T>` = `LengthPrefixedFramer` + `JsonMessageCodec<T>`。
- `AsLineTextSession` = `LineFramer` + `Utf8StringCodec`。
- 扩展类放在 `Microsoft.Extensions.Hosting.Net`，调用方只需 `using Microsoft.Extensions.Hosting.Net;`。

### 9. 目录与命名空间

```text
src/Galosys.Foundation.Primitives/
  Session/
    IChannelSession.cs
    ChannelSession.cs
    ChannelContextSessionExtensions.cs
    PooledBufferWriter.cs
  Microsoft/Extensions/Options/
    MessageChannelOptions.cs
```

`IChannelSession<T>`、扩展类、`MessageChannelOptions` 为 public；`ChannelSession<T>`、`PooledBufferWriter` 为 internal。`ChannelContext` 的槽位也是 internal，不扩大公开 API。

## Risks / Trade-offs

- **`DisposeAsync` 与并发写等待者的竞态**：disposed 标记会阻止新的写，但已经通过检查、尚未拿到信号量的写可能观察到释放后的信号量。实现应优先保证“不关闭连接”和“在途写完成”，必要时保留信号量不释放（`SemaphoreSlim` 不持有非托管句柄）；测试覆盖 Dispose 不关闭连接。
- **坏帧即中止连接**：不尝试跳过坏帧继续。协议错误通常意味着流已不可信；需要容错的业务应在更高层实现。
- **1 MiB 消息的内存成本**：池化 buffer 和 Pipe 都可能同时持有 payload；测试覆盖 1 MiB 背压，但不承诺零分配。
- **读取消后继续的边界**：取消发生在 `ReadAsync` 期间是安全的；取消发生在同步 framer/codec 期间不会被观察到。这个取舍保证不会丢弃已取出的完整帧。
- **空行与 UTF-8 codec**：`LineFramer` 允许空行，但 `Utf8StringCodec` 拒绝空帧；使用 `AsLineTextSession` 的协议不能发送空行。文档必须写明。
- **session 槽位永不复用**：同一连接释放 session 后不能再建，换取 Reader 所有权的确定性；需要重新协商协议时应新建连接。
- **写入取消会中止连接**：这是有意的安全语义；调用方应把写取消视为连接级失败。

## Migration Plan

1. 在 `ChannelContext` 增加 internal 槽位与 `TrySetSession`，不改变公开成员。
2. 新增 `IChannelSession<T>`、`MessageChannelOptions` 和 `PooledBufferWriter`。
3. 实现 `ChannelSession<T>` 的构造/读取/写入/释放，再补 `ChannelContextSessionExtensions`。
4. 新增 `SessionTests.cs`，先覆盖半包/粘包，再覆盖 EOF、取消、并发、1 MiB、重复创建和 Dispose。
5. 更新 Primitives README 的会话章节。
6. 运行 Primitives 构建、focused session 测试和全量测试；确认现有连接生命周期测试不受影响。
7. 本 change 合入后再执行 `primitives-message-sample`；示例迁移依赖 P1.5a 的 `ChannelServer` 与 `SampleChannelHandler`。

## Open Questions

无。后台读循环、`Completion`、客户端、连接池和协议分发明确不在本 change。
