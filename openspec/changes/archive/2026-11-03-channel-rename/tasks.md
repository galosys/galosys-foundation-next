# Tasks

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 把 `Galosys.Foundation.Primitives` 重命名为 `Galosys.Foundation.Channel`，并将核心公共类型从 `Microsoft.Extensions.Hosting.Net` 迁移到 `Galosys.Foundation.Channel` 命名空间。

**Architecture:** 三个提交分层落地：提交 1 纯目录/项目文件重命名（零内容变更，代码仍可编译），提交 2 做命名空间迁移与目录重排（唯一语义变更点），提交 3 更新文档与 openspec spec。公共类型名（`ChannelServer` 等 46 个）全部不变，破坏面收敛到程序集名与命名空间两项。

**Tech Stack:** .NET 10、xUnit、openspec、PowerShell

---

## 1. 提交 1：纯重命名（目录、项目文件、引用路径）

**Files:**
- Move: `src/Galosys.Foundation.Primitives/` → `src/Galosys.Foundation.Channel/`
- Move: `src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj` → `src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj`
- Move: `tests/Galosys.Foundation.Primitives.Tests/` → `tests/Galosys.Foundation.Channel.Tests/`
- Move: `tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj` → `tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj`
- Modify: `src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj`
- Modify: `tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj`
- Modify: `Galosys.Foundation.Next.slnx`
- Modify: `samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`

- [x] 1.1 用 `git mv` 重命名库目录与 csproj：`git mv src/Galosys.Foundation.Primitives src/Galosys.Foundation.Channel`，然后 `git mv src/Galosys.Foundation.Channel/Galosys.Foundation.Primitives.csproj src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj`。验证：`Test-Path src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj` 为 True 且 `Test-Path src/Galosys.Foundation.Primitives` 为 False

- [x] 1.2 用 `git mv` 重命名测试目录与 csproj：`git mv tests/Galosys.Foundation.Primitives.Tests tests/Galosys.Foundation.Channel.Tests`，然后 `git mv tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Primitives.Tests.csproj tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj`。验证：两个路径 `Test-Path` 检查通过

- [x] 1.3 更新库 csproj 的 `InternalsVisibleTo`：把 `<InternalsVisibleTo Include="Galosys.Foundation.Primitives.Tests" />` 改为 `<InternalsVisibleTo Include="Galosys.Foundation.Channel.Tests" />`。验证：`Select-String -Path src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj -Pattern 'Galosys.Foundation.Channel.Tests'` 命中 1 处

- [x] 1.4 更新测试 csproj 的 `RootNamespace` 与 `ProjectReference`：`<RootNamespace>Galosys.Foundation.Primitives.Tests</RootNamespace>` 改为 `Galosys.Foundation.Channel.Tests`；`<ProjectReference Include="..\..\src\Galosys.Foundation.Primitives\Galosys.Foundation.Primitives.csproj" />` 改为 `..\..\src\Galosys.Foundation.Channel\Galosys.Foundation.Channel.csproj`。验证：`Select-String -Path tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj -Pattern 'Primitives'` 无输出

- [x] 1.5 更新 `Galosys.Foundation.Next.slnx` 的两处 `<Project Path>`：`src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj` → `src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj`；`tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj` → `tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj`。**保留原有 `Id` GUID 不变**。验证：`dotnet sln Galosys.Foundation.Next.slnx list` 成功退出且输出含 `Galosys.Foundation.Channel.csproj` 与 `Galosys.Foundation.Channel.Tests.csproj`

- [x] 1.6 更新 `samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj` 的 `ProjectReference`：路径改为 `..\..\src\Galosys.Foundation.Channel\Galosys.Foundation.Channel.csproj`。验证：`Select-String -Path samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj -Pattern 'Primitives'` 无输出

