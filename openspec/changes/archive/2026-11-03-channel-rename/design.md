# Design

## Context

`Galosys.Foundation.Primitives` 是一个逻辑通道传输框架：socket 传输（`System.Net.Sockets` + `System.IO.Pipelines`）、消息分帧与编解码、客户端/服务端、宿主接入。公共类型全部以 `Channel` 复合词前缀命名（`ChannelServer`、`ChannelClient`、`ChannelContext`、`IChannelSession` 等 46 个），但程序集名为 `Primitives`、核心类型寄居在 `Microsoft.Extensions.Hosting.Net` 命名空间。

当前命名空间布局：

| 命名空间 | 文件数 | 物理目录 |
|---|---|---|
| `Microsoft.Extensions.Hosting.Net` | 44 | `Microsoft/Extensions/Hosting/Net/**`（部分）、`Codecs/`、`Framing/`、`Session/` |

目标命名空间布局：

| 命名空间 | 文件数 | 物理目录 |
|---|---|---|
| `Galosys.Foundation.Channel` | 44 | `Galosys/Foundation/Channel/**` |
| `Microsoft.Extensions.Options` | 10 | `Microsoft/Extensions/Options/` |
| `Microsoft.Extensions.DependencyInjection` | 1 | `Microsoft/Extensions/DependencyInjection/` |
| `Microsoft.Extensions.Hosting` | 1 | `Microsoft/Extensions/Hosting/` |

约束：

- 仅 Windows，.NET SDK 10.0.401（`global.json` 固定）
- 库不得引用 `Microsoft.AspNetCore.*`（`channel-server-transport` 的硬约束，有自动化测试守护）
- 解决方案整体构建会失败（`Galosys.Foundation.Nats.Net` 不可编译），只能单项目构建
- `openspec/changes/archive/**` 是历史记录，不可改写
- `openspec/specs/` 下 8 个活 spec 直接写死了程序集名或命名空间

## Goals / Non-Goals

**Goals:**

- 程序集名、命名空间名、类型名前缀三者统一到 `Channel`
- 公共 API 的破坏面收敛到「程序集名 + 命名空间」两项；类型名与成员签名完全不变
- 物理目录与命名空间一一对应，兑现 `foundation-namespace-conventions` 的既有约定
- 不产生 `System.Threading.Channels` 的编译歧义

**Non-Goals:**

- 不重命名任何 `Channel*` 公共类型（`ChannelServer` → `TransportServer` 等一律不做）
- 不重命名 `NetServer.ConsoleApp` 示例项目（其名字描述「网络服务器」语义，与库名无关）
- 不拆分多个 transport 子包；`SocketChannelListener` / `NamedPipeChannelListener` 保持 `internal`
- 不改在途 change 的目录名与 change ID（`primitives-tls-transport-1/2`、`primitives-namedpipe`、`primitives-diagnostics`、`primitives-performance-baseline`）
- 不改 `openspec/changes/archive/**`

## Decisions

### 决策 1：库名 `Galosys.Foundation.Channel`，命名空间 `Galosys.Foundation.Channel`

**理由**：库内 46 个公共类型全部是 `Channel*` 前缀。程序集名、命名空间、类型名用同一个词，读者无需维护额外映射。

**备选与放弃原因**：

| 备选 | 放弃原因 |
|---|---|
| `Galosys.Foundation.Sockets` | 与 `Channel*` 类型名错位；且 `primitives-namedpipe` 计划引入 NamedPipe transport，`Sockets` 会把库名锁死在 socket 实现上 |
| `Galosys.Foundation.Transport` / `Transports` | `Transports` 复数暗示「多个并列 transport 实现」，但公共 API 里 transport 实现全是 `internal`，用户看不到；`Transport` 单数则与 `Channel*` 类型名不一致 |
| `Galosys.Extensions.Sockets` | `Extensions` 在 .NET 生态惯例指扩展方法集，与完整框架定位不符；且破坏既有 `galosys.foundation.*` 品牌一致性 |
| `Galosys.Foundation.Primitives`（维持现状） | 名字与内容不符，且无法容纳 NamedPipe 等后续 transport |

### 决策 2：公共类型名保持 `Channel*` 不变

**理由**：本次改名的唯一破坏点已经足够多（程序集名 + 命名空间 + 目录 + 全部引用方）。类型名不变意味着：

