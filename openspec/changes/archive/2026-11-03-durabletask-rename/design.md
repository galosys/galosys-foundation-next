# Design

## Context

参见 `proposal.md` - Why。本节只补充实现所需的现状事实与约束。

**现状事实（已验证）：**

- 库当前导出且仅导出 3 个命名空间（由 `obj/Debug/net10.0/*.dll` 元数据扫描确认）：
  - `DurableTask.Core` —— `TaskHubClientExtensions`（public static）、`ServiceProviderObjectCreator`（internal）
  - `Microsoft.Extensions.DependencyInjection` —— `DurableTaskServiceCollectionExtensions`
  - `Microsoft.Extensions.Hosting` —— `DurableTaskHostedService`（internal）
- `DurableTaskAttribute.cs` 被 csproj 的 `<Compile Remove="DurableTask\Core\DurableTaskAttribute.cs" />` 排除在编译之外，**不在**产物中；其声明的命名空间 `Galosys.Foundation.Azure.DurableTask.Core.DurableTask.Core` 目前是死代码，且是全仓唯一以旧项目全名开头的命名空间。
- `DurableTask.Core` 同时是上游 `Microsoft.Azure.Durabletask.core` 包的命名空间。本库通过在同命名空间下追加类型来"扩展"它。
- 消费方 `samples/DTFx.ConsoleApp/Worker.cs` 依赖 `using DurableTask.Core;` 同时获得：上游包的 `TaskHubClient` / `TaskOrchestration<,>` / `TaskActivity<,>` / `OrchestrationContext`，以及本库的 `RaiseAsync` 扩展方法。
- 该消费方还依赖 `Microsoft.Extensions.DependencyModel` 的 `GetImplementTypes()`（来自 `galosys.foundation.core` 包）做程序集扫描。

**约束：**

- 仓库已有一次同类先例：`Galosys.Foundation.Primitives` → `Galosys.Foundation.Channel`（`openspec/changes/archive/2026-11-03-channel-rename/`），采用三提交分层：纯重命名 → 命名空间迁移 → 文档/spec 更新。本 change 沿用同一模式。
- `AGENTS.md` 规定只构建改动的单个项目，`dotnet build Galosys.Foundation.Next.slnx` 会失败（`Galosys.Foundation.Nats.Net` 不可编译）。
- `git-x` 约束：禁止在 `main` 上直接提交，须从 `main` 新建分支。

## Goals / Non-Goals

**Goals:**

- 程序集名、命名空间名、物理目录三者统一为 `DurableTask`。
- 核心类型不再扩展上游 NuGet 包的命名空间，遵循仓库既有的"不占用他人命名空间"约定。
- 破坏面收敛到**程序集名 + 核心类型命名空间 + 消费方一处 `using`** 三项，类型名与成员签名零变更。
- 保留上游包的 `DurableTask.Core` 命名空间**可被消费方正常使用**（不破坏 `using DurableTask.Core;` 对上游类型的解析）。

**Non-Goals:**

- 不改 `DurableTaskAttribute` 的编译排除状态——它当前无使用方，恢复编译会引入一个无人使用的 public API，属独立决策。
- 不改 DI 注册语义（`AddDurableTaskCore` 的扫描与注册逻辑保持原样）。
- 不改上游包版本、不引入新 NuGet 依赖。
- 不为 `Galosys.Foundation.DurableTask.Core` 新建测试项目——仓库当前只有 Channel 一个测试项目，且本库无可测行为面（全部是对上游 SDK 的薄封装）。

## Decisions

### 决策 1：核心类型迁出 `DurableTask.Core`，迁到 `Galosys.Foundation.DurableTask.Core`

**选择**：迁移。

**理由**：这与 Channel 先例的动机完全一致——仓库当初特意把类型从 `Microsoft.Extensions.Hosting.Net` 挪到 `Galosys.Foundation.Channel`，就是为了不占用框架命名空间。`DurableTask.Core` 是上游 NuGet 包的命名空间，属于同一类问题：在他人命名空间里追加类型，会让消费方的 `using DurableTask.Core;` 语义变得模糊（本库类型 vs 上游类型），也让程序集名 / 命名空间名 / 类型归属三者不对应。

**备选方案**：
- *保留 `DurableTask.Core`（零改动）*：破坏面最小，消费方无需加 `using`。但占用上游包命名空间的问题原样保留，且新程序集名 `Galosys.Foundation.DurableTask.Core` 与核心命名空间 `DurableTask.Core` 的差距依旧存在——本次重命名的收益会被大幅抵消。**否决**。
- *迁移到 `Galosys.Foundation.DurableTask`*（不含 `.Core`）：更短，但 `Core` 后缀与仓库内 `Galosys.Foundation.Channel` / `Galosys.Foundation.Doris` 的命名风格不一致（均无 `Core` 后缀）。**否决**，沿用 `.Core` 以贴近用户给定的目标名。

### 决策 2：消费方 `Worker.cs` 显式补 `using Galosys.Foundation.DurableTask.Core;`

**选择**：补一行 `using`。

**理由**：迁移后 `RaiseAsync` 扩展方法位于新命名空间，而 `TaskHubClient` 仍来自上游包的 `DurableTask.Core`。消费方需要两个 `using` 才能同时拿到扩展方法与被扩展类型。显式 `using` 是 C# 的标准做法，且比"隐式同名命中"更清晰。

**注意**：`using Galosys.Foundation.DurableTask.Core;` 与 `using DurableTask.Core;` 并存时，`TaskHubClient` 只在上游命名空间存在，**无歧义**；`RaiseAsync` 只在新命名空间存在，**无歧义**。两者不会产生类型名冲突。

