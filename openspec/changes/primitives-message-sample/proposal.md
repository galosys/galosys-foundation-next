## Why

P1.5a 已经把 `NetServer.ConsoleApp` 迁到组合式 `ChannelServer`，但示例仍在 handler 里直接按块 `Encoding.UTF8.GetString(buffer)`。TCP 不保证一次读取对应一条消息，多字节 UTF-8 字符可能被拆到两次读取中，示例会打印出替换字符；同时示例也没有展示 P2 的消息会话。本 change 把示例迁到 `context.AsLineTextSession()`，用 `LineFramer` + `Utf8StringCodec` 保证按行、按完整字符处理，并保留现有监听地址、日志和 Ctrl+C 行为。

## What Changes

- 把 `SampleChannelHandler.HandleAsync` 改为在终端 handler 内创建 `context.AsLineTextSession()`，循环读取行并打印 `>>>>> {line} <<<<<`；`EndOfStreamException` 和连接关闭引起的 `OperationCanceledException` 视为正常结束。
- `SenderBootstrapper` 每秒发送的 `hello` 改为 `hello\n`，让 `LineFramer` 能切出完整消息；逻辑消息仍是 `hello`。
- 保留监听 `0.0.0.0:8000`、每秒发送 `hello`、Ctrl+C 优雅停机，以及 P1.5a 的 `ChannelServer` + `ChannelServerRunner` 装配。
- 更新 `samples/NetServer.ConsoleApp/README.md` 与 `src/Galosys.Foundation.Primitives/README.md`，说明 session 只在终端 handler 创建、行协议包含换行分隔符，以及多字节 UTF-8 的验证方法。
- 在 `SessionTests.cs` 增加“UTF-8 多字节字符跨两次写入仍被完整解码”的回归测试；示例 README 同时提供手工验证命令和预期输出。
- 在 `channel-session` 能力中新增 `示例迁移` 要求；不修改 `channel-hosting`。
- 本 change 不修改库代码、消息会话契约、`ChannelServer` 或 `ChannelServerRunner`。

## Capabilities

### New Capabilities

无。

### Modified Capabilities

- `channel-session`: 新增 `示例迁移` 要求，规定 `NetServer.ConsoleApp` 使用 `AsLineTextSession`、保留既有示例行为，并验证多字节 UTF-8 不跨读取截断。

## Impact

- 代码：修改 `samples/NetServer.ConsoleApp/SampleChannelHandler.cs`、`samples/NetServer.ConsoleApp/SenderBootstrapper.cs`；修改 `samples/NetServer.ConsoleApp/README.md`、`src/Galosys.Foundation.Primitives/README.md`；修改 `tests/Galosys.Foundation.Primitives.Tests/SessionTests.cs`。
- 公共 API：无新增或删除；示例只消费 `IChannelSession<T>`、`AsLineTextSession`、`LineFramer`、`Utf8StringCodec`。
- 依赖：依赖 P1.5a（`primitives-server-composition-1` 与 `primitives-server-composition-2`）已合入，示例已有 `SampleChannelHandler`/`ChannelServerRunner`；依赖 `primitives-message-session` 已合入。
- 测试：新增 1 个多字节 UTF-8 跨写入回归测试；现有测试不改写。
- 验证：`dotnet build samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`、`dotnet run --project samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~SessionTests`。
- 顺序：必须在 `primitives-message-session` 合入后执行；不修改 `channel-hosting`，避免与 `primitives-server-composition-2` 的宿主规格冲突。
