# Spec Delta

## MODIFIED Requirements

### Requirement: 迁移后的项目引用与构建路径有效

迁移后的示例项目 MUST 能通过新的 `samples/` 路径构建，且对 `src/` 下 foundation 类库的项目引用 MUST 正确解析。`NetServer.ConsoleApp` MUST 解析到 `src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj`，`DTFx.ConsoleApp` MUST 解析到 `src/Galosys.Foundation.DurableTask.Core/Galosys.Foundation.DurableTask.Core.csproj`，测试项目 MUST 解析到 `src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj`。全部示例项目 MUST 能单独构建通过。

#### Scenario: 迁移示例可单独构建

- **WHEN** 逐个执行 `dotnet build samples/<Project>/<Project>.csproj`
- **THEN** 全部 9 个示例项目构建成功
- **AND** `NetServer.ConsoleApp` 能解析 `Galosys.Foundation.Channel` 项目引用
- **AND** `DTFx.ConsoleApp` 能解析 `Galosys.Foundation.DurableTask.Core` 项目引用
- **AND** `NatsSample.ConsoleApp` 能解析 `Galosys.Foundation.Nats.Net` 项目引用

#### Scenario: 测试项目引用保持不变

- **WHEN** 执行 `dotnet test tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj`
- **THEN** 测试项目仍通过 `..\..\src\Galosys.Foundation.Channel\...` 解析被测项目
- **AND** 测试运行成功

### Requirement: 当前文档与命令使用新路径

仓库中面向当前使用的文档 MUST 使用 `samples/` 与 `src/` 的新路径；历史归档 `openspec/changes/archive/**` 与历史设计文档保持原样。文档 MUST NOT 声称某个可构建项目不可编译。

#### Scenario: 根文档命令可复制执行

- **WHEN** 按 `README.md` 或 `AGENTS.md` 中的示例命令执行
- **THEN** 示例项目路径为 `samples/<Project>/<Project>.csproj`
- **AND** foundation 类库路径为 `src/<Project>/<Project>.csproj`

#### Scenario: 根文档收录全部 foundation 类库

- **WHEN** 查看 `README.md` 的项目一览表
- **THEN** 表格包含 `Galosys.Foundation.DurableTask.Core` 行且路径为 `src/Galosys.Foundation.DurableTask.Core/`
- **AND** 表格不包含 `Galosys.Foundation.Azure.DurableTask.Core` 行

#### Scenario: 文档不声称可构建项目不可编译

- **WHEN** 查看 `README.md` 与 `AGENTS.md` 中关于 `Galosys.Foundation.Nats.Net` 的描述
- **THEN** 两者均不包含"当前不可编译"或等价的断言
- **AND** 两者准确描述该类库提供的消费者能力与其运行所需的外部依赖

#### Scenario: 历史记录不被改写

- **WHEN** 检查 `openspec/changes/archive/**` 与 `docs/superpowers/specs/**`
- **THEN** 其中的旧 `src/` 示例路径保持不变
