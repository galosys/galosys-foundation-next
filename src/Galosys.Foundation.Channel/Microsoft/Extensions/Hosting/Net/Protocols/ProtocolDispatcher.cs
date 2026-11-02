namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>把连接分发给选择器选中的协议。</summary>
    public sealed class ProtocolDispatcher
    {
        private readonly IChannelProtocolSelector _selector;

        /// <summary>创建分发器。</summary>
        /// <param name="selector">协议选择器，不能为 null。</param>
        public ProtocolDispatcher(IChannelProtocolSelector selector)
        {
            ArgumentNullException.ThrowIfNull(selector);
            _selector = selector;
        }

        /// <summary>匹配 <see cref="ChannelHandlerDelegate"/>，可直接传给 pipeline.Run。</summary>
        /// <param name="context">当前连接视图。</param>
        /// <returns>分发完成的异步任务。</returns>
        public async Task HandleAsync(ChannelContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var cancellationToken = context.ConnectionClosed;

            if (cancellationToken.IsCancellationRequested)
                return;

            IChannelProtocol? protocol;

            try
            {
                protocol = await _selector
                    .SelectAsync(context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                context.Abort(error as InvalidDataException
                    ?? new InvalidDataException("协议选择失败。", error));
                return;
            }

            if (protocol is null)
            {
                context.Abort(new InvalidDataException("没有匹配的协议。"));
                return;
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            await protocol.HandleAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }
}
