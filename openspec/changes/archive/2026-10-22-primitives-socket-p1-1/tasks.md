# P1-1 服务端内核与契约 Implementation Plan

> **修订记录（2026-10-21）：** 原计划复用 Kestrel public transport。实现前评估发现 `FrameworkReference Microsoft.AspNetCore.App`
> 会把 ASP.NET Core 共享框架带给所有引用方，违背核心库的宿主无关定位。最终实现改为基于
> `System.Net.Sockets` + `System.IO.Pipelines` 的自包含 transport；公共 API 不变，`IOQueueCount` 随 Kestrel 方案删除。
> 下方任务已按最终实现更新。

**Goal:** 用自包含 socket transport 替换 `Galosys.Foundation.Primitives` 的旧内核，冻结服务端公共契约，并用 loopback 集成测试锁定行为。

**Architecture:** `ChannelServerBase` 通过 internal `IChannelListener` / `IChannelConnection` 使用 `SocketChannelListener` 与 `SocketChannelConnection`；每连接创建 `ChannelContext` 与独立 `IServiceScope`；`Configure(IChannelPipeline)` 构建一次委托链并被所有连接复用；`StopAsync` 停止 accept、等待在途连接、超时 abort。

**Tech Stack:** .NET 10、`System.Net.Sockets`、`System.IO.Pipelines`（in-box）、`Microsoft.Extensions.DependencyInjection.Abstractions`、`Microsoft.Extensions.Logging.Abstractions`、xunit 2.9.3。

---

## 1. 测试工程与依赖

- [x] 1.1 创建测试项目（完成）

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`

测试项目使用 xunit 2.9.3、`Microsoft.NET.Test.Sdk` 17.14.1、`xunit.runner.visualstudio` 3.1.4，并通过 `InternalsVisibleTo` 访问内部类型。

- [x] 1.2 配置 Primitives 依赖与内部可见性（完成）

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`

**Step 1: 项目依赖**

```xml
	<ItemGroup>
		<PackageReference Include="microsoft.extensions.dependencyinjection.abstractions" Version="10.0.13" />
		<PackageReference Include="microsoft.extensions.logging.abstractions" Version="10.0.13" />
	</ItemGroup>

	<ItemGroup>
		<InternalsVisibleTo Include="Galosys.Foundation.Primitives.Tests" />
	</ItemGroup>
```

**注意：** 不添加 `<FrameworkReference Include="Microsoft.AspNetCore.App" />`；`System.IO.Pipelines` 在 .NET 10 中由 `Microsoft.NETCore.App` 提供，无需额外包。

- [x] 1.3 建立测试辅助类型（完成）

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/TestSupport.cs`

包含 `TestChannelServer`、`ConfiguredTestServer`、`TestServerHandle`、`TestServer`、`TestClient`、`TestHandlers`、`TrackingScopeFactory`。

## 2. Options 与校验

- [x] 2.1 写 Options 校验测试（完成）
- [x] 2.2 实现 `ChannelServerOptions` 与 `ChannelKeepAliveOptions`（完成）
- [x] 2.3 运行 Options 测试（完成）

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Options/ChannelServerOptions.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Options/ChannelKeepAliveOptions.cs`
- Create: `tests/Galosys.Foundation.Primitives.Tests/OptionsValidationTests.cs`

最终字段：`EndPoint`、`Backlog`、`MaxConnections`、`NoDelay`、`KeepAlive`、`LingerState`、`ReuseAddress`、`DualMode`、`ShutdownTimeout`、`MaxReadBufferSize`、`MaxWriteBufferSize`、`CreateBoundListenSocket`。`IOQueueCount` 已删除。

## 3. 管线

