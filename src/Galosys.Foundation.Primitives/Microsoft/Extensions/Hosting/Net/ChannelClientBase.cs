using System.Net;

namespace Microsoft.Extensions.Hosting.Net
{
    internal class ChannelClientBase
        : IChannelServer
    {
        public EndPoint? LocalEndPoint => throw new NotImplementedException();

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        public ValueTask DisposeAsync()
        {
            throw new NotImplementedException();
        }

        public Task StartAsync(CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public Task RunAsync(CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public Task StopAsync(CancellationToken ct)
        {
            throw new NotImplementedException();
        }
    }
}
