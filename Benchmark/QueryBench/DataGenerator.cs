using System.Globalization;

namespace NewLife.QueryBench;

/// <summary>基准数据行</summary>
public readonly struct BenchRow
{
    /// <summary>实例化</summary>
    public BenchRow() { }

    /// <summary>主键</summary>
    public Int32 Id { get; init; }

    /// <summary>用户编号</summary>
    public Int32 UserId { get; init; }

    /// <summary>状态</summary>
    public Int32 Status { get; init; }

    /// <summary>业务编码</summary>
    public String Code { get; init; } = "";

    /// <summary>金额</summary>
    public Double Amount { get; init; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>备注</summary>
    public String Remark { get; init; } = "";
}

/// <summary>确定性数据生成器。固定种子，按行号可复现，不整体驻留内存</summary>
public static class DataGenerator
{
    /// <summary>固定种子</summary>
    public const UInt64 Seed = 20261121;

    /// <summary>时间锚点。数据落在锚点前 365 天内，保证多次运行与断点续跑语义一致</summary>
    public static readonly DateTime Anchor = new(2026, 11, 21, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>时间跨度（秒），365 天</summary>
    private const Int64 SpanSeconds = 365L * 24 * 60 * 60;

    /// <summary>创建时间乘数。与 365 天秒数互质，保证行号到时间偏移是一一映射</summary>
    private const UInt64 TimeMultiplier = 1_000_003UL;

    /// <summary>用户编号上界</summary>
    /// <param name="total"></param>
    /// <returns></returns>
    public static Int32 MaxUserId(Int64 total) => (Int32)Math.Max(1, total / 10);

    /// <summary>生成指定行号的数据</summary>
    /// <param name="index">行号，从 1 开始</param>
    /// <param name="total">总行数</param>
    /// <returns></returns>
    public static BenchRow Create(Int64 index, Int64 total)
    {
        if (index < 1) throw new ArgumentOutOfRangeException(nameof(index));
        if (index > 308_915_776L) throw new ArgumentOutOfRangeException(nameof(index), "code 的 6 位基数编码上限为 26^6");

        var hash = Mix(Seed ^ (UInt64)index);
        var maxUser = MaxUserId(total);

        var userId = 1 + (Int32)(Mix(hash ^ 0x11) % (UInt64)maxUser);
        var status = (Int32)(Mix(hash ^ 0x21) % 5);
        var amount = (Int32)(Mix(hash ^ 0x31) % 1_000_001) / 100.0;
        var seconds = (Int64)(((UInt64)index * TimeMultiplier) % (UInt64)SpanSeconds);
        var createdAt = Anchor.AddSeconds(-seconds - 1);

        var suffix = EncodeBase26(index, 6);
        var prefix = Mix(hash ^ 0x41) % 5 == 0
            ? "AB"
            : new String([(Char)('A' + (Int32)(Mix(hash ^ 0x42) % 26)), (Char)('A' + (Int32)(Mix(hash ^ 0x43) % 26))]);
        var code = prefix + suffix;

        var remark = BuildRemark(hash);

        return new BenchRow
        {
            Id = (Int32)index,
            UserId = userId,
            Status = status,
            Code = code,
            Amount = amount,
            CreatedAt = createdAt,
            Remark = remark,
        };
    }

    /// <summary>SplitMix64 混合</summary>
    /// <param name="value"></param>
    /// <returns></returns>
    private static UInt64 Mix(UInt64 value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    /// <summary>26 进制编码为固定长度大写字母</summary>
    /// <param name="value"></param>
    /// <param name="length"></param>
    /// <returns></returns>
    private static String EncodeBase26(Int64 value, Int32 length)
    {
        var chars = new Char[length];
        for (var i = length - 1; i >= 0; i--)
        {
            chars[i] = (Char)('A' + (Int32)(value % 26));
            value /= 26;
        }
        return new String(chars);
    }

    /// <summary>生成 20~200 字符备注，约 4% 的行包含 abc</summary>
    /// <param name="hash"></param>
    /// <returns></returns>
    private static String BuildRemark(UInt64 hash)
    {
        var length = 20 + (Int32)(Mix(hash ^ 0x61) % 181);
        var chars = new Char[length];
        var state = Mix(hash ^ 0x62) | 1;
        for (var i = 0; i < length; i++)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            var value = state * 0x2545F4914F6CDD1DUL;
            chars[i] = (Char)('a' + (Int32)(value % 26));
        }

        // 少量行埋入 abc，供 LIKE '%abc%' 全表扫描查询命中
        if (Mix(hash ^ 0x63) % 25 == 0 && length >= 20)
        {
            var pos = (Int32)(Mix(hash ^ 0x64) % (UInt64)(length - 2));
            chars[pos] = 'a';
            chars[pos + 1] = 'b';
            chars[pos + 2] = 'c';
        }

        return new String(chars);
    }

    /// <summary>格式化为 SQL 字符串字面量</summary>
    /// <param name="text"></param>
    /// <returns></returns>
    public static String ToSqlLiteral(String? text) => text == null ? "NULL" : $"'{text.Replace("'", "''")}'";

    /// <summary>格式化为 SQL 时间字面量</summary>
    /// <param name="time"></param>
    /// <returns></returns>
    public static String ToSqlDateTime(DateTime time) => $"'{time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}'";

    /// <summary>数据库文本时间格式</summary>
    /// <param name="time"></param>
    /// <returns></returns>
    public static String ToTextDateTime(DateTime time) => time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
