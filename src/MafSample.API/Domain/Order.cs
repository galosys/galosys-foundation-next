using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MafSample.API.Domain
{
    /// <summary>
    /// 
    /// </summary>
    //[Table("cs_sample",Schema ="cs")]
    [Table("order")]
    public class Order
    {
        /// <summary>
        /// 
        /// </summary>
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public string? OrderNo { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public short State { get; set; }
    }

    /// <summary>
    /// 
    /// </summary>
    public enum OrderState
    {
        Pending = 0,
        Closed = 10,
        Finished = 70
    }

    public enum OrderEvent
    {
        Close,
        Finish
    }

    public class OrderContext
    {

    }
}
