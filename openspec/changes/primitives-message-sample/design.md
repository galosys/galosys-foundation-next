## Context

P1.5a-2 之后，`NetServer.ConsoleApp` 的结构是 `ChannelServer` + `ChannelServerRunner` + `SampleChannelHandler`：handler 直接从 `context.Reader` 读块，用 `Encoding.UTF8.GetString(buffer)` 打印，`SenderBootstrapper` 每秒发送不带换行的 `hello`。这种写法无法保证一次读取对应一条消息，多字节 UTF-8 字符可能跨读取被截断。

P2 已提供 `IChannelSession<T>`、`LineFramer` 和 `Utf8StringCodec`。本 change 只迁移示例与文档，不修改库实现；它依赖 P1.5a 和 `primitives-message-session` 都已合入。

## Goals / Non-Goals

**Goals:**

- 示例使用 `context.AsLineTextSession()`，按行读取并打印完整文本。
- 保留 `0.0.0.0:8000`、每秒发送 `hello`、Ctrl+C 优雅停机。
- 在终端 handler 内创建 session，展示正确的入口位置。
- 用自动化测试和手工命令证明多字节 UTF-8 不会跨读取截断。
- 更新 Primitives README 与 sample README。

**Non-Goals:**

- 不修改 `IChannelSession<T>`、framer、codec、`ChannelServer` 或 `ChannelServerRunner`。
- 不修改 `channel-hosting` 规格；宿主装配沿用 P1.5a-2。
- 不引入客户端框架、连接池、重连或协议分发。
- 不改变示例的日志级别、监听地址或端口。
- 不实现空行、二进制协议或自定义分隔符的示例。

## Decisions

### 1. 用 `AsLineTextSession()`，不再按块解码

`SampleChannelHandler` 在 handler 内创建行文本会话：

```csharp
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
    catch (OperationCanceledException) when (context.ConnectionClosed.IsCancellationRequested)
    {
        break;
    }

    _logger.LogInformation(">>>>> {Line} <<<<<", line);
}
```

`AsLineTextSession` 组合 `LineFramer`（默认 `\n`，裁剪 `\r`）与 `Utf8StringCodec`。framer 先保证一帧完整，codec 再解码，因此多字节字符不会跨读取截断。示例不再直接调用 `Encoding.UTF8.GetString(buffer)`。

### 2. 发送端补换行，逻辑消息不变

`LineFramer` 需要分隔符才能切帧；现有发送端每秒发送 `hello` 没有 `\n`，迁移后示例将永远读不到消息。`SenderBootstrapper` 改为：

```csharp
var bytes = Encoding.UTF8.GetBytes("hello\n");
```

逻辑消息仍是 `hello`；换行是示例行协议的帧边界，不属于日志内容。README 必须说明线格式是 `hello\n`。

### 3. session 只在终端 handler 创建

`Program.cs`、`ChannelServerRunner` 和 `IChannelPipeline.Use` 都不创建 session。原因与 P2 设计一致：中间件契约是“只包装 Transport，不读取业务数据”；session 要读 `context.Reader`，只能由终端 handler 持有。示例 README 会把这一约束写成显式说明。

### 4. EOF 与连接关闭都按正常结束处理

- 客户端半关闭或正常断开：`ReadAsync` 抛 `EndOfStreamException`，handler 跳出循环。
- 服务器停机/连接中止：`ReadAsync` 可能抛 `OperationCanceledException`，仅当 `context.ConnectionClosed.IsCancellationRequested` 时视为正常结束；其他取消继续向上抛，让框架记录。
- 不再用 `ReadResult.IsCompleted` 手写循环；这些规则由 session 统一处理。

### 5. 多字节 UTF-8 验证分两层

- **自动化**：在 `SessionTests.cs` 增加 `LineTextSession_MultibyteUtf8SplitAcrossWrites_DecodesWholeCharacter`：服务端 handler 使用 `AsLineTextSession`，客户端先写 `"你"` 的 UTF-8 第一个字节，延迟后再写剩余字节和 `\n`，断言服务端读到完整的 `"你好"`。
- **手工**：sample README 给出 PowerShell 命令，启动示例后另开终端，把 `"你好\n"` 的第一个字节与剩余字节分两次写入，预期日志为 `>>>>> 你好 <<<<<`，不出现 `�`。

自动化测试验证的是与示例相同的 session 组合；手工步骤验证示例进程的日志输出。

### 6. 保留 P1.5a-2 的宿主装配

`Program.cs` 仍注册 `ChannelServer` 工厂、`ChannelServerRunner` 和 `SenderBootstrapper`；监听地址仍是 `IPAddress.Any:8000`；`ChannelServerRunner` 仍先于 `SenderBootstrapper` 注册。本 change 不碰 `channel-hosting` 的规格与代码。

### 7. 空行不在示例协议内

`LineFramer` 允许空行并返回空帧，但 `Utf8StringCodec` 按设计拒绝空帧。示例发送端只发送 `hello\n`，不发送空行；README 说明这一限制，避免用户手工发送空行时误以为示例崩溃。

## Risks / Trade-offs

- **依赖 P1.5a-2 的 `SampleChannelHandler`**：如果只完成 composition-1，示例仍是 `SampleChannelServer`，本 change 的路径不成立；proposal 已把 P1.5a 两个 change 都列为前置条件。
- **线格式增加换行**：外部手工客户端若仍发送裸 `hello`，示例不会打印；README 必须把 `hello\n` 写清楚。
- **空行会导致连接中止**：这是 `Utf8StringCodec` 的既定策略；示例不发送空行，文档说明限制。
- **手工验证依赖 8000 端口**：端口被占用时示例启动失败；README 提示先释放端口。
- **日志输出包含 sender 的 hello**：手工验证时每秒都会看到 `hello` 行；预期结果应说明目标行是 `你好`，不是唯一输出。
- **测试与示例不完全等价**：自动化测试复用 session 组合，不启动示例进程；手工步骤补齐进程级验证。

## Migration Plan

1. 在 `SessionTests.cs` 增加多字节 UTF-8 跨写入回归测试，先确认当前 session 实现通过。
2. 修改 `SampleChannelHandler.HandleAsync`，改用 `AsLineTextSession` 并处理 EOF/取消。
3. 修改 `SenderBootstrapper`，发送 `hello\n`。
4. 构建 sample，运行并确认每秒打印 `hello`；Ctrl+C 正常退出。
5. 按 README 的手工命令验证 `你好` 不被截断。
6. 更新 Primitives README 与 sample README。
7. 运行 sample 构建、全量测试和 `openspec validate`。回滚只需还原 handler、sender 和两份 README，并删除新增测试。

## Open Questions

无。示例不引入空行、二进制协议或客户端框架；这些留给后续示例或 P4。
