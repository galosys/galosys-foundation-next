# Tasks

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 把 `Galosys.Foundation.Azure.DurableTask.Core` 重命名为 `Galosys.Foundation.DurableTask.Core`，并将核心类型从上游包的 `DurableTask.Core` 命名空间迁移到 `Galosys.Foundation.DurableTask.Core`。

**Architecture:** 三个提交分层落地，与 Channel 重命名先例同构：提交 1 纯目录/项目文件重命名（零内容变更，代码仍可编译）；提交 2 命名空间迁移 + 物理目录重排（唯一语义变更点）；提交 3 文档与 openspec spec 更新。公共类型名（`TaskHubClientExtensions`、`DurableTaskServiceCollectionExtensions`、`AddDurableTaskCore` 等）全部不变，破坏面收敛到程序集名、核心命名空间、消费方一处 `using` 三项。

**Tech Stack:** .NET 10（SDK 10.0.401）、Microsoft.Azure.Durabletask.Core 3.11.2、openspec 1.15.0、PowerShell、git

**前置：** 本计划需在 `refactor/rename-durabletask-core` 分支上执行（从 `main` 新建），不得直接在 `main` 上提交。

---

## 1. 提交 1：纯重命名（目录、项目文件、引用路径）

**Files:**
- Move: `src/Galosys.Foundation.Azure.DurableTask.Core/` → `src/Galosys.Foundation.DurableTask.Core/`
- Move: `src/Galosys.Foundation.Azure.DurableTask.Core/Galosys.Foundation.Azure.DurableTask.Core.csproj` → `src/Galosys.Foundation.DurableTask.Core/Galosys.Foundation.DurableTask.Core.csproj`
- Modify: `Galosys.Foundation.Next.slnx`
- Modify: `samples/DTFx.ConsoleApp/DTFx.ConsoleApp.csproj`

- [x] 1.1 从 `main` 新建分支：`git checkout -b refactor/rename-durabletask-core`。验证：`git branch --show-current` 输出 `refactor/rename-durabletask-core`，且 `git status --short` 无输出（工作区干净）

- [x] 1.2 用 `git mv` 重命名库目录与 csproj：`git mv src/Galosys.Foundation.Azure.DurableTask.Core src/Galosys.Foundation.DurableTask.Core`，然后 `git mv src/Galosys.Foundation.DurableTask.Core/Galosys.Foundation.Azure.DurableTask.Core.csproj src/Galosys.Foundation.DurableTask.Core/Galosys.Foundation.DurableTask.Core.csproj`。验证：`Test-Path src/Galosys.Foundation.DurableTask.Core/Galosys.Foundation.DurableTask.Core.csproj` 为 True 且 `Test-Path src/Galosys.Foundation.Azure.DurableTask.Core` 为 False

- [x] 1.3 更新 `Galosys.Foundation.Next.slnx` 的 `<Project Path>`：把 `src/Galosys.Foundation.Azure.DurableTask.Core/Galosys.Foundation.Azure.DurableTask.Core.csproj` 改为 `src/Galosys.Foundation.DurableTask.Core/Galosys.Foundation.DurableTask.Core.csproj`。**保留原有 `Id="6d66d934-769c-494a-9ec6-684694597db9"` 不变**。验证：`Select-String -Path Galosys.Foundation.Next.slnx -Pattern 'DurableTask'` 命中 1 处且输出含新路径

- [x] 1.4 更新 `samples/DTFx.ConsoleApp/DTFx.ConsoleApp.csproj` 的 `<ProjectReference>`：路径改为 `..\..\src\Galosys.Foundation.DurableTask.Core\Galosys.Foundation.DurableTask.Core.csproj`。验证：`Select-String -Path samples/DTFx.ConsoleApp/DTFx.ConsoleApp.csproj -Pattern 'Galosys.Foundation.DurableTask.Core\\Galosys.Foundation.DurableTask.Core.csproj'` 命中 1 处

- [x] 1.5 构建库，确认纯重命名后仍可编译：`dotnet build src/Galosys.Foundation.DurableTask.Core/Galosys.Foundation.DurableTask.Core.csproj`。预期：0 error（此阶段命名空间仍是 `DurableTask.Core`，只换了程序集名）

- [x] 1.6 构建示例，确认项目引用解析正确：`dotnet build samples/DTFx.ConsoleApp/DTFx.ConsoleApp.csproj`。预期：0 error

- [x] 1.7 确认解决方案清单有效：`dotnet sln Galosys.Foundation.Next.slnx list`。预期：命令成功退出，输出含 `Galosys.Foundation.DurableTask.Core.csproj`

