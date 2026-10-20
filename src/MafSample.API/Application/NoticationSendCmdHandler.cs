using Galosys.Foundation.Core;
using MafSample.API.Infrastructure;

namespace MafSample.API.Application
{
    public class NoticationSendCmdHandler
        : ICommandHandler<NotificationSendCmd, int>
    {
        //private readonly ApplicationDbContext _db;

        public NoticationSendCmdHandler(
            //ApplicationDbContext db
            )
        {
            //_db = db;
        }

        public async Task<int> HandleAsync(NotificationSendCmd command, CancellationToken cancellationToken = default)
        {
            await Task.Delay(1);
            return 2;
        }
    }
}
