using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Channels;

namespace Microsoft.Extensions.Hosting
{
    /// <summary>
    /// 通用 Channel + Rx 批处理后台服务
    /// 泛型参数：TMessage（消息类型），TKey（分组键类型）
    /// </summary>
    public abstract class WindowedBatchWorker<TMessage, TKey> : BackgroundService
    {
        private readonly Channel<TMessage> _channel;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly CompositeDisposable _cleanup = new();

        // 窗口配置（子类可重写）
        protected virtual TimeSpan IdleTimeout => TimeSpan.FromSeconds(30);  // 空闲超时
        protected virtual int MaxBatchSize => 100;                            // 最大批次大小
        protected virtual int MaxDegreeOfParallelism => 1;                    // 并发批处理数

        protected WindowedBatchWorker(IServiceScopeFactory scopeFactory)
        {
            _channel = Channel.CreateBounded<TMessage>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait });
            _scopeFactory = scopeFactory;
        }

        /// <summary>
        /// 子类实现：从消息中提取分组键
        /// </summary>
        protected abstract TKey GetGroupKey(TMessage message);

        /// <summary>
        /// 子类实现：在 DI 作用域内处理一个批次
        /// </summary>
        protected abstract Task ProcessBatchAsync(
            TKey groupKey,
            IReadOnlyList<TMessage> batch,
            IServiceProvider sp,
            CancellationToken ct);

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Channel 读取流 → Rx Observable
            var stream = _channel.Reader
                .ReadAllAsync(stoppingToken)
                .ToObservable();   // 需要 System.Interactive.Async

            // 核心 Rx 管道
            var pipeline = stream
                .Publish();   // 转为热流

            var subscription = pipeline.Subscribe(onNext: value => Console.WriteLine(value));

            _cleanup.Add(subscription);
            _cleanup.Add(pipeline.Connect());  // 激活热流

            // 优雅关闭：停止令牌触发时释放所有订阅
            stoppingToken.Register(() =>
            {
                _cleanup.Dispose();
                _channel.Writer.TryComplete();
            });

            return Task.CompletedTask;
        }

        /// <summary>
        /// 在独立的 DI 作用域内处理批次
        /// </summary>
        private async Task ProcessBatchInScopeAsync(
            BatchResult<TKey, TMessage> batch,
            CancellationToken ct)
        {
            // 每个批次创建独立作用域，确保 Scoped 服务（如 DbContext）正确释放
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sp = scope.ServiceProvider;

            try
            {
                await ProcessBatchAsync(batch.GroupKey, batch.Messages, sp, ct);
            }
            catch (Exception ex)
            {
                // 根据业务需求决定是否重投或死信
                throw;  // 重新抛出，让 Rx 的 onError 捕获
            }
        }

        public override void Dispose()
        {
            _cleanup.Dispose();
            base.Dispose();
        }

        public async Task EnqueueAsync(TMessage item)
        {
            await _channel.Writer.WriteAsync(item);
        }
    }
}