- [x] 1.8 提交：`git add -A && git commit -m "refactor!: rename Galosys.Foundation.Azure.DurableTask.Core to Galosys.Foundation.DurableTask.Core"`。预期：提交成功，`git show --stat HEAD` 的变更仅为重命名与路径更新

## 2. 提交 2：命名空间迁移与物理目录重排

**Files:**
- Move: `src/Galosys.Foundation.DurableTask.Core/DurableTask/Core/TaskHubClientExtensions.cs` → `src/Galosys.Foundation.DurableTask.Core/Galosys/Foundation/DurableTask/Core/TaskHubClientExtensions.cs`
- Move: `src/Galosys.Foundation.DurableTask.Core/DurableTask/Core/ServiceProviderObjectCreator.cs` → `src/Galosys.Foundation.DurableTask.Core/Galosys/Foundation/DurableTask/Core/ServiceProviderObjectCreator.cs`
- Move: `src/Galosys.Foundation.DurableTask.Core/DurableTask/Core/DurableTaskAttribute.cs` → `src/Galosys.Foundation.DurableTask.Core/Galosys/Foundation/DurableTask/Core/DurableTaskAttribute.cs`
- Move: `src/Galosys.Foundation.DurableTask.Core/DurableTask/` → 删除（空目录树）
- Modify: `src/Galosys.Foundation.DurableTask.Core/Galosys/Foundation/DurableTask/Core/TaskHubClientExtensions.cs`
- Modify: `src/Galosys.Foundation.DurableTask.Core/Galosys/Foundation/DurableTask/Core/ServiceProviderObjectCreator.cs`
- Modify: `src/Galosys.Foundation.DurableTask.Core/Galosys/Foundation/DurableTask/Core/DurableTaskAttribute.cs`
- Modify: `src/Galosys.Foundation.DurableTask.Core/Galosys.Foundation.DurableTask.Core.csproj`
- Modify: `samples/DTFx.ConsoleApp/Worker.cs`

- [x] 2.1 创建目标目录树并用 `git mv` 迁移三个文件：先 `New-Item -ItemType Directory -Force src/Galosys.Foundation.DurableTask.Core/Galosys/Foundation/DurableTask/Core`，然后 `git mv src/Galosys.Foundation.DurableTask.Core/DurableTask/Core/TaskHubClientExtensions.cs src/Galosys.Foundation.DurableTask.Core/Galosys/Foundation/DurableTask/Core/TaskHubClientExtensions.cs`（`ServiceProviderObjectCreator.cs`、`DurableTaskAttribute.cs` 同理）。验证：`Get-ChildItem src/Galosys.Foundation.DurableTask.Core/Galosys/Foundation/DurableTask/Core` 输出含全部 3 个 `.cs` 文件

- [x] 2.2 删除空的 `DurableTask/` 目录树：`Remove-Item -Recurse -Force src/Galosys.Foundation.DurableTask.Core/DurableTask`。验证：`Test-Path src/Galosys.Foundation.DurableTask.Core/DurableTask` 为 False

- [x] 2.3 把 `TaskHubClientExtensions.cs` 的 `namespace DurableTask.Core` 改为 `namespace Galosys.Foundation.DurableTask.Core`。该文件同时需要 `using DurableTask.Core;` 以解析上游包的 `TaskHubClient` 与 `OrchestrationInstance`。验证：`Select-String -Path src/Galosys.Foundation.DurableTask.Core/Galosys/Foundation/DurableTask/Core/TaskHubClientExtensions.cs -Pattern 'namespace Galosys.Foundation.DurableTask.Core'` 命中 1 处

- [x] 2.4 把 `ServiceProviderObjectCreator.cs` 的 `namespace DurableTask.Core` 改为 `namespace Galosys.Foundation.DurableTask.Core`。该文件同时需要 `using DurableTask.Core;` 以解析上游包的 `ObjectCreator<T>`、`NameVersionHelper`。验证：`Select-String -Path src/Galosys.Foundation.DurableTask.Core/Galosys/Foundation/DurableTask/Core/ServiceProviderObjectCreator.cs -Pattern 'namespace Galosys.Foundation.DurableTask.Core'` 命中 1 处

- [x] 2.5 把 `DurableTaskAttribute.cs` 的 `namespace Galosys.Foundation.Azure.DurableTask.Core.DurableTask.Core` 改为 `namespace Galosys.Foundation.DurableTask.Core.DurableTask.Core`。验证：`Select-String -Path src/Galosys.Foundation.DurableTask.Core/Galosys/Foundation/DurableTask/Core/DurableTaskAttribute.cs -Pattern 'Galosys.Foundation.Azure'` 无输出

