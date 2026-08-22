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
            if (!DateTime.TryParse(dateToken.Value, out var beforeTime))
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
            throw new NovaException(ErrorCode.InvalidArgument, $"Invalid binlog file name: {fileName}");

        var idxStr = fileName.Substring(dot + 1);
        if (!Int32.TryParse(idxStr, out var index))
            throw new NovaException(ErrorCode.InvalidArgument, $"Invalid binlog file index: {idxStr}");

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

        stmt.TableName = ExpectIdentifier();
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
                Expect(SqlTokenType.RightParen);
                continue;
            }

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

        // 解析表级选项：ENGINE 和 COMMENT
        ParseTableOptions(stmt);

        return stmt;
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
        stmt.TableName = ExpectIdentifier();
        Expect(SqlTokenType.LeftParen);

        do
        {
            stmt.Columns.Add(ExpectIdentifier());
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

        stmt.TableName = ExpectIdentifier();
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
        stmt.TableName = ExpectIdentifier();
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
            TableName = ExpectIdentifier()
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
            throw SyntaxError($"Expected ADD, MODIFY, DROP or COMMENT after ALTER TABLE, got '{next.Value}'");
        }

        return stmt;
    }

    private TruncateTableStatement ParseTruncate()
    {
        Expect(SqlTokenType.Truncate);
        Expect(SqlTokenType.Table);

        return new TruncateTableStatement
        {
            TableName = ExpectIdentifier()
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

        var tableName = ExpectIdentifier();

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

        var tableName = ExpectIdentifier();

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

        var tableName = ExpectIdentifier();

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
            TableName = ExpectIdentifier()
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
            TableName = ExpectIdentifier()
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
            stmt.TableName = ExpectIdentifier();

            // 支持点号分隔的表名（如 _sys.tables）
            while (Peek().Type == SqlTokenType.Dot)
            {
                Advance();
                stmt.TableName += "." + ExpectIdentifier();
            }

            // 可选表别名
            if (Peek().Type == SqlTokenType.As)
            {
                Advance();
                stmt.TableAlias = ExpectIdentifier();
            }
            else if (Peek().Type == SqlTokenType.Identifier)
            {
                // 无 AS 的表别名（需排除后续关键字）
                var next = Peek();
                if (!IsClauseKeyword(next.Type))
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

            do
            {
                stmt.GroupBy.Add(ExpectIdentifier());
            }
            while (TryConsume(SqlTokenType.Comma));
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
        if (Peek().Type == SqlTokenType.Limit)
        {
            Advance();
            stmt.Limit = Int32.Parse(Expect(SqlTokenType.IntegerLiteral).Value);
        }

        // OFFSET
        if (Peek().Type == SqlTokenType.Offset)
        {
            Advance();
            stmt.OffsetValue = Int32.Parse(Expect(SqlTokenType.IntegerLiteral).Value);
        }

        return stmt;
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

        // 别名
        if (Peek().Type == SqlTokenType.As)
        {
            Advance();
            col.Alias = ExpectIdentifier();
        }
        else if (Peek().Type == SqlTokenType.Identifier)
        {
            // 无 AS 关键字的别名
            col.Alias = ExpectIdentifier();
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
            TableName = ExpectIdentifier()
        };

        // 可选别名
        if (Peek().Type == SqlTokenType.As)
        {
            Advance();
            clause.Alias = ExpectIdentifier();
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
                    Value = Int64.Parse(token.Value),
                    DataType = Int64.Parse(token.Value) is >= Int32.MinValue and <= Int32.MaxValue ? DataType.Int32 : DataType.Int64
                };

            case SqlTokenType.FloatLiteral:
                Advance();
                return new LiteralExpression { Value = Double.Parse(token.Value), DataType = DataType.Double };

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
                // 检查表名前缀 table.column
                if (Peek().Type == SqlTokenType.Dot)
                {
                    Advance();
                    var colName = ExpectIdentifier();
                    return new ColumnRefExpression { TablePrefix = token.Value, ColumnName = colName };
                }
                return new ColumnRefExpression { ColumnName = token.Value };

            default:
                // 软关键字（STATUS/COLUMNS/FULL/DATABASES 等）在表达式上下文中作为列名
                if (IsSoftKeyword(token.Type))
                {
                    Advance();
                    if (Peek().Type == SqlTokenType.LeftParen)
                        return ParseScalarFunction(token.Value);
                    if (Peek().Type == SqlTokenType.Dot)
                    {
                        Advance();
                        var colName = ExpectIdentifier();
                        return new ColumnRefExpression { TablePrefix = token.Value, ColumnName = colName };
                    }
                    return new ColumnRefExpression { ColumnName = token.Value };
                }
                throw SyntaxError($"Unexpected token '{token.Value}' at position {token.Position}");
        }
    }

    /// <summary>解析标量函数调用</summary>
    /// <param name="functionName">函数名</param>
    /// <returns>函数表达式</returns>
    private FunctionExpression ParseScalarFunction(String functionName)
    {
        Expect(SqlTokenType.LeftParen);

        var func = new FunctionExpression
        {
            FunctionName = functionName.ToUpper(),
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
            FunctionName = funcToken.Value.ToUpper(),
            IsAggregate = funcToken.Type is SqlTokenType.Count or SqlTokenType.Sum or SqlTokenType.Avg
                or SqlTokenType.Min or SqlTokenType.Max or SqlTokenType.StringAgg
                or SqlTokenType.GroupConcat or SqlTokenType.Stddev or SqlTokenType.Variance
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
