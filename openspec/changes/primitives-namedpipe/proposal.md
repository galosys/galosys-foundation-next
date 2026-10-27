## Why

P1/P1.5 的服务端内核只支持 TCP：`ChannelServerCore` 在启动时直接调用 `SocketChannelListener.BindAsync`，`ChannelServerOptions.EndPoint` 也只能表达 socket 端点。Windows 上的本机进程间通信需要命名管道，它不占用 TCP 端口、不暴露网络监听面，适合桌面程序与本地后台服务之间的连接。若继续把 listener 写死为 socket，新增 transport 就必须修改 accept loop 所在的核心类，NamedPipe 也无法与 TCP 共享同一套每连接 scope、`MaxConnections`、优雅停机和取消语义。

本 change 把“如何创建 listener”抽成 internal 工厂缝，并在其上增加 NamedPipe 服务端实现。公共契约 `IChannelServer`、`ChannelContext`、`ChannelServer` 的成员保持不变。

## What Changes

- 新增 public `ChannelTransportKind`（`Tcp` 为默认值，新增 `NamedPipe`）与 public `NamedPipeEndPoint`；`ChannelServerOptions` 增加 `Transport`，启动前校验传输类型与端点类型匹配。
- 新增 internal `IChannelListenerFactory`、`ChannelListenerFactory` 解析器、`SocketChannelListenerFactory` 与 `NamedPipeChannelListenerFactory`。`ChannelServerCore` 只依赖工厂，不再直接调用 `SocketChannelListener.BindAsync`。
- 新增 internal `NamedPipeChannelListener` 与 `NamedPipeChannelConnection`：使用 `NamedPipeServerStream`，通过 `PipeReader.Create` / `PipeWriter.Create` 接入现有 `DuplexPipe` 和 `IChannelConnection` 契约。
- 复用 `ChannelServerCore` 现有的 accept loop、连接跟踪、每连接 `IServiceScope`、`MaxConnections`、优雅停机与取消逻辑；NamedPipe 连接同样创建 `ChannelContext`。
- 新增 NamedPipe 测试：loopback 回显、两个并发客户端、管道名已被占用、优雅停机、取消、默认 TCP、非法传输/端点组合、公开程序集不泄漏 listener/connection 类型。
- 更新 Primitives README，增加 NamedPipe 用法、Windows 限制、唯一管道名要求与 `NamedPipeClientStream` 客户端示例。
- 不修改 public `IChannelServer` / `ChannelContext` 契约；不新增 NamedPipe 客户端封装；不实现 Unix domain socket；不改变 TCP 行为。

## Capabilities

### New Capabilities

- `channel-namedpipe`: 增加命名管道服务端 transport、传输选择配置、internal listener 工厂缝，以及与 TCP 一致的生命周期、连接 scope、停机与取消语义。

### Modified Capabilities

无。现有 `channel-server-transport` 的 TCP 要求保持有效；`Transport` 与 `NamedPipeEndPoint` 是新增配置表面，不改变既有 requirement 的行为。

## Impact

- 代码：新增 `Microsoft/Extensions/Options/ChannelTransportKind.cs`、`Microsoft/Extensions/Hosting/Net/NamedPipeEndPoint.cs`、`Microsoft/Extensions/Hosting/Net/Transports/IChannelListenerFactory.cs`、`ChannelListenerFactory.cs`、`SocketChannelListenerFactory.cs`、`NamedPipeChannelListener.cs`、`NamedPipeChannelConnection.cs`；修改 `ChannelServerOptions.cs`、`ChannelServerCore.cs`。
- 公共 API：新增 `ChannelTransportKind`、`NamedPipeEndPoint`、`ChannelServerOptions.Transport`；`IChannelServer`、`ChannelContext`、`ChannelServer` 无破坏性变更；`IChannelListener` / `IChannelConnection` / 具体 listener 与 connection 类型继续保持 internal。
- 测试：`tests/Galosys.Foundation.Primitives.Tests/` 增加 `NamedPipeTransportTests.cs` 与 `NamedPipeTestSupport.cs`，并在 `NamespaceConventionTests.cs` 增加 internal 类型可见性断言。
- 依赖：不新增 NuGet 包；仅使用 .NET 10 的 `System.IO.Pipes`、`System.IO.Pipelines` 与现有依赖。
- 平台：NamedPipe 服务端仅在 Windows 上验证；测试使用每次唯一的管道名，避免并行测试互相占用。
- 前置：需要先应用 `primitives-server-composition-1`（`ChannelServerCore`、`ChannelServer`），本 change 不以当前仓库中的 `ChannelServerBase` 为最终落点。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`、`openspec validate "primitives-namedpipe" --strict --no-interactive`。
