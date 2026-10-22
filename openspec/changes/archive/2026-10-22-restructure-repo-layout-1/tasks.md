# restructure-repo-layout-1 实施计划

> 由 `writing-plans` 约定生成：每个任务粒度 2-5 分钟，包含精确路径、命令与预期结果。

## 1. 迁移前检查

- [x] 1.1 记录工作区状态，确认 `MafSample.API` 的未提交文件清单（已完成：5 个修改文件已核对）
  - Run: `git status --short`
  - Expected: 仅 `src/MafSample.API/Application/Bootstrapper.cs`、`Domain/Order.cs`、`Domain/Sample.cs`、`InternalModule.cs`、`MafSample.API.csproj` 为 ` M`；无暂存内容。

- [x] 1.2 创建根目录 `samples/`（已完成）
  - Run: `New-Item -ItemType Directory -Path samples -Force`
  - Expected: `samples/` 存在且为空。

## 2. 移动 8 个示例项目

- [x] 2.1 移动 `AopSample.ConsoleApp`（已完成）
  - Run: `git mv src/AopSample.ConsoleApp samples/AopSample.ConsoleApp`

- [x] 2.2 移动 `DataIngestionSample.ConsoleApp`（已完成）
  - Run: `git mv src/DataIngestionSample.ConsoleApp samples/DataIngestionSample.ConsoleApp`

- [x] 2.3 移动 `MafSample.Adapter`（已完成）
  - Run: `git mv src/MafSample.Adapter samples/MafSample.Adapter`

- [x] 2.4 移动 `MafSample.API`（保留未提交修改；已完成，5 个文件内容 SHA-256 前后一致，diffstat 不变）
  - Run: `git mv src/MafSample.API samples/MafSample.API`
  - Verify: `git status --short` 中 5 个 `MafSample.API` 文件仍为 ` M`，路径变为 `samples/MafSample.API/...`。

- [x] 2.5 移动 `MafSample.ConsoleApp`（已完成）
  - Run: `git mv src/MafSample.ConsoleApp samples/MafSample.ConsoleApp`

- [x] 2.6 移动 `MafSample.WpfApp`（已完成）
  - Run: `git mv src/MafSample.WpfApp samples/MafSample.WpfApp`

- [x] 2.7 移动 `NatsSample.ConsoleApp`（已完成）
  - Run: `git mv src/NatsSample.ConsoleApp samples/NatsSample.ConsoleApp`

- [x] 2.8 移动 `NetServer.ConsoleApp`（已完成）
  - Run: `git mv src/NetServer.ConsoleApp samples/NetServer.ConsoleApp`

- [x] 2.9 校验目录边界（已完成：`src` 3 个类库、`samples` 8 个示例）
  - Run: `Get-ChildItem src -Directory | Select-Object -ExpandProperty Name`
  - Expected: `Galosys.Foundation.Doris`、`Galosys.Foundation.Nats.Net`、`Galosys.Foundation.Primitives`（允许空的 `.vs`/`obj` 目录，稍后清理）。
  - Run: `(Get-ChildItem samples -Directory).Count`
  - Expected: `8`。

## 3. 修正项目引用

- [x] 3.1 修改 `samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`（已完成：改为 `..\..\src\...`）
  - 将：
    ```xml
    <ProjectReference Include="..\Galosys.Foundation.Primitives\Galosys.Foundation.Primitives.csproj" />
    ```
    改为：
    ```xml
    <ProjectReference Include="..\..\src\Galosys.Foundation.Primitives\Galosys.Foundation.Primitives.csproj" />
    ```

- [x] 3.2 修改 `samples/NatsSample.ConsoleApp/NatsSample.ConsoleApp.csproj`（已完成：改为 `..\..\src\...`）
  - 将：
    ```xml
    <ProjectReference Include="..\Galosys.Foundation.Nats.Net\Galosys.Foundation.Nats.Net.csproj" />
    ```
    改为：
    ```xml
    <ProjectReference Include="..\..\src\Galosys.Foundation.Nats.Net\Galosys.Foundation.Nats.Net.csproj" />
    ```

