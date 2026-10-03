using System.Text;

namespace MasterDuelSwitcher.Core.Services;

/// <summary>保留 Valve KeyValues 文档节点顺序和重复键。</summary>
public sealed class VdfDocument
{
    /// <summary>文档顶层条目，按原始数据顺序存放。</summary>
    public List<VdfNode> Nodes { get; } = [];

    /// <summary>解析 Valve KeyValues 文本。</summary>
    public static VdfDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var document = new VdfDocument();
        var reader = new TokenReader(text);
        var parents = new Stack<List<VdfNode>>();
        var destination = document.Nodes;

        while (true)
        {
            var key = reader.Read();
            if (key.Kind == TokenKind.End)
            {
                if (parents.Count != 0)
                {
                    throw reader.Error("对象缺少结束括号。");
                }

                return document;
            }

            if (key.Kind == TokenKind.CloseBrace)
            {
                if (!parents.TryPop(out var parent))
                {
                    throw reader.Error("文档出现多余的结束括号。");
                }

                destination = parent;
                continue;
            }

            if (key.Kind != TokenKind.Text)
            {
                throw reader.Error("对象的开始括号前缺少键。");
            }

            var value = reader.Read();
            if (value.Kind == TokenKind.OpenBrace)
            {
                var node = new VdfNode(key.Text);
                destination.Add(node);
                parents.Push(destination);
                destination = node.Children;
            }
            else if (value.Kind == TokenKind.Text)
            {
                destination.Add(new VdfNode(key.Text, value.Text));
            }
            else
            {
                throw reader.Error("键后缺少字符串值或对象。");
            }
        }
    }

    /// <summary>序列化文档为 Valve KeyValues 文本。</summary>
    public string Serialize()
    {
        var output = new StringBuilder();
        var frames = new Stack<(List<VdfNode> Nodes, int Index)>();
        frames.Push((Nodes, 0));

        while (frames.TryPeek(out var frame))
        {
            if (frame.Index == frame.Nodes.Count)
            {
                frames.Pop();
                if (frames.Count > 0)
                {
                    output.Append('\t', frames.Count - 1).Append("}\r\n");
                }

                continue;
            }

            frames.Pop();
            frames.Push((frame.Nodes, frame.Index + 1));
            var node = frame.Nodes[frame.Index];
            output.Append('\t', frames.Count - 1);
            AppendQuoted(output, node.Key);
            if (node.Value is not null)
            {
                output.Append('\t');
                AppendQuoted(output, node.Value);
                output.Append("\r\n");
            }
            else
            {
                output.Append("\r\n").Append('\t', frames.Count - 1).Append("{\r\n");
                frames.Push((node.Children, 0));
            }
        }

        return output.ToString();
    }

    /// <summary>在文档当前层查找大小写不敏感的首个键。</summary>
    public VdfNode? Find(string key) => VdfNode.FindIn(Nodes, key);

    /// <summary>更新当前层的首个匹配键，缺失时追加字符串条目。</summary>
    public VdfNode SetValue(string key, string value) => VdfNode.SetValueIn(Nodes, key, value);

    /// <summary>将字符串以带引号的转义形式追加到输出。</summary>
    private static void AppendQuoted(StringBuilder output, string value)
    {
        output.Append('"');
        foreach (var character in value)
        {
            output.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => character.ToString()
            });
        }

        output.Append('"');
    }

    /// <summary>区分字符串令牌、对象括号和文档结束。</summary>
    private enum TokenKind
    {
        /// <summary>字符串键或值。</summary>
        Text,
        /// <summary>对象开始括号。</summary>
        OpenBrace,
        /// <summary>对象结束括号。</summary>
        CloseBrace,
        /// <summary>已到达文本末尾。</summary>
        End
    }

    /// <summary>读取 Valve KeyValues 令牌，跳过空白和行注释。</summary>
    private sealed class TokenReader
    {
        /// <summary>待解析文本。</summary>
        private readonly string _text;

        /// <summary>下一次读取的字符位置。</summary>
        private int _position;

        /// <summary>初始化读取器并跳过文档开头的 BOM。</summary>
        internal TokenReader(string text)
        {
            _text = text;
            _position = text.StartsWith('\uFEFF') ? 1 : 0;
        }

        /// <summary>读取下一个字符串或结构令牌。</summary>
        internal (TokenKind Kind, string Text) Read()
        {
            SkipTrivia();
            if (_position == _text.Length)
            {
                return (TokenKind.End, "");
            }

            var character = _text[_position++];
            if (character == '{')
            {
                return (TokenKind.OpenBrace, "");
            }

            if (character == '}')
            {
                return (TokenKind.CloseBrace, "");
            }

            if (character == '"')
            {
                return (TokenKind.Text, ReadQuoted());
            }

            var start = _position - 1;
            while (_position < _text.Length
                   && !char.IsWhiteSpace(_text[_position])
                   && _text[_position] is not ('{' or '}' or '"'))
            {
                _position++;
            }

            return (TokenKind.Text, _text[start.._position]);
        }

        /// <summary>生成只包含位置和语法原因的异常，不输出文档内容。</summary>
        internal FormatException Error(string message) => new($"VDF 格式错误（字符位置 {_position}）：{message}");

        /// <summary>跳过空白及令牌边界处的双斜杠行注释。</summary>
        private void SkipTrivia()
        {
            while (_position < _text.Length)
            {
                if (char.IsWhiteSpace(_text[_position]))
                {
                    _position++;
                    continue;
                }

                if (_text[_position] != '/' || _position + 1 >= _text.Length || _text[_position + 1] != '/')
                {
                    return;
                }

                _position += 2;
                while (_position < _text.Length && _text[_position] is not ('\r' or '\n'))
                {
                    _position++;
                }
            }
        }

        /// <summary>读取已打开的引号字符串并保留未知转义的反斜杠。</summary>
        private string ReadQuoted()
        {
            var value = new StringBuilder();
            while (_position < _text.Length)
            {
                var character = _text[_position++];
                if (character == '"')
                {
                    return value.ToString();
                }

                if (character != '\\')
                {
                    value.Append(character);
                    continue;
                }

                if (_position == _text.Length)
                {
                    throw Error("字符串在转义符后结束。");
                }

                var escaped = _text[_position++];
                switch (escaped)
                {
                    case '\\':
                    case '"':
                        value.Append(escaped);
                        break;
                    case 'n':
                        value.Append('\n');
                        break;
                    case 'r':
                        value.Append('\r');
                        break;
                    case 't':
                        value.Append('\t');
                        break;
                    default:
                        value.Append('\\').Append(escaped);
                        break;
                }
            }

            throw Error("字符串缺少结束引号。");
        }
    }
}
