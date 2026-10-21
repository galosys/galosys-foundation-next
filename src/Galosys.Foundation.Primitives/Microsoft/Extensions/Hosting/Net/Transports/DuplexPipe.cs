using System.IO.Pipelines;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>由现有管道读写端组成的双向传输。</summary>
    internal sealed class DuplexPipe : IDuplexPipe
    {
        public DuplexPipe(PipeReader input, PipeWriter output)
        {
            Input = input;
            Output = output;
        }

        public PipeReader Input { get; }

        public PipeWriter Output { get; }
    }
}
