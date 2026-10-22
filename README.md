# Galosys.Foundation.Next

本仓库是 `Galosys.Foundation` 框架的示例与原型试验场。`samples/` 下的每个项目都是独立、体量较小的功能演示，
用于验证框架的某一块能力。多数示例“能跑但未完成”，保留着注释掉的实验代码和未实现的占位方法，**不是可直接
用于生产的模板**。

## 环境要求

- **Windows**：WPF 示例目标为 `net10.0-windows`。
- **.NET SDK 10.0.401**：由根目录 `global.json` 固定。
- **NuGet**：根目录 `nuget.config` 仅启用 nuget.org，`galosys.foundation.*` 包可直接还原。

## 项目一览

| 项目 | 位置 | 类型 | 说明 |
|---|---|---|---|
| `AopSample.ConsoleApp` | `samples/AopSample.ConsoleApp/` | 示例（控制台） | Castle 动态代理 + `[CatchLogging]` 拦截器，打印拦截日志。 |
| `DataIngestionSample.ConsoleApp` | `samples/DataIngestionSample.ConsoleApp/` | 示例（控制台） | `Microsoft.Extensions.DataIngestion`（预览包）Markdown 读取/分块试验，管线尚未接线。 |
| `MafSample.Adapter` | `samples/MafSample.Adapter/` | 示例（ASP.NET Core） | 适配器模板，监听 `:5190`，可独立运行。 |
| `MafSample.API` | `samples/MafSample.API/` | 示例（ASP.NET Core） | 模块化 API：CQRS、状态机、RabbitMQ/AMQP handler、EF Core、Swagger、HybridCache；需外部基础设施。 |
| `MafSample.ConsoleApp` | `samples/MafSample.ConsoleApp/` | 示例（Worker Service） | 模块化 Worker：Channel + Rx 窗口批处理；`ProcessBatchAsync` 尚未实现。 |
| `MafSample.WpfApp` | `samples/MafSample.WpfApp/` | 示例（WPF） | 基于 `ChromeWindow` 导航，菜单树来自 `appsettings.json` 的 `Menu` 节。 |
| `NatsSample.ConsoleApp` | `samples/NatsSample.ConsoleApp/` | 示例（控制台） | NATS 发布/订阅，连接 `nats://127.0.0.1:4222`，依赖本地 NATS。 |
| `NetServer.ConsoleApp` | `samples/NetServer.ConsoleApp/` | 示例（控制台） | Primitives 的服务端示例，监听 `0.0.0.0:8000`；见 [README](samples/NetServer.ConsoleApp/README.md)。 |
| `Galosys.Foundation.Primitives` | `src/Galosys.Foundation.Primitives/` | 本地类库 | 自包含 TCP + `System.IO.Pipelines` 服务端基架；见 [README](src/Galosys.Foundation.Primitives/README.md)。 |
| `Galosys.Foundation.Doris` | `src/Galosys.Foundation.Doris/` | 本地类库 | Doris `_stream_load` 的 HTTP 客户端，默认 FE HTTP 端口 `8030`。 |
| `Galosys.Foundation.Nats.Net` | `src/Galosys.Foundation.Nats.Net/` | 本地类库 | NATS 消费者扫描原型，当前不可编译。 |
| `tests/Galosys.Foundation.Primitives.Tests` | `tests/Galosys.Foundation.Primitives.Tests/` | 测试（xUnit） | 覆盖 Primitives 的单元测试。 |

## 构建、测试与运行

只构建你改动的那一个项目：

```powershell
dotnet build samples/<Project>/<Project>.csproj
dotnet build src/<Project>/<Project>.csproj
```

运行测试：

```powershell
dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj
```

运行示例：

```powershell
dotnet run --project samples/<Project>/<Project>.csproj
```

多数示例是长期运行的 `BackgroundService`，需要 `Ctrl+C` 才能退出。

> **已知限制**：`dotnet build Galosys.Foundation.Next.slnx` 会失败。解决方案包含
> `Galosys.Foundation.Nats.Net`，该项目是代码不完整的原型、当前无法编译，而 `NatsSample.ConsoleApp`
> 又引用了它。请改用上面的单项目构建。

## 基础设施

`MafSample.API` 没有外部基础设施无法启动：

| 组件 | 端口 | 用途 |
|---|---|---|
| PostgreSQL | `15432` | EF Core 数据存储 |
| Redis | `6379` | HybridCache / 缓存 |
| Apache RocketMQ | `8081` | 默认消息总线 |
| RabbitMQ | `5672` | AMQP handler |
| OTLP collector | `5080` | OpenTelemetry 导出 |

API 监听 `http://localhost:5134`，Swagger 位于 `/swagger`。`MafSample.Adapter` 监听 `:5190`，不依赖上述
设施，可独立运行。修改 API 时以编译验证为主，不要依赖本地启动。

## 目录结构

| 目录 | 用途 |
|---|---|
| `samples/` | 示例项目 |
| `src/` | foundation 本地类库 |
| `tests/` | xUnit 测试（目前仅覆盖 Primitives） |
| `openspec/` | 规格（`specs/`）与变更记录（`changes/`，含历史归档） |
| `docs/superpowers/` | 设计文档（`specs/`） |
| `.opencode/` | OpenCode 的命令与技能配置 |

## 已知限制

- `Galosys.Foundation.Nats.Net` 当前不可编译，导致整个解决方案无法构建。
- `DataIngestionSample.ConsoleApp` 的读取/分块管线尚未接线，只是试验代码。
- `ChannelClientBase`、`WindowedBatchWorker.ProcessBatchAsync` 等仍是未实现的占位（抛
  `NotImplementedException`）。
- `MafSample.Adapter` 是适配器模板，业务逻辑有限。
- `MafSample.API` 依赖外部基础设施，无法开箱即用地启动。

## 说明

- 各 `appsettings.json` 中的凭据与密钥仅用于本地演示，**禁止复用、外传或提交到公开仓库**。
- `openspec/changes/archive/**` 是历史记录，保留当时的解决方案文件名与结论，不要改写。

## 相关文档

- [Galosys.Foundation.Primitives](src/Galosys.Foundation.Primitives/README.md)
- [NetServer.ConsoleApp](samples/NetServer.ConsoleApp/README.md)
- [OpenSpec 规格](openspec/specs/)
