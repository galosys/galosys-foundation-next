using System;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>客户端状态迁移事件参数。</summary>
    public sealed class ChannelStateChangedEventArgs : EventArgs
    {
        /// <summary>创建状态迁移事件参数。</summary>
        /// <param name="oldState">迁移前状态。</param>
        /// <param name="newState">迁移后状态。</param>
        /// <param name="failure">导致迁移的失败；正常迁移为 null。</param>
        public ChannelStateChangedEventArgs(
            ChannelConnectionState oldState,
            ChannelConnectionState newState,
            ChannelConnectionFailure? failure = null)
        {
            OldState = oldState;
            NewState = newState;
            Failure = failure;
        }

        /// <summary>迁移前状态。</summary>
        public ChannelConnectionState OldState { get; }

        /// <summary>迁移后状态。</summary>
        public ChannelConnectionState NewState { get; }

        /// <summary>导致迁移的失败；正常迁移为 null。</summary>
        public ChannelConnectionFailure? Failure { get; }
    }
}
