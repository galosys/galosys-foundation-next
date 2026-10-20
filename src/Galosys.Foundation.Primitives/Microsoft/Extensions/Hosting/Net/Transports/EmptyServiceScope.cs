using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting.Net
{
    internal sealed class EmptyServiceScope : IServiceScope, IAsyncDisposable
    {
        public static readonly EmptyServiceScope Instance = new();

        private EmptyServiceScope()
        {
        }

        public IServiceProvider ServiceProvider => EmptyServiceProvider.Instance;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        private EmptyServiceProvider()
        {
        }

        public object? GetService(Type serviceType) => null;
    }
}
