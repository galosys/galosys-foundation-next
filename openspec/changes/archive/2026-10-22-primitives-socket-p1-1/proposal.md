## Why

`Galosys.Foundation.Primitives` 现有 socket 封装把监听、连接、字节流与 DI 生命周期压进 4 个类型：没有中间件缝隙、没有优雅停机、没有每连接 scope，`ChannelContext` 还是可变的公开数据袋。要让这套封装成为可复用的框架级传输层（TLS、帧协议、遥测、客户端重连），必须先替换内核并冻结服务端最小公共契约；否则后续阶段没有可挂载的抽象。

## What Changes

- **BREAKING** `IChannelServer.StartAsync` 语义改为“绑定成功即返回”，新增 `RunAsync` 承担“阻塞到停机”的旧用法；接口增加 `LocalEndPoint`，主路径改为 `IAsyncDisposable`。
- **BREAKING** `ChannelContext` 从可变数据袋改为只读连接视图：新增 `ConnectionId`、`LocalEndPoint`、`RemoteEndPoint`、`Transport`、`ConnectionClosed`、`Services`、`Abort`；`Reader`/`Writer` 变为只读投影；构造函数收为 internal。
- 保留 `ChannelServerBase` 继承入口与 `ChannelHandleAsync` 签名，现有子类不改代码；内部改用基于 `System.Net.Sockets` + `System.IO.Pipelines` 的自包含 transport，删除旧 accept/收/发循环，不引入 `Microsoft.AspNetCore.*` 依赖。
- 新增管线契约 `ChannelHandlerDelegate`、`ChannelMiddleware`、`IChannelPipeline`，`ChannelServerBase.Configure` 成为组合缝。
- 新增 `ChannelServerOptions`、`ChannelKeepAliveOptions`，覆盖 backlog、连接上限、NoDelay、缓冲、停机超时等；删除写死的 IPv4/backlog=128。
- 每个连接创建独立 `IServiceScope`，连接释放时先触发 `ConnectionClosed` 再释放 scope。
- 删除死代码 `AbstractChannelHandler`。
- 新建测试项目 `tests/Galosys.Foundation.Primitives.Tests`，以真实 loopback socket 覆盖大消息、停机等待在途连接、端口占用、`ConnectionClosed` 恰好一次。
- `src/NetServer.ConsoleApp/ListenerBootstrapper.cs` 改用 `RunAsync`，保持示例行为等价。

## Capabilities

### New Capabilities

- `channel-server-transport`: 服务端监听器生命周期、绑定语义、连接建立/释放、优雅停机与连接级资源所有权。
- `channel-pipeline`: 每连接处理管线的组成、执行顺序与中间件对 `Transport` 的包装约束。

### Modified Capabilities

无。`openspec/specs/` 当前为空，本次为该传输层首次建立规范。

## Impact

- 代码：`src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/**`（重写 4 个类型、新增 Options/Pipeline/Transports）、`src/NetServer.ConsoleApp/ListenerBootstrapper.cs`、新增 `tests/Galosys.Foundation.Primitives.Tests/**`。
- 依赖：`Galosys.Foundation.Primitives.csproj` 使用 .NET 10 内置的 `System.IO.Pipelines`，并显式引用 `Microsoft.Extensions.DependencyInjection.Abstractions`、`Microsoft.Extensions.Logging.Abstractions`；**不增加** `Microsoft.AspNetCore.App` FrameworkReference。测试项目增加 xunit / Microsoft.NET.Test.Sdk。
- 公共 API：`IChannelServer`、`ChannelContext`、`ChannelServerBase` 有破坏性变更；`ChannelClientBase` 保持占位不动（P4 处理）。
- 运行时：仅 `NetServer.ConsoleApp` 引用本库，影响面 1 个示例；`MafSample.API`、`MafSample.WpfApp` 不引用。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`；`MafSample.slnx` 因含不可编译的 `Nats.Net` 不参与验证。
