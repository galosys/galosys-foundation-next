# restructure-repo-layout-2 实施计划

> 前置条件：`restructure-repo-layout-1` 已应用（`NetServer.ConsoleApp` 位于 `samples/`）。
> 每个任务粒度 2-5 分钟，包含精确路径、命令与预期结果。

## 1. 先写失败的命名空间约定测试

- [x] 1.1 新建 `tests/Galosys.Foundation.Primitives.Tests/NamespaceConventionTests.cs`（已完成：2 个约定测试）
  - 写入：
    ```csharp
    using Microsoft.Extensions.Hosting.Net;

    namespace Galosys.Foundation.Primitives.Tests;

    public sealed class NamespaceConventionTests
    {
        [Fact]
        public void OptionsTypes_UseMicrosoftExtensionsOptionsNamespace()
        {
            var assembly = typeof(ChannelServerBase).Assembly;

            Assert.NotNull(assembly.GetType("Microsoft.Extensions.Options.ChannelServerOptions"));
            Assert.NotNull(assembly.GetType("Microsoft.Extensions.Options.ChannelKeepAliveOptions"));
            Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelServerOptions"));
            Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelKeepAliveOptions"));
        }

        [Fact]
        public void DiExtensions_UseMicrosoftExtensionsDependencyInjectionNamespace()
        {
            var assembly = typeof(ChannelServerBase).Assembly;

            Assert.NotNull(assembly.GetType("Microsoft.Extensions.DependencyInjection.ChannelServerServiceCollectionExtensions"));
            Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelServerServiceCollectionExtensions"));
        }
    }
    ```

- [x] 1.2 运行测试确认失败（已完成：迁移前 2 个测试失败）
  - Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~NamespaceConventionTests"`
  - Expected: FAIL，首个失败为 `Assert.NotNull() Failure`，说明 `Microsoft.Extensions.Options.ChannelServerOptions` 尚不存在。

## 2. 迁移 Primitives 的 Options

- [x] 2.1 将 Options 目录上移（已完成）
  - Run: `git mv src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Options src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options`
  - Expected: 文件变为 `Microsoft/Extensions/Options/ChannelServerOptions.cs` 与 `ChannelKeepAliveOptions.cs`。

- [x] 2.2 修改 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/ChannelServerOptions.cs`（已完成：`Microsoft.Extensions.Options`）
  - 将 `namespace Microsoft.Extensions.Hosting.Net` 改为 `namespace Microsoft.Extensions.Options`。

- [x] 2.3 修改 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/ChannelKeepAliveOptions.cs`（已完成：`Microsoft.Extensions.Options`）
  - 将 `namespace Microsoft.Extensions.Hosting.Net` 改为 `namespace Microsoft.Extensions.Options`。

- [x] 2.4 为 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerBase.cs` 补 using（已完成）
  - 在 `using Microsoft.Extensions.Logging;` 后添加 `using Microsoft.Extensions.Options;`。

- [x] 2.5 为 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/IChannelServer.cs` 补 using（已完成）
  - 在 `using System.Net;` 后添加 `using Microsoft.Extensions.Options;`。

- [x] 2.6 为 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/SocketChannelListener.cs` 补 using（已完成）
  - 在 `using Microsoft.Extensions.Logging;` 后添加 `using Microsoft.Extensions.Options;`。

- [x] 2.7 为 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/SocketChannelConnection.cs` 补 using（已完成）
  - 在 `using Microsoft.Extensions.Logging;` 后添加 `using Microsoft.Extensions.Options;`。

## 3. 迁移 Primitives 的 DI 扩展

- [x] 3.1 创建目标目录并移动文件（已完成）
  - Run: `New-Item -ItemType Directory -Path src/Galosys.Foundation.Primitives/Microsoft/Extensions/DependencyInjection -Force`
  - Run: `git mv src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Hosting/ChannelServerServiceCollectionExtensions.cs src/Galosys.Foundation.Primitives/Microsoft/Extensions/DependencyInjection/ChannelServerServiceCollectionExtensions.cs`
  - Expected: 文件位于 `Microsoft/Extensions/DependencyInjection/`。