- [x] 1.7 构建库并运行测试，确认纯重命名后代码仍可编译：`dotnet build src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj` 然后 `dotnet test tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj`。预期：两者均成功退出，测试全部通过（此时类型仍在 `Microsoft.Extensions.Hosting.Net`，只换了程序集名）

- [x] 1.8 提交：`git add -A && git commit -m "refactor!: rename Galosys.Foundation.Primitives to Galosys.Foundation.Channel"`。预期：提交成功且变更仅含重命名与项目文件路径

## 2. 提交 2：命名空间迁移与目录重排

**Files:**
- Move: `src/Galosys.Foundation.Channel/Codecs/` → `src/Galosys.Foundation.Channel/Channel/Codecs/`
- Move: `src/Galosys.Foundation.Channel/Framing/` → `src/Galosys.Foundation.Channel/Channel/Framing/`
- Move: `src/Galosys.Foundation.Channel/Session/` → `src/Galosys.Foundation.Channel/Channel/Session/`
- Move: `src/Galosys.Foundation.Channel/Microsoft/Extensions/Hosting/Net/Client/` → `src/Galosys.Foundation.Channel/Channel/Client/`
- Move: `src/Galosys.Foundation.Channel/Microsoft/Extensions/Hosting/Net/Pipeline/` → `src/Galosys.Foundation.Channel/Channel/Pipeline/`
- Move: `src/Galosys.Foundation.Channel/Microsoft/Extensions/Hosting/Net/Protocols/` → `src/Galosys.Foundation.Channel/Channel/Protocols/`
- Move: `src/Galosys.Foundation.Channel/Microsoft/Extensions/Hosting/Net/Transports/` → `src/Galosys.Foundation.Channel/Channel/Transports/`
- Move: `src/Galosys.Foundation.Channel/Microsoft/Extensions/Hosting/Net/ChannelContext.cs` 等 6 个根级文件 → `src/Galosys.Foundation.Channel/Channel/`
- Move: `src/Galosys.Foundation.Channel/Microsoft/Extensions/Hosting/Net/Hosting/ChannelServerGroupHostedService.cs` → `src/Galosys.Foundation.Channel/Microsoft/Extensions/Hosting/`
- Modify: 全部 44 个 `namespace Microsoft.Extensions.Hosting.Net` 文件
- Modify: `src/Galosys.Foundation.Channel/Microsoft/Extensions/Hosting/ChannelServerGroupHostedService.cs`
- Modify: `src/Galosys.Foundation.Channel/Microsoft/Extensions/DependencyInjection/ChannelServerGroupServiceCollectionExtensions.cs`
- Modify: `tests/Galosys.Foundation.Channel.Tests/` 全部测试文件的 `using`

- [x] 2.1 用 `git mv` 收拢 `Codecs/`、`Framing/`、`Session/` 到 `Channel/` 下：`git mv src/Galosys.Foundation.Channel/Codecs src/Galosys.Foundation.Channel/Channel/Codecs`（`Channel/` 需先 `New-Item -ItemType Directory`），同样处理 `Framing/` 与 `Session/`。验证：`Get-ChildItem src/Galosys.Foundation.Channel/Channel` 输出含 `Codecs`、`Framing`、`Session` 三个目录

- [x] 2.2 用 `git mv` 迁移 `Client/`、`Pipeline/`、`Protocols/`、`Transports/` 四个目录到 `Channel/` 下。验证：`Get-ChildItem src/Galosys.Foundation.Channel/Channel -Directory` 输出含全部 7 个子目录

- [x] 2.3 用 `git mv` 迁移 6 个根级文件到 `Channel/`：`ChannelContext.cs`、`ChannelServer.cs`、`ChannelServerCore.cs`、`ChannelServerDefinition.cs`、`ChannelServerGroup.cs`、`IChannelServer.cs`。验证：`Test-Path src/Galosys.Foundation.Channel/Channel/ChannelServer.cs` 为 True