### 决策 3：物理目录重排为 `Galosys/Foundation/DurableTask/Core/`

**选择**：对齐命名空间，参照 Channel 先例的 `Galosys/Foundation/Channel/` 布局。

**理由**：`foundation-namespace-conventions` spec 已要求"物理目录与命名空间一致"，且 Channel 先例已确立 `Galosys/Foundation/<Name>/` 这一布局范式。当前 `DurableTask/Core/` 既不对应新命名空间，其 `Core` 子目录本身也是冗余的（`DurableTask.Core` 命名空间的两段在新方案下已无意义）。

**备选方案**：保留 `DurableTask/Core/` 不动。改动面更小，但会让"目录与命名空间一致"的约定在本库上破例，且 `DurableTask/Core/` 目录名与上游包命名空间 `DurableTask.Core` 视觉上高度相似，日后极易混淆。**否决**。

### 决策 4：`DurableTaskAttribute.cs` 的命名空间同步但保持排除编译

**选择**：把 `namespace Galosys.Foundation.Azure.DurableTask.Core.DurableTask.Core` 改为 `namespace Galosys.Foundation.DurableTask.Core.DurableTask.Core`，保持 `<Compile Remove>` 不变。

**理由**：该文件当前不参与编译（产物中无此类型），但它是全仓唯一残留旧项目全名 `Galosys.Foundation.Azure.DurableTask.Core` 的标识符。若不处理，"重命名后仓库中不再有旧名残留"的验收条件将无法通过。保持排除编译状态则不引入新的 public API，不扩大本次重命名的破坏面。

**注意**：`<Compile Remove>` 的路径需随目录重排同步更新，否则会指向不存在的路径——`Compile Remove` 对不存在的路径不报错，会**静默失效**，导致该文件在迁移后被意外编译进来。这是本 change 最容易踩的坑，已在 tasks 中单列验证步骤。

### 决策 5：三提交分层落地

**选择**：提交 1 纯重命名（零内容变更）→ 提交 2 命名空间迁移 + 目录重排 → 提交 3 文档与 spec 更新。

**理由**：沿用 Channel 先例的提交结构。每层单独可编译、可回滚，出问题时二分定位清晰。提交 1 之后代码仍用旧命名空间但程序集已改名，是合法的中间态。

## Risks / Trade-offs

**[消费方 `using DurableTask.Core;` 失效导致示例编译失败]** → 迁移后若忘记补 `using Galosys.Foundation.DurableTask.Core;`，`client.RaiseAsync(...)` 会报"找不到扩展方法"。缓解：提交 2 末尾强制 `dotnet build samples/DTFx.ConsoleApp/DTFx.ConsoleApp.csproj` 作为验收门禁。

**[`<Compile Remove>` 路径失效导致 `DurableTaskAttribute.cs` 被意外编译]** → 目录重排后旧路径不存在，`Compile Remove` 静默失效，文件被纳入编译；此时它的 `namespace` 仍指向旧名，会让"无旧名残留"的验收失败。缓解：tasks 中单列一条任务，显式断言编译产物中不存在 `DurableTaskAttribute` 类型。

**[上游包的 `DurableTask.Core` 类型被误认为本库所有]** → 本库迁移后，消费方 `using DurableTask.Core;` 拿到的全部是上游类型。缓解：spec 中「上游包命名空间由包单独拥有」场景显式固定该行为。

**[`DurableTask.Core` 命名空间的扩展方法在 DI 扫描时不被发现]** → `AddDurableTaskCore` 通过 `DependencyContext.Default.GetImplementTypes()` 扫描 `TaskOrchestration<,>` / `TaskActivity<,>` 的实现类型。迁移的是本库的扩展方法与 internal 类型，**不涉及**被扫描的编排/活动类型（它们由消费方在 `DTFx.ConsoleApp` 命名空间下定义）。缓解：构建 + 运行示例验证编排被正确注册。

**[`GenerateDocumentationFile=true` 产生的 CS1591 警告]** → 本库 csproj 未单独开启文档生成，但 `Directory.Build.props` 全局设置 `GenerateDocumentationFile=true`。重命名不新增 public 成员，不引入新的 CS1591。缓解：构建输出对比警告数（当前基线 5 个）。

**[NuGet 包名 `galosys.foundation.durabletask.core` 尚未发布]** → `Directory.Packages.props` 中新增的 `PackageVersion` 仅作占位对齐，该库当前未被任何项目以包形式引用（全部走 ProjectReference），因此不存在还原失败风险。

## Migration Plan

1. 从 `main` 新建分支（如 `refactor/rename-durabletask-core`）。
2. 提交 1：`git mv` 目录与 csproj → 更新 `slnx` + `DTFx.ConsoleApp.csproj` 引用路径 → 构建库与示例 → 提交。
3. 提交 2：迁移两个核心类型的命名空间 → 补消费方 `using` → 重排目录 → 同步 `<Compile Remove>` 路径 → 构建库与示例 → 提交。
4. 提交 3：更新 `README.md`、`AGENTS.md`、`Directory.Packages.props`、`openspec/specs/**` → 提交。
5. 归档：`openspec` 归档把 delta spec 合并进 `openspec/specs/`，并完成 README 任务。

**回滚策略**：三个提交均为纯重命名/命名空间/文档变更，无数据迁移、无配置切换。回滚即 `git revert` 对应提交，或直接丢弃分支。

## Open Questions

无。命名空间归属、目录布局、提交分层、消费方影响范围均已确定，且不影响 spec 内容与任务拆分。
