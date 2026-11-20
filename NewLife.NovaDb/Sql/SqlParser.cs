using System.Globalization;
using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Sql;

/// <summary>SQL 解析器，将 SQL 文本解析为 AST</summary>
public class SqlParser
{
    private readonly List<SqlToken> _tokens;
    private Int32 _pos;

    /// <summary>创建 SQL 解析器</summary>
    /// <param name="sql">SQL 文本</param>
    public SqlParser(String sql)
    {
        if (sql == null) throw new ArgumentNullException(nameof(sql));

        var lexer = new SqlLexer(sql);
        _tokens = lexer.Tokenize();
        _pos = 0;
    }

    /// <summary>解析 SQL 语句</summary>
    /// <returns>SQL AST</returns>
    public SqlStatement Parse()
    {
        var token = Peek();
        var stmt = token.Type switch
        {
            SqlTokenType.Select => (SqlStatement)ParseSelect(),
            SqlTokenType.Insert => ParseInsert(),
            SqlTokenType.Merge => ParseMerge(),
            SqlTokenType.Update => ParseUpdate(),
            SqlTokenType.Delete => ParseDelete(),
            SqlTokenType.Create => ParseCreate(),
            SqlTokenType.Drop => ParseDrop(),
            SqlTokenType.Alter => ParseAlter(),
            SqlTokenType.Truncate => ParseTruncate(),
            SqlTokenType.Explain => ParseExplain(),
            SqlTokenType.Show => ParseShow(),
            SqlTokenType.Begin or SqlTokenType.Start => ParseBegin(),
            SqlTokenType.Commit => ParseTransaction(SqlStatementType.CommitTransaction),
            SqlTokenType.Rollback => ParseTransaction(SqlStatementType.RollbackTransaction),
            _ when String.Equals(token.Value, "REPLACE", StringComparison.OrdinalIgnoreCase) => ParseReplace(),
            _ when String.Equals(token.Value, "PURGE", StringComparison.OrdinalIgnoreCase) => ParsePurgeBinlogs(),
            _ when String.Equals(token.Value, "FLUSH", StringComparison.OrdinalIgnoreCase) => ParseFlushBinlogs(),
            _ when String.Equals(token.Value, "RENAME", StringComparison.OrdinalIgnoreCase) => ParseRenameTable(),
            _ => throw SyntaxError($"Unexpected token '{token.Value}' at position {token.Position}")
        };

        // 跳过可选的分号
        if (Peek().Type == SqlTokenType.Semicolon)
            Advance();

        return stmt;
    }

    /// <summary>获取下一条语句的起始字符位置（分号分隔的多语句支持）</summary>
    /// <returns>下一条语句首 token 的字符位置，无后续语句返回 -1</returns>
    public Int32 GetNextStatementPosition()
    {
        while (_pos < _tokens.Count)
        {
            var token = _tokens[_pos];
            if (token.Type == SqlTokenType.Eof || token.Type == SqlTokenType.Semicolon)
            {
                _pos++;
                continue;
            }

            return token.Position;
        }

        return -1;
    }

    /// <summary>解析 BEGIN / START TRANSACTION</summary>
    private TransactionStatement ParseBegin()
    {
        // BEGIN 或 START
        Advance();

        // 可选：TRANSACTION 关键字（BEGIN TRANSACTION / START TRANSACTION）
        if (Peek().Type == SqlTokenType.Transaction)
            Advance();

        return new TransactionStatement(SqlStatementType.BeginTransaction);
    }

    /// <summary>解析 COMMIT / ROLLBACK [WORK | TRANSACTION]</summary>
    /// <param name="statementType">事务语句类型</param>
    private TransactionStatement ParseTransaction(SqlStatementType statementType)
    {
        Advance();

        // 可选：WORK 或 TRANSACTION 关键字（COMMIT WORK / ROLLBACK TRANSACTION）
        if (Peek().Type == SqlTokenType.Transaction ||
            String.Equals(Peek().Value, "WORK", StringComparison.OrdinalIgnoreCase))
        {
            Advance();
        }

        return new TransactionStatement(statementType);
    }

    private ShowStatement ParseShow()
    {
        Expect(SqlTokenType.Show);
        var stmt = new ShowStatement();

        var next = Peek();

        // SHOW DATABASES [LIKE 'pattern']
        if (next.Type == SqlTokenType.Databases)
        {
            Advance();
            stmt.ShowType = ShowType.Databases;
            if (Peek().Type == SqlTokenType.Like)
            {
                Advance();
                stmt.LikePattern = Expect(SqlTokenType.StringLiteral).Value;
            }
            return stmt;
        }

        // SHOW TABLES [FROM db] [LIKE 'pattern']
        if (next.Type == SqlTokenType.Tables)
        {
            Advance();
            stmt.ShowType = ShowType.Tables;
            if (Peek().Type == SqlTokenType.From)
            {
                Advance();
                stmt.DatabaseName = ExpectIdentifier();
            }
            if (Peek().Type == SqlTokenType.Like)
            {
                Advance();
                stmt.LikePattern = Expect(SqlTokenType.StringLiteral).Value;
            }
            return stmt;
        }

        // SHOW TABLE STATUS [FROM db] [LIKE 'pattern']，MySQL 兼容（XCode 元数据用）
        if (next.Type == SqlTokenType.Table)
        {
            Advance();
            var after = Peek();
            if (after.Type != SqlTokenType.Identifier || !after.Value.EqualIgnoreCase("STATUS"))
                throw SyntaxError($"Expected STATUS after SHOW TABLE, got '{after.Value}'");
            Advance();

            stmt.ShowType = ShowType.TableStatus;
            if (Peek().Type == SqlTokenType.From)
            {
                Advance();
                stmt.DatabaseName = ExpectIdentifier();
            }
            if (Peek().Type == SqlTokenType.Like)
            {
                Advance();
                stmt.LikePattern = Expect(SqlTokenType.StringLiteral).Value;
            }
            return stmt;
        }

        // SHOW COLUMNS FROM [`db`.`table` | `table`]
        if (next.Type == SqlTokenType.Columns)
        {
            Advance();
            stmt.ShowType = ShowType.Columns;
            if (Peek().Type == SqlTokenType.From)
            {
                Advance();
                var first = ExpectIdentifier();
                if (Peek().Type == SqlTokenType.Dot)
                {
                    Advance();
                    stmt.DatabaseName = first;
                    stmt.TableName = ExpectIdentifier();
                }
                else
                {
                    stmt.TableName = first;
                }
            }
            return stmt;
        }

        // SHOW INDEX FROM [`db`.`table` | `table`]
        if (next.Type == SqlTokenType.Index)
        {
            Advance();
            stmt.ShowType = ShowType.Index;
            if (Peek().Type == SqlTokenType.From)
            {
                Advance();
                var first = ExpectIdentifier();
                if (Peek().Type == SqlTokenType.Dot)
                {
                    Advance();
                    stmt.DatabaseName = first;
                    stmt.TableName = ExpectIdentifier();
                }
                else
                {
                    stmt.TableName = first;
                }
            }
            return stmt;
        }

        // SHOW VARIABLES [LIKE 'pattern']
        if (next.Type == SqlTokenType.Variables)
        {
            Advance();
            stmt.ShowType = ShowType.Variables;
            if (Peek().Type == SqlTokenType.Like)
            {
                Advance();
                stmt.LikePattern = Expect(SqlTokenType.StringLiteral).Value;
            }
            return stmt;
        }

        // SHOW USERS [WHERE ...]
        if (next.Type == SqlTokenType.Identifier && next.Value.EqualIgnoreCase("USERS"))
        {
            Advance();
            stmt.ShowType = ShowType.Users;
            // 消费剩余 token 作为 Where 字符串（简单处理）
            if (Peek().Type == SqlTokenType.Where)
            {
                var start = _pos;
                while (Peek().Type != SqlTokenType.Eof && Peek().Type != SqlTokenType.Semicolon)
                    Advance();
                stmt.Where = String.Join(" ", _tokens.Skip(start).TakeWhile(t => t.Type != SqlTokenType.Eof && t.Type != SqlTokenType.Semicolon).Select(t => t.Value));
            }
            return stmt;
        }

        // SHOW BINLOGS —— Binlog 文件列表
        if (next.Type == SqlTokenType.Identifier && next.Value.EqualIgnoreCase("BINLOGS"))
        {
            Advance();
            stmt.ShowType = ShowType.Binlogs;
            return stmt;
        }

        // SHOW BINLOG STATUS —— 当前 Binlog 状态
        if (next.Type == SqlTokenType.Identifier && next.Value.EqualIgnoreCase("BINLOG"))
        {
            Advance();
            var after = Peek();
            if (after.Type != SqlTokenType.Identifier || !after.Value.EqualIgnoreCase("STATUS"))
                throw SyntaxError($"Expected STATUS after SHOW BINLOG, got '{after.Value}'");
            Advance();
            stmt.ShowType = ShowType.BinlogStatus;
            return stmt;
        }

        throw SyntaxError($"Unexpected token '{next.Value}' after SHOW");
    }

