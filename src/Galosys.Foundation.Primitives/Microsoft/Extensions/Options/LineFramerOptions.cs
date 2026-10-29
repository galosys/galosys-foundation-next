namespace Microsoft.Extensions.Options
{
    /// <summary>换行分帧配置。</summary>
    public sealed class LineFramerOptions
    {
        /// <summary>单帧 payload 的硬上限，默认 1 MiB。</summary>
        public int MaxFrameSize { get; set; } = 1024 * 1024;

        /// <summary>分隔符字节，默认 <c>\n</c>。</summary>
        public byte Separator { get; set; } = (byte)'\n';

        /// <summary>是否裁剪分隔符前的一个 <c>\r</c>，默认 true。</summary>
        /// <remarks>
        /// 分隔符本身为 <c>\r</c> 时仍会检查其前一个字节，裁剪行为可能不直观；
        /// 需要保留所有 <c>\r</c> 时应将该选项设为 false。
        /// </remarks>
        public bool TrimCarriageReturn { get; set; } = true;
    }
}
