# Tasks

## 1. 派发路径接口化

- [x] 1.1 从 `src/Galosys.Foundation.Nats.Net/Galosys/Foundation/Nats/Net/NatsSubscription.cs` 删除 `MethodInfo InvokableHandleAsync` 参数；验证：`Select-String -Path <该文件> -Pattern "InvokableHandleAsync"` 无输出，且 `HandlerMethod`、`MessageType` 等其余参数保持不变
- [x] 1.2 在 `src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/DependencyInjection/ServiceProviderExtensions.cs` 把 `ResolveHandleAsync`（第 112–139 行）替换为 `IsMessageType` 校验，并把第 89–103 行的调用与 `NatsSubscription` 构造改为不传 `handleAsync`；验证：`Select-String -Path <该文件> -Pattern "MakeGenericType"` 无输出
- [x] 1.3 在 `src/Galosys.Foundation.Nats.Net/Galosys/Foundation/Nats/Net/NatsConsumerDispatcher.cs` 把第 77–106 行改为非泛型 `IMessageHandler` 调用并删除 `TargetInvocationException` 分支；验证：`Select-String -Path <该文件> -Pattern "TargetInvocationException|\.Invoke\("` 无输出
- [x] 1.4 在 `src/Galosys.Foundation.Nats.Net/README.md` 的「语义说明」小节补充派发契约：处理器必须实现 `IMessageHandler<T>`，框架经非泛型 `IMessageHandler` 派发；验证：`Select-String -Path <该文件> -Pattern "非泛型"` 有匹配
- [x] 1.5 构建类库并确认 0 错误：`dotnet build src/Galosys.Foundation.Nats.Net/Galosys.Foundation.Nats.Net.csproj --no-incremental -v q --nologo`，期望输出 `0 个错误`（允许既有的 1 条 NU1506 警告）

### 参考实现

`ServiceProviderExtensions` 中的校验方法（替换 `ResolveHandleAsync`）：

```csharp
private static bool IsMessageType(Type messageType, MethodInfo method, ILogger? logger)
{
    if (typeof(IMessage).IsAssignableFrom(messageType))
        return true;

    logger?.LogWarning(
        "NATS 消费者方法 {Method} 的形参类型 {MessageType} 未实现 IMessage，跳过。",
        method.DeclaringType?.FullName,
        messageType.FullName);

    return false;
}
```

`NatsConsumerDispatcher.DispatchAsync` 的调用段（替换原第 77–106 行）：

```csharp
if (handler is not IMessageHandler messageHandler)
{
    _logger.LogError(
        "NATS 消费者未实现 IMessageHandler 契约: subject={Subject}, type={Type}",
        subscription.Subject,
        subscription.HandlerMethod.DeclaringType?.FullName);
    return false;
}

try
{
    // IMessageHandler<T> 带有默认接口实现，会转发到 HandleAsync(T)；
    // 这里按非泛型契约调用，避免每条消息上的反射与实参数组分配。
    return await messageHandler.HandleAsync((IMessage)message).ConfigureAwait(false);
}
catch (Exception ex)
{
    _logger.LogError(
        ex,
        "NATS 消费者处理失败: subject={Subject}, type={Type}",
        subscription.Subject,
        subscription.HandlerMethod.DeclaringType?.FullName);
    return false;
}
```

同时删除 `NatsConsumerDispatcher.cs` 顶部不再需要的 `using System.Reflection;`。

## 2. 示例构建与端到端验证

- [x] 2.1 构建示例并确认 0 错误：`dotnet build samples/NatsSample.ConsoleApp/NatsSample.ConsoleApp.csproj --no-incremental -v q --nologo`，期望输出 `0 个错误`
- [x] 2.2 准备本地 nats-server：下载 `https://github.com/nats-io/nats-server/releases/download/v2.15.2/nats-server-v2.15.2-windows-amd64.zip` 解压到 `C:\Users\zhangjian\AppData\Local\Temp\opencode\nats-server`，写 `nats.conf`（`port: 4222` + `authorization { user: appuser, password: AppPass123 }`）并后台启动；验证：`Test-NetConnection 127.0.0.1 -Port 4222 -InformationLevel Quiet` 返回 `True`
- [x] 2.3 正常路径：运行 `samples/NatsSample.ConsoleApp/bin/Debug/net10.0/NatsSample.ConsoleApp.exe`（工作目录设为示例目录）约 12 秒后终止，捕获 stdout；验证：输出同时包含 `NATS 订阅已建立: subject=sample.greet` 与 `收到 NATS 消息`
- [x] 2.4 异常路径：备份 `samples/NatsSample.ConsoleApp/Adapter/Handlers/SampleGreatHandler.cs`，在日志行后临时插入 `throw new InvalidOperationException("boom");`，重新构建并运行约 12 秒；验证：输出包含 `NATS 消费者处理失败` 与 `boom`，且进程在 12 秒后仍存活（`alive=True`）；随后用备份还原该文件并重新构建
- [x] 2.5 停机路径：在临时宿主中调用 `host.StartAsync()` → 等待 3 秒 → `host.StopAsync()`，验证：`StopAsync` 在 1 秒内返回，且停机后再发布消息不会触发处理器
- [x] 2.6 清理：`Get-Process nats-server | Stop-Process -Force`，并删除临时 nats-server 目录；验证：`Get-Process nats-server -ErrorAction SilentlyContinue` 无输出

## 3. OpenSpec 校验

- [x] 3.1 运行 `openspec validate "nats-consumer-interface-dispatch" --strict --no-interactive`，期望输出 `Change 'nats-consumer-interface-dispatch' is valid`

## Workflow follow-up

- 实现与验证完成后，用 `/opsx-archive-x` 归档到 `openspec/changes/archive/2026-11-05-nats-consumer-interface-dispatch/`。
- 归档时确认 `skip_specs: true` 仍成立：本次不产生 spec delta，`openspec/specs/nats-consumer/spec.md` 不应被修改。
- 归档时评估 `AGENTS.md` 是否需要更新（预计不需要：库的能力描述与构建命令均未变化）。
