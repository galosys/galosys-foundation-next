using Microsoft.Extensions.Options;
using System.Net.Http.Json;

namespace Galosys.Foundation.Doris
{
    public class DorisStreamLoadClient
    {
        private readonly IHttpClientFactory _factory;
        private readonly IOptionsSnapshot<DorisOptions> _optionsSnapshot;

        public DorisStreamLoadClient(
            IHttpClientFactory factory,
            IOptionsSnapshot<DorisOptions> optionsSnapshot)
        {
            _factory = factory;
            _optionsSnapshot = optionsSnapshot;
        }


        public async Task<DorisStreamLoadResult?> StreamLoadAsync<T>(DorisStreamLoadRequest request)
        {
            var http = _factory.CreateClient();
            var options = _optionsSnapshot.Value;
            http.BaseAddress = new Uri($"http://{options.FeHost}:{options.HttpPort}");

            http.DefaultRequestHeaders.TryAddWithoutValidation("Expect", "100-continue");
            http.DefaultRequestHeaders.TryAddWithoutValidation("format", "json");

            var responseMsg = await http.PutAsJsonAsync<T>($"/api/{request.Db}/{request.Table}/_stream_load", default);
            responseMsg.EnsureSuccessStatusCode();

            return await responseMsg.Content.ReadFromJsonAsync<DorisStreamLoadResult>();
        }
    }
}
