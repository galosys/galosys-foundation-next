using Galosys.Foundation.Core;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MafSample.API.Domain
{
    /// <summary>
    /// 
    /// </summary>
    [Table("cs_sample", Schema = "cs")]
    public class Sample
    {
        /// <summary>
        /// 
        /// </summary>
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// 
        /// </summary>
        [CreatedAt]
        public DateTimeOffset CreatedAt { get; set; }
    }
}
