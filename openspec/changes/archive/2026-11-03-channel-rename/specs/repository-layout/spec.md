# Spec Delta

## MODIFIED Requirements

### Requirement: 示例与类库顶层目录分离

仓库 MUST 将示例应用放在根目录 `samples/` 下，将 foundation 类库放在根目录 `src/` 下。`src/` 下不得再出现示例项目。

#### Scenario: samples 包含全部示例

- **WHEN** 列出 `samples/` 下的项目目录
- **THEN** 包含 `AopSample.ConsoleApp`、`DataIngestionSample.ConsoleApp`、`MafSample.Adapter`、`MafSample.API`、`MafSample.ConsoleApp`、`MafSample.WpfApp`、`NatsSample.ConsoleApp`、`NetServer.ConsoleApp`
- **AND** 这些项目不再出现在 `src/` 下

#### Scenario: src 只包含 foundation 类库

- **WHEN** 列出 `src/` 下的项目目录
- **THEN** 仅包含 `Galosys.Foundation.Channel`、`Galosys.Foundation.Doris`、`Galosys.Foundation.Nats.Net`
- **AND** 不存在示例项目目录
- **AND** 不存在名为 `Galosys.Foundation.Primitives` 的目录

### Requirement: 迁移后的项目引用与构建路径有效

迁移后的示例项目 MUST 能通过新的 `samples/` 路径构建，且对 `src/` 下 foundation 类库的项目引用 MUST 正确解析。`NetServer.ConsoleApp` MUST 解析到 `src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj`，测试项目 MUST 解析到 `src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj`。

#### Scenario: 迁移示例可单独构建

- **WHEN** 执行 `dotnet build samples/<Project>/<Project>.csproj`
- **THEN** 除已知不可编译的 `NatsSample.ConsoleApp` 外，项目构建成功
- **AND** `NetServer.ConsoleApp` 能解析 `Galosys.Foundation.Channel` 项目引用

#### Scenario: 测试项目引用保持不变

- **WHEN** 执行 `dotnet test tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj`
- **THEN** 测试项目仍通过 `..\..\src\Galosys.Foundation.Channel\...` 解析被测项目
- **AND** 测试运行成功
