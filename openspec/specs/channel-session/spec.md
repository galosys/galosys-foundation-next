# channel-session Specification

## Purpose
定义每连接一个的拉模型消息会话 `IChannelSession<T>`：创建约束、读写行为、EOF/取消/超限错误语义、写入串行化、背压、释放所有权，以及 `NetServer.ConsoleApp` 的示例迁移。

## Requirements

### Requirement: 每连接消息会话契约

The implementation SHALL provide `IChannelSession<T> : IAsyncDisposable` with `ValueTask<T> ReadAsync(CancellationToken ct)` and `ValueTask WriteAsync(T message, CancellationToken ct)`. The session MUST use a pull model: it MUST NOT start a background read loop, MUST NOT maintain an unbounded message queue, and MUST NOT expose a `Completion` property. Connection completion MUST be observed through `ChannelContext.ConnectionClosed`. The public surface MUST be the interface and extension methods; the concrete `ChannelSession<T>` implementation MAY remain internal.

#### Scenario: 处理器主动拉取消息

- **WHEN** handler 调用 `ReadAsync`
- **THEN** session 只在该调用期间读取连接
- **AND** 没有后台读循环或预读队列

#### Scenario: 不暴露 Completion

- **WHEN** 调用方需要等待连接关闭
- **THEN** 使用 `ChannelContext.ConnectionClosed`
- **AND** `IChannelSession<T>` 不提供 `Completion` 成员

#### Scenario: 会话不拥有连接

- **WHEN** handler 正常返回或释放 session
- **THEN** 连接收尾仍由 `ChannelServer` 负责
- **AND** session 不调用 `Transport.Input.Complete` 或 `Transport.Output.Complete`

### Requirement: 会话创建与单连接约束

The implementation SHALL provide `AsSession<T>`, `AsJsonSession<T>`, and `AsLineTextSession` extension methods. `AsSession<T>` MUST reject null `framer`, `codec`, or `context` arguments. A `ChannelContext` MUST allow at most one session for its entire lifetime; a second `AsSession` call MUST throw `InvalidOperationException` without replacing or disposing the first session. The one-session slot MUST remain occupied after the first session is disposed. Session creation MUST be documented as a terminal-handler responsibility, not a pipeline-middleware responsibility.

#### Scenario: 第一次 AsSession 成功

- **WHEN** 在一个尚无 session 的连接上调用 `context.AsSession(framer, codec)`
- **THEN** 返回可用的 `IChannelSession<T>`
- **AND** 连接槽位记录该 session

#### Scenario: 重复 AsSession 抛错

- **WHEN** 同一连接上第二次调用 `context.AsSession(...)` 或 `AsLineTextSession()`
- **THEN** 抛出 `InvalidOperationException`
- **AND** 第一个 session 仍然可用

#### Scenario: 释放后仍不能重建

- **WHEN** 第一个 session 已经 `DisposeAsync`，再次调用 `AsSession`
- **THEN** 仍然抛出 `InvalidOperationException`
- **AND** 不会出现两个 session 抢同一个 `PipeReader`

#### Scenario: 只在终端 handler 创建

- **WHEN** 查看示例或 README 中的 session 用法
- **THEN** `AsSession`/`AsLineTextSession` 只在终端 handler 内调用
- **AND** 不在 `IChannelPipeline.Use` 中间件内调用

### Requirement: 读取、EOF 与消息上限

The implementation SHALL read from `ChannelContext.Reader`, pass the current buffer to `IMessageFramer.Parse`, and decode exactly one complete frame with `IMessageCodec<T>.Decode`. An empty buffer with `ReadResult.IsCompleted` MUST throw `EndOfStreamException` (clean EOF). A completed read with a non-empty but incomplete frame MUST throw `InvalidDataException` (mid-frame EOF). A frame whose payload length exceeds `MessageChannelOptions.MaxMessageSize` MUST throw `InvalidDataException` and MUST abort the connection. Framing or codec failures MUST abort the connection and propagate the original exception.

#### Scenario: 半包继续等待

- **WHEN** 第一次读取只包含帧的一部分
- **THEN** session 按 framer 的 `Consumed`/`Examined` 位置 `AdvanceTo`
- **AND** 继续读取，直到帧完整或连接结束

#### Scenario: 干净 EOF

- **WHEN** 连接输入结束且缓冲区为空
- **THEN** `ReadAsync` 抛出 `EndOfStreamException`
- **AND** 不把干净 EOF 当作格式错误

#### Scenario: 帧中途 EOF

