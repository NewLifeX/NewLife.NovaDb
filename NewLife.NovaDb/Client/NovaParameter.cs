using System.Collections;
using System.Data;
using System.Data.Common;

namespace NewLife.NovaDb.Client;

#pragma warning disable CS8765 // Nullability of parameter doesn't match overridden member

/// <summary>NovaDb ADO.NET 参数</summary>
public class NovaParameter : DbParameter
{
    /// <summary>参数数据类型</summary>
    public override DbType DbType { get; set; }

    /// <summary>参数方向</summary>
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    /// <summary>是否可为空</summary>
    public override Boolean IsNullable { get; set; }

    /// <summary>参数名称</summary>
    public override String ParameterName { get; set; } = String.Empty;

    /// <summary>参数大小</summary>
    public override Int32 Size { get; set; }

    /// <summary>源列名</summary>
    public override String SourceColumn { get; set; } = String.Empty;

    /// <summary>源列空值映射</summary>
    public override Boolean SourceColumnNullMapping { get; set; }

    /// <summary>参数值</summary>
    public override Object? Value { get; set; }

    /// <summary>数据版本</summary>
    public override DataRowVersion SourceVersion { get; set; }

    /// <summary>重置数据类型</summary>
    public override void ResetDbType() => DbType = DbType.String;
}

/// <summary>NovaDb 参数集合</summary>
public class NovaParameterCollection : DbParameterCollection
{
    private readonly List<NovaParameter> _parameters = [];

    /// <summary>参数数量</summary>
    public override Int32 Count => _parameters.Count;

    /// <summary>同步根对象</summary>
    public override Object SyncRoot { get; } = new Object();

    /// <summary>是否固定大小</summary>
    public override Boolean IsFixedSize => false;

    /// <summary>是否只读</summary>
    public override Boolean IsReadOnly => false;

    /// <summary>是否同步访问</summary>
    public override Boolean IsSynchronized => false;

    /// <summary>添加参数</summary>
    /// <param name="value">参数对象</param>
    /// <returns>参数索引</returns>
    public override Int32 Add(Object value)
    {
        _parameters.Add((NovaParameter)value);
        return _parameters.Count - 1;
    }

    /// <summary>批量添加参数</summary>
    /// <param name="values">参数数组</param>
    public override void AddRange(Array values)
    {
        foreach (NovaParameter p in values)
        {
            _parameters.Add(p);
        }
    }

    /// <summary>清空参数</summary>
    public override void Clear() => _parameters.Clear();

    /// <summary>是否包含指定参数</summary>
    /// <param name="value">参数对象</param>
    /// <returns>是否包含</returns>
    public override Boolean Contains(Object value) => _parameters.Contains((NovaParameter)value);

    /// <summary>是否包含指定名称的参数</summary>
    /// <param name="value">参数名称</param>
    /// <returns>是否包含</returns>
    public override Boolean Contains(String value) => IndexOf(value) >= 0;

    /// <summary>复制到数组</summary>
    /// <param name="array">目标数组</param>
    /// <param name="index">起始索引</param>
    public override void CopyTo(Array array, Int32 index) => ((ICollection)_parameters).CopyTo(array, index);

    /// <summary>获取枚举器</summary>
    /// <returns>枚举器</returns>
    public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();

    /// <summary>获取参数索引</summary>
    /// <param name="value">参数对象</param>
    /// <returns>索引，未找到返回 -1</returns>
    public override Int32 IndexOf(Object value) => _parameters.IndexOf((NovaParameter)value);

    /// <summary>按名称获取参数索引</summary>
    /// <param name="parameterName">参数名称</param>
    /// <returns>索引，未找到返回 -1</returns>
    public override Int32 IndexOf(String parameterName)
    {
        for (var i = 0; i < _parameters.Count; i++)
        {
            if (String.Equals(_parameters[i].ParameterName, parameterName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    /// <summary>插入参数</summary>
    /// <param name="index">插入位置</param>
    /// <param name="value">参数对象</param>
    public override void Insert(Int32 index, Object value) => _parameters.Insert(index, (NovaParameter)value);

    /// <summary>移除参数</summary>
    /// <param name="value">参数对象</param>
    public override void Remove(Object value) => _parameters.Remove((NovaParameter)value);

    /// <summary>按索引移除参数</summary>
    /// <param name="index">参数索引</param>
    public override void RemoveAt(Int32 index) => _parameters.RemoveAt(index);

    /// <summary>按名称移除参数</summary>
    /// <param name="parameterName">参数名称</param>
    public override void RemoveAt(String parameterName)
    {
        var index = IndexOf(parameterName);
        if (index >= 0)
            _parameters.RemoveAt(index);
    }

    /// <summary>按索引获取参数</summary>
    /// <param name="index">参数索引</param>
    /// <returns>参数实例</returns>
    protected override DbParameter GetParameter(Int32 index) => _parameters[index];

    /// <summary>按名称获取参数</summary>
    /// <param name="parameterName">参数名称</param>
    /// <returns>参数实例</returns>
    protected override DbParameter GetParameter(String parameterName)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
            throw new KeyNotFoundException($"Parameter '{parameterName}' not found");

        return _parameters[index];
    }

    /// <summary>按索引设置参数</summary>
    /// <param name="index">参数索引</param>
    /// <param name="value">参数实例</param>
    protected override void SetParameter(Int32 index, DbParameter value) => _parameters[index] = (NovaParameter)value;

    /// <summary>按名称设置参数</summary>
    /// <param name="parameterName">参数名称</param>
    /// <param name="value">参数实例</param>
    protected override void SetParameter(String parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
            throw new KeyNotFoundException($"Parameter '{parameterName}' not found");

        _parameters[index] = (NovaParameter)value;
    }
}
