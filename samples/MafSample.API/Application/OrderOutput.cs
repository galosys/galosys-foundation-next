using Microsoft.Extensions.Compliance.Classification;

namespace MafSample.API.Application
{
    /// <summary>
    /// 
    /// </summary>
    public class OrderOutput
    {
        /// <summary>
        /// 
        /// </summary>
        [SensitiveData]
        public string? OrderNo { get; set; }

        /// <summary>
        /// 
        /// </summary>
        [PrivateData]
        public string? Mobile { get; set; }
    }
}