- [x] 3.3 确认没有其他跨目录引用（已完成：仅 2 个引用，均以 `..\..\src\` 开头）
  - Run: `rg -n "ProjectReference" samples -g "*.csproj"`
  - Expected: 仅上述两个引用，且均以 `..\..\src\` 开头。

## 4. 更新解决方案

- [x] 4.1 重写 `Galosys.Foundation.Next.slnx` 的项目路径与文件夹（已完成：项目 Id 保持不变，示例挂 `/samples/`，foundation 挂 `/src/`）
  - 目标内容：
    ```xml
    <Solution>
      <Folder Name="/samples/">
        <Project Path="samples/AopSample.ConsoleApp/AopSample.ConsoleApp.csproj" Id="14ad0c7c-d903-4820-bb5d-abf52e96eb5b" />
        <Project Path="samples/DataIngestionSample.ConsoleApp/DataIngestionSample.ConsoleApp.csproj" Id="11be50f7-8be7-470e-9553-fe013b7a92b4" />
        <Project Path="samples/MafSample.Adapter/MafSample.Adapter.csproj" Id="f8dcffda-b587-4e83-a982-95141db79365" />
        <Project Path="samples/MafSample.API/MafSample.API.csproj" Id="39a5756d-e643-4a69-bce1-31822b218ad5" />
        <Project Path="samples/MafSample.ConsoleApp/MafSample.ConsoleApp.csproj" Id="447c17a1-bc05-4f5f-966e-6c3e83eb8f17" />
        <Project Path="samples/MafSample.WpfApp/MafSample.WpfApp.csproj" Id="421047a0-d728-4095-826e-d1bae07e9bfc" />
        <Project Path="samples/NatsSample.ConsoleApp/NatsSample.ConsoleApp.csproj" Id="459ae57f-f4e4-4596-9a9b-01a01108a981" />
        <Project Path="samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj" Id="e02e4150-edde-41da-b52a-4627455b8d24" />
      </Folder>
      <Folder Name="/src/">
        <Project Path="src/Galosys.Foundation.Doris/Galosys.Foundation.Doris.csproj" Id="f079b074-cc52-469b-bb37-b68ea622fce1" />
        <Project Path="src/Galosys.Foundation.Nats.Net/Galosys.Foundation.Nats.Net.csproj" Id="55fed2f9-5bc7-4972-9597-b28d201c5f31" />
        <Project Path="src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj" Id="8341af14-392f-441c-bf48-bfad24c7b15e" />
      </Folder>
      <Folder Name="/tests/">
        <Project Path="tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj" />
      </Folder>
    </Solution>
    ```

- [x] 4.2 校验解决方案路径（已完成：`dotnet sln list` 列出 12 个项目）
  - Run: `dotnet sln Galosys.Foundation.Next.slnx list`
  - Expected: 成功退出，列出 8 个 `samples/` 项目、3 个 `src/` 项目、1 个 `tests/` 项目。

## 5. 更新当前文档

- [x] 5.1 更新根 `README.md`（已完成：目录说明、项目位置、构建/运行命令与链接已同步）
  - 将开头“`src/` 下的每个项目都是…示例”改为“`samples/` 下的每个项目都是…示例”。
  - 项目表中为每个项目补充位置：示例为 `samples/<Project>/`，类库为 `src/<Project>/`。
  - 构建命令改为：
    ```powershell
    dotnet build samples/<Project>/<Project>.csproj
    dotnet build src/<Project>/<Project>.csproj
    ```
  - 运行命令改为：
    ```powershell
    dotnet run --project samples/<Project>/<Project>.csproj
    ```
  - 目录结构表增加 `samples/`（示例项目），`src/` 改为“foundation 本地类库”。
  - “相关文档”中的 `NetServer.ConsoleApp` 链接改为 `samples/NetServer.ConsoleApp/README.md`。

- [x] 5.2 更新 `AGENTS.md`（已完成：目录约定、构建/运行命令与示例路径已同步）
  - 开头 `src/*` 改为 `samples/*`。
  - 构建命令改为 `dotnet build samples/<Project>/<Project>.csproj`，并补充 foundation 类库使用 `src/<Project>/<Project>.csproj`。
  - 运行命令改为 `dotnet run --project samples/<Project>/<Project>.csproj`。
  - 架构事实中的 `MafSample.ConsoleApp/...`、`MafSample.WpfApp`、`MafSample.API` 路径加上 `samples/` 前缀。
  - 增加一行目录约定：`src/` 只放 foundation 类库，`samples/` 放示例，`tests/` 放测试。

- [x] 5.3 更新 `samples/NetServer.ConsoleApp/README.md`（已完成：运行路径改为 `samples/`）
  - 运行命令改为：
    ```powershell
    dotnet run --project samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj
    ```

- [x] 5.4 更新 `openspec/specs/channel-hosting/spec.md` 中的示例运行路径（已完成：改为 `samples/NetServer.ConsoleApp/...`）
  - 将 `dotnet run --project src/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj` 改为 `dotnet run --project samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`。

- [x] 5.5 检查是否残留旧示例路径（已完成：代码、`.slnx`、AGENTS、当前规格与两个 README 均无旧示例路径）
  - Run: `rg -n "src/(AopSample|DataIngestionSample|MafSample|NatsSample|NetServer)" --glob "!openspec/changes/archive/**" --glob "!docs/superpowers/**" --glob "!**/obj/**" --glob "!**/bin/**" .`
  - Expected: 无输出。

## 6. 清理、构建与测试

- [x] 6.1 删除被移动示例的构建缓存（已完成：已清理 `samples/*/bin|obj` 与 `src/.vs|obj`）
  - Run: `Get-ChildItem samples -Directory | ForEach-Object { Remove-Item -Recurse -Force "$($_.FullName)\bin", "$($_.FullName)\obj" -ErrorAction SilentlyContinue }`
  - Run: `Remove-Item -Recurse -Force src/.vs, src/obj -ErrorAction SilentlyContinue`
  - Expected: 迁移项目无 `bin/`、`obj/`。

- [x] 6.2 构建 foundation 类库（已完成：Primitives 0 警告/0 错误；Doris 0 错误，保留既有 CS8604 警告）
  - Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
  - Run: `dotnet build src/Galosys.Foundation.Doris/Galosys.Foundation.Doris.csproj`
  - Expected: 两个项目均 Build succeeded。

- [x] 6.3 运行 Primitives 测试（已完成：67/67 通过）
  - Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
  - Expected: 全部测试通过。

- [x] 6.4 构建控制台示例（已完成：Aop、DataIngestion、MafSample.ConsoleApp 均成功）
  - Run: `dotnet build samples/AopSample.ConsoleApp/AopSample.ConsoleApp.csproj`
  - Run: `dotnet build samples/DataIngestionSample.ConsoleApp/DataIngestionSample.ConsoleApp.csproj`
  - Run: `dotnet build samples/MafSample.ConsoleApp/MafSample.ConsoleApp.csproj`
  - Expected: 均 Build succeeded。

- [x] 6.5 构建 Web 示例（已完成：Adapter 0 警告；API 成功，29 个既有警告，与迁移无关）
  - Run: `dotnet build samples/MafSample.Adapter/MafSample.Adapter.csproj`
  - Run: `dotnet build samples/MafSample.API/MafSample.API.csproj`
  - Expected: 均 Build succeeded；若 `MafSample.API` 因用户未提交修改失败，记录原始错误并确认与路径迁移无关。

- [x] 6.6 构建 WPF 与 NetServer 示例（已完成：WPF 0 警告；NetServer 成功并解析 Primitives）
  - Run: `dotnet build samples/MafSample.WpfApp/MafSample.WpfApp.csproj`
  - Run: `dotnet build samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
  - Expected: 均 Build succeeded，且 NetServer 能解析 `src/Galosys.Foundation.Primitives`。

- [x] 6.7 确认 NATS 仍按已知限制排除（已完成：失败来源为 `cts` 未定义与 `IMessage` 参数类型，属于原型既有问题，不是项目引用路径）
  - Run: `dotnet build samples/NatsSample.ConsoleApp/NatsSample.ConsoleApp.csproj`
  - Expected: 失败，错误来自 `Galosys.Foundation.Nats.Net` 原型缺失类型/命名空间，而非项目引用路径；在验证记录中注明“已知不可编译”。

- [x] 6.8 核对用户修改与最终状态（已完成：5 个 API 文件为 `RM`，未提交修改内容与 diffstat 均保留）
  - Run: `git status --short`
  - Expected: 5 个 `samples/MafSample.API/...` 文件仍为 ` M`；其余为 `R`/` M` 的迁移与文档改动。
