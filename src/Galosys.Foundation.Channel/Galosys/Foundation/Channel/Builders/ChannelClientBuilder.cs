using System;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel
{
    /// <summary>客户端构建器：单连接客户端的唯一公开装配入口。</summary>
    /// <remarks>
    /// 构建器持有配置的副本，<see cref="Build"/> 产出的客户端不随调用方后续修改而改变。
    /// 需要连接池时调用 <see cref="AsPool"/> 切换构建目标。
    /// </remarks>
    public sealed class ChannelClientBuilder
    {
        private ChannelClientOptions _options;

        private ChannelClientBuilder(ChannelClientOptions options)
        {
            _options = options;
        }

        /// <summary>用已构造的配置创建构建器；配置会被克隆并校验。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
        /// <exception cref="ArgumentException">配置非法。</exception>
        public static ChannelClientBuilder Create(ChannelClientOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var clone = options.Clone();
            clone.Validate();

            return new ChannelClientBuilder(clone);
        }

        /// <summary>用配置回调创建构建器。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> 为 null。</exception>
        /// <exception cref="ArgumentException">回调产出的配置非法。</exception>
        public static ChannelClientBuilder Create(Action<ChannelClientOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            var options = new ChannelClientOptions();
            configure(options);

            return Create(options);
        }

        /// <summary>设置重连策略；null 表示失败后不重试。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="policy"/> 为 null。</exception>
        public ChannelClientBuilder WithReconnect(ChannelReconnectPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);

            _options.Reconnect = policy.Clone();
            return this;
        }

        /// <summary>设置时间源；用于重连退避与超时。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> 为 null。</exception>
        public ChannelClientBuilder WithTimeProvider(TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);

            _options.TimeProvider = timeProvider;
            return this;
        }

        /// <summary>设置 TCP keep-alive 配置。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="keepAlive"/> 为 null。</exception>
        public ChannelClientBuilder WithKeepAlive(ChannelKeepAliveOptions keepAlive)
        {
            ArgumentNullException.ThrowIfNull(keepAlive);

            _options.KeepAlive = keepAlive;
            return this;
        }

        /// <summary>把构建目标切换为连接池；客户端配置被继承到池配置。</summary>
        public ChannelClientPoolBuilder AsPool()
            => new ChannelClientPoolBuilder(_options);

        /// <summary>构建客户端实例。</summary>
        /// <exception cref="ArgumentException">配置非法。</exception>
        public IChannelClient Build()
        {
            var options = _options.Clone();
            options.Validate();

            return new ChannelClient(options);
        }
    }
}
