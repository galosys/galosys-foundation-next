# Galosys.Foundation.DurableTask.Core

Durable Task 编排/活动封装：把上游 `Microsoft.Azure.Durabletask.Core` 的 `TaskHubWorker`、`TaskHubClient` 与
.NET 宿主生命周期打通，提供 DI 注册、宿主生命周期适配与 `TaskHubClient` 扩展方法三件套。

核心类型位于 `Galosys.Foundation.DurableTask.Core`；DI 扩展位于 `Microsoft.Extensions.DependencyInjection`；
宿主生命周期适配器位于 `Microsoft.Extensions.Hosting`。

## 能力

- `AddDurableTaskCore()`：扫描当前 `DependencyContext` 中所有 `TaskOrchestration<,>` 与 `TaskActivity<,>` 实现，
  自动注册为 transient，并把编排/活动装配进 `TaskHubWorker`；同时注册 `TaskHubClient` 与宿主适配器。
- `AddDurableTaskOrchestration<T>()`：注册一个同时实现 `IOrchestrationService` 与 `IOrchestrationServiceClient`
  的持久化后端（SQL Server / Azure Storage / Service Bus 等），并作为单例暴露给 `TaskHubWorker` 与 `TaskHubClient`。
- `DurableTaskHostedService`：把 `TaskHubWorker` 的启停映射到 `IHostedService`，宿主启动时开始拉取任务，
  停机时优雅退出。
- `TaskHubClientExtensions.RaiseAsync<TEvent>`：向运行中的编排实例投递强类型外部事件，事件体自动 JSON 序列化。

## 持久化后端

本库是厂商中立封装：只依赖上游 `Microsoft.Azure.Durabletask.Core` SDK（SQL Server / Azure Storage /
Service Bus 等后端由上游包提供），本库不绑定任何具体存储实现。

后端通过 `AddDurableTaskOrchestration<T>` 注入，`T` 需同时实现 `IOrchestrationService` 与
`IOrchestrationServiceClient`，例如 `SqlOrchestrationService`。库本身不提供后端实现，消费方需自行引用对应的
上游包（如 `Microsoft.DurableTask.SqlServer`）。

## 命名空间归属

| 类型 | 命名空间 |
|---|---|
| `TaskHubClientExtensions` | `Galosys.Foundation.DurableTask.Core` |
| `DurableTaskServiceCollectionExtensions`（`AddDurableTaskCore`、`AddDurableTaskOrchestration`） | `Microsoft.Extensions.DependencyInjection` |
| `DurableTaskHostedService` | `Microsoft.Extensions.Hosting` |

上游包的 `DurableTask.Core` 命名空间由 `Microsoft.Azure.Durabletask.Core` 单独拥有。本库不在该命名空间下声明
任何类型，因此消费方需要显式 `using Galosys.Foundation.DurableTask.Core;` 才能使用本库的类型。

物理目录与命名空间一致：

| 内容 | 目录 |
|---|---|
| 核心类型 | `Galosys/Foundation/DurableTask/Core/` |
| DI 扩展 | `Microsoft/Extensions/DependencyInjection/` |
| 宿主适配器 | `Microsoft/Extensions/Hosting/` |

## 用法

### 注册

```csharp
var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default");

builder.Services.AddDurableTaskCore()
    .AddDurableTaskOrchestration(sp => new SqlOrchestrationService(
        new SqlOrchestrationServiceSettings(connectionString, "Default", "dtfx")));

var host = builder.Build();
host.Run();
```

`AddDurableTaskCore()` 内部已注册 `DurableTaskHostedService`，无需再手动 `AddHostedService`。

### 投递外部事件

```csharp
using Galosys.Foundation.DurableTask.Core;

var client = sp.GetRequiredService<TaskHubClient>();
var instance = await client.CreateOrchestrationInstanceAsync(typeof(SampleOrchestration), "user-1", "user-1");

await client.RaiseAsync(instance, "Approval", new ApprovalResult
{
    BusinessId = 1,
    BusinessType = 1,
    ApproverId = "user-1"
});
```

`RaiseAsync<TEvent>` 是 `TaskHubClient` 的扩展方法：把 `TEvent` 用 Newtonsoft.Json 序列化为字符串，再调用上游的
`RaiseEventAsync`。编排侧通过 `OnEvent` 接收并反序列化：

```csharp
public override void OnEvent(OrchestrationContext context, string name, string input)
{
    if (name.Equals("Approval"))
    {
        var result = JsonConvert.DeserializeObject<ApprovalResult>(input);
        _approvalSource.TrySetResult(result);
    }

    base.OnEvent(context, name, input);
}
```

### 编写编排与活动

编排继承 `TaskOrchestration<TInput, TOutput>`，活动继承 `TaskActivity<TInput, TOutput>`，两者都会被
`AddDurableTaskCore()` 自动发现并注册。

```csharp
class SampleOrchestration : TaskOrchestration<string, string>
{
    public override async Task<string> RunTask(OrchestrationContext context, string input)
    {
        return await context.ScheduleTask<string>(typeof(SayHelloActivity), input);
    }
}

class SayHelloActivity : TaskActivity<string, string>
{
    protected override string Execute(TaskContext context, string input) => "ok";
}
```

## 构建

```powershell
dotnet build src/Galosys.Foundation.DurableTask.Core/Galosys.Foundation.DurableTask.Core.csproj
```

运行示例：

```powershell
dotnet run --project samples/DTFx.ConsoleApp/DTFx.ConsoleApp.csproj
```

示例需要可访问的 SQL Server 实例，连接串取自配置节 `ConnectionStrings:Default`，详见
`samples/DTFx.ConsoleApp/README.md`。

## 说明

- `DurableTaskAttribute` 已从编译中排除（`<Compile Remove>`），不属于本库的可用 API。
- `AddDurableTaskCore()` 通过 `DependencyContext.Default` 扫描类型，消费方项目需引用本库才能被扫到。
- 编排/活动实现以 transient 注册，`ServiceProviderObjectCreator` 在每次创建时从容器解析实例，
  因此构造函数可注入 scoped 与 transient 依赖。
