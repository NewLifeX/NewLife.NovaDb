namespace NewLife.NovaDb.Engine;

/// <summary>
/// 对象包装器，用于 SkipList 键比较
/// </summary>
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

        // 如果两者都实现了 IComparable，使用它
        if (Value is IComparable comparable && other.Value is IComparable)
        {
            return comparable.CompareTo(other.Value);
        }

        // 否则使用 HashCode 比较
        return Value.GetHashCode().CompareTo(other.Value.GetHashCode());
    }

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
