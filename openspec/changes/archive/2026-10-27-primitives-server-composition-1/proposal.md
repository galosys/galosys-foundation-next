## Why

P1 的 `ChannelServerBase` 把 accept loop、连接跟踪、停机逻辑和管线配置压在继承里。一个 host 要跑多个 server、同一 server 类跑多个端口时，只能不断派生子类；在删除这个基类之前，需要先提供一个不破坏现有消费方的组合式入口，并把生命周期内核从继承层里抽出来。

## What Changes

- 从 `ChannelServerBase` 抽取 internal `ChannelServerCore`，原样承载 accept loop、连接跟踪、每连接 scope、优雅停机和释放逻辑。
- 新增 public sealed `ChannelServer`，构造函数接收 `ChannelServerOptions`、`Action<IChannelPipeline>` 以及可空的 `ILoggerFactory` / `IServiceScopeFactory`；不再要求继承。
- `ChannelServerBase` 暂时保留为兼容外观，内部委托给 `ChannelServerCore`；现有 sample、tests 和 `AddChannelServer<T>` 不改。
- `ChannelServer` 的 pipeline 配置在 `StartAsync` 时执行一次，必须调用 `Run`；options 在构造时克隆，多个实例互不影响。
- 新增 `ChannelServer` 的生命周期、回显、端口占用、`RunAsync`、无 logger/scope、pipeline 配置测试。
- 更新 Primitives README，增加组合式入口示例；继承式用法保留到下一个 change 再删除。
- 本 change 不删除 `ChannelServerBase`，不迁移 `NetServer.ConsoleApp`，不改 `AddChannelServer<T>`。

## Capabilities

### New Capabilities

无。

### Modified Capabilities

- `channel-server-transport`: 增加 sealed `ChannelServer` 组合式服务器入口，明确其构造、pipeline 配置、独立 options 与生命周期语义；现有 `IChannelServer` 行为要求对 `ChannelServer` 同样成立。

## Impact

- 代码：`src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerBase.cs`、新增 `ChannelServerCore.cs`、新增 `ChannelServer.cs`、`src/Galosys.Foundation.Primitives/README.md`。
- 公共 API：新增 `ChannelServer`；`ChannelServerBase`、`IChannelServer` 与 `AddChannelServer<T>` 保持可用，无破坏性变更。
- 测试：`tests/Galosys.Foundation.Primitives.Tests/` 增加 `ChannelServerTests.cs`；现有 67 个测试不需要改写。
- 依赖：不新增 NuGet 包；不引入 `Microsoft.AspNetCore.*`。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet build samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`。
