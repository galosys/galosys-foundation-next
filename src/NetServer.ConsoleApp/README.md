# NetServer.ConsoleApp

`Galosys.Foundation.Primitives` 的服务端示例：监听 `0.0.0.0:8000`，打印收到的 UTF-8 文本，并每秒发送 `hello` 到本机端口 8000。

## 运行

```powershell
dotnet run --project src/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj
```

预期输出（每秒一条）：

```text
>>>>> hello <<<<<
```

按 Ctrl+C 触发宿主优雅停机。

## 结构

| 文件 | 作用 |
|---|---|
| `Program.cs` | 注册 `AddChannelServer<SampleChannelServer>` 与 `SenderBootstrapper` |
| `SampleChannelServer.cs` | 服务端处理器：读取并打印字节 |
| `SenderBootstrapper.cs` | 后台客户端：每秒发送 `hello`（P4 才迁移到框架客户端） |

## 说明

- `SampleChannelServer` 构造函数接收 `ChannelServerOptions`，由 DI 提供。
- 宿主 `ShutdownTimeout` 默认 30 秒，服务器 `ShutdownTimeout` 默认 10 秒。
- 端口 8000 被占用时，`host.Run()` 在启动阶段失败并输出异常。
