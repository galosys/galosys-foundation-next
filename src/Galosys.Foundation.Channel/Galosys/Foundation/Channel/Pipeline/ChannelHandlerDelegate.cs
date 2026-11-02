using System.Threading.Tasks;

namespace Galosys.Foundation.Channel
{
    /// <summary>连接终端处理器。</summary>
    public delegate Task ChannelHandlerDelegate(ChannelContext context);
}