    /// <summary>解析 PURGE BINLOGS TO 'binlog.000123' | BEFORE 'yyyy-MM-dd'</summary>
    private BinlogStatement ParsePurgeBinlogs()
    {
        // 消费 PURGE
        Advance();

        var next = Peek();
        if (next.Type != SqlTokenType.Identifier || !next.Value.EqualIgnoreCase("BINLOGS"))
            throw SyntaxError("Expected BINLOGS after PURGE");
        Advance();

        var stmt = new BinlogStatement(SqlStatementType.PurgeBinlogs);

        var mode = Peek();
        if (mode.Type == SqlTokenType.Identifier && mode.Value.EqualIgnoreCase("TO"))
        {
            Advance();
            stmt.IsTo = true;

            var fileToken = Peek();
            if (fileToken.Type == SqlTokenType.StringLiteral)
            {
                // 'binlog.000123' → 提取索引
                var fileName = fileToken.Value;
                stmt.ToIndex = ParseBinlogFileIndex(fileName);
                Advance();
            }
            else if (fileToken.Type == SqlTokenType.IntegerLiteral)
            {
                stmt.ToIndex = fileToken.Value.ToInt();
                Advance();
            }
            else
            {
                throw SyntaxError($"Expected binlog file name after TO, got '{fileToken.Value}'");
            }
        }
        else if (mode.Type == SqlTokenType.Identifier && mode.Value.EqualIgnoreCase("BEFORE"))
        {
            Advance();
            // BEFORE 'yyyy-MM-dd HH:mm:ss' —— 按时间清理
            var dateToken = Peek();
            if (dateToken.Type != SqlTokenType.StringLiteral)
                throw SyntaxError($"Expected datetime string after BEFORE, got '{dateToken.Value}'");
            Advance();
            // 时间模式：转换为目标文件索引（保留当前文件）
            if (!DateTime.TryParse(dateToken.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var beforeTime))
                throw SyntaxError($"Invalid datetime: {dateToken.Value}");
            stmt.ToIndex = null;
            stmt.IsTo = false;
        }
        else
        {
            throw SyntaxError("Expected TO or BEFORE after PURGE BINLOGS");
        }

        return stmt;
    }

    /// <summary>解析 FLUSH BINLOGS —— 轮转到新 Binlog 文件</summary>
    private BinlogStatement ParseFlushBinlogs()
    {
        // 消费 FLUSH
        Advance();

        var next = Peek();
        if (next.Type != SqlTokenType.Identifier || !next.Value.EqualIgnoreCase("BINLOGS"))
            throw SyntaxError("Expected BINLOGS after FLUSH");
        Advance();
        return new BinlogStatement(SqlStatementType.FlushBinlogs);
    }

    /// <summary>从 binlog 文件名提取文件索引</summary>
    /// <param name="fileName">文件名，如 binlog.000123</param>
    /// <returns>文件索引</returns>
    private static Int32 ParseBinlogFileIndex(String fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot >= fileName.Length - 1)
            throw new NovaException(ErrorCode.InvalidArgument, "Invalid binlog file name: {0}", fileName);