- [x] 2.4 用 `git mv` 把 `ChannelServerGroupHostedService.cs` 从 `Microsoft/Extensions/Hosting/Net/Hosting/` 移到 `Microsoft/Extensions/Hosting/`，并删除空的 `Net/` 目录树。验证：`Test-Path src/Galosys.Foundation.Channel/Microsoft/Extensions/Hosting/ChannelServerGroupHostedService.cs` 为 True 且 `Test-Path src/Galosys.Foundation.Channel/Microsoft/Extensions/Hosting/Net` 为 False

- [x] 2.5 把 44 个文件的 `namespace Microsoft.Extensions.Hosting.Net` 改为 `namespace Galosys.Foundation.Channel`。用 PowerShell 批量替换（注意保留文件原有 CRLF 与 UTF-8 BOM）：

  ```powershell
  $files = Get-ChildItem -Recurse -Include *.cs -Path src\Galosys.Foundation.Channel | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }
  foreach ($f in $files) {
    $c = [IO.File]::ReadAllText($f.FullName)
    if ($c -match 'namespace Microsoft\.Extensions\.Hosting\.Net') {
      $c = $c -replace 'namespace Microsoft\.Extensions\.Hosting\.Net', 'namespace Galosys.Foundation.Channel'
      [IO.File]::WriteAllText($f.FullName, $c)
    }
  }
  ```

  验证：`Select-String -Path src/Galosys.Foundation.Channel/**/*.cs -Pattern 'namespace Microsoft.Extensions.Hosting.Net'` 无输出

- [x] 2.6 把 `ChannelServerGroupHostedService.cs` 的命名空间改为 `namespace Microsoft.Extensions.Hosting`，并把其 `using Microsoft.Extensions.Hosting;` 改为 `using Galosys.Foundation.Channel;`（该类型引用 `ChannelServerGroup`）。验证：`Select-String -Path src/Galosys.Foundation.Channel/Microsoft/Extensions/Hosting/ChannelServerGroupHostedService.cs -Pattern 'namespace Microsoft.Extensions.Hosting'` 命中 1 处且文件含 `using Galosys.Foundation.Channel;`

- [x] 2.7 把 `ChannelServerGroupServiceCollectionExtensions.cs` 的 `using Microsoft.Extensions.Hosting.Net;` 改为 `using Galosys.Foundation.Channel;`（它引用 `ChannelServerDefinition`、`ChannelServer`、`ChannelServerGroup`）。验证：`Select-String -Path src/Galosys.Foundation.Channel/Microsoft/Extensions/DependencyInjection/ChannelServerGroupServiceCollectionExtensions.cs -Pattern 'Hosting.Net'` 无输出

- [x] 2.8 检查库内剩余的 `using Microsoft.Extensions.Hosting.Net;` 并全部替换为 `using Galosys.Foundation.Channel;`（含 `Microsoft/Extensions/Options/` 下 10 个 Options 文件若有引用）。验证：`Select-String -Path src/Galosys.Foundation.Channel/**/*.cs -Pattern 'Microsoft.Extensions.Hosting.Net'` 无输出

- [x] 2.9 构建库并修复编译错误：`dotnet build src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj`。预期：构建成功；若报错说明存在遗漏的命名空间引用或类型未解析，逐个修正后重跑直至成功

- [x] 2.10 更新测试项目的 `using` 引用：把 `tests/Galosys.Foundation.Channel.Tests/` 下全部 `.cs` 文件中的 `using Microsoft.Extensions.Hosting.Net;` 替换为 `using Galosys.Foundation.Channel;`。注意 `NamespaceConventionTests.cs` 同时需要 `Microsoft.Extensions.Options` 的 `using`（保留）。验证：`Select-String -Path tests/Galosys.Foundation.Channel.Tests/**/*.cs -Pattern 'Microsoft.Extensions.Hosting.Net'` 无输出

