using NewLife.NovaDb.Engine;
using Xunit;

namespace XUnitTest.Engine;

/// <summary>全文索引单元测试</summary>
public class FullTextIndexTests
{
    [Fact(DisplayName = "分词：英文按空白标点切词并转小写")]
    public void Tokenize_English()
    {
        var terms = FullTextIndex.Tokenize("Hello World, NewLife!");
        Assert.Equal(["hello", "world", "newlife"], terms);
    }

    [Fact(DisplayName = "分词：中文生成 Unigram + Bigram")]
    public void Tokenize_Chinese()
    {
        var terms = FullTextIndex.Tokenize("数据库");
        Assert.Contains("数", terms);
        Assert.Contains("据", terms);
        Assert.Contains("库", terms);
        Assert.Contains("数据", terms);
        Assert.Contains("据库", terms);
        Assert.Equal(5, terms.Count);
    }

    [Fact(DisplayName = "分词：中英混合")]
    public void Tokenize_Mixed()
    {
        var terms = FullTextIndex.Tokenize("NewLife 数据库");
        Assert.Contains("newlife", terms);
        Assert.Contains("数据", terms);
        Assert.Contains("据库", terms);
    }

    [Fact(DisplayName = "分词：空文本与空串返回空")]
    public void Tokenize_Empty()
    {
        Assert.Empty(FullTextIndex.Tokenize(null!));
        Assert.Empty(FullTextIndex.Tokenize(""));
        Assert.Empty(FullTextIndex.Tokenize("   "));
    }

    [Fact(DisplayName = "检索：BM25 排序，命中文本排在前面")]
    public void Search_Bm25Ranking()
    {
        var idx = new FullTextIndex("idx", ["title"]);
        idx.Add(1, "NewLife database engine");
        idx.Add(2, "NewLife NewLife NewLife");
        idx.Add(3, "another document about nothing");

        var result = idx.Search("newlife");
        Assert.Equal(2, result.Count);
        Assert.Equal(2, result[0].DocId);
        Assert.Equal(1, result[1].DocId);
    }

    [Fact(DisplayName = "检索：中文查询命中")]
    public void Search_Chinese()
    {
        var idx = new FullTextIndex("idx", ["content"]);
        idx.Add(1, "这是一个关于数据库的文档");
        idx.Add(2, "另一个与数据库无关的文档");

        var result = idx.Search("数据库");
        Assert.Equal(2, result.Count);
    }

    [Fact(DisplayName = "检索：无匹配返回空")]
    public void Search_NoMatch()
    {
        var idx = new FullTextIndex("idx", ["content"]);
        idx.Add(1, "hello world");

        Assert.Empty(idx.Search("nonexistent"));
        Assert.Empty(idx.Search(""));
        Assert.Empty(idx.Search(null!));
    }

    [Fact(DisplayName = "检索：TopK 截断")]
    public void Search_TopK()
    {
        var idx = new FullTextIndex("idx", ["content"]);
        for (var i = 1; i <= 10; i++)
            idx.Add(i, $"doc number {i}");

        var result = idx.Search("doc", 3);
        Assert.Equal(3, result.Count);
    }

    [Fact(DisplayName = "维护：移除文档后不再命中")]
    public void Remove_Doc()
    {
        var idx = new FullTextIndex("idx", ["content"]);
        idx.Add(1, "hello world");
        idx.Add(2, "hello newlife");

        Assert.Equal(2, idx.DocCount);

        idx.Remove(1);
        Assert.Equal(1, idx.DocCount);
        Assert.Equal(1, idx.Search("hello").Count);
        Assert.Equal(2, idx.Search("hello")[0].DocId);
    }

    [Fact(DisplayName = "维护：清空索引")]
    public void Clear_Index()
    {
        var idx = new FullTextIndex("idx", ["content"]);
        idx.Add(1, "hello world");
        idx.Add(2, "hello newlife");

        idx.Clear();
        Assert.Equal(0, idx.DocCount);
        Assert.Equal(0, idx.TermCount);
        Assert.Empty(idx.Search("hello"));
    }

    [Fact(DisplayName = "HasMatch：判断查询是否命中")]
    public void HasMatch_Query()
    {
        var idx = new FullTextIndex("idx", ["content"]);
        idx.Add(1, "hello world");

        Assert.True(idx.HasMatch("world"));
        Assert.True(idx.HasMatch("hello"));
        Assert.False(idx.HasMatch("nonexistent"));
        Assert.False(idx.HasMatch(""));
    }

    [Fact(DisplayName = "多列拼接：多列内容合并索引")]
    public void Add_MultiColumn()
    {
        var idx = new FullTextIndex("idx", ["title", "body"]);
        idx.Add(1, "title text");
        idx.Add(2, "body text");

        Assert.Equal(2, idx.Search("text").Count);
    }

    [Fact(DisplayName = "大小写不敏感：查询忽略大小写")]
    public void Search_CaseInsensitive()
    {
        var idx = new FullTextIndex("idx", ["content"]);
        idx.Add(1, "NewLife");
        idx.Add(2, "XCode");

        Assert.Equal(1, idx.Search("newlife").Count);
        Assert.Equal(1, idx.Search("NEWLIFE").Count);
        Assert.Equal(1, idx.Search("xcode").Count);
    }
}