        var idxStr = fileName.Substring(dot + 1);
        if (!Int32.TryParse(idxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            throw new NovaException(ErrorCode.InvalidArgument, "Invalid binlog file index: {0}", idxStr);

        return index;
    }

    private ExplainStatement ParseExplain()
    {
        Expect(SqlTokenType.Explain);

        // 递归解析内部语句
        var inner = Parse();
        return new ExplainStatement { InnerStatement = inner };
    }

    #region DDL

    private SqlStatement ParseCreate()
    {
        Expect(SqlTokenType.Create);

        var next = Peek();
        if (next.Type == SqlTokenType.Table)
            return ParseCreateTable();
        if (next.Type == SqlTokenType.Unique || next.Type == SqlTokenType.Index)
            return ParseCreateIndex();
        if (next.Type == SqlTokenType.Database)
            return ParseCreateDatabase();

        throw SyntaxError($"Expected TABLE, INDEX or DATABASE after CREATE, got '{next.Value}'");
    }

    private CreateTableStatement ParseCreateTable()
    {
        Expect(SqlTokenType.Table);

        var stmt = new CreateTableStatement();

        // IF NOT EXISTS
        if (Peek().Type == SqlTokenType.If)
        {
            Advance();
            Expect(SqlTokenType.Not);
            Expect(SqlTokenType.Exists);
            stmt.IfNotExists = true;
        }

        stmt.TableName = ParseTableName();
        Expect(SqlTokenType.LeftParen);

        // 解析列定义
        String? primaryKeyColumn = null;
        do
        {
            // 检查是否为 PRIMARY KEY 约束
            if (Peek().Type == SqlTokenType.Primary)
            {
                Advance();
                Expect(SqlTokenType.Key);
                Expect(SqlTokenType.LeftParen);
                primaryKeyColumn = ExpectIdentifier();
                if (Peek().Type == SqlTokenType.Comma)
                    throw new NovaException(ErrorCode.NotSupported, "Composite primary key is not supported, use a single-column surrogate key instead");
                Expect(SqlTokenType.RightParen);
                continue;
            }

            // 表内索引：UNIQUE [KEY|INDEX] [name] (cols) / KEY|INDEX [name] (cols)
            if (Peek().Type is SqlTokenType.Unique or SqlTokenType.Key or SqlTokenType.Index)
            {
                stmt.Indexes.Add(ParseTableIndexDef());
                continue;
            }

            // 外键与约束不支持，给出明确提示而不是含糊的语法错误
            if (Peek().Type == SqlTokenType.Identifier && Peek().Value.EqualIgnoreCase("FOREIGN"))
                throw new NovaException(ErrorCode.NotSupported, "FOREIGN KEY is not supported, enforce referential integrity in the application layer");
            if (Peek().Type == SqlTokenType.Identifier && Peek().Value.EqualIgnoreCase("CONSTRAINT"))
                throw new NovaException(ErrorCode.NotSupported, "Named CONSTRAINT is not supported");

            var colDef = ParseColumnDef();
            stmt.Columns.Add(colDef);
        }
        while (TryConsume(SqlTokenType.Comma));

        Expect(SqlTokenType.RightParen);

        // 应用表级 PRIMARY KEY 约束
        if (primaryKeyColumn != null)
        {
            foreach (var col in stmt.Columns)
            {
                if (String.Equals(col.Name, primaryKeyColumn, StringComparison.OrdinalIgnoreCase))
                {
                    col.IsPrimaryKey = true;
                    break;
                }
            }
        }

        // 列级 UNIQUE 约束转换为唯一索引（MySQL 约定：索引名取列名）
        foreach (var col in stmt.Columns)
        {
            if (col.IsUnique)
                stmt.Indexes.Add(new SqlIndexDef { Name = col.Name, Columns = [col.Name], IsUnique = true });
        }

        // 解析表级选项：ENGINE 和 COMMENT
        ParseTableOptions(stmt);

        return stmt;
    }

    /// <summary>跳过可选的括号参数（如 CURRENT_TIMESTAMP(3)、索引前缀 name(20)）</summary>
    private void SkipOptionalParens()
    {
        if (Peek().Type != SqlTokenType.LeftParen) return;

        var depth = 0;
        do
        {
            if (Peek().Type == SqlTokenType.LeftParen) depth++;
            else if (Peek().Type == SqlTokenType.RightParen) depth--;
            Advance();
        }
        while (depth > 0 && Peek().Type != SqlTokenType.Eof);
    }

    /// <summary>解析表内索引定义</summary>
    /// <remarks>支持 UNIQUE [KEY|INDEX] [name] (cols) 与 KEY|INDEX [name] (cols)，未命名时取首列名（MySQL 约定）</remarks>
    /// <returns>索引定义</returns>
    private SqlIndexDef ParseTableIndexDef()
    {
        var index = new SqlIndexDef { IsUnique = TryConsume(SqlTokenType.Unique) };

        // 可选 KEY / INDEX 关键字
        if (Peek().Type == SqlTokenType.Key || Peek().Type == SqlTokenType.Index)
            Advance();

        // 可选索引名
        if (Peek().Type != SqlTokenType.LeftParen)
            index.Name = ExpectIdentifier();

        Expect(SqlTokenType.LeftParen);
        do
        {
            index.Columns.Add(ExpectIdentifier());
            // 兼容索引前缀长度，如 name(20)
            SkipOptionalParens();
        }
        while (TryConsume(SqlTokenType.Comma));
        Expect(SqlTokenType.RightParen);

        if (index.Name.IsNullOrEmpty()) index.Name = index.Columns[0];

        return index;
    }

    private SqlColumnDef ParseColumnDef()
    {
        var col = new SqlColumnDef
        {
            Name = ExpectIdentifier(),
            DataTypeName = ExpectIdentifier()
        };
        // 跳过可选的类型长度参数，如 VARCHAR(255)、DECIMAL(10,2)
        if (Peek().Type == SqlTokenType.LeftParen)
        {
            Advance();
            while (Peek().Type != SqlTokenType.RightParen && Peek().Type != SqlTokenType.Eof)
                Advance();
            Expect(SqlTokenType.RightParen);
        }

        // 解析列约束
        while (Peek().Type != SqlTokenType.Comma && Peek().Type != SqlTokenType.RightParen && Peek().Type != SqlTokenType.Eof)
        {
            var next = Peek();
            if (next.Type == SqlTokenType.Primary)
            {
                Advance();
                Expect(SqlTokenType.Key);
                col.IsPrimaryKey = true;
            }
            else if (next.Type == SqlTokenType.Not)
            {
                Advance();
                Expect(SqlTokenType.Null);
                col.NotNull = true;
            }
            else if (next.Type == SqlTokenType.Null)
            {
                Advance();
                col.NotNull = false;
            }
            else if (next.Type == SqlTokenType.Comment)
            {
                Advance();
                col.Comment = Expect(SqlTokenType.StringLiteral).Value;
            }
            else if (next.Type == SqlTokenType.Identifier && next.Value.EqualIgnoreCase("AUTO_INCREMENT"))
            {
                // MySQL 兼容：自增列
                Advance();
                col.IsAutoIncrement = true;
            }
            else if (next.Type == SqlTokenType.Unique)
            {
                // MySQL 兼容：列级唯一约束，建表时转换为唯一索引
                Advance();
                col.IsUnique = true;
            }
            else if (next.Type == SqlTokenType.Identifier &&
                (next.Value.EqualIgnoreCase("UNSIGNED") || next.Value.EqualIgnoreCase("SIGNED") || next.Value.EqualIgnoreCase("ZEROFILL")))
            {
                // MySQL 数值列修饰符，Nova 不做无符号语义，忽略
                Advance();
            }
            else if (next.Type == SqlTokenType.Identifier && next.Value.EqualIgnoreCase("COLLATE"))
            {
                // COLLATE <name>
                Advance();
                Advance();
            }
            else if (next.Type == SqlTokenType.Identifier && next.Value.EqualIgnoreCase("CHARACTER"))
            {
                // CHARACTER SET <name>
                Advance();
                TryConsume(SqlTokenType.Set);
                if (Peek().Type == SqlTokenType.Identifier) Advance();
            }
            else if (next.Type == SqlTokenType.On)
            {
                // ON UPDATE CURRENT_TIMESTAMP[(n)]，Nova 无自动更新语义，忽略
                Advance();
                TryConsume(SqlTokenType.Update);
                if (Peek().Type == SqlTokenType.Identifier || Peek().Type == SqlTokenType.LeftParen)
                {
                    if (Peek().Type == SqlTokenType.Identifier) Advance();
                    SkipOptionalParens();
                }
            }
            else if (next.Type == SqlTokenType.Identifier && next.Value.EqualIgnoreCase("DEFAULT"))
            {
                // MySQL 兼容：DEFAULT <literal | identifier>
                Advance();
                var v = Peek();
                // 支持负号前缀
                var sign = String.Empty;
                if (v.Type == SqlTokenType.Minus)
                {
                    sign = "-";
                    Advance();
                    v = Peek();
                }
                if (v.Type == SqlTokenType.StringLiteral || v.Type == SqlTokenType.IntegerLiteral
                    || v.Type == SqlTokenType.FloatLiteral || v.Type == SqlTokenType.Null
                    || v.Type == SqlTokenType.True || v.Type == SqlTokenType.False
                    || v.Type == SqlTokenType.Identifier)
                {
                    col.DefaultValue = sign + v.Value;
                    Advance();
                    // 兼容 CURRENT_TIMESTAMP() / NOW() 等带括号形式，跳过参数列表
                    if (Peek().Type == SqlTokenType.LeftParen)
                    {
                        Advance();
                        var depth = 1;
                        while (depth > 0 && Peek().Type != SqlTokenType.Eof)
                        {
                            if (Peek().Type == SqlTokenType.LeftParen) depth++;
                            else if (Peek().Type == SqlTokenType.RightParen) depth--;
                            Advance();
                        }
                    }
                }
                else
                {
                    throw SyntaxError($"Expected default value, got '{v.Value}' ({v.Type}) at position {v.Position}");
                }
            }
            else
            {
                break;
            }
        }

        return col;
    }

    private CreateIndexStatement ParseCreateIndex()
    {
        var stmt = new CreateIndexStatement();

        if (Peek().Type == SqlTokenType.Unique)
        {
            Advance();
            stmt.IsUnique = true;
        }

        Expect(SqlTokenType.Index);
        stmt.IndexName = ExpectIdentifier();
        Expect(SqlTokenType.On);
        stmt.TableName = ParseTableName();
        Expect(SqlTokenType.LeftParen);

        do
        {
            stmt.Columns.Add(ExpectIdentifier());
            // 兼容索引前缀长度，如 name(20)
            SkipOptionalParens();
        }
        while (TryConsume(SqlTokenType.Comma));

        Expect(SqlTokenType.RightParen);
        return stmt;
    }

    /// <summary>解析表级选项（ENGINE 和 COMMENT）</summary>
    private void ParseTableOptions(CreateTableStatement stmt)
    {
        while (Peek().Type != SqlTokenType.Eof && Peek().Type != SqlTokenType.Semicolon)
        {
            var next = Peek();
            if (next.Type == SqlTokenType.Identifier && String.Equals(next.Value, "ENGINE", StringComparison.OrdinalIgnoreCase))
            {
                Advance();
                TryConsume(SqlTokenType.Equals);
                stmt.EngineName = ExpectIdentifier();
            }
            else if (next.Type == SqlTokenType.Comment)
            {
                Advance();
                TryConsume(SqlTokenType.Equals);
                stmt.Comment = Expect(SqlTokenType.StringLiteral).Value;
            }
            else if (next.Type == SqlTokenType.Identifier &&
                (next.Value.EqualIgnoreCase("DEFAULT") || next.Value.EqualIgnoreCase("CHARSET")
                || next.Value.EqualIgnoreCase("CHARACTER") || next.Value.EqualIgnoreCase("COLLATE")
                || next.Value.EqualIgnoreCase("AUTO_INCREMENT") || next.Value.EqualIgnoreCase("ROW_FORMAT")))
            {
                // MySQL 兼容：DEFAULT CHARSET=utf8mb4 / CHARACTER SET=xxx / COLLATE=xxx / AUTO_INCREMENT=1 / ROW_FORMAT=DYNAMIC
                Advance();
                // 可能后续还有一个修饰词（DEFAULT CHARSET / CHARACTER SET）
                if (Peek().Type == SqlTokenType.Identifier &&
                    (Peek().Value.EqualIgnoreCase("CHARSET") || Peek().Value.EqualIgnoreCase("SET")))
                    Advance();
                TryConsume(SqlTokenType.Equals);
                // 消费一个标识符/字面量作为值
                var v = Peek();
                if (v.Type == SqlTokenType.Identifier || v.Type == SqlTokenType.StringLiteral
                    || v.Type == SqlTokenType.IntegerLiteral || v.Type == SqlTokenType.FloatLiteral)
                    Advance();
            }
            else
            {
                break;
            }
        }
    }

    private CreateDatabaseStatement ParseCreateDatabase()
    {
        Expect(SqlTokenType.Database);

        var stmt = new CreateDatabaseStatement();

        if (Peek().Type == SqlTokenType.If)
        {
            Advance();
            Expect(SqlTokenType.Not);
            Expect(SqlTokenType.Exists);
            stmt.IfNotExists = true;
        }

        stmt.DatabaseName = ExpectIdentifier();

        // 消费可选尾部选项：DEFAULT CHARACTER SET utf8mb4 / DEFAULT COLLATE xxx（MySQL 兼容，忽略内容）
        while (Peek().Type != SqlTokenType.Semicolon && Peek().Type != SqlTokenType.Eof)
        {
            if (Peek().Type == SqlTokenType.StringLiteral || Peek().Type == SqlTokenType.Identifier
                || Peek().Type == SqlTokenType.IntegerLiteral)
            {
                Advance();
                // CHARACTER SET 名称可能带 = 或直接跟值
                continue;
            }
            Advance();
        }

        return stmt;
    }

    private SqlStatement ParseDrop()
    {
        Expect(SqlTokenType.Drop);

        var next = Peek();
        if (next.Type == SqlTokenType.Table)
            return ParseDropTable();
        if (next.Type == SqlTokenType.Index)
            return ParseDropIndex();
        if (next.Type == SqlTokenType.Database)
            return ParseDropDatabase();

        throw SyntaxError($"Expected TABLE, INDEX or DATABASE after DROP, got '{next.Value}'");
    }

    private DropTableStatement ParseDropTable()
    {
        Expect(SqlTokenType.Table);

        var stmt = new DropTableStatement();

        if (Peek().Type == SqlTokenType.If)
        {
            Advance();
            Expect(SqlTokenType.Exists);
            stmt.IfExists = true;
        }

        stmt.TableName = ParseTableName();
        return stmt;
    }

    private DropIndexStatement ParseDropIndex()
    {
        Expect(SqlTokenType.Index);

        var stmt = new DropIndexStatement
        {
            IndexName = ExpectIdentifier()
        };

        Expect(SqlTokenType.On);
        stmt.TableName = ParseTableName();
        return stmt;
    }

    private DropDatabaseStatement ParseDropDatabase()
    {
        Expect(SqlTokenType.Database);

        var stmt = new DropDatabaseStatement();

        if (Peek().Type == SqlTokenType.If)
        {
            Advance();
            Expect(SqlTokenType.Exists);
            stmt.IfExists = true;
        }

        stmt.DatabaseName = ExpectIdentifier();
        return stmt;
    }

    private AlterTableStatement ParseAlter()
    {
        Expect(SqlTokenType.Alter);
        Expect(SqlTokenType.Table);

        var stmt = new AlterTableStatement
        {
            TableName = ParseTableName()
        };

        var next = Peek();

        // ALTER TABLE t ADD [COLUMN] col_def
        if (next.Type == SqlTokenType.Add)
        {
            Advance();
            TryConsume(SqlTokenType.Column);
            stmt.Action = AlterTableAction.AddColumn;
            stmt.ColumnDef = ParseColumnDef();
        }
        // ALTER TABLE t MODIFY [COLUMN] col_def
        else if (next.Type == SqlTokenType.Modify)
        {
            Advance();
            TryConsume(SqlTokenType.Column);
            stmt.Action = AlterTableAction.ModifyColumn;
            stmt.ColumnDef = ParseColumnDef();
        }
        // ALTER TABLE t DROP [COLUMN] col_name
        else if (next.Type == SqlTokenType.Drop)
        {
            Advance();
            TryConsume(SqlTokenType.Column);
            stmt.Action = AlterTableAction.DropColumn;
            stmt.ColumnName = ExpectIdentifier();
        }
        // ALTER TABLE t CHANGE [COLUMN] old_col new_col_def
        else if (next.Type == SqlTokenType.Identifier && next.Value.EqualIgnoreCase("CHANGE"))
        {
            Advance();
            TryConsume(SqlTokenType.Column);
            stmt.Action = AlterTableAction.ChangeColumn;
            stmt.ColumnName = ExpectIdentifier();
            stmt.ColumnDef = ParseColumnDef();
        }
        // ALTER TABLE t RENAME [TO|AS] new_name
        else if (next.Type == SqlTokenType.Identifier && next.Value.EqualIgnoreCase("RENAME"))
        {
            Advance();
            if (Peek().Type == SqlTokenType.As ||
                (Peek().Type == SqlTokenType.Identifier && Peek().Value.EqualIgnoreCase("TO")))
                Advance();
            stmt.Action = AlterTableAction.RenameTable;
            stmt.NewTableName = ParseTableName();
        }
        // ALTER TABLE t COMMENT 'xxx' (表注释)
        else if (next.Type == SqlTokenType.Comment)
        {
            Advance();
            TryConsume(SqlTokenType.Equals);
            stmt.Action = AlterTableAction.AddTableComment;
            stmt.Comment = Expect(SqlTokenType.StringLiteral).Value;
        }
        else
        {
            throw SyntaxError($"Expected ADD, MODIFY, CHANGE, DROP, COMMENT or RENAME after ALTER TABLE, got '{next.Value}'");
        }

        return stmt;
    }

    /// <summary>解析 RENAME TABLE old TO new [, old2 TO new2]</summary>
    /// <returns>重命名语句</returns>
    private RenameTableStatement ParseRenameTable()
    {
        // RENAME 未纳入 SqlTokenType（避免与同名列冲突），按值判断
        var token = Advance();
        if (!String.Equals(token.Value, "RENAME", StringComparison.OrdinalIgnoreCase))
            throw SyntaxError($"Expected 'RENAME' but got '{token.Value}' at position {token.Position}");

        Expect(SqlTokenType.Table);

        var stmt = new RenameTableStatement();
        do
        {
            var from = ParseTableName();
            // TO 未纳入关键字表，按值消费
            if (Peek().Type == SqlTokenType.Identifier && Peek().Value.EqualIgnoreCase("TO"))
                Advance();
            else
                throw SyntaxError($"Expected TO after '{from}', got '{Peek().Value}' at position {Peek().Position}");

            stmt.Items.Add((from, ParseTableName()));
        }
        while (TryConsume(SqlTokenType.Comma));

        return stmt;
    }

    private TruncateTableStatement ParseTruncate()
    {
        Expect(SqlTokenType.Truncate);
        Expect(SqlTokenType.Table);

        return new TruncateTableStatement
        {
            TableName = ParseTableName()
        };
    }

    #endregion

    #region DML

    private ReplaceStatement ParseReplace()
    {
        // REPLACE 是关键字但不在 SqlTokenType 中，避免与 REPLACE() 函数冲突
        var token = Advance();
        if (!String.Equals(token.Value, "REPLACE", StringComparison.OrdinalIgnoreCase))
            throw SyntaxError($"Expected 'REPLACE' but got '{token.Value}' at position {token.Position}");
        Expect(SqlTokenType.Into);

        var tableName = ParseTableName();

        // 可选的列名列表
        List<String>? columns = null;
        if (Peek().Type == SqlTokenType.LeftParen)
        {
            Advance();
            columns = [];

            do
            {
                columns.Add(ExpectIdentifier());
            }
            while (TryConsume(SqlTokenType.Comma));

            Expect(SqlTokenType.RightParen);
        }

        Expect(SqlTokenType.Values);

        // 解析一组或多组值
        var valuesList = new List<List<SqlExpression>>();
        do
        {
            Expect(SqlTokenType.LeftParen);
            var values = new List<SqlExpression>();

            do
            {
                values.Add(ParseExpression());
            }
            while (TryConsume(SqlTokenType.Comma));

            Expect(SqlTokenType.RightParen);
            valuesList.Add(values);
        }
        while (TryConsume(SqlTokenType.Comma));

        return new ReplaceStatement
        {
            TableName = tableName,
            Columns = columns,
            ValuesList = valuesList
        };
    }

    private SqlStatement ParseInsert()
    {
        Expect(SqlTokenType.Insert);
        // 可选的 IGNORE 关键字（Insert Ignore Into ...）
        var ignore = false;
        if (Peek().Type == SqlTokenType.Ignore)
        {
            Advance();
            ignore = true;
        }
        Expect(SqlTokenType.Into);

        var tableName = ParseTableName();

        // 可选的列名列表
        List<String>? columns = null;
        if (Peek().Type == SqlTokenType.LeftParen)
        {
            Advance();
            columns = [];

            do
            {
                columns.Add(ExpectIdentifier());
            }
            while (TryConsume(SqlTokenType.Comma));

            Expect(SqlTokenType.RightParen);
        }

        Expect(SqlTokenType.Values);

        // 解析一组或多组值
        var valuesList = new List<List<SqlExpression>>();
        do
        {
            Expect(SqlTokenType.LeftParen);
            var values = new List<SqlExpression>();

            do
            {
                values.Add(ParseExpression());
            }
            while (TryConsume(SqlTokenType.Comma));

            Expect(SqlTokenType.RightParen);
            valuesList.Add(values);
        }
        while (TryConsume(SqlTokenType.Comma));

        // 检查 ON DUPLICATE KEY UPDATE 子句
        if (Peek().Type == SqlTokenType.On)
        {
            Advance();
            Expect(SqlTokenType.Duplicate);
            Expect(SqlTokenType.Key);
            Expect(SqlTokenType.Update);

            var upsert = new UpsertStatement
            {
                TableName = tableName,
                Columns = columns,
                ValuesList = valuesList
            };

            // 解析 SET 子句
            do
            {
                var column = ExpectIdentifier();
                Expect(SqlTokenType.Equals);
                var value = ParseExpression();
                upsert.UpdateClauses.Add((column, value));
            }
            while (TryConsume(SqlTokenType.Comma));

            return upsert;
        }

        return new InsertStatement
        {
            TableName = tableName,
            Columns = columns,
            ValuesList = valuesList,
            Ignore = ignore
        };
    }

    private MergeStatement ParseMerge()
    {
        Expect(SqlTokenType.Merge);
        Expect(SqlTokenType.Into);

        var tableName = ParseTableName();

        // 可选的列名列表
        List<String>? columns = null;
        if (Peek().Type == SqlTokenType.LeftParen)
        {
            Advance();
            columns = [];

            do
            {
                columns.Add(ExpectIdentifier());
            }
            while (TryConsume(SqlTokenType.Comma));

            Expect(SqlTokenType.RightParen);
        }

        Expect(SqlTokenType.Values);

        // 解析一组或多组值
        var valuesList = new List<List<SqlExpression>>();
        do
        {
            Expect(SqlTokenType.LeftParen);
            var values = new List<SqlExpression>();

            do
            {
                values.Add(ParseExpression());
            }
            while (TryConsume(SqlTokenType.Comma));

            Expect(SqlTokenType.RightParen);
            valuesList.Add(values);
        }
        while (TryConsume(SqlTokenType.Comma));

        return new MergeStatement
        {
            TableName = tableName,
            Columns = columns,
            ValuesList = valuesList
        };
    }

    private UpdateStatement ParseUpdate()
    {
        Expect(SqlTokenType.Update);

        var stmt = new UpdateStatement
        {
            TableName = ParseTableName()
        };

        Expect(SqlTokenType.Set);

        do
        {
            var column = ExpectIdentifier();
            Expect(SqlTokenType.Equals);
            var value = ParseExpression();
            stmt.SetClauses.Add((column, value));
        }
        while (TryConsume(SqlTokenType.Comma));

        // WHERE 子句
        if (Peek().Type == SqlTokenType.Where)
        {
            Advance();
            stmt.Where = ParseExpression();
        }

        return stmt;
    }

    private DeleteStatement ParseDelete()
    {
        Expect(SqlTokenType.Delete);
        Expect(SqlTokenType.From);

        var stmt = new DeleteStatement
        {
            TableName = ParseTableName()
        };

        if (Peek().Type == SqlTokenType.Where)
        {
            Advance();
            stmt.Where = ParseExpression();
        }

        return stmt;
    }

    #endregion

    #region SELECT

    private SelectStatement ParseSelect()
    {
        Expect(SqlTokenType.Select);

        var stmt = new SelectStatement();

        // 投影列
        if (Peek().Type == SqlTokenType.Star)
        {
            Advance();
            stmt.Columns.Add(new SelectColumn { IsWildcard = true });
        }
        else
        {
            do
            {
                stmt.Columns.Add(ParseSelectColumn());
            }
            while (TryConsume(SqlTokenType.Comma));
        }

        // FROM
        if (Peek().Type == SqlTokenType.From)
        {
            Advance();
            stmt.TableName = ParseTableName();

            // 可选表别名
            if (Peek().Type == SqlTokenType.As)
            {
                Advance();
                stmt.TableAlias = ExpectAlias();
            }
            else if (Peek().Type == SqlTokenType.Identifier)
            {
                // 无 AS 的表别名（排除行锁提示，避免 LOCK/FOR 被当成别名）
                var next = Peek();
                if (!IsClauseKeyword(next.Type) && !IsLockingHintAhead())
                    stmt.TableAlias = ExpectIdentifier();
            }

            // JOIN 子句
            while (IsJoinKeyword(Peek().Type))
            {
                stmt.Joins ??= [];
                stmt.Joins.Add(ParseJoinClause());
            }
        }

        // WHERE
        if (Peek().Type == SqlTokenType.Where)
        {
            Advance();
            stmt.Where = ParseExpression();
        }

        // GROUP BY
        if (Peek().Type == SqlTokenType.Group)
        {
            Advance();
            Expect(SqlTokenType.By);
            stmt.GroupBy = [];

            // 会话/状态窗口函数（F17）：与普通列混合分组时按位置对齐
            List<SqlExpression?>? windows = null;
            do
            {
                var item = Peek();
                if (item.Type == SqlTokenType.Identifier && IsWindowFunctionName(item.Value) && PeekAt(1).Type == SqlTokenType.LeftParen)
                {
                    var func = ParsePrimary();
                    windows ??= FillNulls(stmt.GroupBy.Count);
                    windows.Add(func);
                    stmt.GroupBy.Add(item.Value.ToUpperInvariant());
                }
                else
                {
                    stmt.GroupBy.Add(ExpectIdentifier());
                    windows?.Add(null);
                }
            }
            while (TryConsume(SqlTokenType.Comma));

            stmt.GroupByWindows = windows;
        }

        // HAVING
        if (Peek().Type == SqlTokenType.Having)
        {
            Advance();
            stmt.Having = ParseExpression();
        }

        // ORDER BY
        if (Peek().Type == SqlTokenType.Order)
        {
            Advance();
            Expect(SqlTokenType.By);
            stmt.OrderBy = [];

            do
            {
                // 支持别名限定列名（如 e.name），MySQL 兼容
                var name = ExpectIdentifier();
                while (Peek().Type == SqlTokenType.Dot)
                {
                    Advance();
                    name += "." + ExpectIdentifier();
                }

                var clause = new OrderByClause { ColumnName = name };
                if (Peek().Type == SqlTokenType.Desc)
                {
                    Advance();
                    clause.Descending = true;
                }
                else if (Peek().Type == SqlTokenType.Asc)
                {
                    Advance();
                }
                stmt.OrderBy.Add(clause);
            }
            while (TryConsume(SqlTokenType.Comma));
        }

        // LIMIT
        // LIMIT count [OFFSET offset] 或 MySQL 逗号分页 LIMIT offset,count
        if (Peek().Type == SqlTokenType.Limit)
        {
            Advance();
            var first = Int32.Parse(Expect(SqlTokenType.IntegerLiteral).Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (Peek().Type == SqlTokenType.Comma)
            {
                Advance();
                stmt.OffsetValue = first;
                stmt.Limit = Int32.Parse(Expect(SqlTokenType.IntegerLiteral).Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            }
            else
                stmt.Limit = first;
        }

        // OFFSET
        if (Peek().Type == SqlTokenType.Offset)
        {
            Advance();
            stmt.OffsetValue = Int32.Parse(Expect(SqlTokenType.IntegerLiteral).Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        // 行锁提示：语法接受，MVCC Read Committed 不提供悲观锁
        ParseLockingClause(stmt);

        return stmt;
    }

    /// <summary>解析行锁提示（FOR UPDATE / FOR SHARE / LOCK IN SHARE MODE）</summary>
    /// <remarks>仅记录语法以兼容 ORM 自动追加的锁定子句，执行层不加行锁</remarks>
    /// <param name="stmt">查询语句</param>
    private void ParseLockingClause(SelectStatement stmt)
    {
        var token = Peek();
        if (token.Type != SqlTokenType.Identifier) return;

        if (token.Value.EqualIgnoreCase("FOR"))
        {
            if (_pos + 1 >= _tokens.Count) return;

            // UPDATE 是关键字 token，SHARE 是普通标识符
            var next = _tokens[_pos + 1];
            if (next.Type != SqlTokenType.Update &&
                (next.Type != SqlTokenType.Identifier || !next.Value.EqualIgnoreCase("SHARE")))
                return;

            Advance();
            Advance();
        }
        else if (token.Value.EqualIgnoreCase("LOCK"))
        {
            // LOCK IN SHARE MODE
            if (_pos + 2 >= _tokens.Count) return;
            if (_tokens[_pos + 1].Type != SqlTokenType.In) return;
            if (_tokens[_pos + 2].Type != SqlTokenType.Identifier || !_tokens[_pos + 2].Value.EqualIgnoreCase("SHARE")) return;

            Advance();
            Advance();
            Advance();
            if (Peek().Type == SqlTokenType.Identifier && Peek().Value.EqualIgnoreCase("MODE")) Advance();
        }
        else
            return;

        stmt.ForUpdate = true;

        // 可选 OF table_list
        if (Peek().Type == SqlTokenType.Identifier && Peek().Value.EqualIgnoreCase("OF"))
        {
            Advance();
            while (Peek().Type == SqlTokenType.Identifier)
            {
                Advance();
                if (!TryConsume(SqlTokenType.Comma)) break;
            }
        }

        // 可选 NOWAIT / SKIP LOCKED
        var tail = Peek();
        if (tail.Type != SqlTokenType.Identifier) return;

        if (tail.Value.EqualIgnoreCase("NOWAIT"))
            Advance();
        else if (tail.Value.EqualIgnoreCase("SKIP"))
        {
            Advance();
            if (Peek().Type == SqlTokenType.Identifier && Peek().Value.EqualIgnoreCase("LOCKED")) Advance();
        }
    }

    /// <summary>当前位置是否紧跟行锁提示，避免把 FOR / LOCK 误解析成表别名</summary>
    /// <returns>是否为行锁提示</returns>
    private Boolean IsLockingHintAhead()
    {
        var token = Peek();
        if (token.Type != SqlTokenType.Identifier) return false;

        var next = PeekAt(1);
        if (token.Value.EqualIgnoreCase("FOR"))
            return next.Type == SqlTokenType.Update ||
                (next.Type == SqlTokenType.Identifier && next.Value.EqualIgnoreCase("SHARE"));

        if (token.Value.EqualIgnoreCase("LOCK") && next.Type == SqlTokenType.In)
        {
            var next2 = PeekAt(2);
            return next2.Type == SqlTokenType.Identifier && next2.Value.EqualIgnoreCase("SHARE");
        }

        return false;
    }

    private SelectColumn ParseSelectColumn()
    {
        var col = new SelectColumn();

        // 检查聚合函数
        var funcType = Peek().Type;
        if (funcType is SqlTokenType.Count or SqlTokenType.Sum or SqlTokenType.Avg or SqlTokenType.Min or SqlTokenType.Max
            or SqlTokenType.StringAgg or SqlTokenType.GroupConcat or SqlTokenType.Stddev or SqlTokenType.Variance)
        {
            col.Expression = ParseFunction();
        }
        else
        {
            col.Expression = ParseExpression();
        }

        // 别名（AS 后可跟字符串字面量，兼容系统视图内省 SQL）
        if (Peek().Type == SqlTokenType.As)
        {
            Advance();
            col.Alias = ExpectAlias();
        }
        else if (Peek().Type == SqlTokenType.Identifier || Peek().Type == SqlTokenType.StringLiteral)
        {
            // 无 AS 关键字的别名
            col.Alias = ExpectAlias();
        }

        return col;
    }

    private JoinClause ParseJoinClause()
    {
        var joinType = JoinType.Inner;

        var token = Peek();
        if (token.Type == SqlTokenType.Left)
        {
            Advance();
            joinType = JoinType.Left;
            // 可选 OUTER
            TryConsume(SqlTokenType.Join); // LEFT JOIN 中的 JOIN 可能紧跟
            if (Peek().Type == SqlTokenType.Join) Advance();
        }
        else if (token.Type == SqlTokenType.Right)
        {
            Advance();
            joinType = JoinType.Right;
            TryConsume(SqlTokenType.Join);
            if (Peek().Type == SqlTokenType.Join) Advance();
        }
        else if (token.Type == SqlTokenType.Inner)
        {
            Advance();
            Expect(SqlTokenType.Join);
            joinType = JoinType.Inner;
        }
        else if (token.Type == SqlTokenType.Join)
        {
            Advance();
            joinType = JoinType.Inner;
        }
        else
        {
            throw SyntaxError($"Expected JOIN keyword, got '{token.Value}'");
        }

        var clause = new JoinClause
        {
            Type = joinType,
            TableName = ParseTableName()
        };

        // 可选别名
        if (Peek().Type == SqlTokenType.As)
        {
            Advance();
            clause.Alias = ExpectAlias();
        }
        else if (Peek().Type == SqlTokenType.Identifier && !IsClauseKeyword(Peek().Type) && Peek().Type != SqlTokenType.On)
        {
            clause.Alias = ExpectIdentifier();
        }

        // ON 条件
        Expect(SqlTokenType.On);
        clause.Condition = ParseExpression();

        return clause;
    }

    private static Boolean IsJoinKeyword(SqlTokenType type) =>
        type is SqlTokenType.Join or SqlTokenType.Left or SqlTokenType.Right or SqlTokenType.Inner;

    private static Boolean IsClauseKeyword(SqlTokenType type) =>
        type is SqlTokenType.Where or SqlTokenType.Group or SqlTokenType.Having
            or SqlTokenType.Order or SqlTokenType.Limit or SqlTokenType.Offset
            or SqlTokenType.Join or SqlTokenType.Left or SqlTokenType.Right or SqlTokenType.Inner
            or SqlTokenType.On or SqlTokenType.Eof or SqlTokenType.Semicolon;

    #endregion

    #region 表达式解析

    private SqlExpression ParseExpression() => ParseOr();

    private SqlExpression ParseOr()
    {
        var left = ParseAnd();

        while (Peek().Type == SqlTokenType.Or)
        {
            Advance();
            var right = ParseAnd();
            left = new BinaryExpression { Left = left, Operator = BinaryOperator.Or, Right = right };
        }

        return left;
    }

    private SqlExpression ParseAnd()
    {
        var left = ParseComparison();

        while (Peek().Type == SqlTokenType.And)
        {
            Advance();
            var right = ParseComparison();
            left = new BinaryExpression { Left = left, Operator = BinaryOperator.And, Right = right };
        }

        return left;
    }

    private SqlExpression ParseComparison()
    {
        var left = ParseAddSub();

        var next = Peek();

        // IS NULL / IS NOT NULL
        if (next.Type == SqlTokenType.Is)
        {
            Advance();
            var isNot = false;
            if (Peek().Type == SqlTokenType.Not)
            {
                Advance();
                isNot = true;
            }
            Expect(SqlTokenType.Null);
            return new IsNullExpression { Operand = left, IsNot = isNot };
        }

        // NOT IN
        if (next.Type == SqlTokenType.Not && PeekAt(1).Type == SqlTokenType.In)
        {
            Advance(); // NOT
            Advance(); // IN
            return ParseInList(left, isNot: true);
        }

        // IN
        if (next.Type == SqlTokenType.In)
        {
            Advance();
            return ParseInList(left, isNot: false);
        }

        // LIKE
        if (next.Type == SqlTokenType.Like)
        {
            Advance();
            var right = ParseAddSub();
            return new BinaryExpression { Left = left, Operator = BinaryOperator.Like, Right = right };
        }

        // 比较运算符
        var op = next.Type switch
        {
            SqlTokenType.Equals => (BinaryOperator?)BinaryOperator.Equal,
            SqlTokenType.NotEquals => BinaryOperator.NotEqual,
            SqlTokenType.LessThan => BinaryOperator.LessThan,
            SqlTokenType.GreaterThan => BinaryOperator.GreaterThan,
            SqlTokenType.LessThanOrEqual => BinaryOperator.LessOrEqual,
            SqlTokenType.GreaterThanOrEqual => BinaryOperator.GreaterOrEqual,
            _ => null
        };

        if (op.HasValue)
        {
            Advance();
            var right = ParseAddSub();
            return new BinaryExpression { Left = left, Operator = op.Value, Right = right };
        }

        return left;
    }

    /// <summary>解析 IN 列表：(expr, expr, ...) 或 (SELECT ...)</summary>
    /// <param name="operand">左操作数</param>
    /// <param name="isNot">是否 NOT IN</param>
    private InExpression ParseInList(SqlExpression operand, Boolean isNot)
    {
        Expect(SqlTokenType.LeftParen);
        var inExpr = new InExpression { Operand = operand, IsNot = isNot };

        // 检查是否为子查询
        if (Peek().Type == SqlTokenType.Select)
        {
            inExpr.Subquery = ParseSelect();
        }
        else
        {
            // 值列表
            inExpr.Values.Add(ParseExpression());
            while (TryConsume(SqlTokenType.Comma))
            {
                inExpr.Values.Add(ParseExpression());
            }
        }

        Expect(SqlTokenType.RightParen);
        return inExpr;
    }

    private SqlExpression ParseAddSub()
    {
        var left = ParseMulDiv();

        while (Peek().Type is SqlTokenType.Plus or SqlTokenType.Minus)
        {
            var op = Advance().Type == SqlTokenType.Plus ? BinaryOperator.Add : BinaryOperator.Subtract;
            var right = ParseMulDiv();
            left = new BinaryExpression { Left = left, Operator = op, Right = right };
        }

        return left;
    }

    private SqlExpression ParseMulDiv()
    {
        var left = ParseUnary();

        while (Peek().Type is SqlTokenType.Star or SqlTokenType.Slash or SqlTokenType.Percent)
        {
            var tok = Advance();
            var op = tok.Type switch
            {
                SqlTokenType.Star => BinaryOperator.Multiply,
                SqlTokenType.Slash => BinaryOperator.Divide,
                SqlTokenType.Percent => BinaryOperator.Modulo,
                _ => throw SyntaxError($"Unexpected operator: {tok.Value}")
            };
            var right = ParseUnary();
            left = new BinaryExpression { Left = left, Operator = op, Right = right };
        }

        return left;
    }

    private SqlExpression ParseUnary()
    {
        if (Peek().Type == SqlTokenType.Not)
        {
            Advance();
            var operand = ParseUnary();
            return new UnaryExpression { Operator = "NOT", Operand = operand };
        }

        if (Peek().Type == SqlTokenType.Minus)
        {
            Advance();
            var operand = ParsePrimary();
            return new UnaryExpression { Operator = "-", Operand = operand };
        }

        return ParsePrimary();
    }

    private SqlExpression ParsePrimary()
    {
        var token = Peek();

        switch (token.Type)
        {
            case SqlTokenType.IntegerLiteral:
                Advance();
                return new LiteralExpression
                {
                    Value = Int64.Parse(token.Value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    DataType = Int64.Parse(token.Value, NumberStyles.Integer, CultureInfo.InvariantCulture) is >= Int32.MinValue and <= Int32.MaxValue ? DataType.Int32 : DataType.Int64
                };

            case SqlTokenType.FloatLiteral:
                Advance();
                return new LiteralExpression { Value = Double.Parse(token.Value, NumberStyles.Float, CultureInfo.InvariantCulture), DataType = DataType.Double };

            case SqlTokenType.StringLiteral:
                Advance();
                return new LiteralExpression { Value = token.Value, DataType = DataType.String };

            case SqlTokenType.True:
                Advance();
                return new LiteralExpression { Value = true, DataType = DataType.Boolean };

            case SqlTokenType.False:
                Advance();
                return new LiteralExpression { Value = false, DataType = DataType.Boolean };

            case SqlTokenType.Null:
                Advance();
                return new LiteralExpression { Value = null, DataType = DataType.String };

            case SqlTokenType.Parameter:
                Advance();
                return new ParameterExpression { ParameterName = token.Value };

            // UPSERT 插入值引用：VALUES(col)
            case SqlTokenType.Values:
                Advance();
                Expect(SqlTokenType.LeftParen);
                var valuesColumn = ExpectIdentifier();
                Expect(SqlTokenType.RightParen);
                return new ValuesExpression { ColumnName = valuesColumn };

            case SqlTokenType.LeftParen:
                Advance();
                var expr = ParseExpression();
                Expect(SqlTokenType.RightParen);
                return expr;

            // CASE WHEN 表达式
            case SqlTokenType.Case:
                return ParseCaseExpression();

            // CAST(expr AS type) 表达式
            case SqlTokenType.Cast:
                return ParseCastExpression();

            // 聚合函数
            case SqlTokenType.Count:
            case SqlTokenType.Sum:
            case SqlTokenType.Avg:
            case SqlTokenType.Min:
            case SqlTokenType.Max:
            case SqlTokenType.StringAgg:
            case SqlTokenType.GroupConcat:
            case SqlTokenType.Stddev:
            case SqlTokenType.Variance:
            case SqlTokenType.First:
            case SqlTokenType.Last:
            case SqlTokenType.Spread:
                return ParseFunction();

            case SqlTokenType.Star:
                Advance();
                return new ColumnRefExpression { ColumnName = "*" };

            // LEFT/RIGHT 关键字后跟 ( 时作为函数调用
            case SqlTokenType.Left:
            case SqlTokenType.Right:
                if (_pos + 1 < _tokens.Count && _tokens[_pos + 1].Type == SqlTokenType.LeftParen)
                {
                    Advance();
                    return ParseScalarFunction(token.Value);
                }
                throw SyntaxError($"Unexpected token '{token.Value}' at position {token.Position}");

            // IF 关键字后跟 ( 时作为 IF() 函数
            case SqlTokenType.If:
                if (_pos + 1 < _tokens.Count && _tokens[_pos + 1].Type == SqlTokenType.LeftParen)
                {
                    Advance();
                    return ParseScalarFunction(token.Value);
                }
                throw SyntaxError($"Unexpected token '{token.Value}' at position {token.Position}");

            // TRUNCATE 关键字后跟 ( 时作为 TRUNCATE() 数值函数
            case SqlTokenType.Truncate:
                if (_pos + 1 < _tokens.Count && _tokens[_pos + 1].Type == SqlTokenType.LeftParen)
                {
                    Advance();
                    return ParseScalarFunction(token.Value);
                }
                throw SyntaxError($"Unexpected token '{token.Value}' at position {token.Position}");

            case SqlTokenType.Identifier:
                Advance();
                // CURRENT_TIMESTAMP 无括号时返回 NOW()
                if (String.Equals(token.Value, "CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase))
                {
                    return new FunctionExpression { FunctionName = "CURRENT_TIMESTAMP", IsAggregate = false };
                }
                // 标识符后跟 ( 时作为标量函数调用
                if (Peek().Type == SqlTokenType.LeftParen)
                {
                    return ParseScalarFunction(token.Value);
                }
                // 表名前缀 table.column 或 库.table.column
                return ParseColumnRef(token.Value);

            default:
                // 软关键字（STATUS/COLUMNS/FULL/DATABASES 等）在表达式上下文中作为列名
                if (IsSoftKeyword(token.Type))
                {
                    Advance();
                    if (Peek().Type == SqlTokenType.LeftParen)
                        return ParseScalarFunction(token.Value);
                    return ParseColumnRef(token.Value);
                }
                throw SyntaxError($"Unexpected token '{token.Value}' at position {token.Position}");
        }
    }

    /// <summary>解析列引用，支持 table.column 与 库.table.column 两种限定写法</summary>
    /// <param name="name">已消费的首段名称</param>
    /// <returns>列引用表达式</returns>
    private ColumnRefExpression ParseColumnRef(String name)
    {
        if (Peek().Type != SqlTokenType.Dot) return new ColumnRefExpression { ColumnName = name };

        Advance();
        var second = ExpectIdentifier();
        if (Peek().Type == SqlTokenType.Dot)
        {
            // 库.table.column：单库模型丢弃库限定符
            Advance();
            return new ColumnRefExpression { TablePrefix = second, ColumnName = ExpectIdentifier() };
        }

        return new ColumnRefExpression { TablePrefix = name, ColumnName = second };
    }

    /// <summary>解析标量函数调用</summary>
    /// <param name="functionName">函数名</param>
    /// <returns>函数表达式</returns>
    private FunctionExpression ParseScalarFunction(String functionName)
    {
        Expect(SqlTokenType.LeftParen);

        var func = new FunctionExpression
        {
            FunctionName = functionName.ToUpperInvariant(),
            IsAggregate = false
        };

        if (Peek().Type != SqlTokenType.RightParen)
        {
            do
            {
                func.Arguments.Add(ParseExpression());
            }
            while (TryConsume(SqlTokenType.Comma));
        }

        Expect(SqlTokenType.RightParen);
        return func;
    }

    /// <summary>判断是否为 GROUP BY 窗口分组函数（SESSION_WINDOW / STATE_WINDOW）</summary>
    /// <param name="name">函数名</param>
    /// <returns>是否支持</returns>
    private static Boolean IsWindowFunctionName(String name) =>
        name.EqualIgnoreCase("SESSION_WINDOW") || name.EqualIgnoreCase("STATE_WINDOW");

    /// <summary>创建指定长度的空位列表，用于对齐已解析的分组项</summary>
    /// <param name="count">空位数量</param>
    /// <returns>填充 null 的列表</returns>
    private static List<SqlExpression?> FillNulls(Int32 count)
    {
        var list = new List<SqlExpression?>(count + 1);
        for (var i = 0; i < count; i++)
        {
            list.Add(null);
        }

        return list;
    }

    /// <summary>解析 CASE WHEN ... THEN ... ELSE ... END 表达式</summary>
    /// <returns>CASE 表达式</returns>
    private CaseExpression ParseCaseExpression()
    {
        Expect(SqlTokenType.Case);
        var caseExpr = new CaseExpression();

        while (Peek().Type == SqlTokenType.When)
        {
            Advance();
            var whenExpr = ParseExpression();
            Expect(SqlTokenType.Then);
            var thenExpr = ParseExpression();
            caseExpr.WhenClauses.Add((whenExpr, thenExpr));
        }

        if (Peek().Type == SqlTokenType.Else)
        {
            Advance();
            caseExpr.ElseExpression = ParseExpression();
        }

        Expect(SqlTokenType.End);
        return caseExpr;
    }

    /// <summary>解析 CAST(expr AS type) 表达式</summary>
    /// <returns>CAST 表达式</returns>
    private CastExpression ParseCastExpression()
    {
        Expect(SqlTokenType.Cast);
        Expect(SqlTokenType.LeftParen);

        var operand = ParseExpression();
        Expect(SqlTokenType.As);
        var typeName = ExpectIdentifier();

        Expect(SqlTokenType.RightParen);

        return new CastExpression { Operand = operand, TargetTypeName = typeName };
    }

    private FunctionExpression ParseFunction()
    {
        var funcToken = Advance();
        Expect(SqlTokenType.LeftParen);

        var func = new FunctionExpression
        {
            FunctionName = funcToken.Value.ToUpperInvariant(),
            IsAggregate = funcToken.Type is SqlTokenType.Count or SqlTokenType.Sum or SqlTokenType.Avg
                or SqlTokenType.Min or SqlTokenType.Max or SqlTokenType.StringAgg
                or SqlTokenType.GroupConcat or SqlTokenType.Stddev or SqlTokenType.Variance
                or SqlTokenType.First or SqlTokenType.Last or SqlTokenType.Spread
        };

        // 解析参数
        if (Peek().Type != SqlTokenType.RightParen)
        {
            // COUNT(*) 特殊处理
            if (Peek().Type == SqlTokenType.Star)
            {
                Advance();
                func.Arguments.Add(new ColumnRefExpression { ColumnName = "*" });
            }
            else
            {
                do
                {
                    func.Arguments.Add(ParseExpression());
                }
                while (TryConsume(SqlTokenType.Comma));
            }
        }

        Expect(SqlTokenType.RightParen);
        return func;
    }

    #endregion

    #region 辅助

    private SqlToken Peek() => _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];

    /// <summary>向前看指定偏移量的 Token</summary>
    /// <param name="offset">偏移量</param>
    private SqlToken PeekAt(Int32 offset) => _pos + offset < _tokens.Count ? _tokens[_pos + offset] : _tokens[^1];

    private SqlToken Advance()
    {
        var token = _tokens[_pos];
        _pos++;
        return token;
    }

    private SqlToken Expect(SqlTokenType type)
    {
        var token = Peek();
        if (token.Type != type)
            throw SyntaxError($"Expected {type}, got '{token.Value}' ({token.Type}) at position {token.Position}");

        return Advance();
    }

    private String ExpectIdentifier()
    {
        var token = Peek();
        if (token.Type != SqlTokenType.Identifier && !IsSoftKeyword(token.Type))
            throw SyntaxError($"Expected identifier, got '{token.Value}' ({token.Type}) at position {token.Position}");

        Advance();
        return token.Value;
    }

    /// <summary>解析表名，兼容 `库`.`表` 与 .`表` 写法</summary>
    /// <remarks>单库模型：丢弃库限定符只取表名；_sys. 与 information_schema. 系统视图前缀原样保留</remarks>
    /// <returns>表名</returns>
    private String ParseTableName()
    {
        // 容忍空的库限定符，如 .`t`
        if (Peek().Type == SqlTokenType.Dot)
        {
            Advance();
            return ExpectIdentifier();
        }

        var name = ExpectIdentifier();
        if (Peek().Type != SqlTokenType.Dot) return name;

        var parts = new List<String> { name };
        while (Peek().Type == SqlTokenType.Dot)
        {
            Advance();
            parts.Add(ExpectIdentifier());
        }

        // 系统视图两段名保持原样
        if (parts.Count == 2 &&
            (parts[0].EqualIgnoreCase("_sys") || parts[0].EqualIgnoreCase("information_schema")))
            return parts[0] + "." + parts[1];

        return parts[^1];
    }

    /// <summary>解析列别名，兼容 `AS 'xxx'` 字符串字面量写法（FreeSql 等 ORM 的系统视图内省 SQL）</summary>
    /// <returns>别名</returns>
    private String ExpectAlias()
    {
        var token = Peek();
        if (token.Type == SqlTokenType.StringLiteral)
        {
            Advance();
            return token.Value;
        }

        return ExpectIdentifier();
    }

    /// <summary>判断关键字是否可作为标识符（软关键字）</summary>
    private static Boolean IsSoftKeyword(SqlTokenType type) => type switch
    {
        SqlTokenType.Columns
        or SqlTokenType.Databases or SqlTokenType.Tables or SqlTokenType.Variables
        or SqlTokenType.Comment or SqlTokenType.Index
        or SqlTokenType.Column or SqlTokenType.Database or SqlTokenType.Key
        or SqlTokenType.Offset or SqlTokenType.Left
        or SqlTokenType.Right or SqlTokenType.Inner or SqlTokenType.Set => true,
        _ => false
    };

    private Boolean TryConsume(SqlTokenType type)
    {
        if (Peek().Type == type)
        {
            Advance();
            return true;
        }

        return false;
    }

    private static NovaException SyntaxError(String message) => new(ErrorCode.SyntaxError, message);

    #endregion
}
