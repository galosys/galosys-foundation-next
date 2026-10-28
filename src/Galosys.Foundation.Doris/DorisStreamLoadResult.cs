using System;
using System.Text.Json.Serialization;

namespace Galosys.Foundation.Doris
{
    public class DorisStreamLoadResult
    {
        public string? Status { get; set; }

        public string? Label { get; set; }


        [JsonPropertyName("NumberLoadedRows")]
        public long LoadedRows { get; set; }


        [JsonPropertyName("NumberFilteredRows")]
        public long FilteredRows { get; set; }


        [JsonPropertyName("ErrorURL")]
        public string? ErrorUrl { get; set; }


        public string? Message { get; set; }

        public bool IsSuccess => "Success".Equals(Status, StringComparison.OrdinalIgnoreCase);
    }
}