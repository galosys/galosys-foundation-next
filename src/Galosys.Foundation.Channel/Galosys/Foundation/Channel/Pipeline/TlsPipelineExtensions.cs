using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel
{
    /// <summary>TLS 服务端管线扩展。</summary>
    /// <remarks>
    /// <c>UseTls</c> 只做传输包装：在调用后续中间件之前完成服务端握手，
    /// 握手成功后把 <see cref="ChannelContext.Transport"/> 替换为解密后的管道，
    /// 管线结束后释放 TLS 包装层。底层连接仍由 <c>ChannelServer</c> 释放。
    /// </remarks>
    public static class TlsPipelineExtensions
    {
        /// <summary>在管线开头完成服务端 TLS 握手，并用解密后的传输替换上下文传输。</summary>
        /// <remarks>
        /// 中间件必须尽量靠前注册；在它之前注册的中间件看到的是加密字节。
        /// 同一管线只应调用一次，重复调用会导致二次握手失败。
        /// </remarks>
        /// <param name="pipeline">当前管线。</param>
        /// <param name="options">TLS 配置；只使用服务端一侧。</param>
        /// <exception cref="ArgumentNullException"><paramref name="pipeline"/> 或 <paramref name="options"/> 为 null。</exception>
        /// <exception cref="ArgumentException">服务端 TLS 配置无效。</exception>
        public static IChannelPipeline UseTls(
            this IChannelPipeline pipeline,
            ChannelTlsOptions options)
        {
            ArgumentNullException.ThrowIfNull(pipeline);
            ArgumentNullException.ThrowIfNull(options);

            // 注册时校验：缺少服务端证书的配置要让 StartAsync 构建管线时就失败，
            // 而不是等到第一个连接进来才暴露。ValidateServer 不读取 ClientOptions。
            options.ValidateServer();

            return pipeline.Use((context, next) => InvokeAsync(context, next, options));
        }

        private static async Task InvokeAsync(
            ChannelContext context,
            ChannelHandlerDelegate next,
            ChannelTlsOptions options)
        {
            // 握手在 next 之前完成；握手失败时异常直接冒泡给 ChannelServer 中止连接，
            // 后续中间件与终端 handler 都不会执行。
            var tls = await TlsDuplexPipe.CreateAsync(
                context.Transport,
                options,
                isServer: true,
                context.ConnectionClosed).ConfigureAwait(false);

            context.UseTransport(tls);

            try
            {
                // next 恰好调用一次。
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                // 正常返回、handler 抛异常或中间件短路都走同一释放路径；
                // 释放的只是 TLS 包装层，底层连接由 ChannelServer 负责。
                await tls.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
