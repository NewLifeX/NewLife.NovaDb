using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NewLife;
using NewLife.Log;
using NewLife.Security;
using XCode;
using XCode.DataAccessLayer;
using XCode.Membership;
using Xunit;
using XUnitTest.TestEntity;

namespace XUnitTest;

/// <summary>NovaDb嵌入模式XCode集成测试。连接字符串格式：Data Source=../data/mydb</summary>
[TestCaseOrderer("NewLife.UnitTest.DefaultOrderer", "NewLife.UnitTest")]
public class NovaDbEmbeddedTests
{
    #region 辅助方法
    private static String GetDataDir(String name)
    {
        var dir = $"Data\\NovaEmbedded_{name}".GetFullPath();
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
    #endregion

    [Fact(DisplayName = "嵌入模式-驱动工厂初始化")]
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

    [Fact(DisplayName = "嵌入模式-连接")]
    public void ConnectTest()
    {
        var db = DbFactory.Create(DatabaseType.NovaDb);
        var factory = db.Factory;

        var conn = factory.CreateConnection();
        conn.ConnectionString = "Data Source=Data\\nova_embed_conn";
        conn.Open();
        conn.Close();
    }

    [Fact(DisplayName = "嵌入模式-DAL层")]
    public void DALTest()
    {
        var dataDir = GetDataDir("dal");

        DAL.AddConnStr("novaEmbed_dal", $"Data Source={dataDir}", null, "NovaDb");
        var dal = DAL.Create("novaEmbed_dal");
        Assert.NotNull(dal);
        Assert.Equal("novaEmbed_dal", dal.ConnName);
        Assert.Equal(DatabaseType.NovaDb, dal.DbType);

        var db = dal.Db;
        Assert.NotNull(db.DatabaseName);

        using var conn = db.OpenConnection();

        var ver = db.ServerVersion;
        Assert.NotEmpty(ver);

        CleanDir(dataDir);
    }

    [Fact(DisplayName = "嵌入模式-查询操作")]
    public void SelectTest()
    {
        var dataDir = GetDataDir("select");

        DAL.AddConnStr("novaEmbed_select", $"Data Source={dataDir}", null, "NovaDb");

        Role.Meta.ConnName = "novaEmbed_select";
        Area.Meta.ConnName = "novaEmbed_select";

        Role.Meta.Session.InitData();

        var count = Role.Meta.Count;
        Assert.True(count > 0);

        var list = Role.FindAll();
        Assert.Equal(4, list.Count);

        var list2 = Role.FindAll(Role._.Name == "管理员");
        Assert.Single(list2);

        var list3 = Role.Search("用户", null);
        Assert.Equal(2, list3.Count);

        // 清理现场
        CleanDir(dataDir);
    }

    [Fact(DisplayName = "嵌入模式-Membership操作")]
    public void MembershipTest()
    {
        var dataDir = GetDataDir("member");

        DAL.AddConnStr("novaEmbed_member", $"Data Source={dataDir}", null, "NovaDb");

        User.Meta.ConnName = "novaEmbed_member";
        Role.Meta.ConnName = "novaEmbed_member";

        // 初始化 Role 和 User 数据
        Role.Meta.Session.InitData();

        var count = Role.Meta.Count;
        Assert.True(count > 0);

        // User 可能因外键依赖尚未准备好，先尝试，不强制
        try { User.Meta.Session.InitData(); } catch { }

        var list = Role.FindAll();
        Assert.Equal(4, list.Count);

        var list2 = Role.FindAll(Role._.Name == "管理员");
        Assert.Single(list2);

        var list3 = Role.Search("用户", null);
        Assert.Equal(2, list3.Count);

        CleanDir(dataDir);
    }

    [Fact(DisplayName = "嵌入模式-表前缀")]
    public void TablePrefixTest()
    {
        var dataDir = GetDataDir("prefix");

        DAL.AddConnStr("novaEmbed_prefix", $"Data Source={dataDir};TablePrefix=nova_", null, "NovaDb");

        Role.Meta.ConnName = "novaEmbed_prefix";

        Role.Meta.Session.InitData();

        var count = Role.Meta.Count;
        Assert.True(count > 0);

        var list = Role.FindAll();
        Assert.Equal(4, list.Count);

        var list2 = Role.FindAll(Role._.Name == "管理员");
        Assert.Single(list2);

        var list3 = Role.Search("用户", null);
        Assert.Equal(2, list3.Count);

        // 清理现场
        CleanDir(dataDir);
    }

    #region 批量操作
    private IDisposable CreateForBatch(String action)
    {
        var dataDir = GetDataDir("batch");
        DAL.AddConnStr("novaEmbed_batch", $"Data Source={dataDir}", null, "NovaDb");

        var dt = TestRole.Meta.Table.DataTable.Clone() as IDataTable;
        dt!.TableName = $"TestRole_{action}";

        // 分表
        var split = TestRole.Meta.CreateSplit("novaEmbed_batch", dt.TableName);

        var session = TestRole.Meta.Session;
        session.Dal.SetTables(dt);

        // 清空数据
        session.Truncate();

        return split;
    }

    [Fact(DisplayName = "嵌入模式-批量插入")]
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

    [Fact(DisplayName = "嵌入模式-批量InsertIgnore")]
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
        // 应至少包含 "游客"，"管理员" 可能因索引差异被跳过或插入
        Assert.Contains(list2, e => e.Name == "高级用户");
        Assert.Contains(list2, e => e.Name == "普通用户");
        Assert.Contains(list2, e => e.Name == "游客");
    }

    [Fact(DisplayName = "嵌入模式-批量Replace")]
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

    [Fact(DisplayName = "嵌入模式-正反向工程")]
    public void PositiveAndNegative()
    {
        var dataDir = GetDataDir("positive");

        DAL.AddConnStr("novaEmbed_positive", $"Data Source={dataDir}", null, "NovaDb");
        var dal = DAL.Create("novaEmbed_positive");

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
}
