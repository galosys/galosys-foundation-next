using Galosys.Foundation.Channel;
using Microsoft.Extensions.Logging;

namespace NetServer.ConsoleApp
{
    /// <summary>
    /// 在一个连接上收发消息的辅助类。
    /// </summary>
    /// <remarks>
    /// 重要：一个连接在其整个生命周期内只能创建一个会话，且会话释放后槽位依然被占用，
    /// 因此同一个连接上不能反复创建会话。本类在构造时创建一次会话并复用，
    /// 连接断开（重连产生新的 <see cref="ChannelContext"/>）时需要换一个新的实例。
    /// </remarks>
    public sealed class ChannelSender
    {
        private readonly IChannelSession<string> _session;
        private readonly ILogger<ChannelSender> _logger;

        /// <summary>为指定连接创建发送器，并在该连接上创建行文本会话。</summary>
        public ChannelSender(ChannelContext connection, ILogger<ChannelSender> logger)
        {
            _logger = logger;
            _session = connection.AsLineTextSession();
        }

        /// <summary>
        /// 发送一条消息并读回一条响应。
        /// </summary>
        /// <exception cref="EndOfStreamException">对端在响应到达前关闭了连接。</exception>
        public async Task<string> SendAndReceiveAsync(string request, CancellationToken ct)
        {
            await _session.WriteAsync(request, ct);
            _logger.LogInformation("发送: {Request}", request);

            var response = await _session.ReadAsync(ct);
            _logger.LogInformation("收到: {Response}", response);

            return response;
        }

        /// <summary>
        /// 批量发送多条消息并按到达顺序读回同样数量的响应。
        /// </summary>
        /// <remarks>
        /// 批量写不保证原子送达，也不做请求-响应配对：服务端逐条回包，
        /// 这里只是按到达顺序把响应读出来打印。
        /// </remarks>
        public async Task SendBatchAndReceiveAsync(IReadOnlyList<string> requests, CancellationToken ct)
        {
            await _session.WriteBatchAsync(requests, ct);
            _logger.LogInformation("批量发送 {Count} 条: {Requests}", requests.Count, string.Join(", ", requests));

            for (var i = 0; i < requests.Count; i++)
            {
                var response = await _session.ReadAsync(ct);
                _logger.LogInformation("收到: {Response}", response);
            }
        }
    }
}
