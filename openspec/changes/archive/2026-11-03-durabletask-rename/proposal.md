# Proposal

## Why

`Galosys.Foundation.Azure.DurableTask.Core` 的名字与它的实际内容不符，存在两处误导：

1. **`Azure` 前缀不成立。** 该库封装的是厂商中立的 `Microsoft.Azure.Durabletask.Core` SDK（后端可选 Azure Storage / MSSQL / Service Bus），仓库内唯一的消费方 `samples/DTFx.ConsoleApp` 用的正是 **SQL Server** 后端而非 Azure。库本身没有任何 Azure 专属依赖（`azure.storage.queues` 等均未引用），`Azure` 前缀属于误导。

2. **核心类型寄生在上游包的命名空间里。** `TaskHubClientExtensions` 与 `ServiceProviderObjectCreator` 声明为 `namespace DurableTask.Core` —— 这是**上游 `Microsoft.Azure.Durabletask.Core` NuGet 包的命名空间**，本库是在扩展它而非拥有它。副作用是消费方 `Worker.cs` 的 `using DurableTask.Core;` 同时解析到上游包与本库，程序集名、命名空间名、类型归属三者互不对应，读者需要额外维护一层映射。

本 change 让程序集名、命名空间名、物理目录三者统一为 `DurableTask`，并把核心类型迁出上游包的命名空间。

## What Changes

- **BREAKING** 程序集与项目重命名：`Galosys.Foundation.Azure.DurableTask.Core` → `Galosys.Foundation.DurableTask.Core`；目录 `src/Galosys.Foundation.Azure.DurableTask.Core/` → `src/Galosys.Foundation.DurableTask.Core/`；NuGet 包名 `galosys.foundation.durabletask.core`。
- **BREAKING** 核心类型命名空间迁移：`TaskHubClientExtensions`、`ServiceProviderObjectCreator` 从 `DurableTask.Core` 迁移到 `Galosys.Foundation.DurableTask.Core`（不再扩展上游包命名空间）。消费方需自行 `using Galosys.Foundation.DurableTask.Core;` 才能使用 `RaiseAsync` 扩展方法。
- **BREAKING** 消费方影响：`samples/DTFx.ConsoleApp/Worker.cs` 的 `client.RaiseAsync(...)` 调用需要新增 `using Galosys.Foundation.DurableTask.Core;`（当前靠命名空间同名隐式命中）。
- 保持不变：`DurableTaskServiceCollectionExtensions`（DI 扩展）继续位于 `Microsoft.Extensions.DependencyInjection`；`DurableTaskHostedService`（宿主适配器）继续位于 `Microsoft.Extensions.Hosting`。
- 保持不变：全部公共类型名不变（`TaskHubClientExtensions`、`ServiceProviderObjectCreator`（internal）、`DurableTaskServiceCollectionExtensions`、`AddDurableTaskCore`、`AddDurableTaskOrchestration`），因此不产生类型级破坏性变更；公共 API 的破坏点仅限程序集名与命名空间两项。
- 物理目录按新命名空间重排：`Galosys/Foundation/DurableTask/Core/`（核心类型）、`Microsoft/Extensions/DependencyInjection/`、`Microsoft/Extensions/Hosting/`（与 `Galosys/` 平级）。
- `DurableTaskAttribute.cs` 当前被 `<Compile Remove>` 排除在编译外，其命名空间为旧项目名的嵌套形式 `Galosys.Foundation.Azure.DurableTask.Core.DurableTask.Core`；本 change 将其命名空间同步为 `Galosys.Foundation.DurableTask.Core.DurableTask.Core`，保持排除编译状态不变（该属性当前无任何使用方）。
- 更新 `Galosys.Foundation.Next.slnx`（保留原 `Id` GUID `6d66d934-769c-494a-9ec6-684694597db9`）、`samples/DTFx.ConsoleApp/DTFx.ConsoleApp.csproj` 的 `ProjectReference`。
- 补齐文档：`README.md` 项目一览表与 `AGENTS.md`「本地类库」段**当前完全未收录**该项目，本次一并补上。
- 更新 `openspec/specs/repository-layout/spec.md`（`src/` 清单需收录第 4 个类库、解决方案项目计数需从 3 改为 4、`samples/` 清单需收录 `DTFx.ConsoleApp`）与 `openspec/specs/foundation-namespace-conventions/spec.md`（新增 DurableTask 核心类型命名空间约定）。
- 不改 `openspec/changes/archive/**`（历史归档）。

## Capabilities

### New Capabilities

无。

### Modified Capabilities

- `repository-layout`: `src/` 项目清单从 3 个类库改为 4 个（新增 `Galosys.Foundation.DurableTask.Core`）；`samples/` 清单新增 `DTFx.ConsoleApp`；解决方案项目计数从「8 个 samples / 3 个 src / 1 个 tests」改为「9 个 samples / 4 个 src / 1 个 tests」；并显式声明不存在名为 `Galosys.Foundation.Azure.DurableTask.Core` 的目录。
- `foundation-namespace-conventions`: 新增「DurableTask 核心类型位于 `Galosys.Foundation.DurableTask.Core` 命名空间」的要求与对应场景，确立核心类型不扩展上游 NuGet 包命名空间的约定；物理目录约定补充 `Galosys/Foundation/DurableTask/Core/`。

## Impact

- 代码：`src/Galosys.Foundation.Azure.DurableTask.Core/` 下 5 个 `.cs` 文件的 `namespace` 声明与 `using` 引用；目录重排；`Galosys.Foundation.Azure.DurableTask.Core.csproj` 的 `<Compile Remove>` 路径同步。
- 示例：`samples/DTFx.ConsoleApp/Worker.cs` 新增 `using Galosys.Foundation.DurableTask.Core;`（`RaiseAsync` 扩展方法解析所需）；`DTFx.ConsoleApp.csproj` 的 `ProjectReference` 路径更新。
- 解决方案：`Galosys.Foundation.Next.slnx` 中 1 个 `<Project Path>`（保留原 Id GUID）。
- 文档：根 `README.md`、`AGENTS.md`（两者当前均未收录该项目）。
- 依赖：不新增 NuGet 包；`Directory.Packages.props` 增加 `galosys.foundation.durabletask.core` 版本项以对齐既有 `galosys.foundation.*` 约定。
- 公共 API：程序集名与核心命名空间为破坏性变更；类型名、成员签名、行为语义全部不变。
- 命名空间冲突：迁移后本库不再导出 `DurableTask.Core` 下的任何类型，上游 `Microsoft.Azure.Durabletask.core` 的 `DurableTask.Core` 命名空间由包单独拥有，`using DurableTask.Core;` 在消费方仍能解析到上游的 `TaskOrchestration<,>` / `TaskActivity<,>`（经由本库的传递包引用）。
