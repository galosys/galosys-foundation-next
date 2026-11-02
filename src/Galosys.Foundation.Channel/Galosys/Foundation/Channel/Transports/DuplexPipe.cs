using System.IO.Pipelines;

namespace Galosys.Foundation.Channel
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