- [x] 2.11 更新 `NamespaceConventionTests.cs` 的反射断言：
  - `assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelServerOptions")` → `"Galosys.Foundation.Channel.ChannelServerOptions"`
  - `assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelKeepAliveOptions")` → `"Galosys.Foundation.Channel.ChannelKeepAliveOptions"`
  - `assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelServerBase")` → `"Galosys.Foundation.Channel.ChannelServerBase"`
  - `assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelClientBase")` → `"Galosys.Foundation.Channel.ChannelClientBase"`
  - `assembly.GetType("Microsoft.Extensions.DependencyInjection.ChannelServerServiceCollectionExtensions")` 保持不变
  - `typeof(ChannelClient).Namespace` 的期望值 `"Microsoft.Extensions.Hosting.Net"` → `"Galosys.Foundation.Channel"`
  - `typeof(ChannelClientOptions).Namespace` 的期望值 `"Microsoft.Extensions.Options"` 保持不变
  - `assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelServerCore")` → `"Galosys.Foundation.Channel.ChannelServerCore"`

  验证：`dotnet test tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj --filter "FullyQualifiedName~NamespaceConventionTests"` 全部通过

- [x] 2.12 在 `NamespaceConventionTests.cs` 补一条断言，验证宿主适配器与 DI 扩展的新命名空间（对应 spec 的「宿主适配器与 DI 扩展的命名空间」场景）：

  ```csharp
  [Fact]
  public void HostedServiceAndDiExtensions_UseMicrosoftExtensionsNamespaces()
  {
      var assembly = typeof(ChannelServer).Assembly;

      Assert.NotNull(assembly.GetType("Microsoft.Extensions.Hosting.ChannelServerGroupHostedService"));
      Assert.NotNull(assembly.GetType("Microsoft.Extensions.DependencyInjection.ChannelServerGroupServiceCollectionExtensions"));
  }
  ```

  验证：`dotnet test tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj --filter "FullyQualifiedName~NamespaceConventionTests"` 全部通过

- [x] 2.13 在 `NamespaceConventionTests.cs` 补一条断言，验证程序集不再导出 `Microsoft.Extensions.Hosting.Net` 下的任何类型（对应 spec 的「核心类型命名空间」场景）：

  ```csharp
  [Fact]
  public void Assembly_ExposesNoTypesUnderLegacyHostingNetNamespace()
  {
      var assembly = typeof(ChannelServer).Assembly;

      Assert.Empty(assembly.GetTypes().Where(t => t.Namespace == "Microsoft.Extensions.Hosting.Net"));
  }
  ```

  验证：`dotnet test tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj --filter "FullyQualifiedName~NamespaceConventionTests"` 全部通过

- [x] 2.14 运行全量测试：`dotnet test tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj`。预期：全部测试通过，无失败

- [x] 2.15 构建样例项目：`dotnet build samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`。预期：构建成功（其 `using` 需为 `Galosys.Foundation.Channel`）

- [x] 2.16 提交：`git add -A && git commit -m "refactor!: move public types to Galosys.Foundation.Channel namespace"`。预期：提交成功

## 2A. 最终目录结构（已完成）

> **说明**：第 2 组任务的中间态把核心类型收拢到 `src/Galosys.Foundation.Channel/Channel/`。为让物理目录与命名空间 `Galosys.Foundation.Channel` 完全一一对应（`foundation-namespace-conventions` spec 的「核心类型文件目录」场景要求），核心类型最终再下沉一层到 `src/Galosys.Foundation.Channel/Galosys/Foundation/Channel/`。
> `Microsoft.Extensions.*` 命名空间对应的文件保持不变，仍位于项目根的 `src/Galosys.Foundation.Channel/Microsoft/Extensions/`（与 `Galosys/` 平级）。
> 第 2 组的 Move 步骤为历史记录，已实际执行并保留；本组描述其后的最终一次嵌套移动。

