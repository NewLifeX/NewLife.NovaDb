using System.Globalization;

namespace NewLife.NovaDb.Engine;

/// <summary>对象包装器，用于 SkipList 键比较</summary>
internal class ComparableObject : IComparable<ComparableObject>
{
    public Object Value { get; }

    public ComparableObject(Object value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public Int32 CompareTo(ComparableObject? other)
    {
        if (other == null)
            return 1;

        return CompareValues(Value, other.Value);
    }

    /// <summary>按键值比较两个对象。数组（联合索引键、向量、二进制）按元素逐位比较</summary>
    /// <param name="left">左值</param>
    /// <param name="right">右值</param>
    /// <returns>比较结果</returns>
    /// <remarks>联合索引键是 Object[]，必须按元素比较才能保证跳表有序；否则键会落回 HashCode 比较而错序，等值与前缀查询都不可用</remarks>
    internal static Int32 CompareValues(Object? left, Object? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left == null)
            return -1;
        if (right == null)
            return 1;

        // 数组按元素比较；数组与非数组之间给出确定顺序，避免同一键集合出现两种排序
        if (left is Array leftArray)
        {
            if (right is Array rightArray)
                return CompareArrays(leftArray, rightArray);

            return 1;
        }
        if (right is Array)
            return -1;

        // 使用 Object.Equals 进行比较
        if (left.Equals(right))
            return 0;

        // 数值类型允许跨类型比较：ALTER TABLE 改列类型后，存量行仍是旧类型，按值比较才不会抛异常也不会错序
        if (IsNumeric(left) && IsNumeric(right))
        {
            if (left is Single or Double || right is Single or Double)
                return Convert.ToDouble(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture));

            return Convert.ToDecimal(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(right, CultureInfo.InvariantCulture));
        }

        // 如果两者都实现了 IComparable，使用它
        if (left is IComparable comparable && right is IComparable)
        {
            return comparable.CompareTo(right);
        }

        // 否则使用 HashCode 比较
        return left.GetHashCode().CompareTo(right.GetHashCode());
    }

    private static Boolean IsNumeric(Object value) => value is
        Byte or SByte or Int16 or UInt16 or Int32 or UInt32 or Int64 or UInt64 or Single or Double or Decimal;

    /// <summary>按元素比较两个数组，先逐位比较共同长度，再按长度决定顺序</summary>
    private static Int32 CompareArrays(Array left, Array right)
    {
        // 向量与二进制列常用，走无装箱的快速路径
        if (left is Single[] leftSingles && right is Single[] rightSingles)
            return CompareElements(leftSingles, rightSingles);

        if (left is Byte[] leftBytes && right is Byte[] rightBytes)
            return CompareElements(leftBytes, rightBytes);

        var length = Math.Min(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            var cmp = CompareValues(left.GetValue(i), right.GetValue(i));
            if (cmp != 0) return cmp;
        }

        return left.Length.CompareTo(right.Length);
    }

    private static Int32 CompareElements<T>(T[] left, T[] right) where T : IComparable<T>
    {
        var length = Math.Min(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            var cmp = left[i].CompareTo(right[i]);
            if (cmp != 0) return cmp;
        }

        return left.Length.CompareTo(right.Length);
    }

    public override Boolean Equals(Object? obj)
    {
        if (obj is not ComparableObject co) return false;

        return Value is Array leftArray && co.Value is Array rightArray
            ? CompareArrays(leftArray, rightArray) == 0
            : Value.Equals(co.Value);
    }

    public override Int32 GetHashCode() => Value is Array array ? GetArrayHashCode(array) : Value.GetHashCode();

    /// <summary>按元素累加数组哈希，与 <see cref="Equals"/> 对数组的结构比较保持一致</summary>
    private static Int32 GetArrayHashCode(Array array)
    {
        var hash = 17;
        for (var i = 0; i < array.Length; i++)
        {
            var value = array.GetValue(i);
            hash = hash * 31 + (value == null ? 0 : value.GetHashCode());
        }

        return hash;
    }
}
