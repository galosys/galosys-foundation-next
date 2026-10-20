using Galosys.Foundation.Core;
//using Galosys.Foundation.Integration.Im.Abstractions;
using MafSample.API.Application;
using MafSample.API.Domain;
using MafSample.API.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Messaging;

namespace MafSample.API.Controllers
{
    /// <summary>
    /// RocketMQ
    /// </summary>
    [ApiController]
    [Route("api/[controller]/[action]")]
    [AllowAnonymous]
    public class RmqController : ControllerBase
    {
        private readonly ILogger<RmqController> _logger;
        private readonly IMessageBus _bus;
        //private readonly ApplicationDbContext _db;
        private readonly HybridCache _cache;
        private readonly IObjectSerializer _serializer;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="rmq"></param>
        /// <param name="batching"></param>
        /// <param name="bus"></param>
        /// <param name="db"></param>
        /// <param name="cache"></param>
        /// <param name="serializer"></param>
        public RmqController(ILogger<RmqController> logger
            , IMessageBus bus
            //, ApplicationDbContext db
            , HybridCache cache
            , IObjectSerializer serializer)
        {
            _logger = logger;
            _bus = bus;
            //_db = db;
            _cache = cache;
            _serializer = serializer;
        }

        /// <summary>
        /// 发送
        /// </summary>
        /// <returns></returns>
        [HttpGet, RequestLog("发送")]
        public async Task<string> SendAsync()
        {
            var input = new MsgSampleInput();

            //await _db.AddAsync(new Sample());
            await _bus.SendAsync("test_send", input);
            //await _db.SaveChangesAsync();
            return "ok";
        }

        /// <summary>
        /// IM
        /// </summary>
        /// <returns></returns>
    //    [HttpGet]
    //    public async Task<string> ImAsync([FromServices] IImPlatform imPlatform)
    //    {
    //        var msgId = await imPlatform.Notification.SendAsync(new ImMessage(
    //ImTargetType.User,
    //"17551031463",           // 钉钉 userId
    //ImMessageType.Text,
    //"审批通知",
    //"您有一笔采购单待审批，请及时处理。",
    //null));
    //        return msgId;
    //    }

        /// <summary>
        /// chat
        /// </summary>
        /// <returns></returns>
        //[HttpGet]
        //public async Task<string> ChatAsync([FromKeyedServices("default")] AIAgent agent, [FromServices] AgentSessionStore agentSessionStore)
        //{
        //    var id =$"conversation-{Guid.NewGuid().ToString("N")}";
        //    var session = await agent.CreateSessionAsync();
        //    await agentSessionStore.SaveSessionAsync(agent, id, session);
        //    return id;
        //}
    }
}
