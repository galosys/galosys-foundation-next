# NetServer.ConsoleApp

`Galosys.Foundation.Primitives` 的服务端示例：监听 `0.0.0.0:8000`，打印收到的 UTF-8 文本，并每秒发送 `hello` 到本机端口 8000。

## 运行

```powershell
dotnet run --project samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj
```

预期输出（每秒一条）：

```text
>>>>> hello <<<<<
```

按 Ctrl+C 触发宿主优雅停机。

## 结构

| 文件 | 作用 |
|---|---|
| `Program.cs` | 注册 `ChannelServer` 工厂、`ChannelServerRunner` 与 `SenderBootstrapper` |
| `SampleChannelHandler.cs` | 服务端处理器：读取并打印字节 |
| `ChannelServerRunner.cs` | 把 `ChannelServer` 启停映射到宿主 |
| `SenderBootstrapper.cs` | 后台客户端：每秒发送 `hello`（P4 才迁移到框架客户端） |

## 说明

- `ChannelServer` 由工厂创建，不再使用 `AddChannelServer<T>`。
- `ChannelServerRunner` 必须先于 `SenderBootstrapper` 注册，确保 sender 连接前监听已就绪。
- 宿主 `ShutdownTimeout` 默认 30 秒，服务器 `ShutdownTimeout` 默认 10 秒。
- 端口 8000 被占用时，`host.Run()` 在启动阶段失败并输出异常。
