namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>客户端连接状态。</summary>
    public enum ChannelConnectionState
    {
        /// <summary>未连接。</summary>
        Disconnected = 0,

        /// <summary>正在建立连接。</summary>
        Connecting = 1,

        /// <summary>已连接且可读写字节。</summary>
        Connected = 2,

        /// <summary>正在重连；P4-2 使用。</summary>
        Reconnecting = 3,

        /// <summary>终态，不可再连接。</summary>
        Closed = 4,
    }
}
