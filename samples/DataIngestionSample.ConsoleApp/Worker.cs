using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;

namespace DataIngestionSample.ConsoleApp
{
    public class Worker(ILogger<Worker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var reader = new MarkdownReader();

            var tokenizer = TiktokenTokenizer.CreateForModel("");

            var chunker = new HeaderChunker(new IngestionChunkerOptions(tokenizer)
            {
                OverlapTokens = 0
            });
            //var pipeline = new IngestionPipeline(reader,chunker,);
        }
    }
}
