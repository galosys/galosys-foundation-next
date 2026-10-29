# NetServer.ConsoleApp

`Galosys.Foundation.Primitives` 的服务端示例：监听 `0.0.0.0:8000`，通过
`context.AsLineTextSession()` 按行读取并打印 UTF-8 文本，同时每秒向本机 8000 端口发送 `hello\n`。

## 运行

```powershell
dotnet run --project samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj
```

预期输出（每秒一条）：

```text
>>>>> hello <<<<<
```

按 Ctrl+C 触发宿主优雅停机。

## 多字节 UTF-8 验证

保持示例运行，另开一个 PowerShell 终端：

```powershell
$client = [System.Net.Sockets.TcpClient]::new()
$client.Connect('127.0.0.1', 8000)
$stream = $client.GetStream()
$bytes = [System.Text.Encoding]::UTF8.GetBytes("你好`n")
$stream.Write($bytes, 0, 1)
Start-Sleep -Milliseconds 100
$stream.Write($bytes, 1, $bytes.Length - 1)
$stream.Flush()
Start-Sleep -Milliseconds 500
$client.Close()
```

预期在示例终端看到：

```text
>>>>> 你好 <<<<<
```

同时仍会看到每秒的 `>>>>> hello <<<<<`；不应出现替换字符 `�`。

## 结构

| 文件 | 作用 |
|---|---|
| `Program.cs` | 注册 `ChannelServer` 工厂、`ChannelServerRunner` 与 `SenderBootstrapper` |
| `SampleChannelHandler.cs` | 终端处理器：用 `AsLineTextSession()` 读取并打印行 |
| `ChannelServerRunner.cs` | 把 `ChannelServer` 启停映射到宿主 |
| `SenderBootstrapper.cs` | 后台客户端：每秒发送 `hello\n`（P4 才迁移到框架客户端） |

## 说明

- session 只在 `SampleChannelHandler.HandleAsync` 中创建，不在 pipeline 中间件中创建。
- `LineFramer` 用 `\n` 作为帧边界；发送端必须包含换行，裸 `hello` 不会形成一条消息。
- `Utf8StringCodec` 拒绝空帧，因此示例协议不包含空行。
- `ChannelServerRunner` 必须先于 `SenderBootstrapper` 注册，确保 sender 连接前监听已就绪。
- 端口 8000 被占用时，`host.Run()` 在启动阶段失败并输出异常。
