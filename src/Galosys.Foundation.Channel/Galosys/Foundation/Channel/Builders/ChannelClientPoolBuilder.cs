using System;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel
{
    /// <summary>连接池构建器：客户端构建器切换到池模式后的配置入口。</summary>
    /// <remarks>
    /// 池配置继承自进入池模式时的客户端配置；<see cref="Build"/> 产出
    /// <see cref="ChannelClientPool"/>，其租借、归还与槽位语义与既有实现一致。
    /// </remarks>
    public sealed class ChannelClientPoolBuilder
    {
        private readonly ChannelClientOptions _clientOptions;
        private readonly ChannelClientPoolOptions _options;
        private IChannelClientFactory? _factory;

        internal ChannelClientPoolBuilder(ChannelClientOptions clientOptions)
        {
            _clientOptions = clientOptions.Clone();
            _options = new ChannelClientPoolOptions
            {
                ClientOptions = _clientOptions,
            };
        }

        /// <summary>设置连接数上限；必须大于 0。</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSize"/> 小于等于 0。</exception>
        public ChannelClientPoolBuilder WithMaxSize(int maxSize)
        {
            if (maxSize <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(maxSize),
                    maxSize,
                    "连接数上限必须大于 0。");

            _options.MaxSize = maxSize;
            return this;
        }

        /// <summary>设置租借等待超时；必须大于 0 且不超过 1 天。</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="rentTimeout"/> 非正或超过 1 天。</exception>
        public ChannelClientPoolBuilder WithRentTimeout(TimeSpan rentTimeout)
        {
            if (rentTimeout <= TimeSpan.Zero || rentTimeout > TimeSpan.FromDays(1))
                throw new ArgumentOutOfRangeException(
                    nameof(rentTimeout),
                    rentTimeout,
                    "租借等待超时必须大于 0 且不超过 1 天。");

            _options.RentTimeout = rentTimeout;
            return this;
        }

        /// <summary>指定客户端工厂；null 表示使用默认工厂。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="factory"/> 为 null。</exception>
        public ChannelClientPoolBuilder WithFactory(IChannelClientFactory factory)
        {
            ArgumentNullException.ThrowIfNull(factory);

            _factory = factory;
            return this;
        }

        /// <summary>构建连接池实例。</summary>
        /// <exception cref="ArgumentException">配置非法。</exception>
        public ChannelClientPool Build()
        {
            var options = _options.Clone();
            options.Validate();

            return new ChannelClientPool(options, _factory);
        }
    }
}