**Files:**
- Move: `src/Galosys.Foundation.Channel/Channel/` → `src/Galosys.Foundation.Channel/Galosys/Foundation/Channel/`
- 最终核心类型目录: `src/Galosys.Foundation.Channel/Galosys/Foundation/Channel/`
  - `ChannelContext.cs`、`ChannelServer.cs`、`ChannelServerCore.cs`、`ChannelServerDefinition.cs`、`ChannelServerGroup.cs`、`IChannelServer.cs`
  - `Client/`、`Codecs/`、`Framing/`、`Pipeline/`、`Protocols/`、`Session/`、`Transports/`
- 最终宿主适配器目录: `src/Galosys.Foundation.Channel/Microsoft/Extensions/Hosting/ChannelServerGroupHostedService.cs`（不变）
- 最终 DI 扩展目录: `src/Galosys.Foundation.Channel/Microsoft/Extensions/DependencyInjection/ChannelServerGroupServiceCollectionExtensions.cs`（不变）
- 最终 Options 目录: `src/Galosys.Foundation.Channel/Microsoft/Extensions/Options/`（不变，10 个文件）
- Modify: `openspec/specs/foundation-namespace-conventions/spec.md`（「核心类型文件目录」场景）
- Modify: `openspec/changes/channel-rename/specs/foundation-namespace-conventions/spec.md`（「核心类型文件目录」场景）
- Modify: `openspec/changes/channel-rename/design.md`（决策 6 目标物理布局与命名空间布局表）
- Modify: `openspec/changes/channel-rename/proposal.md`（物理目录按新命名空间重排）
- Modify: 在途 change 的核心类型路径引用（`primitives-tls-transport-1/2`、`primitives-namedpipe`、`primitives-diagnostics`、`primitives-performance-baseline`）
- Modify: `src/Galosys.Foundation.Channel/README.md`、`AGENTS.md`

- [x] 2A.1 用 `git mv` 把 `Channel/` 整棵子树移动到 `Galosys/Foundation/Channel/`：先 `New-Item -ItemType Directory -Force src/Galosys.Foundation.Channel/Galosys/Foundation`，再 `git mv src/Galosys.Foundation.Channel/Channel src/Galosys.Foundation.Channel/Galosys/Foundation/Channel`。验证：`Test-Path src/Galosys.Foundation.Channel/Galosys/Foundation/Channel/ChannelServer.cs` 为 True 且 `Test-Path src/Galosys.Foundation.Channel/Channel` 为 False

- [x] 2A.2 确认 `Microsoft/Extensions/` 未受影响：`Test-Path src/Galosys.Foundation.Channel/Microsoft/Extensions/Options/ChannelServerOptions.cs` 为 True，`Test-Path src/Galosys.Foundation.Channel/Microsoft/Extensions/DependencyInjection/ChannelServerGroupServiceCollectionExtensions.cs` 为 True，`Test-Path src/Galosys.Foundation.Channel/Microsoft/Extensions/Hosting/ChannelServerGroupHostedService.cs` 为 True

- [x] 2A.3 构建库确认移动未破坏编译：`dotnet build src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj`。预期：0 error

- [x] 2A.4 更新活 spec 与 delta spec 的「核心类型文件目录」场景：目录由 `Channel/` 改为 `Galosys/Foundation/Channel/`。验证：`Select-String -Path openspec/specs/foundation-namespace-conventions/spec.md,openspec/changes/channel-rename/specs/foundation-namespace-conventions/spec.md -Pattern 'Galosys/Foundation/Channel/'` 各命中 1 处

- [x] 2A.5 更新 `design.md` 的决策 6 目标物理布局树与命名空间布局表，使其反映 `Galosys/Foundation/Channel/**` 与项目根 `Microsoft/Extensions/**` 的最终布局

