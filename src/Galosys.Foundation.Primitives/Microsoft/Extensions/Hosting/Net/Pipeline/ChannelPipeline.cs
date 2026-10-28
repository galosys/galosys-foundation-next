using System;
using System.Collections.Generic;

namespace Microsoft.Extensions.Hosting.Net
{
    internal sealed class ChannelPipeline : IChannelPipeline
    {
        private readonly List<ChannelMiddleware> _middlewares = new();
        private ChannelHandlerDelegate? _handler;
        private ChannelHandlerDelegate? _built;

        public IChannelPipeline Use(ChannelMiddleware middleware)
        {
            ArgumentNullException.ThrowIfNull(middleware);

            if (_built is not null)
                throw new InvalidOperationException("管线已构建，不能再添加中间件。");

            _middlewares.Add(middleware);
            return this;
        }

        public void Run(ChannelHandlerDelegate handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            if (_handler is not null)
                throw new InvalidOperationException("Run 只能调用一次。");

            _handler = handler;
        }

        internal ChannelHandlerDelegate Build()
        {
            if (_built is not null)
                return _built;

            if (_handler is null)
                throw new InvalidOperationException("管线缺少 Run 终端处理器。");

            var next = _handler;
            for (var i = _middlewares.Count - 1; i >= 0; i--)
            {
                var middleware = _middlewares[i];
                var captured = next;
                next = context => middleware(context, captured);
            }

            _built = next;
            return _built;
        }
    }
}
