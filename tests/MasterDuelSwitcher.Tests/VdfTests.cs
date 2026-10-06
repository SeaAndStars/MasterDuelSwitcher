using MasterDuelSwitcher.Core.Services;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>验证 Valve KeyValues 的数据保留、语法解析和定点修改行为。</summary>
public sealed class VdfTests
{
    /// <summary>验证嵌套对象、重复键和顶层顺序均保留。</summary>
    [Fact]
    public void Parse_PreservesNestedObjectsDuplicateKeysAndOrder()
    {
        var document = VdfDocument.Parse("\"Users\" { \"123\" { \"Name\" \"甲\" \"Name\" \"乙\" } } \"tail\" \"end\"");

        Assert.Equal(new[] { "Users", "tail" }, document.Nodes.Select(node => node.Key));
        var users = Assert.IsType<VdfNode>(document.Find("users"));
        Assert.Null(users.Value);
        var account = Assert.IsType<VdfNode>(users.Find("123"));
        Assert.Equal(new[] { "甲", "乙" }, account.Children.Select(node => node.Value));
        Assert.Equal("end", document.Find("TAIL")?.Value);
    }

    /// <summary>验证 BOM、CRLF、空白和行注释不会被解释为数据。</summary>
    [Fact]
    public void Parse_AcceptsBomCrLfAndComments()
    {
        var document = VdfDocument.Parse("\uFEFF// 文件说明\r\n root // 对象说明\r\n {\r\n key value // 行尾说明\r\n empty {}\r\n } // 最后说明");

        var root = Assert.IsType<VdfNode>(document.Find("root"));
        Assert.Equal("value", root.Find("key")?.Value);
        Assert.Empty(Assert.IsType<VdfNode>(root.Find("empty")).Children);
        Assert.Null(root.Find("empty")?.Value);
    }

    /// <summary>验证带引号的数据内保留注释符号和结构符号。</summary>
    [Fact]
    public void Parse_PreservesCommentAndBraceCharactersInsideQuotedStrings()
    {
        var document = VdfDocument.Parse("\"url\" \"https://host/{item}\" \"empty\" \"\"");

        Assert.Equal("https://host/{item}", document.Find("url")?.Value);
        Assert.Equal("", document.Find("empty")?.Value);
    }

    /// <summary>验证裸令牌中的路径和网址保留原始反斜杠与斜杠。</summary>
    [Fact]
    public void Parse_PreservesSlashCharactersInsideUnquotedTokens()
    {
        var document = VdfDocument.Parse("path C:\\Games\\Steam url https://host/path");

        Assert.Equal(@"C:\Games\Steam", document.Find("path")?.Value);
        Assert.Equal("https://host/path", document.Find("url")?.Value);
    }

    /// <summary>验证标准转义被解码、未知转义的反斜杠保留。</summary>
    [Fact]
    public void Parse_DecodesKnownEscapesAndPreservesUnknownEscapes()
    {
        var document = VdfDocument.Parse("\"value\" \"a\\\"b\\\\c\\n\\r\\t\\q\\u1234\"");

        Assert.Equal("a\"b\\c\n\r\t\\q\\u1234", document.Find("value")?.Value);
    }

    /// <summary>验证相邻引号令牌和紧贴对象的结构符号可正确解析。</summary>
    [Fact]
    public void Parse_AcceptsAdjacentQuotedTokensAndBraces()
    {
        var document = VdfDocument.Parse("\"root\"{\"key\"\"value\"}");

        Assert.Equal("value", document.Find("root")?.Find("key")?.Value);
    }

    /// <summary>验证裸令牌紧邻引号或两种结构括号时正确终止，而不吞掉边界字符。</summary>
    [Fact]
    public void Parse_AcceptsAdjacentUnquotedTokensAndBoundaries()
    {
        var document = VdfDocument.Parse("root{key\"value\" tail end}");

        Assert.Equal("value", document.Find("root")?.Find("key")?.Value);
        Assert.Equal("end", document.Find("root")?.Find("tail")?.Value);
    }

    /// <summary>验证空文本和纯注释可表示空文档。</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t// 没有数据")]
    public void Parse_AcceptsEmptyDocuments(string text)
    {
        Assert.Empty(VdfDocument.Parse(text).Nodes);
    }

    /// <summary>验证缺失值、未闭合对象、意外括号和未闭合字符串被拒绝。</summary>
    [Theory]
    [InlineData("key")]
    [InlineData("root { key value")]
    [InlineData("}")]
    [InlineData("{ key value }")]
    [InlineData("root { key }")]
    [InlineData("\"key\" \"unterminated")]
    [InlineData("\"key\" \"trailing\\")]
    [InlineData("key value }")]
    public void Parse_RejectsMalformedDocuments(string text)
    {
        Assert.Throws<FormatException>(() => VdfDocument.Parse(text));
    }