- [x] 2A.6 更新在途 change 的核心类型路径引用（`src/Galosys.Foundation.Channel/Channel/` → `src/Galosys.Foundation.Channel/Galosys/Foundation/Channel/`）；`Microsoft/Extensions/Options/`、`Microsoft/Extensions/DependencyInjection/`、`Microsoft/Extensions/Hosting/` 路径保持不变。验证：`Get-ChildItem -Recurse -Include *.md -Path openspec | Where-Object { $_.FullName -notmatch '\\changes\\archive\\' } | Select-String -Pattern 'src/Galosys\.Foundation\.Channel/Channel/'` 输出为空

- [x] 2A.7 更新 `src/Galosys.Foundation.Channel/README.md` 与 `AGENTS.md` 中的目录布局描述（`Framing/`、`Codecs/`、`Session/` 等子目录 → `Galosys/Foundation/Channel/Framing/` 等）。注意 `AGENTS.md` 中 `samples/MafSample.ConsoleApp/Microsoft/Extensions/Hosting/WindowedBatchWorker.cs` 属于另一个项目，不做改动

## 3. 提交 3：文档与 openspec spec 更新

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `src/Galosys.Foundation.Channel/README.md`
- Modify: `samples/NetServer.ConsoleApp/README.md`
- Modify: `Directory.Packages.props`
- Modify: `openspec/specs/repository-layout/spec.md`
- Modify: `openspec/specs/foundation-namespace-conventions/spec.md`
- Modify: `openspec/specs/channel-server-transport/spec.md`
- Modify: `openspec/specs/channel-client/spec.md`
- Modify: `openspec/specs/channel-client-pool/spec.md`
- Modify: `openspec/specs/channel-server-group/spec.md`
- Modify: `openspec/specs/channel-protocol-dispatch/spec.md`
- Modify: `openspec/specs/channel-hosting/spec.md`
- Modify: `openspec/changes/primitives-tls-transport-1/tasks.md`、`proposal.md`、`design.md`
- Modify: `openspec/changes/primitives-tls-transport-2/tasks.md`、`proposal.md`、`design.md`
- Modify: `openspec/changes/primitives-namedpipe/tasks.md`、`proposal.md`、`design.md`
- Modify: `openspec/changes/primitives-diagnostics/tasks.md`、`proposal.md`、`design.md`
- Modify: `openspec/changes/primitives-performance-baseline/tasks.md`、`proposal.md`、`design.md`

- [x] 3.1 更新根 `README.md`：项目一览表中 `Galosys.Foundation.Primitives` 行改为 `Galosys.Foundation.Channel`（路径改为 `src/Galosys.Foundation.Channel/`，说明改为「逻辑通道传输框架：socket 传输 + 分帧编解码 + 客户端/服务端」）；`tests/Galosys.Foundation.Primitives.Tests` 行改为 `tests/Galosys.Foundation.Channel.Tests`；测试命令改为 `dotnet test tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj`；底部链接 `[Galosys.Foundation.Primitives](src/Galosys.Foundation.Primitives/README.md)` 改为 `[Galosys.Foundation.Channel](src/Galosys.Foundation.Channel/README.md)`；`NetServer.ConsoleApp` 行描述中的「Primitives 的服务端示例」改为「Channel 的服务端示例」。验证：`Select-String -Path README.md -Pattern 'Primitives'` 无输出

- [x] 3.2 更新 `AGENTS.md`：测试项目路径改为 `tests/Galosys.Foundation.Channel.Tests`（两处）；「本地类库」段落中 `Galosys.Foundation.Primitives` 改为 `Galosys.Foundation.Channel`，描述改为「逻辑通道传输框架（socket 传输 + 分帧编解码 + 客户端/服务端），核心类型用 `Galosys.Foundation.Channel`，Options 类型用 `Microsoft.Extensions.Options`」。验证：`Select-String -Path AGENTS.md -Pattern 'Primitives'` 无输出

