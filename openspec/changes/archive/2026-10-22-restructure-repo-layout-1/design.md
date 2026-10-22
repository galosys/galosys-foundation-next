## Context

仓库当前把 8 个示例应用与 3 个本地 foundation 类库平铺在 `src/` 下。README/AGENTS 将仓库定位为“示例试验场”，但目录本身无法表达哪些是示例、哪些是可复用类库。`Galosys.Foundation.Next.slnx` 也复制了这种混合结构，并用 `/src/Infras/` 单独收纳类库，与磁盘层级不一致。

本次改造只移动目录和更新路径，不修改示例业务代码、不修复已知不可编译的 NATS 原型。执行时工作区中 `src/MafSample.API` 尚有未提交修改，迁移必须保留这些修改。

## Goals / Non-Goals

**Goals:**

- 根目录形成 `samples/`（示例）、`src/`（foundation 类库）、`tests/`（测试）三个清晰边界。
- `.slnx` 的解决方案文件夹与磁盘目录一一对应。
- 所有项目引用、构建命令、当前文档路径同步更新。
- 用 `git mv` 保留文件历史，并保留 `MafSample.API` 的未提交修改。
- 迁移后除已知的 NATS 原型外，foundation 项目、测试和示例均可独立构建。

**Non-Goals:**

- 不修改示例或类库的运行时行为。
- 不修复 `Galosys.Foundation.Nats.Net` / `NatsSample.ConsoleApp` 的编译错误。
- 不调整 `tests/` 的位置。
- 不重命名项目、不修改项目 `Id`、不调整包版本。
- 不重写 `openspec/changes/archive/**` 与 `docs/superpowers/specs/**` 中的历史路径。

## Decisions

### D1：使用 `git mv` 整目录迁移

用 `git mv src/<Project> samples/<Project>` 移动 8 个示例目录。相比先复制再删除，`git mv` 能保留 Git 重命名检测，且会把 `MafSample.API` 的未提交工作区修改一并带到新路径。

替代方案：用 `Move-Item` 后让 Git 自行检测重命名。可行，但会丢失索引层面的移动信息，并在有未提交修改时更难核对；不采用。

### D2：`samples/` 保持扁平，不按技术栈再分组

8 个示例直接放在 `samples/<Project>`，不增加 `console/`、`web/`、`wpf/` 等中间层。项目名已经带 `ConsoleApp`、`API`、`Adapter`、`WpfApp` 等后缀，再分层会增加路径长度且与当前规模不匹配。

替代方案：按宿主类型分层。当前仅 8 个项目，收益低；未来数量显著增长时再引入。

### D3：解决方案文件夹直接映射磁盘

`.slnx` 使用 `/samples/`、`/src/`、`/tests/`。删除 `/src/Infras/`，让 3 个 foundation 类库直接位于 `/src/` 下。项目 `Id` 保持不变，只改 `Path`。

替代方案：保留 `/src/Infras/` 作为逻辑分组。与“解决方案文件夹同磁盘”的目标冲突；不采用。

### D4：只更新两个跨目录项目引用

移动后只有 `NetServer.ConsoleApp`（引用 Primitives）和 `NatsSample.ConsoleApp`（引用 Nats.Net）需要把 `..\Galosys.Foundation.*` 改为 `..\..\src\Galosys.Foundation.*`。测试项目从 `tests/` 出发的相对路径 `..\..\src\...` 不变。

### D5：迁移后清理构建产物再验证

示例目录中的 `bin/`、`obj/` 是忽略目录，但其中的 `project.assets.json` 记录旧项目路径。迁移后删除这些目录并重新还原，避免 NuGet/MSBuild 使用过期缓存。

### D6：文档更新范围

更新 `README.md`、`AGENTS.md`、`samples/NetServer.ConsoleApp/README.md`、`openspec/specs/channel-hosting/spec.md`。`openspec/changes/archive/**` 按仓库约定不改写；`docs/superpowers/specs/2026-10-20-primitives-socket-transport-design.md` 作为历史设计记录保留旧路径和旧命名空间结论。

## Risks / Trade-offs

- [未提交的 `MafSample.API` 修改在移动中丢失或被覆盖] → 迁移前记录 `git status`，使用 `git mv` 整目录移动，迁移后核对 `git status` 中 5 个修改文件仍存在。
- [旧 `obj/bin` 缓存导致还原或构建引用旧路径] → 删除被移动项目的 `bin/`、`obj/` 后重新构建。
- [外部脚本或 IDE 启动配置仍指向 `src/<Sample>`] → 属于已知 **BREAKING**；同步更新仓库内文档，外部配置由使用者重新指向。
- [解决方案文件路径错误但未被发现] → 用 `dotnet sln ... list` 校验全部 12 个项目路径，再逐项目构建。
- [NATS 原型不可编译被误判为迁移失败] → 验证清单明确排除 `NatsSample.ConsoleApp` 与 `Galosys.Foundation.Nats.Net`，并保留 README 中的已知限制说明。

## Migration Plan

1. 记录 `git status`，确认 `MafSample.API` 的未提交文件清单。
2. 新建 `samples/`，用 `git mv` 移动 8 个示例目录。
3. 修正 `NetServer.ConsoleApp`、`NatsSample.ConsoleApp` 的 `ProjectReference`。
4. 更新 `.slnx` 的项目路径与解决方案文件夹。
5. 删除被移动示例的 `bin/`、`obj/`，重新还原。
6. 更新当前文档与规格中的路径。
7. 执行 `dotnet sln list`、foundation 构建、测试项目测试、可编译示例的逐项目构建。
8. 核对 `src/` 只剩 3 个 foundation 项目、`samples/` 包含 8 个示例、用户未提交修改仍在。

回滚策略：本次不修改示例源码内容，可用 `git mv` 将目录移回 `src/` 并 `git checkout -- Galosys.Foundation.Next.slnx README.md AGENTS.md ...` 恢复文档；未提交的 `MafSample.API` 修改随目录移动，回滚时同样随目录返回。
