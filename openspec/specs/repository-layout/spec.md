# repository-layout Specification

## Purpose
定义仓库顶层目录边界与解决方案文件夹映射规则，保证示例、foundation 类库与测试各自位于独立顶层目录，并使 `.slnx` 与磁盘结构一致。
## Requirements
### Requirement: 示例与类库顶层目录分离

仓库 MUST 将示例应用放在根目录 `samples/` 下，将 foundation 类库放在根目录 `src/` 下。`src/` 下不得再出现示例项目。

#### Scenario: samples 包含全部示例

- **WHEN** 列出 `samples/` 下的项目目录
- **THEN** 包含 `AopSample.ConsoleApp`、`DataIngestionSample.ConsoleApp`、`MafSample.Adapter`、`MafSample.API`、`MafSample.ConsoleApp`、`MafSample.WpfApp`、`NatsSample.ConsoleApp`、`NetServer.ConsoleApp`
- **AND** 这些项目不再出现在 `src/` 下

#### Scenario: src 只包含 foundation 类库

- **WHEN** 列出 `src/` 下的项目目录
- **THEN** 仅包含 `Galosys.Foundation.Primitives`、`Galosys.Foundation.Doris`、`Galosys.Foundation.Nats.Net`
- **AND** 不存在示例项目目录

### Requirement: 解决方案结构映射磁盘目录

`Galosys.Foundation.Next.slnx` MUST 使用 `/samples/`、`/src/`、`/tests/` 三个解决方案文件夹，并使每个 `<Project Path>` 指向实际存在的项目文件。

#### Scenario: 解决方案路径全部有效

- **WHEN** 执行 `dotnet sln Galosys.Foundation.Next.slnx list`
- **THEN** 命令成功退出
- **AND** 输出包含 8 个 `samples/` 项目、3 个 `src/` 项目与 1 个 `tests/` 项目

#### Scenario: 解决方案文件夹与磁盘一致

- **WHEN** 查看 `.slnx` 中的解决方案文件夹
- **THEN** 示例项目位于 `/samples/` 下
- **AND** foundation 类库位于 `/src/` 下
- **AND** 不存在与实际磁盘目录不对应的 `/src/Infras/` 文件夹

### Requirement: 迁移后的项目引用与构建路径有效

迁移后的示例项目 MUST 能通过新的 `samples/` 路径构建，且对 `src/` 下 foundation 类库的项目引用 MUST 正确解析。

#### Scenario: 迁移示例可单独构建

- **WHEN** 执行 `dotnet build samples/<Project>/<Project>.csproj`
- **THEN** 除已知不可编译的 `NatsSample.ConsoleApp` 外，项目构建成功
- **AND** `NetServer.ConsoleApp` 能解析 `Galosys.Foundation.Primitives` 项目引用

#### Scenario: 测试项目引用保持不变

- **WHEN** 执行 `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
- **THEN** 测试项目仍通过 `..\..\src\Galosys.Foundation.Primitives\...` 解析被测项目
- **AND** 测试运行成功

### Requirement: 当前文档与命令使用新路径

仓库中面向当前使用的文档 MUST 使用 `samples/` 与 `src/` 的新路径；历史归档 `openspec/changes/archive/**` 与历史设计文档保持原样。

#### Scenario: 根文档命令可复制执行

- **WHEN** 按 `README.md` 或 `AGENTS.md` 中的示例命令执行
- **THEN** 示例项目路径为 `samples/<Project>/<Project>.csproj`
- **AND** foundation 类库路径为 `src/<Project>/<Project>.csproj`

#### Scenario: 历史记录不被改写

- **WHEN** 检查 `openspec/changes/archive/**` 与 `docs/superpowers/specs/**`
- **THEN** 其中的旧 `src/` 示例路径保持不变
