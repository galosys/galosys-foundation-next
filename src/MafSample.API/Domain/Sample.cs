using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MafSample.API.Domain
{
    /// <summary>
    /// 
    /// </summary>
    //[Table("cs_sample",Schema ="cs")]
    [Table("sample")]
    public class Sample
    {
        /// <summary>
        /// 
        /// </summary>
        [Key]
        public int Id { get; set; }
    }
}
