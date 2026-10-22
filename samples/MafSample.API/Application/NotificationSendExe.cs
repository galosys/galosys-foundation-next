using Galosys.Foundation.Core;
//using Galosys.Foundation.Notification;
using Microsoft.Extensions.Resilience;

namespace MafSample.API.Application
{
    public class NotificationSendExe
        : ExecutorBase<NotificationSendCmd>
    {
        //private readonly INotificationDispatcher _dispatcher;

        public NotificationSendExe(
            //INotificationDispatcher dispatcher,
            ILogger<ExecutorBase<NotificationSendCmd>> logger,
            IServiceProvider serviceProvider,
            IResiliencePipeline? pipeline = null) : base(logger, serviceProvider, pipeline)
        {
            //_dispatcher = dispatcher;
        }
        protected override async Task<UnifiedResponse> DoExecuteAsync(NotificationSendCmd command, CancellationToken cancellationToken)
        {
            //await _dispatcher.SendAsync(new NotificationRequest(Channel: "dingtalk", Content: "随便发点啥"));
            return UnifiedResponse.Succeed();
        }
    }
}
