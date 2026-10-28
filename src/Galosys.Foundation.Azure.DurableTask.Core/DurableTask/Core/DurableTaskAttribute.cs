namespace Galosys.Foundation.Azure.DurableTask.Core.DurableTask.Core
{
    /// <summary>
    /// 
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class DurableTaskAttribute
        : Attribute
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="name"></param>
        public DurableTaskAttribute(string name)
        {
            Name = name;
        }

        /// <summary>
        /// 
        /// </summary>
        public string Name { get; }
    }
}
