## Why

多服务器组合解决了“不同端口、不同 server”的编排，但没有解决“同一端口、多个协议”。调用方如果自己在 handler 里读首字节再分支，会把协议探测、取消、窥探不消费和错误中止混进业务处理器，也无法复用。P2.5 需要一个独立的协议选择契约和一个只做编排的分发器，让同一个 `ChannelServer` 可以挂载多个协议。

本 change 先建立最小骨架：`IChannelProtocol`、`IChannelProtocolSelector`、`ProtocolDispatcher`。具体的前缀选择器、ALPN 和 TLS 留给后续 change。

## What Changes

- 新增 `IChannelProtocol`：协议名称与 `HandleAsync(ChannelContext, CancellationToken)`。
- 新增 `IChannelProtocolSelector`：`SelectAsync(ChannelContext, CancellationToken)` 返回可空协议；选择器只能窥探输入，不能消费字节。
- 新增 sealed `ProtocolDispatcher`，其 `HandleAsync(ChannelContext)` 直接匹配 `ChannelHandlerDelegate`，可通过 `pipeline.Run(dispatcher.HandleAsync)` 挂到任意 `ChannelServer`。
- 分发器使用 `context.ConnectionClosed` 作为选择与协议执行的取消令牌，选择器只调用一次，选中后调用协议。
- 选择器返回 null / 无匹配，或选择器抛异常时，分发器用 `InvalidDataException` 中止连接；选择器异常作为 inner exception 保留。
- 取消（连接关闭）不产生 `InvalidDataException`；分发器安静返回。
- 新增 `ProtocolDispatcherTests.cs`：fake selector / protocol，覆盖调用顺序、成功分发、无匹配中止、选择器异常、取消、未知协议不消费输入。
- 更新 Primitives README，增加协议分发用法。
- 本 change 不实现 `PrefixProtocolSelector`、ALPN、TLS 或同端口集成测试。

## Capabilities

### New Capabilities

- `channel-protocol-dispatch`：协议选择契约、`ProtocolDispatcher` 的调用顺序、取消和 `InvalidDataException` 中止语义，以及选择器只窥探不消费的约束。

### Modified Capabilities

无。

## Impact

- 代码：新增 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Protocols/IChannelProtocol.cs`、`IChannelProtocolSelector.cs`、`ProtocolDispatcher.cs`；修改 `src/Galosys.Foundation.Primitives/README.md`。
- 公共 API：新增三个类型；`ChannelServer`、`IChannelPipeline`、`ChannelHandlerDelegate`、`ChannelContext` 不变。
- 测试：新增 `tests/Galosys.Foundation.Primitives.Tests/ProtocolDispatcherTests.cs`；现有 0 个协议测试，不影响现有测试。
- 依赖：建议在 `primitives-server-composition-1/2` 之后执行，测试使用组合式 `ChannelServer` 与现有 `ChannelContext`；不依赖 `ChannelServerGroup`。不新增 NuGet 包，不引入 `Microsoft.AspNetCore.*`。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`、`openspec validate "primitives-protocol-dispatch-1" --strict --no-interactive`。
