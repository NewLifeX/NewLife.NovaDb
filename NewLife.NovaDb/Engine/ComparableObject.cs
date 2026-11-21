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

        // 使用 Object.Equals 和 GetHashCode 进行比较
        if (Value.Equals(other.Value))
            return 0;

        // 数值类型允许跨类型比较：ALTER TABLE 改列类型后，存量行仍是旧类型，按值比较才不会抛异常也不会错序
        if (IsNumeric(Value) && IsNumeric(other.Value))
        {
            if (Value is Single or Double || other.Value is Single or Double)
                return Convert.ToDouble(Value, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(other.Value, CultureInfo.InvariantCulture));

            return Convert.ToDecimal(Value, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(other.Value, CultureInfo.InvariantCulture));
        }

        // 如果两者都实现了 IComparable，使用它
        if (Value is IComparable comparable && other.Value is IComparable)
        {
            return comparable.CompareTo(other.Value);
        }

        // 否则使用 HashCode 比较
        return Value.GetHashCode().CompareTo(other.Value.GetHashCode());
    }

    private static Boolean IsNumeric(Object value) => value is
        Byte or SByte or Int16 or UInt16 or Int32 or UInt32 or Int64 or UInt64 or Single or Double or Decimal;

    public override Boolean Equals(Object? obj)
    {
        if (obj is ComparableObject co)
            return Value.Equals(co.Value);
        return false;
    }

    public override Int32 GetHashCode()
    {
        return Value.GetHashCode();
    }
}