- 使用方只需改 `using` 语句，不需要改任何类型引用
- `NamespaceConventionTests` 等反射断言只需更新命名空间字符串
- 46 个公共类型的 XML 文档、`<see cref>` 引用全部无需改动

**备选与放弃原因**：全量改名为 `Transport*` 涉及 750 处标识符 + 18 个测试文件重命名，且会引入**语义倒置**——现有命名区分了两个层次：公共层是「逻辑通道」（有消息边界、有 framing/codec），内部 `Transports/` 目录才是「传输实现」（`IChannelListener` / `IChannelConnection`）。全量改名后，`IChannelListener`（传输）反而叫 Channel，`ChannelServer`（通道）反而叫 Transport。保留现状的层次区分是正确的。

### 决策 3：`ChannelServerGroupHostedService` 迁到 `Microsoft.Extensions.Hosting`

**理由**：该类型是 `IHostedService` 的宿主生命周期适配器，归属宿主命名空间比归属库命名空间更准确；且 `Microsoft.Extensions.Hosting` 是它所实现的接口所在命名空间，放在那里使用方无需额外 `using` 即可引用。类型名唯一，与 .NET 内置宿主类型无冲突。

### 决策 4：`*Options` 保留 `Microsoft.Extensions.Options`，DI 扩展保留 `Microsoft.Extensions.DependencyInjection`

**理由**：这两处「寄生」是本仓库既定的命名空间约定（`foundation-namespace-conventions` spec 明确规定），且与 `Microsoft.Extensions.Options.ConfigurationExtensions` 的配置绑定惯例、`IServiceCollection` 扩展方法的发现惯例一致。改名它们会破坏这些生态衔接，收益为零。

### 决策 5：核心类型使用单一扁平命名空间 `Galosys.Foundation.Channel`

**理由**：现状是「一个命名空间装 44 个文件」，已经工作良好且被 `foundation-namespace-conventions` 规范化为「目录与命名空间一致」。改成 `Galosys.Foundation.Channel.Codecs` / `.Framing` 等子命名空间会让使用方需要多个 `using`，对一次改名的收益不匹配。

**代价与缓解**：物理目录从「部分在 `Microsoft/Extensions/Hosting/Net/`、部分在项目根」统一收拢到 `Galosys/Foundation/Channel/` 下，消除现状中 `Codecs/`、`Framing/`、`Session/` 在根而 `Client/`、`Pipeline/` 在 `Microsoft/Extensions/Hosting/Net/` 的不一致。

### 决策 6：目标物理布局

```
src/Galosys.Foundation.Channel/
├── Galosys/
│   └── Foundation/
│       └── Channel/                    → namespace Galosys.Foundation.Channel
│           ├── ChannelContext.cs
│           ├── ChannelServer.cs
│           ├── ChannelServerCore.cs
│           ├── ChannelServerDefinition.cs
│           ├── ChannelServerGroup.cs
│           ├── IChannelServer.cs
│           ├── Client/          (8 个文件)
│           ├── Codecs/          (4 个文件)
│           ├── Framing/         (5 个文件)
│           ├── Pipeline/        (4 个文件)
│           ├── Protocols/       (5 个文件)
│           ├── Session/         (6 个文件)
│           └── Transports/      (6 个文件)
├── Microsoft/
│   └── Extensions/
│       ├── DependencyInjection/  → namespace Microsoft.Extensions.DependencyInjection
│       │   └── ChannelServerGroupServiceCollectionExtensions.cs
│       ├── Hosting/              → namespace Microsoft.Extensions.Hosting
│       │   └── ChannelServerGroupHostedService.cs
│       └── Options/              → namespace Microsoft.Extensions.Options
│           └── (10 个 Options 文件)
└── Galosys.Foundation.Channel.csproj
```

### 决策 7：分 3 个 commit 落地

**理由**：本次改动跨越目录重命名、命名空间迁移、文档与 spec 更新三层。混在一个 commit 里 review 困难、bisect 无效、回滚粒度太粗。拆分后：

1. `refactor!: rename Galosys.Foundation.Primitives to Galosys.Foundation.Channel` — 纯 `git mv` + csproj + slnx + ProjectReference，零内容变更
2. `refactor!: move public types to Galosys.Foundation.Channel namespace` — 56 个文件的 namespace/using + 目录重排 + 测试断言
3. `docs: update docs and openspec specs for Channel rename` — README、AGENTS.md、活 spec、在途 change 路径引用

