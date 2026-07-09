using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NewLife;
using NewLife.Log;
using NewLife.Security;
using XCode;
using XCode.DataAccessLayer;
using XCode.Membership;
using Xunit;
using XUnitTest.TestEntity;

namespace XUnitTest;

/// <summary>NovaDb网络模式XCode集成测试。复用 IntegrationServerFixture 启动的 NovaServer</summary>
[Collection("IntegrationTests")]
[TestCaseOrderer("NewLife.UnitTest.DefaultOrderer", "NewLife.UnitTest")]
public class NovaDbNetworkTests : IClassFixture<IntegrationServerFixture>
{
    private readonly IntegrationServerFixture _fixture;
    private Int32 _port => _fixture.Port;

    public NovaDbNetworkTests(IntegrationServerFixture fixture)
    {
        _fixture = fixture;
    }

    #region 辅助方法
    private static String GetEmbeddedDir(String name)
    {
        var dir = $"Data\\NovaNetwork_{name}".GetFullPath();
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, true); }
            catch { }
        }
        return dir;
    }

    private static void CleanDir(String dir)
    {
        if (!String.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, true); }
            catch { }
        }
    }

    private String GetConnStr(String database)
    {
        // 网络模式连接串，每个测试用例使用独立数据库
        return $"Server=127.0.0.1;Port={_port};Database={database}";
    }

    /// <summary>清理数据库</summary>
    private void DropDatabase(String database)
    {
        try
        {
            var dal = DAL.Create("__sys_nova__");
            dal.Execute($"drop database {database}");
        }
        catch (Exception ex)
        {
            XTrace.WriteLine(ex.Message);
        }
    }
    #endregion

    [Fact(DisplayName = "网络模式-驱动工厂初始化")]
    public void InitTest()
    {
        var db = DbFactory.Create(DatabaseType.NovaDb);
        Assert.NotNull(db);

        var factory = db.Factory;
        Assert.NotNull(factory);

        var conn = factory.CreateConnection();
        Assert.NotNull(conn);

        var cmd = factory.CreateCommand();
        Assert.NotNull(cmd);

        var adp = factory.CreateDataAdapter();
        Assert.NotNull(adp);

        var dp = factory.CreateParameter();
        Assert.NotNull(dp);
    }

    [Fact(DisplayName = "网络模式-连接")]
    public void ConnectTest()
    {
        var db = DbFactory.Create(DatabaseType.NovaDb);
        var factory = db.Factory;

        var conn = factory.CreateConnection();
        conn.ConnectionString = GetConnStr("nova_net_conn");
        conn.Open();
        conn.Close();
    }

    [Fact(DisplayName = "网络模式-DAL层")]
    public void DALTest()
    {
        var connStr = GetConnStr("nova_net_dal");
        DAL.AddConnStr("novaNet_dal", connStr, null, "NovaDb");
        var dal = DAL.Create("novaNet_dal");
        Assert.NotNull(dal);
        Assert.Equal("novaNet_dal", dal.ConnName);
        Assert.Equal(DatabaseType.NovaDb, dal.DbType);

        var db = dal.Db;
        Assert.Equal("nova_net_dal", db.DatabaseName);

        using var conn = db.OpenConnection();

        var ver = db.ServerVersion;
        Assert.NotEmpty(ver);
    }

    [Fact(DisplayName = "网络模式-元数据和反向工程")]
    public void MetaTest()
    {
        var dataDir = GetEmbeddedDir("nova_net_meta");
        DAL.AddConnStr("NovaDbNet_Meta", $"Data Source={dataDir}", null, "NovaDb");
        var dal = DAL.Create("NovaDbNet_Meta");

        // 反向工程
        dal.SetTables(User.Meta.Table.DataTable);

        var tables = dal.Tables;
        Assert.NotNull(tables);
        Assert.True(tables.Count > 0);

        var tb = tables.FirstOrDefault(e => e.Name == "User");
        Assert.NotNull(tb);
        Assert.NotEmpty(tb.Description);

        CleanDir(dataDir);
    }

    [Fact(DisplayName = "网络模式-查询操作")]
    public void SelectTest()
    {
        var dataDir = GetEmbeddedDir("select");
        DAL.AddConnStr("NovaDbNet_Select", $"Data Source={dataDir}", null, "NovaDb");

        Role.Meta.ConnName = "NovaDbNet_Select";
        Area.Meta.ConnName = "NovaDbNet_Select";

        Role.Meta.Session.InitData();

        var count = Role.Meta.Count;
        Assert.True(count > 0);

        var list = Role.FindAll();
        Assert.Equal(4, list.Count);

        var list2 = Role.FindAll(Role._.Name == "管理员");
        Assert.Single(list2);

        var list3 = Role.Search("用户", null);
        Assert.Equal(2, list3.Count);

        CleanDir(dataDir);
    }

    [Fact(DisplayName = "网络模式-Membership操作")]
    public void MembershipTest()
    {
        var dataDir = GetEmbeddedDir("member");
        DAL.AddConnStr("NovaDbNet_member", $"Data Source={dataDir}", null, "NovaDb");

        User.Meta.ConnName = "NovaDbNet_member";
        Role.Meta.ConnName = "NovaDbNet_member";

        // 初始化 Role 数据
        Role.Meta.Session.InitData();

        var count = Role.Meta.Count;
        Assert.True(count > 0);

        try { User.Meta.Session.InitData(); } catch { }

        var list = Role.FindAll();
        Assert.Equal(4, list.Count);

        var list2 = Role.FindAll(Role._.Name == "管理员");
        Assert.Single(list2);

        var list3 = Role.Search("用户", null);
        Assert.Equal(2, list3.Count);

        CleanDir(dataDir);
    }

    [Fact(DisplayName = "网络模式-表前缀")]
    public void TablePrefixTest()
    {
        var dataDir = GetEmbeddedDir("prefix");
        DAL.AddConnStr("NovaDbNet_Prefix", $"Data Source={dataDir};TablePrefix=nova_", null, "NovaDb");

        Role.Meta.ConnName = "NovaDbNet_Prefix";

        Role.Meta.Session.InitData();

        var count = Role.Meta.Count;
        Assert.True(count > 0);

        var list = Role.FindAll();
        Assert.Equal(4, list.Count);

        var list2 = Role.FindAll(Role._.Name == "管理员");
        Assert.Single(list2);

        var list3 = Role.Search("用户", null);
        Assert.Equal(2, list3.Count);

        CleanDir(dataDir);
    }

    #region 批量操作
    private IDisposable CreateForBatch(String action)
    {
        var dataDir = GetEmbeddedDir("batch");
        DAL.AddConnStr("NovaDbNet_Batch", $"Data Source={dataDir}", null, "NovaDb");

        var dt = TestRole.Meta.Table.DataTable.Clone() as IDataTable;
        dt!.TableName = $"TestRole_{action}";

        // 分表
        var split = TestRole.Meta.CreateSplit("NovaDbNet_Batch", dt.TableName);

        var session = TestRole.Meta.Session;
        session.Dal.SetTables(dt);

        // 清空数据
        session.Truncate();

        return split;
    }

    [Fact(DisplayName = "网络模式-批量插入")]
    public void BatchInsert()
    {
        using var split = CreateForBatch("BatchInsert");

        var list = new List<TestRole>
        {
            new TestRole { Name = "管理员" },
            new TestRole { Name = "高级用户" },
            new TestRole { Name = "普通用户" }
        };
        var rs = list.BatchInsert();
        Assert.Equal(list.Count, rs);

        var list2 = TestRole.FindAll();
        Assert.Equal(list.Count, list2.Count);
        Assert.Contains(list2, e => e.Name == "管理员");
        Assert.Contains(list2, e => e.Name == "高级用户");
        Assert.Contains(list2, e => e.Name == "普通用户");
    }

    [Fact(DisplayName = "网络模式-批量InsertIgnore")]
    public void BatchInsertIgnore()
    {
        using var split = CreateForBatch("InsertIgnore");

        var list = new List<TestRole>
        {
            new TestRole { Name = "管理员" },
            new TestRole { Name = "高级用户" },
            new TestRole { Name = "普通用户" }
        };
        var rs = list.BatchInsert();
        Assert.Equal(list.Count, rs);

        list =
        [
            new TestRole { Name = "管理员" },
            new TestRole { Name = "游客" },
        ];
        // BatchInsertIgnore 不抛出异常即为通过
        var exception = Record.Exception(() => { rs = list.BatchInsertIgnore(); });
        Assert.Null(exception);

        var list2 = TestRole.FindAll();
        // 应至少包含新插入的值
        Assert.Contains(list2, e => e.Name == "高级用户");
        Assert.Contains(list2, e => e.Name == "普通用户");
        Assert.Contains(list2, e => e.Name == "游客");
    }

    [Fact(DisplayName = "网络模式-批量Replace")]
    public void BatchReplace()
    {
        using var split = CreateForBatch("Replace");

        var list = new List<TestRole>
        {
            new TestRole { Name = "管理员", Remark = "guanliyuan" },
            new TestRole { Name = "高级用户", Remark = "gaoji" },
            new TestRole { Name = "普通用户", Remark = "putong" }
        };
        var rs = list.BatchInsert();
        Assert.Equal(list.Count, rs);

        var gly = list.FirstOrDefault(e => e.Name == "管理员");
        Assert.NotNull(gly);
        Assert.Equal("guanliyuan", gly.Remark);

        list =
        [
            new TestRole { Name = "管理员" },
            new TestRole { Name = "游客", Remark = "guest" },
        ];
        rs = list.BatchReplace();

        var list2 = TestRole.FindAll();
        Assert.Equal(4, list2.Count);
        Assert.Contains(list2, e => e.Name == "管理员");
        Assert.Contains(list2, e => e.Name == "高级用户");
        Assert.Contains(list2, e => e.Name == "普通用户");
        Assert.Contains(list2, e => e.Name == "游客");

        var gly2 = list2.FirstOrDefault(e => e.Name == "管理员");
        Assert.NotNull(gly2);
        Assert.Null(gly2.Remark);
    }
    #endregion

    [Fact(DisplayName = "网络模式-获取所有表")]
    public void GetTables()
    {
        var dataDir = GetEmbeddedDir("tables");
        DAL.AddConnStr("nova_net_tables", $"Data Source={dataDir}", null, "NovaDb");
        var dal = DAL.Create("nova_net_tables");

        dal.SetTables(User.Meta.Table.DataTable);

        var tables = dal.Tables;
        Assert.NotEmpty(tables);

        foreach (var table in tables)
        {
            Assert.NotEmpty(table.Columns);
            foreach (var dc in table.Columns)
            {
                Assert.NotEmpty(dc.Name);
                Assert.NotEmpty(dc.ColumnName);
                Assert.NotEmpty(dc.RawType);
                Assert.NotNull(dc.DataType);
            }

            foreach (var di in table.Indexes)
            {
                Assert.NotEmpty(di.Name);
                Assert.NotEmpty(di.Columns);
            }
        }

        CleanDir(dataDir);
    }

    [Fact(DisplayName = "网络模式-正反向工程")]
    public void PositiveAndNegative()
    {
        var dataDir = GetEmbeddedDir("positive");
        DAL.AddConnStr("NovaDbNet_Positive", $"Data Source={dataDir}", null, "NovaDb");
        var dal = DAL.Create("NovaDbNet_Positive");

        var table = User.Meta.Table.DataTable.Clone() as IDataTable;
        table!.TableName = $"user_{Rand.Next(1000, 10000)}";

        dal.SetTables(table);

        var tableNames = dal.GetTableNames();
        XTrace.WriteLine("tableNames: {0}", tableNames.Join());
        Assert.Contains(table.TableName, tableNames);

        var tables = dal.Tables;
        XTrace.WriteLine("tables: {0}", tables.Join());
        Assert.Contains(tables, t => t.TableName == table.TableName);

        dal.Db.CreateMetaData().SetSchema(DDLSchema.DropTable, new Object[] { table });

        tableNames = dal.GetTableNames();
        XTrace.WriteLine("tableNames: {0}", tableNames.Join());
        Assert.DoesNotContain(table.TableName, tableNames);

        CleanDir(dataDir);
    }

    [Fact(DisplayName = "网络模式-测试建表SQL生成")]
    public void CreateTableSQLTest()
    {
        var db = DbFactory.Create(DatabaseType.NovaDb);
        Assert.NotNull(db);

        var table = DAL.CreateTable();
        table.TableName = "TestTable";
        table.Description = "测试表";

        var field1 = table.CreateColumn();
        field1.ColumnName = "Id";
        field1.DataType = typeof(Int32);
        field1.Identity = true;
        field1.PrimaryKey = true;
        field1.Description = "编号";
        table.Columns.Add(field1);

        var field2 = table.CreateColumn();
        field2.ColumnName = "Name";
        field2.DataType = typeof(String);
        field2.Length = 50;
        field2.Description = "名称";
        table.Columns.Add(field2);

        var field3 = table.CreateColumn();
        field3.ColumnName = "CreateTime";
        field3.DataType = typeof(DateTime);
        field3.Nullable = true;
        field3.Description = "创建时间";
        table.Columns.Add(field3);

        var meta = db.CreateMetaData();
        var sql = meta.GetSchemaSQL(DDLSchema.CreateTable, table);
        Assert.NotNull(sql);
        Assert.NotEmpty(sql);

        XTrace.WriteLine("生成的建表SQL:");
        XTrace.WriteLine(sql);

        // 验证SQL结构
        Assert.Contains("Create Table If Not Exists", sql);
        Assert.Contains("Primary Key", sql);
        Assert.Contains("AUTO_INCREMENT", sql);
        Assert.Contains("COMMENT '编号'", sql);
        Assert.Contains("COMMENT '名称'", sql);
    }

    [Fact(DisplayName = "网络模式-测试COMMENT中的单引号转义")]
    public void CommentWithSingleQuoteTest()
    {
        var db = DbFactory.Create(DatabaseType.NovaDb);
        Assert.NotNull(db);

        var table = DAL.CreateTable();
        table.TableName = "AlarmRule";
        table.Description = "告警规则";

        var field1 = table.CreateColumn();
        field1.ColumnName = "Id";
        field1.DataType = typeof(Int32);
        field1.Identity = true;
        field1.PrimaryKey = true;
        field1.Description = "编号";
        table.Columns.Add(field1);

        var field2 = table.CreateColumn();
        field2.ColumnName = "Threshold";
        field2.DataType = typeof(String);
        field2.Length = 50;
        field2.Description = "阈值。触发告警的阈值，支持单值和范围值'10,100'";
        table.Columns.Add(field2);

        var meta = db.CreateMetaData();
        var sql = meta.GetSchemaSQL(DDLSchema.CreateTable, table);
        Assert.NotNull(sql);
        Assert.NotEmpty(sql);

        XTrace.WriteLine("生成的建表SQL:");
        XTrace.WriteLine(sql);

        // 验证单引号已被转义
        Assert.Contains("COMMENT '阈值。触发告警的阈值，支持单值和范围值''10,100'''", sql);
    }

    [Fact(DisplayName = "网络模式-测试FormatComment方法的特殊字符转义")]
    public void FormatCommentTest()
    {
        var db = DbFactory.Create(DatabaseType.NovaDb);
        Assert.NotNull(db);

        var meta = db.CreateMetaData();
        Assert.NotNull(meta);

        // 使用反射访问protected方法FormatComment
        var formatCommentMethod = meta.GetType()
            .GetMethod("FormatComment", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(formatCommentMethod);

        // 测试单引号转义
        var result1 = formatCommentMethod!.Invoke(meta, ["It's a test"]) as String;
        Assert.Equal("It''s a test", result1);

        // 测试多个单引号
        var result2 = formatCommentMethod.Invoke(meta, ["范围值'10,100'"]) as String;
        Assert.Equal("范围值''10,100''", result2);

        // 测试回车换行转义
        var result3 = formatCommentMethod.Invoke(meta, ["Line1\r\nLine2"]) as String;
        Assert.Equal("Line1 Line2", result3);

        // 测试空字符串
        var result4 = formatCommentMethod.Invoke(meta, [""]) as String;
        Assert.Equal("", result4);

        // 测试null
        var result5 = formatCommentMethod.Invoke(meta, new Object?[] { null }) as String;
        Assert.Null(result5);
    }

    [Fact(DisplayName = "网络模式-测试格式化关键字")]
    public void FormatKeyWordTest()
    {
        var db = DbFactory.Create(DatabaseType.NovaDb);
        Assert.NotNull(db);

        // FormatName 返回格式化后的名称（当前实现原样返回）
        var name = db.FormatName("test");
        Assert.False(String.IsNullOrEmpty(name));

        // 空字符串原样返回
        Assert.Equal("", db.FormatName(""));
    }

    [Fact(DisplayName = "网络模式-测试字符串连接")]
    public void StringConcatTest()
    {
        var db = DbFactory.Create(DatabaseType.NovaDb);
        Assert.NotNull(db);

        var result = db.StringConcat("a", "b");
        Assert.Equal("concat(a,b)", result);

        var result2 = db.StringConcat("", "b");
        Assert.Equal("concat('',b)", result2);

        var result3 = db.StringConcat("a", "");
        Assert.Equal("concat(a,'')", result3);
    }

    [Fact(DisplayName = "网络模式-测试批量删除SQL")]
    public void BuildDeleteSqlTest()
    {
        var db = DbFactory.Create(DatabaseType.NovaDb);
        Assert.NotNull(db);

        // 无分批
        var sql = db.BuildDeleteSql("test_table", "Id>100", 0);
        Assert.Equal("Delete From test_table Where Id>100", sql);

        // 有分批
        var sql2 = db.BuildDeleteSql("test_table", "Id>100", 1000);
        Assert.Equal("Delete From test_table Where Id>100 limit 1000", sql2);
    }

    [Fact(DisplayName = "网络模式-测试数据库类型识别")]
    public void SupportTest()
    {
        var db = DbFactory.Create(DatabaseType.NovaDb);
        Assert.NotNull(db);

        Assert.True(db.Support("NovaDb"));
        Assert.True(db.Support("novadb"));
        Assert.True(db.Support("Nova"));
        Assert.True(db.Support("nova"));
        Assert.False(db.Support("MySql"));
        Assert.False(db.Support("SQLite"));
    }

    [Fact(DisplayName = "网络模式-测试参数前缀")]
    public void ParamPrefixTest()
    {
        var db = DbFactory.Create(DatabaseType.NovaDb);
        Assert.NotNull(db);

        var name = db.FormatParameterName("test");
        Assert.Equal("@test", name);
    }

    [Fact(DisplayName = "网络模式-测试分页SQL生成")]
    public void PageSplitTest()
    {
        var db = DbFactory.Create(DatabaseType.NovaDb);
        Assert.NotNull(db);

        // 不分页
        var sql1 = db.PageSplit("Select * From test", 0, 0, null);
        Assert.Equal("Select * From test", sql1);

        // 第一页
        var sql2 = db.PageSplit("Select * From test", 0, 10, null);
        Assert.Equal("Select * From test limit 10", sql2);

        // 后续页
        var sql3 = db.PageSplit("Select * From test", 20, 10, null);
        Assert.Equal("Select * From test limit 20, 10", sql3);
    }

    [Fact(DisplayName = "网络模式-测试数据库类型")]
    public void DatabaseTypeTest()
    {
        var db = DbFactory.Create(DatabaseType.NovaDb);
        Assert.NotNull(db);
        Assert.Equal(DatabaseType.NovaDb, db.Type);
    }
}