- [x] 2.6 更新 csproj 的 `<Compile Remove>` 路径：把 `<Compile Remove="DurableTask\Core\DurableTaskAttribute.cs" />` 改为 `<Compile Remove="Galosys\Foundation\DurableTask\Core\DurableTaskAttribute.cs" />`。**注意：`Compile Remove` 对不存在的路径不报错而是静默失效**，改完必须验证。验证：`Select-String -Path src/Galosys.Foundation.DurableTask.Core/Galosys.Foundation.DurableTask.Core.csproj -Pattern 'Compile Remove="Galosys\\Foundation\\DurableTask\\Core\\DurableTaskAttribute.cs"'` 命中 1 处

- [x] 2.7 给消费方 `samples/DTFx.ConsoleApp/Worker.cs` 补 `using Galosys.Foundation.DurableTask.Core;`（`RaiseAsync` 扩展方法所需），保留原有的 `using DurableTask.Core;`（上游包的 `TaskHubClient`、`TaskOrchestration<,>`、`TaskActivity<,>` 所需）。验证：`Select-String -Path samples/DTFx.ConsoleApp/Worker.cs -Pattern 'using Galosys.Foundation.DurableTask.Core;'` 命中 1 处，且 `using DurableTask.Core;` 仍存在

- [x] 2.8 构建库，验证命名空间迁移后仍可编译：`dotnet build src/Galosys.Foundation.DurableTask.Core/Galosys.Foundation.DurableTask.Core.csproj`。预期：0 error

- [x] 2.9 构建示例，验证扩展方法解析正确：`dotnet build samples/DTFx.ConsoleApp/DTFx.ConsoleApp.csproj`。预期：0 error，且**无** CS1061（找不到 `RaiseAsync` 扩展方法）错误

- [x] 2.10 验证 `DurableTaskAttribute` 未被意外编译进产物：构建后检查 `src/Galosys.Foundation.DurableTask.Core/obj/Debug/net10.0/Galosys.Foundation.DurableTask.Core.dll` 的元数据中不含 `DurableTaskAttribute` 类型名。验证命令：

  ```powershell
  $dll = "src\Galosys.Foundation.DurableTask.Core\obj\Debug\net10.0\Galosys.Foundation.DurableTask.Core.dll"
  $txt = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($dll))
  $txt.Contains("DurableTaskAttribute")   # 预期输出 False
  ```

  若输出 `True` 说明 2.6 的 `<Compile Remove>` 路径写错，需修正后重新构建

- [x] 2.11 确认库内不再有旧项目全名残留：`Get-ChildItem -Recurse -Include *.cs,*.csproj -Path src/Galosys.Foundation.DurableTask.Core | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | Select-String -Pattern 'Galosys\.Foundation\.Azure'`。预期：无输出

- [x] 2.12 提交：`git add -A && git commit -m "refactor!: move DurableTask core types to Galosys.Foundation.DurableTask.Core namespace"`。预期：提交成功

## 3. 提交 3：文档与 openspec spec 更新

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `Directory.Packages.props`
- Modify: `openspec/specs/repository-layout/spec.md`
- Modify: `openspec/specs/foundation-namespace-conventions/spec.md`

- [x] 3.1 更新根 `README.md` 的项目一览表：在 `src/` 类库行追加 `| Galosys.Foundation.DurableTask.Core | src/Galosys.Foundation.DurableTask.Core/ | 本地类库 | Durable Task 编排/活动封装：DI 注册 + 宿主生命周期适配 + TaskHubClient 扩展 |`；在 `samples/` 行追加 `| DTFx.ConsoleApp | samples/DTFx.ConsoleApp/ | 示例外围控制台 | Durable Task 示例：SQL Server 持久化编排 + 审批事件 |`。同时更新表格底部"本地类库"的数量描述（若有）。验证：`Select-String -Path README.md -Pattern 'Galosys.Foundation.DurableTask.Core'` 命中且输出不含 `Galosys.Foundation.Azure`

- [x] 3.2 更新 `AGENTS.md` 的「本地类库」段落：追加 `Galosys.Foundation.DurableTask.Core`（Durable Task 编排/活动封装，核心类型用 `Galosys.Foundation.DurableTask.Core`，DI 扩展用 `Microsoft.Extensions.DependencyInjection`，宿主适配器用 `Microsoft.Extensions.Hosting`）。同时在「构建与验证」段落补充 `dotnet build samples/DTFx.ConsoleApp/DTFx.ConsoleApp.csproj` 的运行说明。验证：`Select-String -Path AGENTS.md -Pattern 'Galosys.Foundation.DurableTask.Core'` 命中

- [x] 3.3 在 `Directory.Packages.props` 新增 `<PackageVersion Include="galosys.foundation.durabletask.core" Version="$(MainVersion)" />`，与既有 `galosys.foundation.*` 对齐。验证：`Select-String -Path Directory.Packages.props -Pattern 'galosys.foundation.durabletask.core'` 命中 1 处

