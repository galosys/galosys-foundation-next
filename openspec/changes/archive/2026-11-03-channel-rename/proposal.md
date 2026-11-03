# Proposal

## Why

`Galosys.Foundation.Primitives` 的名字与它的实际内容不符：它不是"一堆通用小工具"，而是一个有明确边界的**逻辑通道传输框架**（socket 传输 + 消息分帧编解码 + 客户端/服务端 + 宿主接入）。`Primitives` 这个名字（类比 `Newtonsoft.Json` 的 Primitives）会让使用者低估它的能力边界，也无法在新增 NamedPipe 等 transport 时继续成立。

同时，公共类型全部使用 `Channel*` 前缀（`ChannelServer`、`ChannelClient`、`ChannelContext` 等 46 个），却寄居在 `Microsoft.Extensions.Hosting.Net` 命名空间下。程序集名、命名空间名、类型名三者互不对应，读者需要额外维护一层映射。本 change 让三者统一为 `Channel`。

## What Changes

- **BREAKING** 程序集与项目重命名：`Galosys.Foundation.Primitives` → `Galosys.Foundation.Channel`；目录 `src/Galosys.Foundation.Primitives/` → `src/Galosys.Foundation.Channel/`；测试项目 `tests/Galosys.Foundation.Primitives.Tests/` → `tests/Galosys.Foundation.Channel.Tests/`；NuGet 包名 `galosys.foundation.channel`。
- **BREAKING** 公共类型命名空间迁移：核心类型从 `Microsoft.Extensions.Hosting.Net` 迁移到 `Galosys.Foundation.Channel`（codec、framing、session、server、client、pipeline、protocols、transports 共 44 个文件）。
- **BREAKING** `ChannelServerGroupHostedService` 从 `Microsoft.Extensions.Hosting.Net` 迁移到 `Microsoft.Extensions.Hosting`（宿主生命周期适配器归属宿主命名空间）。
- 保持不变：`*Options`（10 个）继续位于 `Microsoft.Extensions.Options`；DI 扩展 `AddChannelServer` / `AddChannelServerGroup` 继续位于 `Microsoft.Extensions.DependencyInjection`。
- 保持不变：**全部 `Channel*` 公共类型名不变**，因此不产生类型级破坏性变更；公共 API 的唯一破坏点是程序集名与命名空间。
- 物理目录按新命名空间重排：`Galosys/Foundation/Channel/`（核心类型）、`Microsoft/Extensions/Options/`、`Microsoft/Extensions/DependencyInjection/`、`Microsoft/Extensions/Hosting/`（后者三者位于项目根，与 `Galosys/` 平级）。
- 更新 `Galosys.Foundation.Next.slnx`、`NetServer.ConsoleApp` 项目引用、根 `README.md`、`AGENTS.md`、库 `README.md`、`Directory.Packages.props`。
- 更新 `openspec/specs/` 中受影响的活 spec，以及在途 change（`primitives-tls-transport-1/2`、`primitives-namedpipe`、`primitives-diagnostics`、`primitives-performance-baseline`）内部的路径引用；**change ID 与目录名保持不变**。
- 不改 `openspec/changes/archive/**`（历史归档）。

## Capabilities

### New Capabilities

无。

### Modified Capabilities

- `repository-layout`: 项目清单中的库名与测试项目名从 `Galosys.Foundation.Primitives` 改为 `Galosys.Foundation.Channel`；解决方案路径与引用路径同步更新。
- `foundation-namespace-conventions`: Options 类型的程序集归属改为 `Galosys.Foundation.Channel`；新增"核心类型位于 `Galosys.Foundation.Channel` 命名空间、目录与命名空间一致"的约定。
- `channel-server-transport`: 反射断言的程序集名与类型全限定名从 `Galosys.Foundation.Primitives` / `Microsoft.Extensions.Hosting.Net.ChannelServerBase` 改为 `Galosys.Foundation.Channel` / `Galosys.Foundation.Channel.ChannelServerBase`；核心依赖边界的程序集名同步更新。
- `channel-client`: `IChannelClient` / `ChannelClient` 的命名空间断言从 `Microsoft.Extensions.Hosting.Net` 改为 `Galosys.Foundation.Channel`。
- `channel-client-pool`: `IChannelClientFactory` 的命名空间断言同步更新。
- `channel-server-group`: `ChannelServerGroup` 的命名空间断言同步更新。
- `channel-protocol-dispatch`: `IChannelProtocol` / `IChannelProtocolSelector` 的命名空间断言同步更新。
- `channel-hosting`: `ChannelServerDefinition` 与 DI 扩展的命名空间断言同步更新。

## Impact

- 代码：`src/Galosys.Foundation.Channel/` 下 56 个文件的 `namespace` 声明与 `using` 引用；目录重排；`Galosys.Foundation.Channel.csproj` 的 `InternalsVisibleTo` 指向 `Galosys.Foundation.Channel.Tests`。
- 测试：`tests/Galosys.Foundation.Channel.Tests/` 全部测试文件的 `using` 引用；`NamespaceConventionTests` 的反射断言全限定名；测试 csproj 的 `RootNamespace`。
- 解决方案：`Galosys.Foundation.Next.slnx` 中 2 个 `<Project Path>`（保留原 Id GUID）。
- 引用方：`samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj` 的 `ProjectReference`。
- 依赖：不新增 NuGet 包；`Directory.Packages.props` 增加 `galosys.foundation.channel` 版本项以对齐既有 `galosys.foundation.*` 约定。
- 公共 API：程序集名与核心命名空间为破坏性变更；类型名、成员签名、行为语义全部不变。
- 命名空间冲突：库内无裸 `Channel` 类型（全部为 `Channel*` 复合词），且 `src/`、`tests/` 不引用 `System.Threading.Channels`，因此与 BCL `Channel<T>` 无编译冲突。`ChannelClientPool.cs` 内部使用 BCL `Channel<IChannelClient>` 保持不变。
