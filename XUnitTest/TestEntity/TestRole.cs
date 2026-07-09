#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.Serialization;
using NewLife;
using NewLife.Data;
using XCode;
using XCode.Configuration;
using XCode.DataAccessLayer;

namespace XUnitTest.TestEntity;

/// <summary>测试角色。用于 NovaDb XCode 集成测试中的批量操作</summary>
[Serializable]
[DataObject]
[Description("测试角色")]
[BindIndex("IU_TestRole_Name", true, "Name")]
[BindTable("TestRole", Description = "测试角色", ConnName = "NovaDbTest", DbType = DatabaseType.None)]
public class TestRole : Entity<TestRole>
{
    #region 属性
    private Int32 _ID;
    /// <summary>编号</summary>
    [DisplayName("编号")]
    [Description("编号")]
    [DataObjectField(true, true, false, 0)]
    [BindColumn("ID", "编号", "")]
    public Int32 ID { get => _ID; set { if (OnPropertyChanging("ID", value)) { _ID = value; OnPropertyChanged("ID"); } } }

    private String _Name = null!;
    /// <summary>名称</summary>
    [DisplayName("名称")]
    [Description("名称")]
    [DataObjectField(false, false, false, 50)]
    [BindColumn("Name", "名称", "", Master = true)]
    public String Name { get => _Name; set { if (OnPropertyChanging("Name", value)) { _Name = value; OnPropertyChanged("Name"); } } }

    private String? _Remark;
    /// <summary>说明</summary>
    [DisplayName("说明")]
    [Description("说明")]
    [DataObjectField(false, false, true, 200)]
    [BindColumn("Remark", "说明", "")]
    public String? Remark { get => _Remark; set { if (OnPropertyChanging("Remark", value)) { _Remark = value; OnPropertyChanged("Remark"); } } }
    #endregion

    #region 获取/设置 字段值
    /// <summary>获取/设置 字段值</summary>
    /// <param name="name">字段名</param>
    /// <returns>字段值</returns>
    public override Object? this[String name]
    {
        get => name switch
        {
            "ID" => _ID,
            "Name" => _Name,
            "Remark" => _Remark,
            _ => base[name]
        };
        set
        {
            switch (name)
            {
                case "ID": _ID = value.ToInt(); break;
                case "Name": _Name = Convert.ToString(value); break;
                case "Remark": _Remark = Convert.ToString(value); break;
                default: base[name] = value; break;
            }
        }
    }
    #endregion

    #region 字段名
    /// <summary>字段名</summary>
    public partial class _
    {
        /// <summary>编号</summary>
        public static readonly Field ID = FindByName("ID");

        /// <summary>名称</summary>
        public static readonly Field Name = FindByName("Name");

        /// <summary>说明</summary>
        public static readonly Field Remark = FindByName("Remark");

        static Field FindByName(String name) => Meta.Table.FindByName(name);
    }
    #endregion
}
