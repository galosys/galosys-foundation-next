using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>从连接创建消息会话的组合入口。</summary>
    public static class ChannelContextSessionExtensions
    {
        /// <summary>用指定 framer 和 codec 创建会话；每个连接只能创建一次。</summary>
        /// <exception cref="ArgumentNullException">context、framer 或 codec 为 null。</exception>
        /// <exception cref="InvalidOperationException">连接上已经创建过会话。</exception>
        public static IChannelSession<T> AsSession<T>(
            this ChannelContext context,
            IMessageFramer framer,
            IMessageCodec<T> codec,
            MessageChannelOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(framer);
            ArgumentNullException.ThrowIfNull(codec);

            return new ChannelSession<T>(
                context,
                framer,
                codec,
                options ?? new MessageChannelOptions());
        }

        /// <summary>创建“4 字节大端长度前缀 + JSON”会话。</summary>
        public static IChannelSession<T> AsJsonSession<T>(
            this ChannelContext context,
            LengthFieldFramerOptions? framer = null,
            JsonSerializerOptions? json = null,
            MessageChannelOptions? options = null)
            => context.AsSession(
                new LengthPrefixedFramer(framer),
                new JsonMessageCodec<T>(json),
                options);

        /// <summary>创建“换行分隔 + 严格 UTF-8 文本”会话。</summary>
        public static IChannelSession<string> AsLineTextSession(
            this ChannelContext context,
            LineFramerOptions? framer = null,
            MessageChannelOptions? options = null)
            => context.AsSession(
                new LineFramer(framer),
                new Utf8StringCodec(),
                options);
    }
}
