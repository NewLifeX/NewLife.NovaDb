using System.Collections;
using System.Data.Common;

namespace NewLife.NovaDb.Client;

/// <summary>NovaDb ADO.NET 数据读取器</summary>
public class NovaDataReader : DbDataReader
{
    private readonly List<Object?[]> _rows = [];
    private readonly List<String> _columnNames = [];
    private Int32 _currentRow = -1;
    private Boolean _isClosed;

    /// <summary>字段数量</summary>
    public override Int32 FieldCount => _columnNames.Count;

    /// <summary>是否有数据行</summary>
    public override Boolean HasRows => _rows.Count > 0;

    /// <summary>是否已关闭</summary>
    public override Boolean IsClosed => _isClosed;

    /// <summary>受影响行数</summary>
    public override Int32 RecordsAffected => -1;

    /// <summary>嵌套深度</summary>
    public override Int32 Depth => 0;

    /// <summary>按索引获取值</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>列值</returns>
    public override Object this[Int32 ordinal] => GetValue(ordinal);

    /// <summary>按名称获取值</summary>
    /// <param name="name">列名</param>
    /// <returns>列值</returns>
    public override Object this[String name] => GetValue(GetOrdinal(name));

    /// <summary>设置列名</summary>
    /// <param name="columns">列名数组</param>
    public void SetColumns(params String[] columns)
    {
        _columnNames.Clear();
        _columnNames.AddRange(columns);
    }

    /// <summary>添加数据行</summary>
    /// <param name="row">行数据</param>
    public void AddRow(Object?[] row)
    {
        if (row == null) throw new ArgumentNullException(nameof(row));
        _rows.Add(row);
    }

    /// <summary>读取下一行</summary>
    /// <returns>是否还有数据</returns>
    public override Boolean Read()
    {
        if (_isClosed) return false;

        _currentRow++;
        return _currentRow < _rows.Count;
    }

    /// <summary>移动到下一个结果集</summary>
    /// <returns>是否有下一个结果集</returns>
    public override Boolean NextResult() => false;

    /// <summary>关闭读取器</summary>
    public override void Close() => _isClosed = true;

    /// <summary>获取列名</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>列名</returns>
    public override String GetName(Int32 ordinal) => _columnNames[ordinal];

    /// <summary>获取列索引</summary>
    /// <param name="name">列名</param>
    /// <returns>列索引</returns>
    public override Int32 GetOrdinal(String name)
    {
        for (var i = 0; i < _columnNames.Count; i++)
        {
            if (String.Equals(_columnNames[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        throw new IndexOutOfRangeException($"Column '{name}' not found");
    }

    /// <summary>获取值</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>列值</returns>
    public override Object GetValue(Int32 ordinal) => _rows[_currentRow][ordinal] ?? DBNull.Value;

    /// <summary>获取所有列值</summary>
    /// <param name="values">目标数组</param>
    /// <returns>实际填充的列数</returns>
    public override Int32 GetValues(Object[] values)
    {
        var row = _rows[_currentRow];
        var count = Math.Min(values.Length, row.Length);
        for (var i = 0; i < count; i++)
        {
            values[i] = row[i] ?? DBNull.Value;
        }

        return count;
    }

    /// <summary>判断列值是否为 DBNull</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>是否为 DBNull</returns>
    public override Boolean IsDBNull(Int32 ordinal) => _rows[_currentRow][ordinal] == null;

    /// <summary>获取布尔值</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>布尔值</returns>
    public override Boolean GetBoolean(Int32 ordinal) => Convert.ToBoolean(GetValue(ordinal));

    /// <summary>获取字节值</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>字节值</returns>
    public override Byte GetByte(Int32 ordinal) => Convert.ToByte(GetValue(ordinal));

    /// <summary>获取字节数组</summary>
    /// <param name="ordinal">列索引</param>
    /// <param name="dataOffset">数据偏移</param>
    /// <param name="buffer">目标缓冲区</param>
    /// <param name="bufferOffset">缓冲区偏移</param>
    /// <param name="length">读取长度</param>
    /// <returns>实际读取的字节数</returns>
    public override Int64 GetBytes(Int32 ordinal, Int64 dataOffset, Byte[]? buffer, Int32 bufferOffset, Int32 length)
    {
        if (buffer == null) return 0;

        var data = (Byte[])GetValue(ordinal);
        var count = Math.Min(length, data.Length - (Int32)dataOffset);
        Array.Copy(data, (Int32)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    /// <summary>获取字符值</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>字符值</returns>
    public override Char GetChar(Int32 ordinal) => Convert.ToChar(GetValue(ordinal));

    /// <summary>获取字符数组</summary>
    /// <param name="ordinal">列索引</param>
    /// <param name="dataOffset">数据偏移</param>
    /// <param name="buffer">目标缓冲区</param>
    /// <param name="bufferOffset">缓冲区偏移</param>
    /// <param name="length">读取长度</param>
    /// <returns>实际读取的字符数</returns>
    public override Int64 GetChars(Int32 ordinal, Int64 dataOffset, Char[]? buffer, Int32 bufferOffset, Int32 length)
    {
        if (buffer == null) return 0;

        var str = GetString(ordinal);
        var count = Math.Min(length, str.Length - (Int32)dataOffset);
        str.CopyTo((Int32)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    /// <summary>获取 DateTime 值</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>日期时间值</returns>
    public override DateTime GetDateTime(Int32 ordinal) => Convert.ToDateTime(GetValue(ordinal));

    /// <summary>获取 Decimal 值</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>Decimal 值</returns>
    public override Decimal GetDecimal(Int32 ordinal) => Convert.ToDecimal(GetValue(ordinal));

    /// <summary>获取 Double 值</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>Double 值</returns>
    public override Double GetDouble(Int32 ordinal) => Convert.ToDouble(GetValue(ordinal));

    /// <summary>获取字段类型</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>字段类型</returns>
    public override Type GetFieldType(Int32 ordinal)
    {
        if (_currentRow >= 0 && _currentRow < _rows.Count)
        {
            var val = _rows[_currentRow][ordinal];
            if (val != null) return val.GetType();
        }

        return typeof(Object);
    }

    /// <summary>获取 Float 值</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>Float 值</returns>
    public override Single GetFloat(Int32 ordinal) => Convert.ToSingle(GetValue(ordinal));

    /// <summary>获取 Guid 值</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>Guid 值</returns>
    public override Guid GetGuid(Int32 ordinal) => Guid.Parse(GetValue(ordinal).ToString()!);

    /// <summary>获取 Int16 值</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>Int16 值</returns>
    public override Int16 GetInt16(Int32 ordinal) => Convert.ToInt16(GetValue(ordinal));

    /// <summary>获取 Int32 值</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>Int32 值</returns>
    public override Int32 GetInt32(Int32 ordinal) => Convert.ToInt32(GetValue(ordinal));

    /// <summary>获取 Int64 值</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>Int64 值</returns>
    public override Int64 GetInt64(Int32 ordinal) => Convert.ToInt64(GetValue(ordinal));

    /// <summary>获取字符串值</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>字符串值</returns>
    public override String GetString(Int32 ordinal) => GetValue(ordinal).ToString()!;

    /// <summary>获取列数据类型名称</summary>
    /// <param name="ordinal">列索引</param>
    /// <returns>类型名称</returns>
    public override String GetDataTypeName(Int32 ordinal) => GetFieldType(ordinal).Name;

    /// <summary>获取枚举器</summary>
    /// <returns>枚举器</returns>
    public override IEnumerator GetEnumerator() => new DbEnumerator(this);
}
