using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel
{
    /// <summary>服务器构建器：配置、中间件与终端处理器的唯一公开装配入口。</summary>
    /// <remarks>
    /// 构建器持有配置的副本，<see cref="Build"/> 产出的服务器不随调用方后续修改而改变；
    /// 同一个构建器可以多次构建，每次得到互相独立的实例。
    /// </remarks>
    public sealed class ChannelServerBuilder
    {
        private readonly List<ChannelMiddleware> _middlewares = new();
        private ChannelHandlerDelegate? _handler;
        private ChannelServerOptions _options;
        private ILoggerFactory? _loggerFactory;
        private IServiceScopeFactory? _scopeFactory;
        private ChannelTlsOptions? _tlsOptions;

        private ChannelServerBuilder(ChannelServerOptions options)
        {
            _options = options;
        }

        /// <summary>用已构造的配置创建构建器；配置会被克隆并校验。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
        /// <exception cref="ArgumentException">配置非法。</exception>
        public static ChannelServerBuilder Create(ChannelServerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var clone = options.Clone();
            clone.Validate();

            return new ChannelServerBuilder(clone);
        }

        /// <summary>用配置回调创建构建器。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> 为 null。</exception>
        /// <exception cref="ArgumentException">回调产出的配置非法。</exception>
        public static ChannelServerBuilder Create(Action<ChannelServerOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            var options = new ChannelServerOptions();
            configure(options);

            return Create(options);
        }

        /// <summary>注册中间件；注册顺序即由外向内的执行顺序。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="middleware"/> 为 null。</exception>
        public ChannelServerBuilder Use(ChannelMiddleware middleware)
        {
            ArgumentNullException.ThrowIfNull(middleware);

            _middlewares.Add(middleware);
            return this;
        }

        /// <summary>设置终端处理器；只能调用一次。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> 为 null。</exception>
        /// <exception cref="InvalidOperationException">已经设置过终端处理器。</exception>
        public ChannelServerBuilder Run(ChannelHandlerDelegate handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            if (_handler is not null)
                throw new InvalidOperationException("终端处理器只能设置一次。");

            _handler = handler;
            return this;
        }

        /// <summary>指定日志工厂；null 表示不记录日志。</summary>
        public ChannelServerBuilder WithLoggerFactory(ILoggerFactory? loggerFactory)
        {
            _loggerFactory = loggerFactory;
            return this;
        }

        /// <summary>指定每连接作用域工厂；null 表示每连接使用共享空作用域。</summary>
        public ChannelServerBuilder WithServiceScopeFactory(IServiceScopeFactory? scopeFactory)
        {
            _scopeFactory = scopeFactory;
            return this;
        }

        /// <summary>启用服务端 TLS；握手在管线最前完成。</summary>
        /// <remarks>
        /// TLS 配置在调用时立即校验：缺少服务端证书或握手超时非法时立刻抛出
        /// <see cref="ArgumentException"/>，不会推迟到构建或启动。
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
        /// <exception cref="ArgumentException">服务端 TLS 配置非法。</exception>
        public ChannelServerBuilder UseTls(ChannelTlsOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            // 与 TlsPipelineExtensions.UseTls 一致：注册时校验，不等到第一次连接。
            options.ValidateServer();

            _tlsOptions = options;
            return this;
        }

        /// <summary>构建服务器实例。</summary>
        /// <remarks>
        /// 中间件列表与终端处理器在构建时快照，之后修改构建器不影响已构建的实例。
        /// 每次启动尝试都会重新物化管线，与既有 <see cref="ChannelServer"/> 语义一致。
        /// </remarks>
        /// <exception cref="InvalidOperationException">没有设置终端处理器。</exception>
        public ChannelServer Build()
        {
            var handler = _handler;

            if (handler is null)
                throw new InvalidOperationException("管线缺少 Run 终端处理器。");

            var middlewares = _middlewares.ToArray();
            var tls = _tlsOptions;

            var configure = new Action<IChannelPipeline>(pipeline =>
            {
                if (tls is not null)
                    pipeline.UseTls(tls);

                foreach (var middleware in middlewares)
                    pipeline.Use(middleware);

                pipeline.Run(handler);
            });

            return new ChannelServer(
                _options.Clone(),
                configure,
                _loggerFactory,
                _scopeFactory);
        }
    }
}