- **WHEN** 连接输入结束但缓冲区中还有不完整帧
- **THEN** `ReadAsync` 抛出 `InvalidDataException`
- **AND** 连接被中止

#### Scenario: 超过 MaxMessageSize

- **WHEN** framer 返回的完整帧 payload 长度大于 `MaxMessageSize`
- **THEN** `ReadAsync` 抛出 `InvalidDataException`
- **AND** `ChannelContext` 被中止

#### Scenario: 解码失败中止连接

- **WHEN** framer 抛 `InvalidDataException`，或 codec 抛 `JsonException`/`DecoderFallbackException` 等解码异常
- **THEN** session 调用 `ChannelContext.Abort`
- **AND** 原异常传播给 handler
- **AND** 不尝试跳过坏帧继续读取

### Requirement: 读取取消与并发约束

The implementation SHALL allow cancellation of `ReadAsync` without consuming buffered bytes. When `PipeReader.ReadAsync(ct)` is canceled, the session MUST NOT call `AdvanceTo`, so a later `ReadAsync` can continue from the same data. The session MUST allow only one concurrent reader per session; a second concurrent `ReadAsync` MUST throw `InvalidOperationException` immediately rather than waiting. The single-reader guard MUST be released when the read completes, faults, or is canceled.

#### Scenario: 取消后可以继续读

- **WHEN** handler 用一个已取消的 token 调用 `ReadAsync`
- **THEN** 调用抛出 `OperationCanceledException`
- **AND** 没有调用 `AdvanceTo`
- **AND** 下一次用未取消的 token 调用可以读到同一帧

#### Scenario: 并发读直接抛错

- **WHEN** 第一个 `ReadAsync` 尚未完成时调用第二次 `ReadAsync`
- **THEN** 第二次调用立即抛出 `InvalidOperationException`
- **AND** 不排队、不打乱消息顺序

#### Scenario: 取消后释放单读者标记

- **WHEN** 一次读取因取消而退出
- **THEN** 单读者标记被重置
- **AND** 后续读取可以正常开始

### Requirement: 写入串行化与背压

The implementation SHALL serialize writes with a `SemaphoreSlim` so that bytes from concurrent `WriteAsync` calls never interleave within a frame. `WriteAsync` MUST encode the message into a pooled buffer, pass the encoded payload to `IMessageFramer.WriteFrame`, call `PipeWriter.FlushAsync`, and check `FlushResult.IsCompleted`. When `IsCompleted` is true, `WriteAsync` MUST throw `IOException`. Write cancellation after bytes may have been written MUST abort the connection before propagating `OperationCanceledException`. The session MUST NOT close the connection after a successful or failed write.

#### Scenario: 并发写不交错

- **WHEN** 两个任务同时对同一 session 调用 `WriteAsync`
- **THEN** 两帧的字节不会交错
- **AND** 每个接收方都能解析出完整帧

#### Scenario: 先编码再写 frame

- **WHEN** 调用 `WriteAsync`
- **THEN** codec 先写入池化 buffer
- **AND** framer 再从该 buffer 写出长度头/分隔符和 payload
- **AND** 不采用回头回填长度头的方案

#### Scenario: 对端已关闭时写入

- **WHEN** `FlushAsync` 返回 `IsCompleted = true`
- **THEN** `WriteAsync` 抛出 `IOException`
- **AND** session 不尝试完成或释放连接

#### Scenario: 写取消中止连接

- **WHEN** 写操作在 `WriteFrame` 或 `FlushAsync` 期间被取消
- **THEN** session 调用 `ChannelContext.Abort`
- **AND** 原 `OperationCanceledException` 传播给调用方
- **AND** 不继续向该连接写数据

### Requirement: 会话释放与连接所有权

The implementation SHALL make `IChannelSession<T>.DisposeAsync` idempotent. Disposal MUST wait for an in-flight write to finish, MUST release session-owned state, and MUST NOT close, complete, or abort the underlying connection. The session slot on `ChannelContext` MUST remain occupied after disposal. A write or read after disposal MUST throw `ObjectDisposedException`.

#### Scenario: Dispose 不关闭连接

- **WHEN** handler 释放 session 后继续通过 `ChannelContext.Writer` 写入原始字节
- **THEN** 客户端仍能收到这些字节
- **AND** `ConnectionClosed` 没有被 session 提前触发

#### Scenario: 重复 Dispose

- **WHEN** 连续调用两次 `DisposeAsync`
- **THEN** 第二次正常返回
- **AND** 不抛异常

#### Scenario: 释放后使用会话

