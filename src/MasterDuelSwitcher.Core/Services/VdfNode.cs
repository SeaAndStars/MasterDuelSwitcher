namespace MasterDuelSwitcher.Core.Services;

/// <summary>表示一个字符串条目或包含子项的 Valve KeyValues 对象。</summary>
public sealed class VdfNode
{
    /// <summary>创建字符串条目，值为空引用时创建对象条目。</summary>
    public VdfNode(string key, string? value = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        Key = key;
        Value = value;
    }

    /// <summary>条目键名，保留原始大小写。</summary>
    public string Key { get; set; }

    /// <summary>字符串条目的值；空引用表示对象条目。</summary>
    public string? Value { get; set; }

    /// <summary>对象子项，按原始数据顺序存放并允许重复键。</summary>
    public List<VdfNode> Children { get; } = [];

    /// <summary>在当前对象层查找大小写不敏感的首个键。</summary>
    public VdfNode? Find(string key) => FindIn(Children, key);

    /// <summary>更新当前对象层的首个匹配键，缺失时追加字符串条目。</summary>
    public VdfNode SetValue(string key, string value)
    {
        if (Value is not null)
        {
            throw new InvalidOperationException("字符串条目不接受子项。");
        }

        return SetValueIn(Children, key, value);
    }

    /// <summary>在指定节点列表中查找当前层首个大小写无关的键。</summary>
    internal static VdfNode? FindIn(List<VdfNode> nodes, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return nodes.Find(node => string.Equals(node.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>修改指定列表中的首个匹配项并清除旧子项，缺失时追加。</summary>
    internal static VdfNode SetValueIn(List<VdfNode> nodes, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var node = FindIn(nodes, key);
        if (node is null)
        {
            node = new VdfNode(key, value);
            nodes.Add(node);
        }
        else
        {
            node.Value = value;
            node.Children.Clear();
        }

        return node;
    }
}
