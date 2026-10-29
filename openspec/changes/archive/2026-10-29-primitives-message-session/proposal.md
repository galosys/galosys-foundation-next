## Why

`primitives-message-framing` 和 `primitives-message-codecs` 分别解决了“消息边界”和“一帧 payload ↔ T”，但处理器仍然要自己写读取循环、处理半包/粘包、区分干净 EOF 与帧中途 EOF、串行化写入并限制消息大小。把这些规则散落在每个 handler 里会重复且容易出错。本 change 提供每连接一个的拉模型会话，把 framer、codec、连接生命周期和并发约束组合起来。

## What Changes

- 新增 `IChannelSession<T>`：同步编解码之上的异步 `ReadAsync`/`WriteAsync`，实现 `IAsyncDisposable`。
- 新增 internal sealed `ChannelSession<T>`：借用 `ChannelContext`，不拥有连接；读循环调用 framer/codec，写路径编码到池化 buffer 后写 frame 并 flush。
- 新增 `MessageChannelOptions`（`Microsoft.Extensions.Options`），提供 `MaxMessageSize`，默认 1 MiB。
- 新增 `ChannelContextSessionExtensions`：`AsSession<T>`、`AsJsonSession<T>`、`AsLineTextSession`，只做组合，不引入第三个合并接口。
- 在 `ChannelContext` 增加 internal 每连接 session 槽位：第二次 `AsSession` 抛 `InvalidOperationException`，避免两个 session 抢同一个 `PipeReader`。
- 读取语义：帧不完整继续等待；干净 EOF 抛 `EndOfStreamException`；帧中途 EOF 抛 `InvalidDataException`；读取消不 `AdvanceTo`，下次可继续；并发读直接抛 `InvalidOperationException`；超过 `MaxMessageSize` 拒绝并中止连接。
- 写入语义：`SemaphoreSlim` 串行化；先编码到池化 buffer 再写 frame；`FlushAsync` 后检查 `IsCompleted`；写取消可能已写半个帧，必须中止连接。
- 释放语义：`DisposeAsync` 幂等，等待在途写并释放 session 自己的状态，不关闭/完成连接；handler 返回后仍由服务器收尾。
- 新增 `SessionTests.cs`，覆盖 loopback 半包/粘包、EOF、取消、并发读、并发写、1 MiB 背压、重复 `AsSession`、Dispose 不关闭连接。
- 更新 Primitives README 的会话章节。
- 本 change 不做后台读循环/队列、`Completion`、客户端/连接池、TLS/压缩、请求响应关联或同端口协议分发。

## Capabilities

### New Capabilities

- `channel-session`: 每连接消息会话的创建约束、拉模型读写、EOF/取消/超限错误语义、写入串行化、背压和释放所有权。

### Modified Capabilities

无。

## Impact

- 代码：新增 `src/Galosys.Foundation.Primitives/Session/IChannelSession.cs`、`ChannelSession.cs`、`ChannelContextSessionExtensions.cs`、`PooledBufferWriter.cs`；新增 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/MessageChannelOptions.cs`；修改 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelContext.cs`（internal 槽位）和 `src/Galosys.Foundation.Primitives/README.md`。
- 公共 API：新增 `IChannelSession<T>`、`ChannelContextSessionExtensions`、`MessageChannelOptions`；`ChannelSession<T>` 保持 internal，调用方只依赖接口。
- 测试：`tests/Galosys.Foundation.Primitives.Tests/SessionTests.cs` 新增 loopback 集成测试；现有测试不需要改写。
- 依赖：依赖 `primitives-message-framing` 与 `primitives-message-codecs` 已合入；不新增 NuGet 包。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~SessionTests`。
- 顺序：必须在 framing 与 codecs 之后实现；`primitives-message-sample` 依赖本 change 和 P1.5a。
