using System.Buffers;
using System.Text.Json;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>使用 System.Text.Json 的帧级消息编解码器。</summary>
    public sealed class JsonMessageCodec<T> : IMessageCodec<T>
    {
        private readonly JsonSerializerOptions _options;
        private readonly JsonReaderOptions _readerOptions;

        /// <summary>创建 JSON 编解码器；序列化配置在构造时复制。</summary>
        /// <param name="options">序列化配置；为 null 时使用默认值。</param>
        public JsonMessageCodec(JsonSerializerOptions? options = null)
        {
            _options = options is null
                ? new JsonSerializerOptions()
                : new JsonSerializerOptions(options);

            _readerOptions = new JsonReaderOptions
            {
                AllowTrailingCommas = _options.AllowTrailingCommas,
                CommentHandling = _options.ReadCommentHandling,
                MaxDepth = _options.MaxDepth,
            };
        }

        /// <summary>把一帧 JSON payload 解码为消息。</summary>
        /// <exception cref="JsonException">帧为空、不是合法 JSON、结果为 null 或包含尾随内容。</exception>
        public T Decode(ReadOnlySequence<byte> frame)
        {
            if (frame.IsEmpty)
                throw new JsonException("JSON 帧不能为空。");

            var reader = new Utf8JsonReader(frame, _readerOptions);
            var value = JsonSerializer.Deserialize<T>(ref reader, _options);

            if (value is null)
                throw new JsonException("JSON 帧不能为 null。");

            if (reader.Read())
                throw new JsonException("JSON 帧包含多余内容。");

            return value;
        }

        /// <summary>把一个消息编码为 JSON payload；不写长度头或分隔符。</summary>
        /// <exception cref="ArgumentNullException">writer 或 message 为 null。</exception>
        public void Encode(IBufferWriter<byte> writer, T message)
        {
            ArgumentNullException.ThrowIfNull(writer);

            if (message is null)
                throw new ArgumentNullException(nameof(message));

            using var jsonWriter = new Utf8JsonWriter(writer, new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            });

            JsonSerializer.Serialize(jsonWriter, message, _options);
            jsonWriter.Flush();
        }
    }
}
