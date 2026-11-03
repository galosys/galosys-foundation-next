# Spec Delta

## MODIFIED Requirements

### Requirement: 唯一公开服务器实现

The implementation SHALL expose sealed `ChannelServer` as the only public server implementation. `ChannelServerBase` MUST be removed from the assembly's public API, and `ChannelServerCore` MUST remain internal. All lifecycle requirements in `channel-server-transport` MUST apply to `ChannelServer`.

#### Scenario: 程序集不再暴露 ChannelServerBase

- **WHEN** 通过反射检查 `Galosys.Foundation.Channel` 程序集中的 `Galosys.Foundation.Channel.ChannelServerBase`
- **THEN** 该类型不存在
- **AND** `ChannelServer` 是 public sealed 类型并实现 `IChannelServer`
- **AND** `ChannelServerCore` 不是 public 类型

#### Scenario: 组合式服务器继续满足生命周期

- **WHEN** 使用 `ChannelServer` 绑定 loopback 端点并启动
- **THEN** 绑定成功后才返回，`LocalEndPoint` 非 null
- **AND** 停止后不再接受新连接
- **AND** 重复 `StopAsync` / `DisposeAsync` 不抛异常

### Requirement: 核心依赖边界

The implementation SHALL satisfy this requirement. `Galosys.Foundation.Channel` 核心程序集不得引用任何 `Microsoft.AspNetCore.*` 程序集，也不得通过 `FrameworkReference` 引入 `Microsoft.AspNetCore.App`。传输实现必须基于 .NET 基础库（`System.Net.Sockets`、`System.IO.Pipelines` 等），使非 Web 宿主可以无 ASP.NET Core 共享框架引用本库。

#### Scenario: 核心程序集无 ASP.NET 引用

- **WHEN** 检查 `Galosys.Foundation.Channel` 的 csproj、还原资产与程序集引用
- **THEN** 不存在 `Microsoft.AspNetCore.*` 条目
- **AND** 自动化测试在出现此类引用时失败

#### Scenario: 非 Web 宿主引用

- **WHEN** 一个普通控制台宿主引用 `Galosys.Foundation.Channel`
- **THEN** 宿主无需安装 ASP.NET Core 共享框架即可构建和运行