    /// <summary>验证空引用输入使用参数异常报告。</summary>
    [Fact]
    public void Parse_RejectsNullInput()
    {
        Assert.Throws<ArgumentNullException>(() => VdfDocument.Parse(null!));
    }

    /// <summary>验证查找仅匹配当前层中的首个大小写无关键。</summary>
    [Fact]
    public void Find_ReturnsFirstDirectMatchWithoutSearchingDescendants()
    {
        var document = VdfDocument.Parse("Name first name second child { hidden inner }");

        Assert.Equal("first", document.Find("NAME")?.Value);
        Assert.Null(document.Find("hidden"));
        Assert.Equal("inner", document.Find("child")?.Find("HIDDEN")?.Value);
    }

    /// <summary>验证定点修改保留同级其他项、重复键以及原键的大小写。</summary>
    [Fact]
    public void SetValue_UpdatesOnlyFirstMatchingEntryAndPreservesOrder()
    {
        var document = VdfDocument.Parse("prefix keep Name first name second suffix untouched");

        var updated = document.SetValue("NAME", "changed");

        Assert.Same(document.Nodes[1], updated);
        Assert.Equal(new[] { "prefix", "Name", "name", "suffix" }, document.Nodes.Select(node => node.Key));
        Assert.Equal(new[] { "keep", "changed", "second", "untouched" }, document.Nodes.Select(node => node.Value));
    }

    /// <summary>验证缺失键追加到末尾且返回新条目。</summary>
    [Fact]
    public void SetValue_AppendsMissingDocumentAndObjectEntries()
    {
        var document = VdfDocument.Parse("root { first keep }");
        var root = Assert.IsType<VdfNode>(document.Find("root"));

        var appendedChild = root.SetValue("second", "added");
        var appendedRoot = document.SetValue("tail", "end");

        Assert.Same(appendedChild, root.Children[1]);
        Assert.Same(appendedRoot, document.Nodes[1]);
        Assert.Equal(new[] { "first", "second" }, root.Children.Select(node => node.Key));
        Assert.Equal("added", root.Find("second")?.Value);
        Assert.Equal("end", document.Find("tail")?.Value);
    }

    /// <summary>验证对象改为字符串值时清除原有子项。</summary>
    [Fact]
    public void SetValue_ConvertsMatchingObjectIntoScalar()
    {
        var document = VdfDocument.Parse("root { obsolete value } tail keep");

        var updated = document.SetValue("ROOT", "scalar");

        Assert.Equal("scalar", updated.Value);
        Assert.Empty(updated.Children);
        Assert.Equal("keep", document.Find("tail")?.Value);
    }

    /// <summary>验证字符串节点拒绝添加子项而不丢失原字符串值。</summary>
    [Fact]
    public void SetValue_RejectsChildWritesToScalarNodes()
    {
        var scalar = new VdfNode("key", "value");

        Assert.Throws<InvalidOperationException>(() => scalar.SetValue("child", "new"));
        Assert.Equal("value", scalar.Value);
        Assert.Empty(scalar.Children);
    }

    /// <summary>验证序列化转义保留字符串内容、重复键和未知项。</summary>
    [Fact]
    public void Serialize_RoundTripsEscapesDuplicateKeysAndUnrelatedEntries()
    {
        var document = VdfDocument.Parse("root { Name first Name second untouched { flag yes } } empty {}");
        document.Find("root")!.SetValue("Name", "引号\"\\反斜杠\n\r\t\\q");

        var reparsed = VdfDocument.Parse(document.Serialize());
        var root = Assert.IsType<VdfNode>(reparsed.Find("root"));

        Assert.Equal(new[] { "root", "empty" }, reparsed.Nodes.Select(node => node.Key));
        Assert.Equal(new[] { "Name", "Name", "untouched" }, root.Children.Select(node => node.Key));
        Assert.Equal("引号\"\\反斜杠\n\r\t\\q", root.Children[0].Value);
        Assert.Equal("second", root.Children[1].Value);
        Assert.Equal("yes", root.Find("untouched")?.Find("flag")?.Value);
        Assert.Null(reparsed.Find("empty")?.Value);
    }

    /// <summary>验证直接构造文档节点可序列化并还原为空字符串和空对象。</summary>
    [Fact]
    public void Serialize_SupportsManuallyConstructedNodes()
    {
        var document = new VdfDocument();
        document.Nodes.Add(new VdfNode("emptyValue", ""));
        document.Nodes.Add(new VdfNode("emptyObject"));

        var reparsed = VdfDocument.Parse(document.Serialize());

        Assert.Equal("", reparsed.Find("emptyValue")?.Value);
        Assert.Null(reparsed.Find("emptyObject")?.Value);
        Assert.Empty(reparsed.Find("emptyObject")!.Children);
    }
}
