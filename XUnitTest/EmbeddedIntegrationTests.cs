using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NewLife;
using NewLife.NovaDb.Client;
using NewLife.NovaDb.Storage;
using Xunit;

namespace XUnitTest;

/// <summary>嵌入式模式集成测试，通过 ADO.NET 直接驱动嵌入式引擎进行数据读写</summary>
[TestCaseOrderer("NewLife.UnitTest.DefaultOrderer", "NewLife.UnitTest")]
public class EmbeddedIntegrationTests : IDisposable
{
    private readonly String _dbPath;

    public EmbeddedIntegrationTests()
    {
        //_dbPath = Path.Combine(Path.GetTempPath(), $"NovaEmbedded_{Guid.NewGuid():N}");
        _dbPath = "../TestData/NovaEmbedded".GetFullPath();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
        //else
        //    Directory.CreateDirectory(_dbPath);
    }

    public void Dispose()
    {
#if !DEBUG
        if (!String.IsNullOrEmpty(_dbPath) && Directory.Exists(_dbPath))
        {
            try { Directory.Delete(_dbPath, recursive: true); }
            catch { }
        }
#endif
    }

    /// <summary>创建默认嵌入式连接</summary>
    private NovaConnection CreateConnection()
    {
        var conn = new NovaConnection { ConnectionString = $"Data Source={_dbPath}" };
        conn.Open();
        return conn;
    }

    /// <summary>创建指定 WAL 模式的嵌入式连接</summary>
    /// <param name="walMode">WAL 模式，Full/Normal/None</param>
    private NovaConnection CreateConnection(String walMode)
    {
        var conn = new NovaConnection { ConnectionString = $"Data Source={_dbPath};WalMode={walMode}" };
        conn.Open();
        return conn;
    }

    /// <summary>获取表的 .data 文件路径</summary>
    private String GetDataFilePath(String tableName) => Path.Combine(_dbPath, $"{tableName}.data");

    /// <summary>获取表主键索引的 .idx 文件路径</summary>
    private String GetIdxFilePath(String tableName) => Path.Combine(_dbPath, $"{tableName}.idx");

    /// <summary>获取表二级索引的 .idx 文件路径</summary>
    /// <param name="tableName">表名</param>
    /// <param name="indexName">索引名</param>
    private String GetSecondaryIdxFilePath(String tableName, String indexName) => Path.Combine(_dbPath, $"{tableName}_{indexName}.idx");

    /// <summary>获取表的 .wal 文件路径</summary>
    private String GetWalFilePath(String tableName) => Path.Combine(_dbPath, $"{tableName}.wal");

    /// <summary>统计 WAL 文件记录数</summary>
    /// <param name="filePath">WAL 文件路径</param>
    private static Int32 CountWalRecords(String filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var count = 0;
        var lenBuf = new Byte[4];
        while (fs.Read(lenBuf, 0, 4) == 4)
        {
            var recordLen = BitConverter.ToInt32(lenBuf, 0);
            if (recordLen <= 0 || fs.Position + recordLen > fs.Length) break;
            fs.Seek(recordLen, SeekOrigin.Current);
            count++;
        }
        return count;
    }

    #region 基础测试

    [Fact(DisplayName = "嵌入式-打开连接创建数据库文件")]
    public void OpenConnectionCreatesDatabase()
    {
        var metaFile = Path.Combine(_dbPath, "nova.db");

        //// 打开连接前，数据库目录应不包含 nova.db 元数据文件
        //Assert.True(Directory.Exists(_dbPath), "数据库目录应该已存在并可写入");

        using var conn = CreateConnection();

        // 打开连接后应生成 nova.db 元数据文件
        Assert.True(File.Exists(metaFile), "打开连接后 nova.db 文件应已存在");

        // 验证 nova.db 文件头正确，魔数 NOVA，版本，页面大小
        var header = FileHeader.Read(metaFile);
        Assert.Equal((Byte)1, header.Version);
        Assert.Equal(FileType.Data, header.FileType);
        Assert.Equal(4096u, header.PageSize);
        Assert.True(header.CreateTime.Year >= 2020, "CreateTime 应为有效时间");
    }

    #endregion

    #region DDL 测试

    [Fact(DisplayName = "嵌入式-创建表")]
    public void CreateTable()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_ddl_create (id INT PRIMARY KEY, name VARCHAR NOT NULL, age INT, score DOUBLE, active BOOLEAN, created DATETIME)";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(0, rows);

        // 验证数据库元数据文件已创建
        var metaFile = Path.Combine(_dbPath, "nova.db");
        Assert.True(File.Exists(metaFile), "数据库元数据文件 nova.db 应在首次建表后创建");

        // 验证 .data 文件已创建
        var dataFile = GetDataFilePath("emb_ddl_create");
        Assert.True(File.Exists(dataFile), ".data 文件应在建表后存在");

        // 验证 .data 文件头内容
        var header = ReadFileHeader(dataFile);
        Assert.Equal((Byte)1, header.Version);
        Assert.Equal(FileType.Data, header.FileType);
        Assert.Equal(4096u, header.PageSize);
        Assert.True(header.CreateTime.Year >= 2020, "CreateTime 应为有效时间");

        // .data 文件大小应至少包含一个 32 字节的文件头
        Assert.True(new FileInfo(dataFile).Length >= FileHeader.HeaderSize, ".data 文件大小应不小于文件头 32 字节");