- [x] 3.4 更新活 spec `openspec/specs/repository-layout/spec.md`：`src/` 清单加入 `Galosys.Foundation.DurableTask.Core`；`samples/` 清单加入 `DTFx.ConsoleApp`；解决方案项目计数从「8 个 samples / 3 个 src / 1 个 tests」改为「9 个 samples / 4 个 src / 1 个 tests」；在「src 只包含 foundation 类库」场景追加「不存在名为 `Galosys.Foundation.Azure.DurableTask.Core` 的目录」；在「迁移后的项目引用与构建路径有效」要求与场景中补 `DTFx.ConsoleApp` 的引用解析路径。验证：`Select-String -Path openspec/specs/repository-layout/spec.md -Pattern 'DurableTask'` 命中

- [x] 3.5 更新活 spec `openspec/specs/foundation-namespace-conventions/spec.md`：在「物理目录与命名空间一致」要求下追加「DurableTask 核心类型文件目录」场景；新增「DurableTask 核心类型位于 `Galosys.Foundation.DurableTask.Core` 命名空间」要求及 3 个场景（核心类型命名空间 / 宿主适配器与 DI 扩展的命名空间 / 上游包命名空间由包单独拥有）。验证：`Select-String -Path openspec/specs/foundation-namespace-conventions/spec.md -Pattern 'DurableTask'` 命中

- [x] 3.6 校验 openspec 变更合法性：`openspec validate "durabletask-rename" --strict --no-interactive`。预期：输出 `Change 'durabletask-rename' is valid`，退出码 0

- [x] 3.7 提交：`git add -A && git commit -m "docs: update docs and openspec specs for DurableTask rename"`。预期：提交成功

## 4. 最终验证

- [x] 4.1 确认仓库中不再有旧项目全名残留（排除 `openspec/changes/archive/` 与 `bin`/`obj`）：`Get-ChildItem -Recurse -Include *.cs,*.csproj,*.md,*.slnx,*.props -Path src,tests,samples,openspec | Where-Object { $_.FullName -notmatch '\\(changes\\archive|obj|bin)\\' } | Select-String -Pattern 'Galosys\.Foundation\.Azure\.DurableTask'`。预期：无输出

- [x] 4.2 全量构建与测试：`dotnet build src/Galosys.Foundation.DurableTask.Core/Galosys.Foundation.DurableTask.Core.csproj`、`dotnet build samples/DTFx.ConsoleApp/DTFx.ConsoleApp.csproj`、`dotnet test tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj`。预期：三者全部成功，测试全部通过

- [x] 4.3 确认解决方案清单有效：`dotnet sln Galosys.Foundation.Next.slnx list`。预期：成功退出，输出含 `Galosys.Foundation.DurableTask.Core.csproj`、`Galosys.Foundation.Doris.csproj`、`Galosys.Foundation.Channel.csproj`、`Galosys.Foundation.Nats.Net.csproj`

- [x] 4.4 确认三个提交均在分支上且工作区干净：`git log --oneline -3` 与 `git status --short`。预期：最近 3 个提交为本次重命名的三层结构，`git status --short` 无输出

## 5. README 任务（归档阶段处理）

> 以下 README 相关任务在 apply 阶段**跳过**，由归档阶段（`/opsx-archive-x`）统一完成并回填 `- [x]`。

- [ ] 5.1 编写 `src/Galosys.Foundation.DurableTask.Core/README.md`：说明库定位（Durable Task 编排/活动的 DI 与宿主集成封装）、支持的持久化后端（SQL Server / Azure Storage 等）、核心类型与命名空间归属、构建与运行示例的命令。验证：文件存在且包含 `dotnet build samples/DTFx.ConsoleApp/DTFx.ConsoleApp.csproj` 与 `Galosys.Foundation.DurableTask.Core` 命名空间说明

- [ ] 5.2 编写 `samples/DTFx.ConsoleApp/README.md`：说明示例用途（SQL Server 持久化的审批编排）、前置条件（SQL Server 实例与连接串）、运行方式与预期输出。验证：文件存在且包含运行命令与连接串配置说明

## Workflow follow-up

- 在 `openspec/changes/durabletask-rename/` 下执行归档（`/opsx-archive-x`），使 2 个活 spec 的 delta 合并进 `openspec/specs/`
- 归档后复核 `openspec/specs/` 下已含 `Galosys.Foundation.DurableTask.Core`，且不含 `Galosys.Foundation.Azure.DurableTask.Core`
- 归档后完成第 5 组 README 任务并回填 `- [x]`
- 视需要将分支合并回 `main`（合并后切回主线）