- [x] 3.3 更新库 `README.md`：标题 `# Galosys.Foundation.Primitives` 改为 `# Galosys.Foundation.Channel`；「构建与测试」章节的命令改为新路径；开头的描述改为「逻辑通道传输框架」；补充一句消歧说明「本库的 Channel 指网络通道，与 `System.Threading.Channels.Channel<T>` 的进程内队列无关」。验证：`Select-String -Path src/Galosys.Foundation.Channel/README.md -Pattern 'Primitives'` 无输出，且文件含 `System.Threading.Channels` 消歧句

- [x] 3.4 检查 `samples/NetServer.ConsoleApp/README.md` 是否提及旧库名，若有则更新为 `Galosys.Foundation.Channel` 与新命名空间。验证：`Select-String -Path samples/NetServer.ConsoleApp/README.md -Pattern 'Primitives|Hosting.Net'` 无输出

- [x] 3.5 在 `Directory.Packages.props` 新增 `galosys.foundation.channel` 版本项，与既有 `galosys.foundation.*` 对齐：`<PackageVersion Include="galosys.foundation.channel" Version="$(MainVersion)" />`。验证：`Select-String -Path Directory.Packages.props -Pattern 'galosys.foundation.channel'` 命中 1 处

- [x] 3.6 更新活 spec `openspec/specs/repository-layout/spec.md`：`src/` 清单中的 `Galosys.Foundation.Primitives` 改为 `Galosys.Foundation.Channel`；场景「测试项目引用保持不变」的命令与路径改为 `tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj` 与 `..\..\src\Galosys.Foundation.Channel\...`；场景「迁移示例可单独构建」中的 `Galosys.Foundation.Primitives` 改为 `Galosys.Foundation.Channel`。验证：`Select-String -Path openspec/specs/repository-layout/spec.md -Pattern 'Primitives'` 无输出

- [x] 3.7 更新活 spec `openspec/specs/foundation-namespace-conventions/spec.md`：场景「Primitives 的 Options 命名空间」中的程序集名改为 `Galosys.Foundation.Channel`，断言中不存在的位置改为 `Galosys.Foundation.Channel.ChannelServerOptions` 与 `Galosys.Foundation.Channel.ChannelKeepAliveOptions`；在「物理目录与命名空间一致」中补充核心类型位于 `Galosys/Foundation/Channel/` 目录的场景（最终目录结构见 2A 组）。验证：`Select-String -Path openspec/specs/foundation-namespace-conventions/spec.md -Pattern 'Primitives'` 仅命中场景标题 1 处

- [x] 3.8 更新活 spec `openspec/specs/channel-server-transport/spec.md`：场景「程序集不再暴露 ChannelServerBase」中的程序集名改为 `Galosys.Foundation.Channel`、类型全限定名改为 `Galosys.Foundation.Channel.ChannelServerBase`；需求「核心依赖边界」及其两个场景中的 `Galosys.Foundation.Primitives` 改为 `Galosys.Foundation.Channel`。验证：`Select-String -Path openspec/specs/channel-server-transport/spec.md -Pattern 'Primitives'` 无输出

- [x] 3.9 更新活 spec `openspec/specs/channel-client/spec.md`：`Microsoft.Extensions.Hosting.Net` 改为 `Galosys.Foundation.Channel`。验证：`Select-String -Path openspec/specs/channel-client/spec.md -Pattern 'Hosting.Net'` 无输出

- [x] 3.10 更新活 spec `openspec/specs/channel-client-pool/spec.md`：`Microsoft.Extensions.Hosting.Net` 改为 `Galosys.Foundation.Channel`。验证：`Select-String -Path openspec/specs/channel-client-pool/spec.md -Pattern 'Hosting.Net'` 无输出

- [x] 3.11 更新活 spec `openspec/specs/channel-server-group/spec.md`：需求「组构造与服务器集合」中的 `Microsoft.Extensions.Hosting.Net` 改为 `Galosys.Foundation.Channel`；需求「宿主集成」补充 `ChannelServerGroupHostedService` 位于 `Microsoft.Extensions.Hosting`。验证：`Select-String -Path openspec/specs/channel-server-group/spec.md -Pattern 'Hosting.Net'` 无输出