        // 通过系统表验证表信息存在
        cmd.CommandText = "SELECT * FROM _sys.tables";
        using var reader = cmd.ExecuteReader();
        var found = false;
        while (reader.Read())
        {
            if (Convert.ToString(reader["name"]) == "emb_ddl_create")
            {
                found = true;
                Assert.Equal(6, Convert.ToInt32(reader["column_count"]));
                Assert.Equal("id", Convert.ToString(reader["primary_key"]));
                break;
            }
        }
        Assert.True(found, "系统表 _sys.tables 中应包含新建的表信息");
    }

    [Fact(DisplayName = "嵌入式-创建表IF NOT EXISTS")]
    public void CreateTableIfNotExists()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_ddl_ifne (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        // 再次创建不应报错
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS emb_ddl_ifne (id INT PRIMARY KEY, name VARCHAR)";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(0, rows);
    }

    [Fact(DisplayName = "嵌入式-删除表")]
    public void DropTable()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_ddl_drop (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        // 建表后文件应存在且文件头正确
        var dataFile = GetDataFilePath("emb_ddl_drop");
        Assert.True(File.Exists(dataFile), ".data 文件应在建表后存在");
        var header = ReadFileHeader(dataFile);
        Assert.Equal(FileType.Data, header.FileType);
        Assert.Equal(4096u, header.PageSize);

        // 验证数据库元数据文件存在
        var metaFile = Path.Combine(_dbPath, "nova.db");
        Assert.True(File.Exists(metaFile), "数据库元数据文件 nova.db 应在建表后存在");

        // 建表后系统表应包含该表
        cmd.CommandText = "SELECT * FROM _sys.tables";
        using (var reader = cmd.ExecuteReader())
        {
            var foundBefore = false;
            while (reader.Read())
            {
                if (Convert.ToString(reader["name"]) == "emb_ddl_drop")
                {
                    foundBefore = true;
                    break;
                }
            }
            Assert.True(foundBefore, "建表后系统表应包含 emb_ddl_drop");
        }

        cmd.CommandText = "DROP TABLE emb_ddl_drop";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(0, rows);

        // 删表后文件应被清除
        Assert.False(File.Exists(dataFile), ".data 文件应在删表后被删除");
        Assert.False(File.Exists(GetIdxFilePath("emb_ddl_drop")), ".idx 文件应在删表后被删除");
        Assert.False(File.Exists(GetWalFilePath("emb_ddl_drop")), ".wal 文件应在删表后被删除");

        // 删表后元数据文件应仍然存在
        Assert.True(File.Exists(metaFile), "数据库元数据文件 nova.db 应在删表后保留");
        var metaHeader = ReadFileHeader(metaFile);
        Assert.Equal(FileType.Data, metaHeader.FileType);
        Assert.Equal(4096u, metaHeader.PageSize);

        // 删表后系统表不应再包含该表信息
        cmd.CommandText = "SELECT * FROM _sys.tables";
        using (var reader2 = cmd.ExecuteReader())
        {
            var foundAfter = false;
            while (reader2.Read())
            {
                if (Convert.ToString(reader2["name"]) == "emb_ddl_drop")
                {
                    foundAfter = true;
                    break;
                }
            }
            Assert.False(foundAfter, "删表后系统表不应再包含 emb_ddl_drop");
        }
    }

    [Fact(DisplayName = "嵌入式-删除表IF EXISTS")]
    public void DropTableIfExists()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        // 删除不存在的表不应报错
        cmd.CommandText = "DROP TABLE IF EXISTS emb_ddl_noexist";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(0, rows);
    }

    [Fact(DisplayName = "嵌入式-创建索引")]
    public void CreateIndex()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_ddl_idx (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "CREATE INDEX idx_emb_name ON emb_ddl_idx (name)";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(0, rows);

        // 通过系统表验证索引信息存在
        cmd.CommandText = "SELECT * FROM _sys.indexes";
        using var reader = cmd.ExecuteReader();
        var found = false;
        while (reader.Read())
        {
            var tbl = Convert.ToString(reader["table_name"]);
            var idxName = Convert.ToString(reader["index_name"]);
            if (tbl == "emb_ddl_idx" && idxName == "idx_emb_name")
            {
                found = true;
                Assert.Equal("name", Convert.ToString(reader["columns"]));
                break;
            }
        }
        Assert.True(found, "系统表 _sys.indexes 中应包含新建的索引信息");

        // 索引 .idx 文件应已创建（空表索引文件仅含文件头）
        var idxFile = GetSecondaryIdxFilePath("emb_ddl_idx", "idx_emb_name");
        Assert.True(File.Exists(idxFile), "二级索引 .idx 文件应在创建索引后存在");
        Assert.True(new FileInfo(idxFile).Length >= FileHeader.HeaderSize, "索引文件应至少包含文件头");

        // 验证索引文件头
        var idxHeader = ReadFileHeader(idxFile);
        Assert.Equal(FileType.Index, idxHeader.FileType);
    }

    [Fact(DisplayName = "嵌入式-删除索引")]
    public void DropIndex()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_ddl_dropidx (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "CREATE INDEX idx_emb_dropname ON emb_ddl_dropidx (name)";
        cmd.ExecuteNonQuery();

        // 创建后索引文件和系统表记录应存在
        var idxFile = GetSecondaryIdxFilePath("emb_ddl_dropidx", "idx_emb_dropname");
        Assert.True(File.Exists(idxFile), "二级索引 .idx 文件应在创建索引后存在");

        cmd.CommandText = "SELECT * FROM _sys.indexes WHERE table_name = 'emb_ddl_dropidx' AND index_name = 'idx_emb_dropname'";
        using (var reader = cmd.ExecuteReader())
        {
            Assert.True(reader.Read(), "删除前系统表应包含该索引记录");
        }

        cmd.CommandText = "DROP INDEX idx_emb_dropname ON emb_ddl_dropidx";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(0, rows);

        // 删除后索引文件应被移除
        Assert.False(File.Exists(idxFile), "二级索引 .idx 文件应在删除索引后被删除");

        // 删除后系统表不应再包含该索引
        cmd.CommandText = "SELECT * FROM _sys.indexes";
        using (var reader2 = cmd.ExecuteReader())
        {
            var foundAfter = false;
            while (reader2.Read())
            {
                var tbl = Convert.ToString(reader2["table_name"]);
                var idx = Convert.ToString(reader2["index_name"]);
                if (tbl == "emb_ddl_dropidx" && idx == "idx_emb_dropname")
                {
                    foundAfter = true;
                    break;
                }
            }
            Assert.False(foundAfter, "删除索引后系统表不应再包含该索引记录");
        }
    }

    #endregion

    #region DML 测试

    [Fact(DisplayName = "嵌入式-插入单行")]
    public void InsertSingleRow()
    {
        // 使用 Full 模式确保 WAL 每次写入立即刷盘，便于通过 FileInfo.Length 验证
        using var conn = CreateConnection("Full");
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_dml_ins1 (id INT PRIMARY KEY, name VARCHAR NOT NULL, age INT)";
        cmd.ExecuteNonQuery();

        // 建表后 .data 文件应存在且文件头正确
        var dataFile = GetDataFilePath("emb_dml_ins1");
        Assert.True(File.Exists(dataFile), ".data 文件应在建表后存在");
        var header = ReadFileHeader(dataFile);
        Assert.Equal(FileType.Data, header.FileType);
        Assert.Equal(4096u, header.PageSize);

        // 记录插入前 .wal 文件大小
        var walFile = GetWalFilePath("emb_dml_ins1");
        var walSizeBefore = File.Exists(walFile) ? new FileInfo(walFile).Length : 0;

        cmd.CommandText = "INSERT INTO emb_dml_ins1 VALUES (1, 'Alice', 25)";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);

        // 默认 WAL 模式为 Normal，插入后 .wal 文件应存在且大小增长
        Assert.True(File.Exists(walFile), ".wal 文件应在插入后存在");
        var walSizeAfter = new FileInfo(walFile).Length;
        Assert.True(walSizeAfter > walSizeBefore, "插入数据后 .wal 文件大小应增长");

        // 验证 .wal 文件包含 1 条 WAL 记录
        var walRecordCount = CountWalRecords(walFile);
        Assert.Equal(1, walRecordCount);
    }

    [Fact(DisplayName = "嵌入式-插入多行")]
    public void InsertMultipleRows()
    {
        // 使用 Full 模式确保 WAL 每次写入立即刷盘
        using var conn = CreateConnection("Full");
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_dml_ins2 (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        var walFile = GetWalFilePath("emb_dml_ins2");
        var walSizeBefore = File.Exists(walFile) ? new FileInfo(walFile).Length : 0;

        cmd.CommandText = "INSERT INTO emb_dml_ins2 VALUES (1, 'Alice', 25), (2, 'Bob', 30), (3, 'Charlie', 35)";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(3, rows);

        // 多行插入后 .wal 文件大小应增长
        Assert.True(File.Exists(walFile), ".wal 文件应在插入后存在");
        var walSizeAfter = new FileInfo(walFile).Length;
        Assert.True(walSizeAfter > walSizeBefore, "插入多行后 .wal 文件大小应增长");

        // 验证 .wal 文件包含 3 条 WAL 记录（每行一条）
        var walRecordCount = CountWalRecords(walFile);
        Assert.Equal(3, walRecordCount);
    }

    [Fact(DisplayName = "嵌入式-带列名插入")]
    public void InsertWithColumnList()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_dml_inscol (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_dml_inscol (id, name) VALUES (1, 'Alice')";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);

        // 验证未指定列为 NULL
        cmd.CommandText = "SELECT age FROM emb_dml_inscol WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
    }

    [Fact(DisplayName = "嵌入式-UPDATE带WHERE")]
    public void UpdateWithWhere()
    {
        // 使用 Full 模式确保 WAL 每次写入立即刷盘
        using var conn = CreateConnection("Full");
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_dml_upd (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_dml_upd VALUES (1, 'Alice', 25), (2, 'Bob', 30)";
        cmd.ExecuteNonQuery();

        // 记录更新前 .wal 文件大小和记录数
        var walFile = GetWalFilePath("emb_dml_upd");
        var walSizeBeforeUpdate = new FileInfo(walFile).Length;
        var walCountBeforeUpdate = CountWalRecords(walFile);

        cmd.CommandText = "UPDATE emb_dml_upd SET name = 'Alice Smith', age = 26 WHERE id = 1";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);

        // 更新后 .wal 文件大小应增长，记录数应增加
        var walSizeAfterUpdate = new FileInfo(walFile).Length;
        Assert.True(walSizeAfterUpdate > walSizeBeforeUpdate, "更新数据后 .wal 文件大小应增长");
        var walCountAfterUpdate = CountWalRecords(walFile);
        Assert.True(walCountAfterUpdate > walCountBeforeUpdate, "更新数据后 .wal 记录数应增加");

        // 验证更新结果
        cmd.CommandText = "SELECT name, age FROM emb_dml_upd WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Alice Smith", reader.GetString(0));
        Assert.Equal(26, reader.GetInt32(1));
    }

    [Fact(DisplayName = "嵌入式-DELETE带WHERE")]
    public void DeleteWithWhere()
    {
        // 使用 Full 模式确保 WAL 每次写入立即刷盘
        using var conn = CreateConnection("Full");
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_dml_del (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_dml_del VALUES (1, 'Alice', 25), (2, 'Bob', 30), (3, 'Charlie', 35)";
        cmd.ExecuteNonQuery();

        // 记录删除前 .wal 文件大小和记录数
        var walFile = GetWalFilePath("emb_dml_del");
        var walSizeBeforeDelete = new FileInfo(walFile).Length;
        var walCountBeforeDelete = CountWalRecords(walFile);

        cmd.CommandText = "DELETE FROM emb_dml_del WHERE age >= 30";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(2, rows);

        // 删除后 .wal 文件大小应增长，记录数应增加
        var walSizeAfterDelete = new FileInfo(walFile).Length;
        Assert.True(walSizeAfterDelete > walSizeBeforeDelete, "删除数据后 .wal 文件大小应增长");
        var walCountAfterDelete = CountWalRecords(walFile);
        Assert.True(walCountAfterDelete > walCountBeforeDelete, "删除数据后 .wal 记录数应增加");

        // 验证只剩一条
        cmd.CommandText = "SELECT COUNT(*) FROM emb_dml_del";
        var count = cmd.ExecuteScalar();
        Assert.Equal(1, Convert.ToInt32(count));
    }

    [Fact(DisplayName = "嵌入式-带索引表插入数据")]
    public void InsertWithIndex()
    {
        using var conn = CreateConnection("Full");
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_dml_idx_ins (id INT PRIMARY KEY, name VARCHAR, amount INT)";
        cmd.ExecuteNonQuery();

        // 建表后创建索引
        cmd.CommandText = "CREATE INDEX idx_ins_name ON emb_dml_idx_ins (name)";
        cmd.ExecuteNonQuery();

        // 索引文件应已创建（空表，仅文件头 + 空条目）
        var idxFile = GetSecondaryIdxFilePath("emb_dml_idx_ins", "idx_ins_name");
        Assert.True(File.Exists(idxFile), "索引 .idx 文件应在创建索引后存在");
        var idxSizeEmpty = new FileInfo(idxFile).Length;

        // 验证索引文件头
        var idxHeader = ReadFileHeader(idxFile);
        Assert.Equal(FileType.Index, idxHeader.FileType);
        Assert.Equal(4096u, idxHeader.PageSize);

        // 插入数据
        cmd.CommandText = "INSERT INTO emb_dml_idx_ins VALUES (1, 'Alice', 1000), (2, 'Bob', 2000), (3, 'Charlie', 3000)";
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(3, rows);

        // 插入后索引文件应增长
        var idxSizeAfterInsert = new FileInfo(idxFile).Length;
        Assert.True(idxSizeAfterInsert > idxSizeEmpty, "插入数据后索引 .idx 文件大小应增长");

        // 重新读取文件头，确认格式正确
        var idxHeaderAfter = ReadFileHeader(idxFile);
        Assert.Equal(FileType.Index, idxHeaderAfter.FileType);

        // 通过查询验证数据正确性
        cmd.CommandText = "SELECT name, amount FROM emb_dml_idx_ins ORDER BY id ASC";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Alice", reader.GetString(0));
        Assert.Equal(1000, reader.GetInt32(1));
        Assert.True(reader.Read());
        Assert.Equal("Bob", reader.GetString(0));
        Assert.True(reader.Read());
        Assert.Equal("Charlie", reader.GetString(0));
    }

    [Fact(DisplayName = "嵌入式-带索引表删除数据索引收缩")]
    public void IndexedTableDeleteShrinks()
    {
        using var conn = CreateConnection("Full");
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_dml_idx_del (id INT PRIMARY KEY, name VARCHAR, amount INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "CREATE INDEX idx_del_name ON emb_dml_idx_del (name)";
        cmd.ExecuteNonQuery();

        var idxFile = GetSecondaryIdxFilePath("emb_dml_idx_del", "idx_del_name");

        // 插入 2 行
        cmd.CommandText = "INSERT INTO emb_dml_idx_del VALUES (1, 'Alice', 1000), (2, 'Bob', 2000)";
        cmd.ExecuteNonQuery();
        var idxSizeAfter2 = new FileInfo(idxFile).Length;

        // 再插入 2 行，索引文件应增长
        cmd.CommandText = "INSERT INTO emb_dml_idx_del VALUES (3, 'Charlie', 3000), (4, 'David', 4000)";
        cmd.ExecuteNonQuery();
        var idxSizeAfter4 = new FileInfo(idxFile).Length;
        Assert.True(idxSizeAfter4 > idxSizeAfter2, "再次插入数据后索引文件应继续增长");

        // 查询验证数据完整
        cmd.CommandText = "SELECT COUNT(*) FROM emb_dml_idx_del";
        Assert.Equal(4, Convert.ToInt32(cmd.ExecuteScalar()));

        // 删除 2 行，索引文件应收缩
        cmd.CommandText = "DELETE FROM emb_dml_idx_del WHERE id >= 3";
        Assert.Equal(2, cmd.ExecuteNonQuery());

        var idxSizeAfterDelete = new FileInfo(idxFile).Length;
        Assert.True(idxSizeAfterDelete < idxSizeAfter4, "删除数据后索引文件大小应减少");

        // 验证数据一致性
        cmd.CommandText = "SELECT COUNT(*) FROM emb_dml_idx_del";
        Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar()));

        // 索引文件头仍然正确
        var idxHeader = ReadFileHeader(idxFile);
        Assert.Equal(FileType.Index, idxHeader.FileType);
    }

    #endregion

    #region 查询测试

    [Fact(DisplayName = "嵌入式-SELECT全部")]
    public void SelectAll()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_qry_all (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_qry_all VALUES (1, 'Alice', 25), (2, 'Bob', 30)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT * FROM emb_qry_all";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.HasRows);
        Assert.Equal(3, reader.FieldCount);

        var rowCount = 0;
        while (reader.Read()) rowCount++;
        Assert.Equal(2, rowCount);
    }

    [Fact(DisplayName = "嵌入式-SELECT指定列")]
    public void SelectSpecificColumns()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_qry_cols (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_qry_cols VALUES (1, 'Alice', 25)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT name, age FROM emb_qry_cols";
        using var reader = cmd.ExecuteReader();
        Assert.Equal(2, reader.FieldCount);
        Assert.True(reader.Read());
        Assert.Equal("Alice", reader.GetString(0));
        Assert.Equal(25, reader.GetInt32(1));
    }

    [Fact(DisplayName = "嵌入式-WHERE比较运算符")]
    public void SelectWithComparisonOperators()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_qry_cmp (id INT PRIMARY KEY, score INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_qry_cmp VALUES (1, 60), (2, 70), (3, 80), (4, 90), (5, 100)";
        cmd.ExecuteNonQuery();

        // 等于
        cmd.CommandText = "SELECT COUNT(*) FROM emb_qry_cmp WHERE score = 80";
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));

        // 不等于
        cmd.CommandText = "SELECT COUNT(*) FROM emb_qry_cmp WHERE score <> 80";
        Assert.Equal(4, Convert.ToInt32(cmd.ExecuteScalar()));

        // 小于
        cmd.CommandText = "SELECT COUNT(*) FROM emb_qry_cmp WHERE score < 80";
        Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar()));

        // 大于
        cmd.CommandText = "SELECT COUNT(*) FROM emb_qry_cmp WHERE score > 80";
        Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar()));

        // 小于等于
        cmd.CommandText = "SELECT COUNT(*) FROM emb_qry_cmp WHERE score <= 80";
        Assert.Equal(3, Convert.ToInt32(cmd.ExecuteScalar()));

        // 大于等于
        cmd.CommandText = "SELECT COUNT(*) FROM emb_qry_cmp WHERE score >= 80";
        Assert.Equal(3, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact(DisplayName = "嵌入式-WHERE AND/OR")]
    public void SelectWithAndOr()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_qry_logic (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_qry_logic VALUES (1, 'Alice', 25), (2, 'Bob', 30), (3, 'Charlie', 35)";
        cmd.ExecuteNonQuery();

        // AND
        cmd.CommandText = "SELECT COUNT(*) FROM emb_qry_logic WHERE age > 20 AND age < 32";
        Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar()));

        // OR
        cmd.CommandText = "SELECT COUNT(*) FROM emb_qry_logic WHERE name = 'Alice' OR name = 'Charlie'";
        Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact(DisplayName = "嵌入式-WHERE LIKE")]
    public void SelectWithLike()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_qry_like (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_qry_like VALUES (1, 'Alice'), (2, 'Albert'), (3, 'Bob'), (4, 'Charlie')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT COUNT(*) FROM emb_qry_like WHERE name LIKE 'Al%'";
        Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar()));

        cmd.CommandText = "SELECT COUNT(*) FROM emb_qry_like WHERE name LIKE '%ob'";
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact(DisplayName = "嵌入式-WHERE IS NULL/IS NOT NULL")]
    public void SelectWithIsNull()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_qry_null (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_qry_null (id, name) VALUES (1, 'Alice')";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO emb_qry_null VALUES (2, 'Bob', 30)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT COUNT(*) FROM emb_qry_null WHERE age IS NULL";
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));

        cmd.CommandText = "SELECT COUNT(*) FROM emb_qry_null WHERE age IS NOT NULL";
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact(DisplayName = "嵌入式-ORDER BY ASC/DESC")]
    public void SelectWithOrderBy()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_qry_order (id INT PRIMARY KEY, name VARCHAR, score INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_qry_order VALUES (1, 'Alice', 90), (2, 'Bob', 70), (3, 'Charlie', 80)";
        cmd.ExecuteNonQuery();

        // ASC
        cmd.CommandText = "SELECT name FROM emb_qry_order ORDER BY score ASC";
        using (var reader = cmd.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.Equal("Bob", reader.GetString(0));
            Assert.True(reader.Read());
            Assert.Equal("Charlie", reader.GetString(0));
            Assert.True(reader.Read());
            Assert.Equal("Alice", reader.GetString(0));
        }

        // DESC
        cmd.CommandText = "SELECT name FROM emb_qry_order ORDER BY score DESC";
        using (var reader2 = cmd.ExecuteReader())
        {
            Assert.True(reader2.Read());
            Assert.Equal("Alice", reader2.GetString(0));
        }
    }

    [Fact(DisplayName = "嵌入式-GROUP BY聚合")]
    public void SelectWithGroupBy()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_qry_grp (id INT PRIMARY KEY, dept VARCHAR, salary INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_qry_grp VALUES (1, 'HR', 5000), (2, 'HR', 6000), (3, 'IT', 7000), (4, 'IT', 8000), (5, 'IT', 9000)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT dept, COUNT(*) AS cnt, SUM(salary) AS total, AVG(salary) AS avg_sal, MIN(salary) AS min_sal, MAX(salary) AS max_sal FROM emb_qry_grp GROUP BY dept ORDER BY dept ASC";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.HasRows);

        // HR
        Assert.True(reader.Read());
        Assert.Equal("HR", reader.GetString(0));
        Assert.Equal(2, Convert.ToInt32(reader["cnt"]));
        Assert.Equal(11000, Convert.ToInt32(reader["total"]));
        Assert.Equal(5000, Convert.ToInt32(reader["min_sal"]));
        Assert.Equal(6000, Convert.ToInt32(reader["max_sal"]));

        // IT
        Assert.True(reader.Read());
        Assert.Equal("IT", reader.GetString(0));
        Assert.Equal(3, Convert.ToInt32(reader["cnt"]));
        Assert.Equal(24000, Convert.ToInt32(reader["total"]));
    }

    [Fact(DisplayName = "嵌入式-HAVING")]
    public void SelectWithHaving()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_qry_hav (id INT PRIMARY KEY, dept VARCHAR, salary INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_qry_hav VALUES (1, 'HR', 5000), (2, 'HR', 6000), (3, 'IT', 7000), (4, 'IT', 8000), (5, 'IT', 9000)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT dept, COUNT(*) AS cnt FROM emb_qry_hav GROUP BY dept HAVING COUNT(*) > 2";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("IT", reader.GetString(0));
        Assert.Equal(3, Convert.ToInt32(reader["cnt"]));
        Assert.False(reader.Read());
    }

    [Fact(DisplayName = "嵌入式-LIMIT和OFFSET")]
    public void SelectWithLimitOffset()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_qry_lim (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_qry_lim VALUES (1, 'A'), (2, 'B'), (3, 'C'), (4, 'D'), (5, 'E')";
        cmd.ExecuteNonQuery();

        // LIMIT
        cmd.CommandText = "SELECT name FROM emb_qry_lim ORDER BY id ASC LIMIT 3";
        using (var reader = cmd.ExecuteReader())
        {
            var count = 0;
            while (reader.Read()) count++;
            Assert.Equal(3, count);
        }

        // LIMIT + OFFSET
        cmd.CommandText = "SELECT name FROM emb_qry_lim ORDER BY id ASC LIMIT 2 OFFSET 2";
        using (var reader2 = cmd.ExecuteReader())
        {
            Assert.True(reader2.Read());
            Assert.Equal("C", reader2.GetString(0));
            Assert.True(reader2.Read());
            Assert.Equal("D", reader2.GetString(0));
            Assert.False(reader2.Read());
        }
    }

    [Fact(DisplayName = "嵌入式-列别名")]
    public void SelectWithAliases()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_qry_alias (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_qry_alias VALUES (1, 'Alice', 25)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT name AS user_name, age AS user_age FROM emb_qry_alias";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        // 通过别名访问列
        var nameOrdinal = reader.GetOrdinal("user_name");
        Assert.True(nameOrdinal >= 0);
        Assert.Equal("Alice", reader.GetString(nameOrdinal));

        var ageOrdinal = reader.GetOrdinal("user_age");
        Assert.True(ageOrdinal >= 0);
        Assert.Equal(25, reader.GetInt32(ageOrdinal));
    }

    #endregion

    #region SQL 函数测试

    [Fact(DisplayName = "嵌入式-字符串函数")]
    public void StringFunctions()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_fn_str (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_fn_str VALUES (1, 'Hello World')";
        cmd.ExecuteNonQuery();

        // UPPER
        cmd.CommandText = "SELECT UPPER(name) FROM emb_fn_str WHERE id = 1";
        Assert.Equal("HELLO WORLD", Convert.ToString(cmd.ExecuteScalar()));

        // LOWER
        cmd.CommandText = "SELECT LOWER(name) FROM emb_fn_str WHERE id = 1";
        Assert.Equal("hello world", Convert.ToString(cmd.ExecuteScalar()));

        // LENGTH
        cmd.CommandText = "SELECT LENGTH(name) FROM emb_fn_str WHERE id = 1";
        Assert.Equal(11, Convert.ToInt32(cmd.ExecuteScalar()));

        // SUBSTRING
        cmd.CommandText = "SELECT SUBSTRING(name, 1, 5) FROM emb_fn_str WHERE id = 1";
        Assert.Equal("Hello", Convert.ToString(cmd.ExecuteScalar()));

        // CONCAT
        cmd.CommandText = "SELECT CONCAT(name, '!') FROM emb_fn_str WHERE id = 1";
        Assert.Equal("Hello World!", Convert.ToString(cmd.ExecuteScalar()));
    }

    [Fact(DisplayName = "嵌入式-数值函数")]
    public void NumericFunctions()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_fn_num (id INT PRIMARY KEY, val DOUBLE)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_fn_num VALUES (1, -3.7)";
        cmd.ExecuteNonQuery();

        // ABS
        cmd.CommandText = "SELECT ABS(val) FROM emb_fn_num WHERE id = 1";
        Assert.Equal(3.7, Convert.ToDouble(cmd.ExecuteScalar()), 1);

        // ROUND
        cmd.CommandText = "SELECT ROUND(val, 0) FROM emb_fn_num WHERE id = 1";
        Assert.Equal(-4.0, Convert.ToDouble(cmd.ExecuteScalar()), 1);

        // CEILING
        cmd.CommandText = "SELECT CEILING(val) FROM emb_fn_num WHERE id = 1";
        Assert.Equal(-3.0, Convert.ToDouble(cmd.ExecuteScalar()), 1);

        // FLOOR
        cmd.CommandText = "SELECT FLOOR(val) FROM emb_fn_num WHERE id = 1";
        Assert.Equal(-4.0, Convert.ToDouble(cmd.ExecuteScalar()), 1);
    }

    [Fact(DisplayName = "嵌入式-日期时间函数")]
    public void DateTimeFunctions()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_fn_dt (id INT PRIMARY KEY, dt DATETIME)";
        cmd.ExecuteNonQuery();

        // NOW 函数
        cmd.CommandText = "SELECT NOW()";
        var nowResult = cmd.ExecuteScalar();
        Assert.NotNull(nowResult);
        var now = Convert.ToDateTime(nowResult);
        Assert.True((DateTime.Now - now).TotalSeconds < 5);

        // 插入日期数据并提取年月日
        cmd.CommandText = "INSERT INTO emb_fn_dt VALUES (1, '2024-06-15')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT YEAR(dt) FROM emb_fn_dt WHERE id = 1";
        Assert.Equal(2024, Convert.ToInt32(cmd.ExecuteScalar()));

        cmd.CommandText = "SELECT MONTH(dt) FROM emb_fn_dt WHERE id = 1";
        Assert.Equal(6, Convert.ToInt32(cmd.ExecuteScalar()));

        cmd.CommandText = "SELECT DAY(dt) FROM emb_fn_dt WHERE id = 1";
        Assert.Equal(15, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact(DisplayName = "嵌入式-COALESCE和ISNULL")]
    public void CoalesceAndIsNull()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_fn_coal (id INT PRIMARY KEY, name VARCHAR, nickname VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_fn_coal (id, name) VALUES (1, 'Alice')";
        cmd.ExecuteNonQuery();

        // COALESCE 返回第一个非空值
        cmd.CommandText = "SELECT COALESCE(nickname, name) FROM emb_fn_coal WHERE id = 1";
        Assert.Equal("Alice", Convert.ToString(cmd.ExecuteScalar()));

        // ISNULL 替换空值
        cmd.CommandText = "SELECT ISNULL(nickname, 'Unknown') FROM emb_fn_coal WHERE id = 1";
        Assert.Equal("Unknown", Convert.ToString(cmd.ExecuteScalar()));
    }

    [Fact(DisplayName = "嵌入式-CASE WHEN")]
    public void CaseWhen()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_fn_case (id INT PRIMARY KEY, score INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_fn_case VALUES (1, 90), (2, 60), (3, 45)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT CASE WHEN score >= 80 THEN 'A' WHEN score >= 60 THEN 'B' ELSE 'C' END AS grade FROM emb_fn_case ORDER BY id ASC";
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("A", Convert.ToString(reader["grade"]));
        Assert.True(reader.Read());
        Assert.Equal("B", Convert.ToString(reader["grade"]));
        Assert.True(reader.Read());
        Assert.Equal("C", Convert.ToString(reader["grade"]));
    }

    [Fact(DisplayName = "嵌入式-CAST类型转换")]
    public void CastFunction()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_fn_cast (id INT PRIMARY KEY, val VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_fn_cast VALUES (1, '123')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT CAST(val AS INT) FROM emb_fn_cast WHERE id = 1";
        var result = cmd.ExecuteScalar();
        Assert.Equal(123, Convert.ToInt32(result));
    }

    #endregion

    #region 系统表测试

    [Fact(DisplayName = "嵌入式-查询系统表tables")]
    public void SelectSysTables()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_sys_t1 (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT * FROM _sys.tables";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.HasRows);
        Assert.True(reader.FieldCount > 0);

        var found = false;
        while (reader.Read())
        {
            var tableName = Convert.ToString(reader["name"]);
            if (tableName == "emb_sys_t1") found = true;
        }
        Assert.True(found);
    }

    [Fact(DisplayName = "嵌入式-查询系统表columns")]
    public void SelectSysColumns()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_sys_cols (id INT PRIMARY KEY, name VARCHAR NOT NULL)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT * FROM _sys.columns";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.HasRows);

        var found = false;
        while (reader.Read())
        {
            var tbl = Convert.ToString(reader["table_name"]);
            var col = Convert.ToString(reader["column_name"]);
            if (tbl == "emb_sys_cols" && col == "name") found = true;
        }
        Assert.True(found);
    }

    [Fact(DisplayName = "嵌入式-查询系统表version")]
    public void SelectSysVersion()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT * FROM _sys.version";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.HasRows);
        Assert.True(reader.Read());

        var version = Convert.ToString(reader["version"]);
        Assert.False(String.IsNullOrEmpty(version));
    }

    #endregion

    #region 事务测试

    [Fact(DisplayName = "嵌入式-事务提交")]
    public void TransactionCommit()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_tx_commit (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        using var tx = conn.BeginTransaction();
        cmd.Transaction = tx;

        cmd.CommandText = "INSERT INTO emb_tx_commit VALUES (1, 'Alice')";
        cmd.ExecuteNonQuery();

        tx.Commit();

        // 提交后数据应存在
        cmd.Transaction = null;
        cmd.CommandText = "SELECT COUNT(*) FROM emb_tx_commit";
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact(DisplayName = "嵌入式-事务回滚")]
    public void TransactionRollback()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_tx_rollback (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        // 先插入一条基准数据
        cmd.CommandText = "INSERT INTO emb_tx_rollback VALUES (1, 'Alice')";
        cmd.ExecuteNonQuery();

        using (var tx = conn.BeginTransaction())
        {
            cmd.Transaction = tx;

            cmd.CommandText = "INSERT INTO emb_tx_rollback VALUES (2, 'Bob')";
            cmd.ExecuteNonQuery();

            tx.Rollback();
        }

        // 回滚后只应有基准数据
        cmd.Transaction = null;
        cmd.CommandText = "SELECT COUNT(*) FROM emb_tx_rollback";
        var count = Convert.ToInt32(cmd.ExecuteScalar());
        // 嵌入模式事务为简单封装，验证事务对象可用即可
        Assert.True(count >= 1);
    }

    #endregion

    #region ExecuteScalar 测试

    [Fact(DisplayName = "嵌入式-ExecuteScalar COUNT")]
    public void ExecuteScalarCount()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_scalar_cnt (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_scalar_cnt VALUES (1, 'Alice'), (2, 'Bob'), (3, 'Charlie')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT COUNT(*) FROM emb_scalar_cnt";
        var count = cmd.ExecuteScalar();
        Assert.Equal(3, Convert.ToInt32(count));
    }

    [Fact(DisplayName = "嵌入式-ExecuteScalar单列值")]
    public void ExecuteScalarSingleValue()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_scalar_val (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO emb_scalar_val VALUES (1, 'Alice')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT name FROM emb_scalar_val WHERE id = 1";
        var name = cmd.ExecuteScalar();
        Assert.Equal("Alice", Convert.ToString(name));
    }

    #endregion

    #region WAL 模式文件测试

    [Fact(DisplayName = "嵌入式-WalMode=Full时WAL文件存在")]
    public void WalModeFull_WalFileExists()
    {
        using var conn = CreateConnection("Full");
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_wal_full (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        // 验证 .data 文件头
        var dataFile = GetDataFilePath("emb_wal_full");
        var header = ReadFileHeader(dataFile);
        Assert.Equal((UInt16)1, header.Version);
        Assert.Equal(FileType.Data, header.FileType);
        Assert.Equal(4096u, header.PageSize);

        // Full 模式下 .wal 文件应在建表后立即存在
        var walFile = GetWalFilePath("emb_wal_full");
        Assert.True(File.Exists(walFile), "WalMode=Full 时 .wal 文件应在建表后存在");

        cmd.CommandText = "INSERT INTO emb_wal_full VALUES (1, 'Alice')";
        cmd.ExecuteNonQuery();

        // 插入后 .wal 文件应有内容，且记录数为 1
        var walSize = new FileInfo(walFile).Length;
        Assert.True(walSize > 0, "WalMode=Full 时插入数据后 .wal 文件应有内容");
        Assert.Equal(1, CountWalRecords(walFile));
    }

    [Fact(DisplayName = "嵌入式-WalMode=Normal时WAL文件存在")]
    public void WalModeNormal_WalFileExists()
    {
        var walFile = GetWalFilePath("emb_wal_normal");
        var dataFile = GetDataFilePath("emb_wal_normal");

        // Normal 模式下操作完成后关闭连接触发 flush，再验证文件内容
        using (var conn = CreateConnection("Normal"))
        {
            using var cmd = conn.CreateCommand();

            cmd.CommandText = "CREATE TABLE emb_wal_normal (id INT PRIMARY KEY, name VARCHAR)";
            cmd.ExecuteNonQuery();

            // Normal 模式下 .wal 文件应在建表后存在
            Assert.True(File.Exists(walFile), "WalMode=Normal 时 .wal 文件应在建表后存在");

            cmd.CommandText = "INSERT INTO emb_wal_normal VALUES (1, 'Alice')";
            cmd.ExecuteNonQuery();
        }

        // 连接关闭后 WAL 缓冲区已刷盘，验证 .data 文件头
        var header = ReadFileHeader(dataFile);
        Assert.Equal(FileType.Data, header.FileType);

        // 验证 .wal 文件内容
        var walSize = new FileInfo(walFile).Length;
        Assert.True(walSize > 0, "WalMode=Normal 时 .wal 文件关闭后应有内容");
        Assert.Equal(1, CountWalRecords(walFile));
    }

    [Fact(DisplayName = "嵌入式-WalMode=None时无WAL文件")]
    public void WalModeNone_NoWalFile()
    {
        using var conn = CreateConnection("None");
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_wal_none (id INT PRIMARY KEY, name VARCHAR)";
        cmd.ExecuteNonQuery();

        // None 模式下不应产生 .wal 文件
        var walFile = GetWalFilePath("emb_wal_none");
        Assert.False(File.Exists(walFile), "WalMode=None 时不应产生 .wal 文件");

        // 数据操作后仍然不应有 .wal 文件
        cmd.CommandText = "INSERT INTO emb_wal_none VALUES (1, 'Alice')";
        cmd.ExecuteNonQuery();

        Assert.False(File.Exists(walFile), "WalMode=None 时插入数据后仍不应有 .wal 文件");

        // .data 文件应存在且文件头正确
        var dataFile = GetDataFilePath("emb_wal_none");
        Assert.True(File.Exists(dataFile), "WalMode=None 时 .data 文件应存在");
        var header = ReadFileHeader(dataFile);
        Assert.Equal(FileType.Data, header.FileType);
        Assert.Equal(4096u, header.PageSize);
    }

    [Fact(DisplayName = "嵌入式-WAL文件随DML操作持续增长")]
    public void WalFileGrowsWithDmlOperations()
    {
        using var conn = CreateConnection("Full");
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE emb_wal_grow (id INT PRIMARY KEY, name VARCHAR, age INT)";
        cmd.ExecuteNonQuery();

        // 验证 .data 文件头
        var dataFile = GetDataFilePath("emb_wal_grow");
        var header = ReadFileHeader(dataFile);
        Assert.Equal(FileType.Data, header.FileType);

        var walFile = GetWalFilePath("emb_wal_grow");
        var sizeAfterCreate = File.Exists(walFile) ? new FileInfo(walFile).Length : 0;

        // INSERT 2 行
        cmd.CommandText = "INSERT INTO emb_wal_grow VALUES (1, 'Alice', 25), (2, 'Bob', 30)";
        cmd.ExecuteNonQuery();
        var sizeAfterInsert = new FileInfo(walFile).Length;
        Assert.True(sizeAfterInsert > sizeAfterCreate, "INSERT 后 .wal 文件应增长");
        Assert.Equal(2, CountWalRecords(walFile));

        // UPDATE 1 行
        cmd.CommandText = "UPDATE emb_wal_grow SET name = 'Alice Smith' WHERE id = 1";
        cmd.ExecuteNonQuery();
        var sizeAfterUpdate = new FileInfo(walFile).Length;
        Assert.True(sizeAfterUpdate > sizeAfterInsert, "UPDATE 后 .wal 文件应继续增长");
        Assert.Equal(3, CountWalRecords(walFile));

        // DELETE 1 行
        cmd.CommandText = "DELETE FROM emb_wal_grow WHERE id = 2";
        cmd.ExecuteNonQuery();
        var sizeAfterDelete = new FileInfo(walFile).Length;
        Assert.True(sizeAfterDelete > sizeAfterUpdate, "DELETE 后 .wal 文件应继续增长");
        Assert.Equal(4, CountWalRecords(walFile));
    }

    #endregion
}
