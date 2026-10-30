## Why

`primitives-protocol-dispatch-1` 只提供了选择器和分发器契约，没有可用的选择器。同端口多协议最常见的入口是“首字节/前缀路由”：HTTP、MQTT、自定义二进制协议经常用固定 magic 或首字节区分。调用方如果自己实现，容易把 `PipeReader` 的 `Consumed` 推进到 `buffer.End`，导致选中的协议再也读不到前缀；也容易在只收到半个前缀时误判为未知协议。

本 change 提供一个只窥探、不消费的 `PrefixProtocolSelector`，并明确半包、EOF、无匹配、前缀重叠和 `MaxPrefixSize` 的边界。

## What Changes

- 新增 public sealed `PrefixProtocolRule`：非空字节前缀 + `IChannelProtocol`，构造时复制前缀。
- 新增 public sealed `PrefixProtocolSelectorOptions`，位于 `Microsoft.Extensions.Options`，包含 `MaxPrefixSize`（默认 64）。
- 新增 public sealed `PrefixProtocolSelector`，实现 `IChannelProtocolSelector`，按注册顺序做 first-match 前缀匹配。
- 选择器只使用 `PipeReader.ReadAsync` 窥探（返回时 `AdvanceTo(Start, Start)`，等待半包时 `AdvanceTo(Start, End)`）；半包时等待更多数据，EOF/无匹配/超过 `MaxPrefixSize` 时返回 null。
- 构造时拒绝空前缀、重复前缀、超过 `MaxPrefixSize` 的规则；`MaxPrefixSize <= 0` 被拒绝。
- 新增 `PrefixProtocolSelectorTests.cs`：first-match/重叠、半包、EOF、未知前缀、取消、不消费、超限、构造校验。
- 新增 `PrefixProtocolIntegrationTests.cs`：一个 `ChannelServer` + `ProtocolDispatcher` + 两个前缀协议；半包前缀和未知前缀场景。
- 更新 Primitives README 的同端口前缀路由章节。
- 本 change 不实现 ALPN/TLS，不修改 `ProtocolDispatcher` 的失败语义；选择器返回 null 时仍由分发器中止连接。

## Capabilities

### New Capabilities

无。

### Modified Capabilities

- `channel-protocol-dispatch`：新增 `前缀选择器` 要求，规定 first-match 顺序、半包等待、EOF/无匹配、`MaxPrefixSize` 和只窥探不消费；分发器的既有要求不变。

## Impact

- 代码：新增 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Protocols/PrefixProtocolRule.cs`、`PrefixProtocolSelector.cs`，新增 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/PrefixProtocolSelectorOptions.cs`；修改 `src/Galosys.Foundation.Primitives/README.md`。
- 公共 API：新增三个类型；`IChannelProtocolSelector`、`ProtocolDispatcher`、`ChannelServer` 不变。
- 测试：新增 `tests/Galosys.Foundation.Primitives.Tests/PrefixProtocolSelectorTests.cs`、`PrefixProtocolIntegrationTests.cs`；不改变现有测试。
- 依赖：必须在 `primitives-protocol-dispatch-1` 之后执行；不依赖 P2 的 framer/codec/session，测试处理器使用原始 `PipeReader`/`PipeWriter`。不新增 NuGet 包，不引入 `Microsoft.AspNetCore.*`。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`、`openspec validate "primitives-protocol-dispatch-2" --strict --no-interactive`。