- [x] 3.12 更新活 spec `openspec/specs/channel-protocol-dispatch/spec.md`：`Microsoft.Extensions.Hosting.Net` 改为 `Galosys.Foundation.Channel`。验证：`Select-String -Path openspec/specs/channel-protocol-dispatch/spec.md -Pattern 'Hosting.Net'` 无输出

- [x] 3.13 更新活 spec `openspec/specs/channel-hosting/spec.md`：需求「多服务器注册」中的 `Microsoft.Extensions.Hosting.Net` 改为 `Galosys.Foundation.Channel`，并补充 `ChannelServerGroupHostedService` 位于 `Microsoft.Extensions.Hosting`。验证：`Select-String -Path openspec/specs/channel-hosting/spec.md -Pattern 'Hosting.Net'` 无输出

- [x] 3.14 更新在途 change 的路径引用（**保留 change 目录名与 ID 不变**）：在 `openspec/changes/primitives-tls-transport-1/`、`primitives-tls-transport-2/`、`primitives-namedpipe/`、`primitives-diagnostics/`、`primitives-performance-baseline/` 各自的 `tasks.md`、`proposal.md`、`design.md` 中，把 `src/Galosys.Foundation.Primitives/` 改为 `src/Galosys.Foundation.Channel/`，`tests/Galosys.Foundation.Primitives.Tests/` 改为 `tests/Galosys.Foundation.Channel.Tests/`，`benchmarks/Galosys.Foundation.Primitives.Benchmarks` 改为 `benchmarks/Galosys.Foundation.Channel.Benchmarks`，`namespace Galosys.Foundation.Primitives.Tests` 改为 `namespace Galosys.Foundation.Channel.Tests`，`Galosys.Foundation.Primitives.Benchmarks` 改为 `Galosys.Foundation.Channel.Benchmarks`。验证：`Select-String -Path openspec/changes/primitives-*/**/*.md -Pattern 'Galosys.Foundation.Primitives'` 无输出（`primitives-*` 为 change 目录名本身，不在匹配范围内）

- [x] 3.15 校验 openspec 变更合法性：`openspec validate "channel-rename" --strict --no-interactive`。预期：输出 `Change 'channel-rename' is valid`，退出码 0

- [x] 3.16 提交：`git add -A && git commit -m "docs: update docs and openspec specs for Channel rename"`。预期：提交成功

## 4. 最终验证

- [x] 4.1 确认 `openspec/changes/archive/**` 未被改动：`git diff --name-only main...HEAD -- openspec/changes/archive` 输出为空
- [x] 4.2 确认仓库中不再有旧名称残留（排除 `openspec/changes/archive/` 与在途 change 目录名本身）：`Get-ChildItem -Recurse -Include *.cs,*.csproj,*.md,*.slnx,*.props -Path src,tests,samples,openspec | Where-Object { $_.FullName -notmatch '\\changes\\archive\\' } | Select-String -Pattern 'Galosys\.Foundation\.Primitives'` 输出为空
- [x] 4.3 全量构建与测试：`dotnet build src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj`、`dotnet test tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj`、`dotnet build samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`。预期：三者全部成功，测试全部通过
- [x] 4.4 确认解决方案清单有效：`dotnet sln Galosys.Foundation.Next.slnx list` 成功退出，输出含 `Galosys.Foundation.Channel.csproj` 与 `Galosys.Foundation.Channel.Tests.csproj`

## Workflow follow-up

- 在 `openspec/changes/channel-rename/` 下执行归档（`/opsx-archive-x`），使 8 个活 spec 的 delta 合并进 `openspec/specs/`
- 归档后复核 `openspec/specs/` 下不再含 `Galosys.Foundation.Primitives` 与 `Microsoft.Extensions.Hosting.Net`（`Options` 命名空间除外）
