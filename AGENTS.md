# AGENTS.md

本仓库是专有框架 `Galosys.Foundation` 的**示例试验场**：`src/*` 下每个项目都是一个独立、体量小、且往往
半成品的功能演示。没有 git 仓库、没有 README、没有 CI，也**没有任何测试**。仓库里已有的 `bin/`、
`obj/`、`.vs/` 是历史构建产物，不要当作源码。

## 构建与验证

- 需要 .NET 10 SDK（本机为 `10.0.401`），且**仅限 Windows**（WPF 项目 + 解决方案中的绝对路径）。
- `dotnet build MafSample.slnx` **会失败**：`Galosys.Foundation.Nats.Net` 是缺包引用、无法编译的半成品
  原型。只构建你改动的那一个项目：`dotnet build src/<Project>/<Project>.csproj`。除此之外所有项目都能
  正常编译。
- 仓库没有测试，`dotnet build` 是唯一的验证手段，不要去找 `dotnet test`。
- 运行示例：`dotnet run --project src/<Project>/<Project>.csproj`。多数示例是长期运行的
  `BackgroundService`，需要 Ctrl+C 才能退出。
- `MafSample.API`（http://localhost:5134，Swagger 在 `/swagger`）**没有外部基础设施无法启动**：
  PostgreSQL `:15432`、Redis `:6379`、Apache RocketMQ `:8081`（默认消息总线）、RabbitMQ `:5672`，
  以及 OTLP `:5080`。改 API 靠编译验证，不要靠运行。`MafSample.Adapter`（`:5190`）可独立运行。

## 从文件名看不出来的架构事实

- **两种应用风格。** "模块化"应用（`MafSample.API`、`MafSample.ConsoleApp`、`AopSample`、`NatsSample`）
  调用 `builder.UseModularization()`，并提供一个 `internal class InternalModule : IModule`，其
  `ConfigureServices` 会在启动时被调用。其余项目（`Adapter`、`NetServer`、`DataIngestionSample`）使用
  普通的 `Host.CreateApplicationBuilder`/`WebApplication.CreateBuilder`，手动注册服务。
- **依赖注入由特性（attribute）驱动。** `[Service]`、`[HostedService]`、`[Query]`、`[Handler]`、
  `[DbContext]`、`[AmqpHandler]` 由 Galosys 框架通过程序集扫描自动发现；再为这类类型手写
  `AddSingleton`/`AddHostedService` 通常是多余的。`InternalModule` 主要用于特性表达不了的注册
  （如 `AddCqrs()`、`AddStateMachine(...)`）。
- `Galosys.Foundation.Primitives` 是**本地**类库（基于 TCP + `System.IO.Pipelines` 的服务端基类，命名
  空间为 `Microsoft.Extensions.Hosting.Net`）；`Galosys.Foundation.Doris` 同样是本地库（Doris
  `_stream_load` 的 HTTP 客户端）。
- `MafSample.ConsoleApp/Microsoft/Extensions/Hosting/WindowedBatchWorker.cs` 是本地辅助类（Channel + Rx
  批处理），不是包里的类型；`ChannelClientBase` 和 `ProcessBatchAsync` 仍然抛
  `NotImplementedException` —— 多个示例是故意不完整的，并保留着注释掉的功能代码。
- `MafSample.WpfApp` 目标为 `net10.0-windows`（UseWPF），基于 `ChromeWindow` 做导航，菜单树来自
  `appsettings.json` 的 `Menu` 节。
- 注释和 XML 文档用中文，这是本仓库的约定。`MafSample.API` 开启了 `GenerateDocumentationFile=true`，因此
  新增 public 成员会产生 CS1591 警告。所有项目都启用了 `Nullable` 和 `ImplicitUsings`。

## NuGet

- `galosys.foundation.*` 是私有包，来自 **用户级** `NuGet.Config`（`%APPDATA%\NuGet\NuGet.Config`）中配置的
  `nexus` 源；仓库内没有配置文件。没有该源的机器无法还原。
- 各示例的包版本不一致（API 用 `26.10.16.2`，其余用 `26.8.*`/`26.9.*`）。新增 Galosys 包时，沿用该项目
  已有的版本号。
- `microsoft.extensions.dataingestion*` 在 DataIngestion 示例中是预览版包。

## 坑点

- `MafSample.slnx` 中保存的是**绝对路径** `G:/Source/...`。移动/重命名仓库后 `dotnet build MafSample.slnx`
  会失效，此时改用显式 `.csproj` 路径构建。
- 各 `appsettings.json` 里包含看似真实的服务凭据（数据库/Redis 密码、钉钉应用密钥、AI API Key）。不要把
  它们复制到新文件、测试或日志里。