中间态（第 1 个 commit 后）代码仍可编译，因为程序集名变化不影响命名空间。第 2 个 commit 是唯一的语义变更点。

## Risks / Trade-offs

**[`Microsoft.Extensions.Hosting.Net` 命名空间是 `Microsoft.Extensions.*` 寄生]** → 迁走后，库不再向使用方暴露任何 `Microsoft.Extensions.*` 下的自定义类型（`Options` 与 DI 扩展除外，那是刻意的生态衔接）。`NetServer.ConsoleApp` 与全部测试需要补 `using Galosys.Foundation.Channel;`。

**[`ChannelClientPool.cs` 内部使用 BCL `Channel<IChannelClient>`]** → 该文件同时需要 `using Galosys.Foundation.Channel;`（库的 `ChannelClient`）和 `using System.Threading.Channels;`（BCL 的 `Channel<T>`）。两者无类型名冲突（库内无裸 `Channel` 类型），编译无歧义。已实测 `src/`、`tests/` 此前完全不引用 `System.Threading.Channels`，无扩散风险。

**[`InternalsVisibleTo` 目标程序集名变更]** → 测试项目内的 `InternalsVisibleTo` 可见性（如 `ChannelServerCore`、`IChannelListener` 的 internal 断言）依赖程序集名匹配。改错会导致测试编译失败或断言失效，`DependencyBoundaryTests` 与 `NamespaceConventionTests` 是主要哨兵。

**[活 spec 写死程序集名导致 archive 阶段失配]** → `channel-server-transport` 的反射场景断言 `Galosys.Foundation.Channel.ChannelServerBase` 不存在。归档时 delta 会替换整块，场景名与 header 必须与主 spec 完全一致，否则 archive 拒绝。已在 `openspec validate --strict` 中校验。

**[在途 change 的 tasks.md 路径失配]** → `primitives-tls-transport-1/2` 等 change 的 tasks.md 引用 `src/Galosys.Foundation.Primitives/...`。本 change 更新这些路径引用，但保留 change 目录名与 ID，避免 openspec 的 change 身份漂移。后续 apply 这些 change 时按新路径执行。

**[`GlobalUsings.g.cs` 与 obj 缓存残留旧程序集名]** → `dotnet build` 会重新生成；`obj/`、`bin/` 已在 `.gitignore` 中，不纳入版本控制。改名后首次构建可能出现陈旧 `project.assets.json`，必要时清理 `obj/`。

**[NuGet 包名变更影响下游]** → `galosys.foundation.primitives` → `galosys.foundation.channel` 是包 ID 破坏性变更。本仓库尚未发布该包（`Directory.Packages.props` 中原本没有 `galosys.foundation.primitives` 条目），因此实际影响为零；本次新增 `galosys.foundation.channel` 版本项以对齐既有 `galosys.foundation.*` 约定。

## Migration Plan

1. **提交 1**：纯重命名。`git mv` 三个目录/文件对，更新 csproj 的 `InternalsVisibleTo` 与测试 csproj 的 `RootNamespace`，更新 `Galosys.Foundation.Next.slnx` 的两个 `<Project Path>`（保留原 Id GUID），更新 `NetServer.ConsoleApp.csproj` 的 `ProjectReference`。
2. **提交 2**：命名空间迁移。56 个文件的 `namespace` 与 `using` 调整，目录重排到决策 6 的布局，`NamespaceConventionTests` 反射断言更新。
3. **提交 3**：文档与 spec。根 `README.md`、`AGENTS.md`、库 `README.md`、`samples/NetServer.ConsoleApp/README.md`、`Directory.Packages.props`；活 spec 8 个文件；在途 change 的路径引用。

**回滚策略**：三个 commit 均为纯重命名/文本替换，无数据迁移、无外部状态。`git revert` 单个 commit 即可回退到上一态。提交 1 与 2 之间代码始终可编译，回滚不需要额外修复。

**验证命令**：

```powershell
dotnet build src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj
dotnet test tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj
dotnet build samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj
dotnet sln Galosys.Foundation.Next.slnx list
openspec validate "channel-rename" --strict --no-interactive
```

## Open Questions

无。命名、命名空间归属、目录布局、提交拆分策略均已确定，且不依赖后续信息。
