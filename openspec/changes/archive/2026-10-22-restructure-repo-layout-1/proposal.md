## Why

当前 `src/` 同时存放 8 个示例应用和 3 个本地 foundation 类库，目录职责不清：README 与 AGENTS 都称本仓库是“示例试验场”，但可复用的类库和一次性示例混在同一层，构建、浏览和后续拆分都依赖项目名而非目录约定。现在需要把示例整体迁出 `src/`，让目录结构直接表达“示例 / foundation 类库 / 测试”的边界。

## What Changes

- 在仓库根目录新建 `samples/`。
- 将以下 8 个示例项目从 `src/` 迁入 `samples/`：
  - `AopSample.ConsoleApp`
  - `DataIngestionSample.ConsoleApp`
  - `MafSample.Adapter`
  - `MafSample.API`
  - `MafSample.ConsoleApp`
  - `MafSample.WpfApp`
  - `NatsSample.ConsoleApp`
  - `NetServer.ConsoleApp`
- `src/` 只保留 3 个 foundation 类库：`Galosys.Foundation.Primitives`、`Galosys.Foundation.Doris`、`Galosys.Foundation.Nats.Net`。
- 更新 `Galosys.Foundation.Next.slnx`：示例挂到 `/samples/` 解决方案文件夹，foundation 类库挂到 `/src/`，取消 `/src/Infras/`，`/tests/` 保持不变。
- 更新示例对 foundation 的项目引用：`NetServer.ConsoleApp` 与 `NatsSample.ConsoleApp` 改为 `..\..\src\...`。
- 同步更新 `README.md`、`AGENTS.md`、示例 README 与当前 OpenSpec 规格中的路径、构建/运行命令。
- **BREAKING**：示例项目路径由 `src/<Project>` 变为 `samples/<Project>`，依赖旧路径的脚本和 IDE 启动配置需要重新指向。示例代码的运行时行为不变。

## Capabilities

### New Capabilities

- `repository-layout`：定义仓库顶层目录边界与解决方案文件夹映射规则，保证示例、foundation 类库、测试各自位于独立顶层目录，`.slnx` 与磁盘结构一致。

### Modified Capabilities

无。本次只调整目录、项目引用与文档，不改变任何既有运行时需求。

## Impact

- 移动 8 个项目目录，涉及其全部 `bin/`、`obj/` 之外的受版本控制文件。
- `Galosys.Foundation.Next.slnx` 的项目路径与解决方案文件夹结构。
- 两个 `.csproj` 的 `ProjectReference` 相对路径。
- `README.md`、`AGENTS.md`、`samples/NetServer.ConsoleApp/README.md`、`openspec/specs/channel-hosting/spec.md`。
- 不影响公共 API；`Galosys.Foundation.Nats.Net` 与 `NatsSample.ConsoleApp` 仍因原型代码不可编译。
