namespace NewLife.NovaDb.Engine;

/// <summary>全文检索索引。基于倒排索引 + BM25 评分，支持中文分词（Unigram/Bigram 混合）</summary>
/// <remarks>
/// 设计目标：为关系型表提供轻量全文检索能力，服务 RAG 场景（与向量检索组合为混合检索）。
/// - 分词：英文按空白/标点切词；中文采用 Unigram（单字）+ Bigram（相邻双字）混合，无需词典
/// - 索引：倒排表 Dict&lt;Term, Dict&lt;DocId, 词频&gt;&gt;，内存驻留（适合中小数据量）
/// - 评分：BM25 算法，考虑文档长度归一化
/// 说明：P2 功能，聚焦核心检索能力；索引持久化与增量维护由 NovaTable 集成负责（后续迭代）。
/// </remarks>
public class FullTextIndex
{
    #region 属性
    /// <summary>索引名称</summary>
    public String Name { get; }

    /// <summary>被索引的列名</summary>
    public String[] Columns { get; }

    /// <summary>倒排表：词项 → 文档ID → 词频</summary>
    private readonly Dictionary<String, Dictionary<Object, Int32>> _inverted = new(StringComparer.Ordinal);

    /// <summary>文档长度：文档ID → 词项总数</summary>
    private readonly Dictionary<Object, Int32> _docLengths = [];

    /// <summary>文档总数</summary>
    public Int32 DocCount => _docLengths.Count;

    /// <summary>词项总数</summary>
    public Int32 TermCount => _inverted.Count;
    #endregion

    #region 构造
    /// <summary>创建全文索引</summary>
    /// <param name="name">索引名称</param>
    /// <param name="columns">被索引的列名</param>
    public FullTextIndex(String name, String[] columns)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
    }
    #endregion

    #region 索引维护
    /// <summary>添加文档到索引</summary>
    /// <param name="docId">文档 ID（主键值，可为任意类型）</param>
    /// <param name="text">文本内容（多列以空格连接）</param>
    public void Add(Object docId, String text)
    {
        if (text == null) return;

        var terms = Tokenize(text);
        if (terms.Count == 0) return;

        // 统计词频
        var freq = new Dictionary<String, Int32>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            freq[term] = freq.TryGetValue(term, out var n) ? n + 1 : 1;
        }

        // 写入倒排表
        foreach (var kv in freq)
        {
            if (!_inverted.TryGetValue(kv.Key, out var postings))
            {
                postings = [];
                _inverted[kv.Key] = postings;
            }
            postings[docId] = kv.Value;
        }

        _docLengths[docId] = terms.Count;
    }

    /// <summary>移除文档</summary>
    /// <param name="docId">文档 ID（主键值）</param>
    public void Remove(Object docId)
    {
        foreach (var postings in _inverted.Values)
        {
            postings.Remove(docId);
        }
        _docLengths.Remove(docId);
    }

    /// <summary>清空索引</summary>
    public void Clear()
    {
        _inverted.Clear();
        _docLengths.Clear();
    }
    #endregion

    #region 检索
    /// <summary>全文检索，返回按 BM25 分数降序的文档 ID 列表</summary>
    /// <param name="query">查询文本</param>
    /// <param name="topK">返回数量上限</param>
    /// <returns>按分数降序的 (文档ID, 分数) 列表</returns>
    public List<(Object DocId, Double Score)> Search(String query, Int32 topK = 10)
    {
        if (String.IsNullOrEmpty(query)) return [];

        var queryTerms = Tokenize(query);
        if (queryTerms.Count == 0) return [];

        var n = DocCount;
        if (n == 0) return [];

        const Double k1 = 1.2;
        const Double b = 0.75;
        var avgdl = _docLengths.Values.Average();

        // 计算每个文档的 BM25 分数
        var scores = new Dictionary<Object, Double>();
        var queryFreq = new Dictionary<String, Int32>(StringComparer.Ordinal);
        foreach (var term in queryTerms)
        {
            queryFreq[term] = queryFreq.TryGetValue(term, out var c) ? c + 1 : 1;
        }

        foreach (var kv in queryFreq)
        {
            if (!_inverted.TryGetValue(kv.Key, out var postings)) continue;

            // 逆文档频率 IDF
            var df = postings.Count;
            var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));

            foreach (var p in postings)
            {
                var dl = _docLengths[p.Key];
                var tfNorm = p.Value * (k1 + 1) / (p.Value + k1 * (1 - b + b * dl / Math.Max(avgdl, 1.0)));
                scores[p.Key] = scores.TryGetValue(p.Key, out var s) ? s + idf * tfNorm : idf * tfNorm;
            }
        }

        // 按分数降序排列，取 Top-K
        var result = scores.Select(kv => (kv.Key, kv.Value)).ToList();
        result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        if (result.Count > topK) result.RemoveRange(topK, result.Count - topK);

        return result;
    }

    /// <summary>判断查询是否命中（至少一个词项在索引中）</summary>
    /// <param name="query">查询文本</param>
    /// <returns>是否命中</returns>
    public Boolean HasMatch(String query)
    {
        if (String.IsNullOrEmpty(query)) return false;

        foreach (var term in Tokenize(query))
        {
            if (_inverted.ContainsKey(term)) return true;
        }
        return false;
    }
    #endregion

    #region 分词
    /// <summary>分词：英文按空白/标点切词，中文采用 Unigram + Bigram 混合</summary>
    /// <param name="text">文本</param>
    /// <returns>词项列表</returns>
    public static List<String> Tokenize(String text)
    {
        var result = new List<String>();
        if (String.IsNullOrEmpty(text)) return result;

        // 先按空白/标点切分为单词（英文/数字）
        var sb = new System.Text.StringBuilder();
        var hasCjk = false;

        foreach (var c in text)
        {
            if (Char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                hasCjk = hasCjk || c > 0x2E80; // CJK 范围
            }
            else
            {
                if (sb.Length > 0)
                {
                    AddWord(result, sb.ToString(), hasCjk);
                    sb.Clear();
                    hasCjk = false;
                }
            }
        }
        if (sb.Length > 0) AddWord(result, sb.ToString(), hasCjk);

        return result;
    }

    /// <summary>将一个单词加入词项列表（中文额外生成 Bigram）</summary>
    /// <param name="result">词项列表</param>
    /// <param name="word">单词</param>
    /// <param name="isCjk">是否含中文字符</param>
    private static void AddWord(List<String> result, String word, Boolean isCjk)
    {
        var lower = word.ToLowerInvariant();

        if (!isCjk)
        {
            // 英文/数字：整词
            result.Add(lower);
            return;
        }

        // 中文：Unigram（单字）+ Bigram（相邻双字）
        var cjkChars = lower.Where(c => c > 0x2E80).ToArray();
        if (cjkChars.Length == 0)
        {
            result.Add(lower);
            return;
        }

        foreach (var c in cjkChars)
        {
            result.Add(c.ToString());
        }

        for (var i = 0; i < cjkChars.Length - 1; i++)
        {
            result.Add(new String([cjkChars[i], cjkChars[i + 1]]));
        }
    }
    #endregion
}