- [x] 3.1 写管线单元测试（完成）
- [x] 3.2 实现管线类型（完成）
- [x] 3.3 运行管线测试（完成）

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Pipeline/ChannelHandlerDelegate.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Pipeline/ChannelMiddleware.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Pipeline/IChannelPipeline.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Pipeline/ChannelPipeline.cs`
- Create: `tests/Galosys.Foundation.Primitives.Tests/PipelineTests.cs`

## 4. 连接契约与自包含传输内核

- [x] 4.1 重写 `ChannelContext`（完成）
- [x] 4.2 重写 `IChannelServer`（完成）
- [x] 4.3 新增自包含 socket 监听与连接（完成）
- [x] 4.4 重写 `ChannelServerBase`（删除旧内核）（完成）
- [x] 4.5 构建 Primitives 并修复编译错误（完成）

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelContext.cs`
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerBase.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/IChannelListener.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/IChannelConnection.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/SocketChannelListener.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/SocketChannelConnection.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/DuplexPipe.cs`
- Delete: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/KestrelChannelListener.cs`

**实现要点：**
- `SocketChannelListener`：创建/绑定/监听 socket；支持 `CreateBoundListenSocket`；应用 `ReuseAddress`/`DualMode`；接受连接后应用 `NoDelay`/`LingerState`/keep-alive。
- `SocketChannelConnection`：输入/输出 `Pipe` 背压；接收循环写入输入 Pipe；发送循环处理部分发送并在正常关闭时排空输出；正常关闭还短暂读空内核接收缓冲区，避免 RST；abort 立即关闭并完成管道。
- `ChannelContext` 包装 `IChannelConnection`，`UseTransport` 可替换传输；正常关闭完成当前传输输出，异常/abort 保留原始 `AbortReason`；对端 RST 通过 `IChannelConnection.Termination` 唤醒 `ConnectionClosed`。
- `ChannelServerBase` 使用 `IChannelListener` 与 `IChannelConnection`，不再引用任何 `Microsoft.AspNetCore.*` 类型；停机超时的 `Abort` 投递到线程池，避免回调重入死锁。

## 5. 集成测试

- [x] 5.1 服务器生命周期测试（完成）
- [x] 5.2 传输与部分发送测试（完成）
- [x] 5.3 连接身份、关闭顺序与作用域测试（完成）
- [x] 5.4 停机与资源释放测试（完成）
- [x] 5.5 依赖边界测试（完成）

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/ServerLifecycleTests.cs`
- Create: `tests/Galosys.Foundation.Primitives.Tests/TransportTests.cs`
- Create: `tests/Galosys.Foundation.Primitives.Tests/ConnectionLifecycleTests.cs`
- Create: `tests/Galosys.Foundation.Primitives.Tests/ShutdownTests.cs`
- Create: `tests/Galosys.Foundation.Primitives.Tests/DependencyBoundaryTests.cs`

**覆盖：** 绑定即返回、端口占用、1 MiB 双向回显、`MaxConnections`、处理器异常隔离、`ConnectionClosed` 恰好一次且先于 scope 释放、优雅关闭排空输出、带未读入站数据的优雅关闭（避免 RST）、客户端半关闭、RST 唤醒空闲处理器、极小 Pipe 阈值、停机等待/超时 abort、`ConnectionClosed` 回调重入 `StopAsync` 不死锁、核心程序集无 ASP.NET 引用。

## 6. 消费方迁移与文档

- [x] 6.1 NetServer 改用 `RunAsync`（完成；P1-2 进一步改为 `AddChannelServer` 宿主托管）
- [x] 6.2 编写 Primitives README（完成）
- [x] 6.3 最终验证（完成）

**Files:**
- Modify: `src/NetServer.ConsoleApp/ListenerBootstrapper.cs`
- Create: `src/Galosys.Foundation.Primitives/README.md`

## 7. 验证记录（2026-10-21）

| 检查 | 结果 |
|---|---|
| `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj` | 0 warnings / 0 errors |
| `dotnet build src/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj` | 成功；仅既有 `CS9113`（`SenderBootstrapper` 未读参数） |
| `dotnet test tests/Galosys.Foundation.Primitives.Tests/...` | 60/60 通过，连续多次稳定 |
| Primitives csproj / `project.assets.json` / 程序集引用 | 无 `Microsoft.AspNetCore.*` 条目 |

## 8. 后续

`primitives-socket-p1-2`（DI、HostedService、示例迁移与文档）已完成（2026-10-21）：见
`../2026-10-22-primitives-socket-p1-2/tasks.md`；全量测试 67/67 通过，Primitives 与 NetServer 构建均无警告。
