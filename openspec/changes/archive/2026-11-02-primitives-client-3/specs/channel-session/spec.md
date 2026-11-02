# Spec Delta

## ADDED Requirements

### Requirement: 批量写入扩展契约

The implementation SHALL provide a public `WriteBatchAsync<T>` extension for `IChannelSession<T>` that accepts `IReadOnlyList<T>` and a cancellation token. Null session or messages MUST throw `ArgumentNullException`. An empty batch MUST complete without acquiring the write lock, writing bytes, or flushing. A custom session without built-in batch support MUST fall back to sequential `WriteAsync` calls and preserve message order.

#### Scenario: 空批次不触碰连接

- **WHEN** 对任意 `IChannelSession<T>` 调用 `WriteBatchAsync` 并传入空列表
- **THEN** 返回已完成的 `ValueTask`
- **AND** 不调用 `WriteAsync`、不写字节、不调用 `FlushAsync`

#### Scenario: 空参数被拒绝

- **WHEN** session 为 null 或 messages 为 null
- **THEN** 抛出 `ArgumentNullException`
- **AND** 不访问连接

#### Scenario: 自定义会话顺序回退

- **WHEN** 对一个没有内置批量能力的自定义 `IChannelSession<T>` 调用 `WriteBatchAsync`
- **THEN** 扩展按输入顺序逐条调用 `WriteAsync`
- **AND** 每条消息只写一次
- **AND** 取消 token 传递给每次写入

### Requirement: 内置会话批量写入路径

The built-in `ChannelSession<T>` MUST hold its existing write lock for the whole non-empty batch, encode each message with the configured codec, write frames in input order, and call `PipeWriter.FlushAsync` exactly once after the last frame.

#### Scenario: 单次 flush 且保持顺序

- **WHEN** 对内置 session 调用 `WriteBatchAsync` 写入三条消息
- **THEN** 三个帧按输入顺序写出
- **AND** `FlushAsync` 只调用一次
- **AND** 帧格式与逐条 `WriteAsync` 相同

#### Scenario: 批次与单条写串行化

- **WHEN** 一个任务调用 `WriteBatchAsync`，另一个任务同时调用 `WriteAsync`
- **THEN** 两个操作的帧不会交错
- **AND** 接收方可以按完整帧解析所有消息

#### Scenario: 空批次不调用 flush

- **WHEN** 对内置 session 调用 `WriteBatchAsync` 并传入空列表
- **THEN** 不获取写锁、不写帧、不调用 `FlushAsync`
- **AND** 连接保持可用

### Requirement: 批量失败语义

If encoding, size validation, framing, or cancellation fails while a batch is being written, the session MUST abort the connection and propagate the original exception. A completed flush MUST throw `IOException` like a single write. A batch MUST NOT be atomic: the receiver MAY see a prefix of the batch.

#### Scenario: 编码失败中止连接

- **WHEN** 批次中第二条消息编码失败，而第一条帧已经写出
- **THEN** 连接被中止
- **AND** 原始编码异常传播给调用方
- **AND** 接收方可能只看到第一批前缀

#### Scenario: 消息超限中止连接

- **WHEN** 批次中某条消息编码后超过 `MaxMessageSize`
- **THEN** 连接被中止
- **AND** 抛出 `InvalidDataException`
- **AND** 不继续写出后续消息

#### Scenario: flush 取消中止连接

- **WHEN** `FlushAsync` 被调用方取消
- **THEN** 连接被中止
- **AND** 原始 `OperationCanceledException` 传播给调用方
- **AND** 不继续向该连接写数据
