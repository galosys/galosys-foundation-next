# DTFx.ConsoleApp

`Galosys.Foundation.DurableTask.Core` 的 Durable Task 编排示例：用 SQL Server 持久化编排状态，演示
「创建编排实例 → 投递审批外部事件 → 编排调度活动」的完整链路。

## 前置条件

- 一个可访问的 SQL Server 实例。
- 连接串配置在 `appsettings.json` 的 `ConnectionStrings:Default` 节（示例仓库中的连接串为占位值，
  需按本地环境替换；也可通过 user-secrets 或环境变量 `ConnectionStrings__Default` 覆盖）。
- 编排以 task hub key `dtfx` 持久化，实例状态保存在 SQL Server 中。

## 运行

```powershell
dotnet run --project samples/DTFx.ConsoleApp/DTFx.ConsoleApp.csproj
```

预期输出：

```text
>>>>> Orchestration: user-1 <<<<<
>>>>> Activity: user-1 <<<<<
```

- 编排在 `SampleOrchestration.RunTask` 中打印 `>>>>> Orchestration: ... <<<<<`。
- 编排随后调度 `SayHelloActivity`，活动在 `Execute` 中打印 `>>>>> Activity: ... <<<<<`。
- 编排重放时打印 `>>>>> Orchestration replaying: ... <<<<<`。

按 Ctrl+C 触发宿主优雅停机，`DurableTaskHostedService` 会停止 `TaskHubWorker`。

## 构建

```powershell
dotnet build samples/DTFx.ConsoleApp/DTFx.ConsoleApp.csproj
```

## 结构

| 文件 | 作用 |
|---|---|
| `Program.cs` | 注册 `AddDurableTaskCore()` + `AddDurableTaskOrchestration(...)`（SQL Server 后端，task hub key `dtfx`）与 `Worker` |
| `Worker.cs` | 后台服务：创建编排实例并用 `RaiseAsync` 投递 `Approval` 事件；含 `SampleOrchestration` 与 `SayHelloActivity` |
| `ApprovalResult.cs` | 审批事件体 |

## 说明

- `AddDurableTaskOrchestration(...)` 的第二个参数是 task hub key，固定为 `dtfx`；第三个参数是 task hub 名。
- `RaiseAsync` 是 `Galosys.Foundation.DurableTask.Core` 提供的 `TaskHubClient` 扩展方法，
  需要 `using Galosys.Foundation.DurableTask.Core;`；`using DurableTask.Core;` 解析的是上游包的类型。
- `Worker` 用 `IServiceScopeFactory` 建作用域后解析 `TaskHubClient` 与 `SqlOrchestrationService`，
  并先调用 `CreateIfNotExistsAsync()` 建表。
- 同一 `instanceId`（示例中为 `user-1`）重复运行会复用已有编排实例。