- [x] 3.2 修改 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/DependencyInjection/ChannelServerServiceCollectionExtensions.cs`（已完成：`Microsoft.Extensions.DependencyInjection`）
  - 将 `namespace Microsoft.Extensions.Hosting.Net` 改为 `namespace Microsoft.Extensions.DependencyInjection`。
  - 删除顶部的 `using Microsoft.Extensions.DependencyInjection;`（已处于同一命名空间）。
  - 添加：
    ```csharp
    using Microsoft.Extensions.Hosting.Net;
    using Microsoft.Extensions.Options;
    ```

## 4. 更新 Primitives 测试的 using

- [x] 4.1 更新 `HostingTests.cs`、`ScopedHostingTests.cs`、`TestSupport.cs`（已完成）
  - 在三个文件的 `using Microsoft.Extensions.Hosting.Net;` 后添加 `using Microsoft.Extensions.Options;`。

- [x] 4.2 更新 `OptionsValidationTests.cs`、`PipelineIntegrationTests.cs`（已完成）
  - 在 `using Microsoft.Extensions.Hosting.Net;` 后添加 `using Microsoft.Extensions.Options;`。

- [x] 4.3 更新 `ServerLifecycleTests.cs`、`ShutdownTests.cs`（已完成）
  - 在 `using Microsoft.Extensions.Hosting.Net;` 后添加 `using Microsoft.Extensions.Options;`。

- [x] 4.4 运行命名空间约定测试确认通过（已完成：2/2 通过）
  - Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~NamespaceConventionTests"`
  - Expected: PASS。

- [x] 4.5 运行全部 Primitives 测试（已完成：69/69 通过）
  - Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
  - Expected: 全部测试通过，`AddChannelServer_RegistersSameServerInstance` 仍通过。

## 5. 迁移 Doris 的 Options

- [x] 5.1 创建目录并移动 `DorisOptions.cs`（已完成）
  - Run: `New-Item -ItemType Directory -Path src/Galosys.Foundation.Doris/Microsoft/Extensions/Options -Force`
  - Run: `git mv src/Galosys.Foundation.Doris/DorisOptions.cs src/Galosys.Foundation.Doris/Microsoft/Extensions/Options/DorisOptions.cs`

- [x] 5.2 修改 `src/Galosys.Foundation.Doris/Microsoft/Extensions/Options/DorisOptions.cs`（已完成：`Microsoft.Extensions.Options`）
  - 将 `namespace Galosys.Foundation.Doris` 改为 `namespace Microsoft.Extensions.Options`。

- [x] 5.3 构建 Doris（已完成：Build succeeded；重新编译时仅保留既有 CS8604 警告）
  - Run: `dotnet build src/Galosys.Foundation.Doris/Galosys.Foundation.Doris.csproj`
  - Expected: Build succeeded；`DorisStreamLoadClient` 已有的 `using Microsoft.Extensions.Options;` 能解析 `DorisOptions`。

## 6. 迁移 Nats 的 Options 与 DI 辅助

- [x] 6.1 创建目录并移动 `NatsOptions.cs`（已完成）
  - Run: `New-Item -ItemType Directory -Path src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/Options -Force`
  - Run: `git mv src/Galosys.Foundation.Nats.Net/NatsOptions.cs src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/Options/NatsOptions.cs`

- [x] 6.2 修改 `src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/Options/NatsOptions.cs`（已完成：`Microsoft.Extensions.Options`）
  - 将 `namespace System` 改为 `namespace Microsoft.Extensions.Options`。

- [x] 6.3 创建目录并移动 `ServiceProviderExtensions.cs`（已完成）
  - Run: `New-Item -ItemType Directory -Path src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/DependencyInjection -Force`
  - Run: `git mv src/Galosys.Foundation.Nats.Net/ServiceProviderExtensions.cs src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/DependencyInjection/ServiceProviderExtensions.cs`

- [x] 6.4 修改 `src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/DependencyInjection/ServiceProviderExtensions.cs`（已完成：`Microsoft.Extensions.DependencyInjection`）
  - 将 `namespace System` 改为 `namespace Microsoft.Extensions.DependencyInjection`。
  - 保留已有 `using Microsoft.Extensions.Options;`，确保 `NatsOptions` 可解析。

- [x] 6.5 静态校验 Nats 改动（Nats 不可编译；已完成：仅见 Options/DI 命名空间，无 `namespace System`）
  - Run: `rg -n "^namespace " src/Galosys.Foundation.Nats.Net/Microsoft/Extensions`
  - Expected: 出现 `Microsoft.Extensions.Options` 与 `Microsoft.Extensions.DependencyInjection`，不出现 `namespace System`。
  - 注意：不要执行 `dotnet build src/Galosys.Foundation.Nats.Net/...`，该原型仍按已知限制不可编译。

