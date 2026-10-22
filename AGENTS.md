# AGENTS.md

本仓库是专有框架 `Galosys.Foundation` 的**示例试验场**：`samples/*` 下每个项目都是独立、体量小、往往半成品
的功能演示，保留着大量注释掉的实验代码。不要把它们当作生产模板。

## 构建与验证

- **仅 Windows**，需要 .NET SDK `10.0.401`（`global.json` 固定）。
- **只构建改动的单个项目**：示例项目用 `dotnet build samples/<Project>/<Project>.csproj`。
  foundation 类库用 `dotnet build src/<Project>/<Project>.csproj`。
  `dotnet build Galosys.Foundation.Next.slnx` 会失败：`Galosys.Foundation.Nats.Net` 是无法编译的原型，
  而 `NatsSample.ConsoleApp` 引用了它。
- 测试项目只有 `tests/Galosys.Foundation.Primitives.Tests`（xUnit）：
  `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`。
- 运行：`dotnet run --project samples/<Project>/<Project>.csproj`。多数示例是长期运行的 `BackgroundService`，
  需 Ctrl+C 退出。
- `MafSample.API` 没有外部基础设施无法启动：PostgreSQL `:15432`、Redis `:6379`、RocketMQ `:8081`
  （默认消息总线）、RabbitMQ `:5672`、OTLP `:5080`。改 API 靠编译验证。`MafSample.Adapter`（`:5190`）
  可独立运行。

## 架构事实

- **目录约定**：`samples/` 放示例项目，`src/` 只放 foundation 类库，`tests/` 放测试。
- **命名空间寄生**：foundation 类库的 `*Options` 放 `Microsoft.Extensions.Options`，`IServiceCollection` / `IServiceProvider` DI 扩展放 `Microsoft.Extensions.DependencyInjection`；核心宿主类型继续留在 `Microsoft.Extensions.Hosting.Net`。
- **两种宿主风格**：模块化应用（`MafSample.API`、`MafSample.ConsoleApp`、`AopSample`、`NatsSample`）
  调用 `UseModularization()` 并提供 `internal class InternalModule : IModule`；其余（`Adapter`、`NetServer`、
  `DataIngestionSample`）使用普通的 `Host.CreateApplicationBuilder`/`WebApplication.CreateBuilder`。
- **特性驱动 DI**：`[Service]`、`[HostedService]`、`[Query]`、`[Handler]`、`[DbContext]`、`[AmqpHandler]`
  由程序集扫描发现，再手写 `AddSingleton`/`AddHostedService` 通常多余；`InternalModule` 只用于特性表达不了
  的注册（如 `AddCqrs()`、`AddStateMachine(...)`）。
- **本地类库**：`Galosys.Foundation.Primitives`（TCP + `System.IO.Pipelines` 服务端基架，核心类型用
  `Microsoft.Extensions.Hosting.Net`，Options 类型用 `Microsoft.Extensions.Options`）、
  `Galosys.Foundation.Doris`（Doris `_stream_load` 客户端）、
  `Galosys.Foundation.Nats.Net`（不可编译的 NATS 消费者扫描原型）。
- `samples/MafSample.ConsoleApp/Microsoft/Extensions/Hosting/WindowedBatchWorker.cs` 是本地辅助类，不是包里的类型。
- `samples/MafSample.WpfApp` 目标 `net10.0-windows`（UseWPF），菜单树来自 `appsettings.json` 的 `Menu` 节。
- 注释和 XML 文档用中文。`samples/MafSample.API` 开启 `GenerateDocumentationFile=true`，新增 public 成员会产生
  CS1591 警告。

## NuGet

- `galosys.foundation.*` 来自 nuget.org（根目录 `nuget.config`）。
- 各项目包版本不统一，新增包时沿用所在项目已有的版本。

## 坑点

- `appsettings.json` 含示例凭据（数据库/Redis 密码、第三方密钥、AI API Key），不要复制到新文件、测试或日志。
- `openspec/changes/archive/**` 是历史记录，不要改写。
