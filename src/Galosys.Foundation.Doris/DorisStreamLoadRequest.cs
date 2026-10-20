namespace Galosys.Foundation.Doris
{
    public class DorisStreamLoadRequest
    {
        public string Db { get; set; } = "";
        public string Table { get; set; } = "";

        /// <summary>
        /// 同批次必须固定，保证 At-Most-Once
        /// </summary>
        public string Label { get; set; } = "";

        public DorisStreamLoadFormatType Format { get; set; } = DorisStreamLoadFormatType.Csv;

        /// <summary>
        /// CSV列分隔符，默认 \t
        /// </summary>
        public string ColumnSeparator { get; set; } = "\t";

        public string LineDelimiter { get; set; } = "\n";

        /// <summary>
        /// 目标表列映射：order_id,created_at,user_id...
        /// </summary>
        public string? Columns { get; set; }

        public double MaxFilterRatio { get; set; } = 0;

        public int TimeoutSec { get; set; } = 600;
    }

    public enum DorisStreamLoadFormatType
    {
        Csv,
        Json
    }
}