- **WHEN** session 已释放后调用 `ReadAsync` 或 `WriteAsync`
- **THEN** 抛出 `ObjectDisposedException`
- **AND** 不影响连接本身

### Requirement: 会话选项与便捷工厂

The implementation SHALL provide `MessageChannelOptions` in the `Microsoft.Extensions.Options` namespace with a positive `MaxMessageSize` defaulting to 1 MiB. `MaxMessageSize <= 0` MUST cause session construction to throw `ArgumentOutOfRangeException`. `AsJsonSession<T>` MUST compose `LengthPrefixedFramer` with `JsonMessageCodec<T>`, and `AsLineTextSession` MUST compose `LineFramer` with `Utf8StringCodec`. Framer and JSON options MUST be passed through so callers can override defaults.

#### Scenario: 默认 1 MiB 上限

- **WHEN** 未提供 `MessageChannelOptions`
- **THEN** session 使用 1 MiB 作为 `MaxMessageSize`
- **AND** 小于等于 1 MiB 的完整帧可以通过

#### Scenario: 非法 MaxMessageSize

- **WHEN** 使用 `MaxMessageSize = 0` 或负值创建 session
- **THEN** 构造函数抛出 `ArgumentOutOfRangeException`
- **AND** 不占用连接槽位

#### Scenario: JSON 会话工厂

- **WHEN** 调用 `context.AsJsonSession<T>()`
- **THEN** 使用 4 字节大端长度前缀 framer 和 JSON codec
- **AND** 可以通过 `LengthFieldFramerOptions`/`JsonSerializerOptions` 覆盖默认配置

#### Scenario: 行文本会话工厂

- **WHEN** 调用 `context.AsLineTextSession()`
- **THEN** 使用默认 `\n` 分隔符的 `LineFramer` 和严格 `Utf8StringCodec`
- **AND** 可以通过 `LineFramerOptions` 覆盖分隔符、CRLF 裁剪和最大帧

### Requirement: 示例迁移

The `NetServer.ConsoleApp` sample SHALL create its message session with `context.AsLineTextSession()` inside the terminal handler only, using `LineFramer` and `Utf8StringCodec`. It MUST keep listening on `0.0.0.0:8000`, print each received line as `>>>>> {line} <<<<<`, send `hello\n` once per second from `SenderBootstrapper`, and shut down gracefully on Ctrl+C. The sample README MUST document a manual verification that splits a multibyte UTF-8 character across two writes and expects the complete character in the log. This requirement MUST NOT change the `channel-hosting` capability.

#### Scenario: 行文本会话替换原始字节读取

- **WHEN** 客户端向示例发送 `hello\n`
- **THEN** `SampleChannelHandler` 通过 `AsLineTextSession` 读到 `hello`
- **AND** 日志输出 `>>>>> hello <<<<<`
- **AND** handler 不再按读取块直接调用 `Encoding.UTF8.GetString`

#### Scenario: 发送端每秒发送带换行的 hello

- **WHEN** 示例启动并连接本机 8000 端口
- **THEN** `SenderBootstrapper` 每秒发送一次 `hello\n`
- **AND** 示例每秒打印一条 `>>>>> hello <<<<<`
- **AND** 逻辑消息仍然是 `hello`，换行只作为行协议边界

#### Scenario: 多字节 UTF-8 跨两次写入

- **WHEN** 客户端把 `"你好\n"` 的 UTF-8 字节拆成两次写入，第一次只写第一个字节，第二次写剩余字节
- **THEN** 示例打印 `>>>>> 你好 <<<<<`
- **AND** 不出现替换字符 `�`
- **AND** 字符不会因为 TCP 分段而被截断

#### Scenario: 会话只在终端处理器创建

- **WHEN** 检查示例的 `Program.cs`、`ChannelServerRunner` 和 pipeline 配置
- **THEN** 这些位置都不调用 `AsSession`/`AsLineTextSession`
- **AND** session 只在 `SampleChannelHandler.HandleAsync` 内创建

#### Scenario: Ctrl+C 优雅停机

- **WHEN** 对运行中的示例按 Ctrl+C
- **THEN** 进程在宿主与服务器的 `ShutdownTimeout` 内退出
- **AND** 不出现未处理异常

#### Scenario: README 提供手工验证

- **WHEN** 阅读 `samples/NetServer.ConsoleApp/README.md`
- **THEN** 可以找到启动示例、另开客户端、分两次写入 `"你好\n"` 的 PowerShell 命令
- **AND** 文档给出预期日志 `>>>>> 你好 <<<<<`
- **AND** 文档说明 sender 的 `hello` 日志会同时出现
