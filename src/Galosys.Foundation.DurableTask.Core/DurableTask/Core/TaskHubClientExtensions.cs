using Newtonsoft.Json;

namespace DurableTask.Core
{
    /// <summary>
    /// 
    /// </summary>
    public static class TaskHubClientExtensions
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="client"></param>
        /// <param name="orchestrationInstance"></param>
        /// <param name="eventName"></param>
        /// <param name="event"></param>
        /// <param name="emitTraceActivity"></param>
        /// <returns></returns>
        public static async Task RaiseAsync<TEvent>(
            this TaskHubClient client,
            OrchestrationInstance orchestrationInstance,
            string eventName,
            TEvent @event,
            bool emitTraceActivity = true)
        {
            var eventData = JsonConvert.SerializeObject(@event);
            await client.RaiseEventAsync(orchestrationInstance, eventName, eventData, emitTraceActivity);
        }
    }
}
