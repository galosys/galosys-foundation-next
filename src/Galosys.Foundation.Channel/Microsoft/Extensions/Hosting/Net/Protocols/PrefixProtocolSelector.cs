using System.Buffers;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>按注册顺序做 first-match 的字节前缀协议选择器。</summary>
    public sealed class PrefixProtocolSelector : IChannelProtocolSelector
    {
        private readonly PrefixProtocolRule[] _rules;
        private readonly int _maxPrefixSize;

        /// <summary>使用默认选项创建选择器。</summary>
        /// <param name="rules">按注册顺序匹配的规则。</param>
        public PrefixProtocolSelector(IEnumerable<PrefixProtocolRule> rules)
            : this(rules, new PrefixProtocolSelectorOptions())
        {
        }

        /// <summary>创建选择器并校验规则。</summary>
        /// <param name="rules">按注册顺序匹配的规则。</param>
        /// <param name="options">扫描上限。</param>
        public PrefixProtocolSelector(
            IEnumerable<PrefixProtocolRule> rules,
            PrefixProtocolSelectorOptions options)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(options);

            if (options.MaxPrefixSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "MaxPrefixSize 必须大于 0。");
            }

            _rules = rules.ToArray();
            _maxPrefixSize = options.MaxPrefixSize;

            foreach (var rule in _rules)
                ArgumentNullException.ThrowIfNull(rule);

            for (var i = 0; i < _rules.Length; i++)
            {
                if (_rules[i].Prefix.Length > _maxPrefixSize)
                {
                    throw new ArgumentException(
                        "规则前缀超过 MaxPrefixSize。",
                        nameof(rules));
                }

                for (var j = i + 1; j < _rules.Length; j++)
                {
                    if (_rules[i].Prefix.Span.SequenceEqual(_rules[j].Prefix.Span))
                    {
                        throw new ArgumentException(
                            "前缀不能重复。",
                            nameof(rules));
                    }
                }
            }
        }

        /// <summary>只窥探输入并按注册顺序返回首个匹配协议；无匹配时返回 null。</summary>
        /// <param name="context">连接视图；只读取 <see cref="ChannelContext.Reader"/>。</param>
        /// <param name="cancellationToken">取消读取的令牌。</param>
        public async ValueTask<IChannelProtocol?> SelectAsync(
            ChannelContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (_rules.Length == 0)
                return null;

            var reader = context.Reader;

            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                var isCompleted = result.IsCompleted;
                var length = buffer.Length;
                var needsMore = false;
                IChannelProtocol? selected = null;

                try
                {
                    foreach (var rule in _rules)
                    {
                        if (StartsWith(buffer, rule.Prefix.Span))
                        {
                            selected = rule.Protocol;
                            break;
                        }

                        if (IsPartialPrefix(buffer, rule.Prefix.Span))
                        {
                            needsMore = true;
                            break;
                        }
                    }
                }
                finally
                {
                    // 返回结果时必须把 examined 留在 Start，后续协议才能立即读到同一批字节；
                    // 只有等待半包时才推进 examined 到 End，让下一次 ReadAsync 等待新数据。
                    if (selected is null && needsMore)
                        reader.AdvanceTo(buffer.Start, buffer.End);
                    else
                        reader.AdvanceTo(buffer.Start, buffer.Start);
                }

                if (selected is not null)
                    return selected;

                if (isCompleted || length >= _maxPrefixSize || !needsMore)
                    return null;
            }
        }

        private static bool StartsWith(
            in ReadOnlySequence<byte> buffer,
            ReadOnlySpan<byte> prefix)
        {
            if (buffer.Length < prefix.Length)
                return false;

            if (buffer.IsSingleSegment)
                return buffer.FirstSpan.StartsWith(prefix);

            var head = buffer.Slice(0, prefix.Length).ToArray();
            return head.AsSpan().SequenceEqual(prefix);
        }

        private static bool IsPartialPrefix(
            in ReadOnlySequence<byte> buffer,
            ReadOnlySpan<byte> prefix)
        {
            if (buffer.Length >= prefix.Length)
                return false;

            if (buffer.IsSingleSegment)
                return buffer.FirstSpan.SequenceEqual(prefix[..(int)buffer.Length]);

            var head = buffer.ToArray();
            return prefix[..head.Length].SequenceEqual(head);
        }
    }
}