## 7. 更新 NetServer 示例

- [x] 7.1 修改 `samples/NetServer.ConsoleApp/SampleChannelServer.cs`（已完成：补 `Microsoft.Extensions.Options`）
  - 在 `using Microsoft.Extensions.Hosting.Net;` 后添加 `using Microsoft.Extensions.Options;`。

- [x] 7.2 修改 `samples/NetServer.ConsoleApp/Program.cs`（已完成：改用 `Microsoft.Extensions.DependencyInjection`）
  - 删除 `using Microsoft.Extensions.Hosting.Net;`。
  - 添加 `using Microsoft.Extensions.DependencyInjection;`，确保 `AddChannelServer` 与 `AddHostedService` 显式可见。

- [x] 7.3 构建 NetServer（已完成：0 警告/0 错误）
  - Run: `dotnet build samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
  - Expected: Build succeeded。

## 8. 更新 README 与 AGENTS

- [x] 8.1 更新 `src/Galosys.Foundation.Primitives/README.md`（已完成：补充命名空间分布与 using 说明）
  - 将开头“命名空间 `Microsoft.Extensions.Hosting.Net`”改为：
    “核心类型位于 `Microsoft.Extensions.Hosting.Net`；`*Options` 位于 `Microsoft.Extensions.Options`，宿主注册扩展 `AddChannelServer` 位于 `Microsoft.Extensions.DependencyInjection`。”
  - 在“宿主接入”章节补一句：使用方通常已引用 `Microsoft.Extensions.DependencyInjection` 与 `Microsoft.Extensions.Options`，无需额外 using。

- [x] 8.2 更新 `AGENTS.md`（已完成：新增命名空间寄生约定，更新本地类库说明）
  - 在“架构事实”中增加：
    ```markdown
    - **命名空间寄生**：foundation 类库的 `*Options` 放 `Microsoft.Extensions.Options`，`IServiceCollection` / `IServiceProvider` DI 扩展放 `Microsoft.Extensions.DependencyInjection`；核心宿主类型继续留在 `Microsoft.Extensions.Hosting.Net`。
    ```
  - 更新“本地类库”条目，说明 Primitives 的 Options 已位于 `Microsoft.Extensions.Options`。

- [x] 8.3 检查文档没有遗留旧命名空间描述（已完成：无输出）
  - Run: `rg -n "ChannelServerOptions.*Microsoft.Extensions.Hosting.Net|DorisOptions.*Galosys.Foundation.Doris|NatsOptions.*System" src tests samples AGENTS.md README.md`
  - Expected: 无输出。

## 9. 最终验证

- [x] 9.1 构建 Primitives 与 Doris（已完成：Primitives 0 警告/0 错误；Doris 0 错误）
  - Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
  - Run: `dotnet build src/Galosys.Foundation.Doris/Galosys.Foundation.Doris.csproj`
  - Expected: 均 Build succeeded。

- [x] 9.2 运行全部 Primitives 测试（已完成：69/69 通过）
  - Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
  - Expected: 全部测试通过。

- [x] 9.3 构建 NetServer 示例（已完成：0 警告/0 错误）
  - Run: `dotnet build samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
  - Expected: Build succeeded。

- [x] 9.4 检查旧命名空间与旧目录已消失（已完成：两条检查均无输出）
  - Run: `rg -n "namespace Microsoft.Extensions.Hosting.Net" src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options src/Galosys.Foundation.Primitives/Microsoft/Extensions/DependencyInjection`
  - Expected: 无输出。
  - Run: `rg -n "namespace (Galosys.Foundation.Doris|System)" src/Galosys.Foundation.Doris/Microsoft/Extensions/Options src/Galosys.Foundation.Nats.Net/Microsoft/Extensions`
  - Expected: 无输出。

- [x] 9.5 确认工作区状态（已完成：迁移与修改均在；`MafSample.API` 用户修改 diffstat 保持 48 插入/40 删除）
  - Run: `git status --short`
  - Expected: 包含文件移动（`R`）与上述源码/测试/文档修改；`MafSample.API` 的用户未提交修改仍在。
