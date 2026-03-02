using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Server;

/// <summary>认证与鉴权管理器，提供账号管理和权限检查</summary>
/// <remarks>
/// 对应模块 X05（认证与鉴权）。
/// 支持用户名/密码认证、角色（admin/readonly/readwrite）和库/表级权限控制。
/// 内存管理，可序列化持久化到 JSON 配置文件。
/// </remarks>
public class AuthManager
{
    private readonly Dictionary<String, UserInfo> _users = new(StringComparer.OrdinalIgnoreCase);
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif

    /// <summary>是否启用认证。禁用时所有操作视为已认证</summary>
    public Boolean Enabled { get; set; }

    /// <summary>用户数量</summary>
    public Int32 UserCount
    {
        get
        {
            lock (_lock) return _users.Count;
        }
    }

    /// <summary>创建认证管理器，默认添加 admin 用户</summary>
    public AuthManager()
    {
        // 默认内置 admin 账号
        _users["admin"] = new UserInfo
        {
            UserName = "admin",
            PasswordHash = HashPassword("admin"),
            Role = UserRole.Admin,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>认证用户</summary>
    /// <param name="userName">用户名</param>
    /// <param name="password">密码</param>
    /// <returns>认证成功返回 true</returns>
    public Boolean Authenticate(String userName, String password)
    {
        if (userName == null) throw new ArgumentNullException(nameof(userName));
        if (password == null) throw new ArgumentNullException(nameof(password));

        if (!Enabled) return true;

        lock (_lock)
        {
            if (!_users.TryGetValue(userName, out var user))
                return false;

            return user.PasswordHash == HashPassword(password);
        }
    }

    /// <summary>检查用户是否有指定操作权限</summary>
    /// <param name="userName">用户名</param>
    /// <param name="permission">权限标识（如 "db:mydb:read"、"table:users:write"）</param>
    /// <returns>有权限返回 true</returns>
    public Boolean HasPermission(String userName, String permission)
    {
        if (userName == null) throw new ArgumentNullException(nameof(userName));
        if (permission == null) throw new ArgumentNullException(nameof(permission));

        if (!Enabled) return true;

        lock (_lock)
        {
            if (!_users.TryGetValue(userName, out var user))
                return false;

            // Admin 拥有所有权限
            if (user.Role == UserRole.Admin) return true;

            // 检查显式授权（优先于角色默认规则）
            if (user.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase))
                return true;

            // ReadOnly 仅允许 read 操作
            if (user.Role == UserRole.ReadOnly)
                return permission.EndsWith(":read", StringComparison.OrdinalIgnoreCase) ||
                       permission.StartsWith("query:", StringComparison.OrdinalIgnoreCase);

            // ReadWrite 允许 read/write，但不允许 admin 操作
            if (user.Role == UserRole.ReadWrite)
                return !permission.StartsWith("admin:", StringComparison.OrdinalIgnoreCase);

            return false;
        }
    }

    /// <summary>创建用户</summary>
    /// <param name="userName">用户名</param>
    /// <param name="password">密码</param>
    /// <param name="role">角色</param>
    public void CreateUser(String userName, String password, UserRole role = UserRole.ReadWrite)
    {
        if (userName == null) throw new ArgumentNullException(nameof(userName));
        if (password == null) throw new ArgumentNullException(nameof(password));

        lock (_lock)
        {
            if (_users.ContainsKey(userName))
                throw new NovaException(ErrorCode.InvalidArgument, $"User '{userName}' already exists");

            _users[userName] = new UserInfo
            {
                UserName = userName,
                PasswordHash = HashPassword(password),
                Role = role,
                CreatedAt = DateTime.UtcNow
            };
        }
    }

    /// <summary>删除用户</summary>
    /// <param name="userName">用户名</param>
    /// <returns>是否删除成功</returns>
    public Boolean DropUser(String userName)
    {
        if (userName == null) throw new ArgumentNullException(nameof(userName));

        lock (_lock)
        {
            if (String.Equals(userName, "admin", StringComparison.OrdinalIgnoreCase))
                throw new NovaException(ErrorCode.InvalidArgument, "Cannot drop built-in admin user");

            return _users.Remove(userName);
        }
    }

    /// <summary>修改用户密码</summary>
    /// <param name="userName">用户名</param>
    /// <param name="newPassword">新密码</param>
    public void ChangePassword(String userName, String newPassword)
    {
        if (userName == null) throw new ArgumentNullException(nameof(userName));
        if (newPassword == null) throw new ArgumentNullException(nameof(newPassword));

        lock (_lock)
        {
            if (!_users.TryGetValue(userName, out var user))
                throw new NovaException(ErrorCode.AuthenticationFailed, $"User '{userName}' not found");

            user.PasswordHash = HashPassword(newPassword);
        }
    }

    /// <summary>修改用户角色</summary>
    /// <param name="userName">用户名</param>
    /// <param name="role">新角色</param>
    public void ChangeRole(String userName, UserRole role)
    {
        if (userName == null) throw new ArgumentNullException(nameof(userName));

        lock (_lock)
        {
            if (!_users.TryGetValue(userName, out var user))
                throw new NovaException(ErrorCode.AuthenticationFailed, $"User '{userName}' not found");

            user.Role = role;
        }
    }

    /// <summary>为用户授予权限</summary>
    /// <param name="userName">用户名</param>
    /// <param name="permission">权限标识</param>
    public void Grant(String userName, String permission)
    {
        if (userName == null) throw new ArgumentNullException(nameof(userName));
        if (permission == null) throw new ArgumentNullException(nameof(permission));

        lock (_lock)
        {
            if (!_users.TryGetValue(userName, out var user))
                throw new NovaException(ErrorCode.AuthenticationFailed, $"User '{userName}' not found");

            if (!user.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase))
                user.Permissions.Add(permission);
        }
    }

    /// <summary>撤销用户权限</summary>
    /// <param name="userName">用户名</param>
    /// <param name="permission">权限标识</param>
    /// <returns>是否撤销成功</returns>
    public Boolean Revoke(String userName, String permission)
    {
        if (userName == null) throw new ArgumentNullException(nameof(userName));
        if (permission == null) throw new ArgumentNullException(nameof(permission));

        lock (_lock)
        {
            if (!_users.TryGetValue(userName, out var user))
                throw new NovaException(ErrorCode.AuthenticationFailed, $"User '{userName}' not found");

            return user.Permissions.Remove(permission);
        }
    }

    /// <summary>获取用户信息</summary>
    /// <param name="userName">用户名</param>
    /// <returns>用户信息，不存在返回 null</returns>
    public UserInfo? GetUser(String userName)
    {
        if (userName == null) throw new ArgumentNullException(nameof(userName));

        lock (_lock)
        {
            return _users.TryGetValue(userName, out var user) ? user : null;
        }
    }

    /// <summary>获取所有用户名</summary>
    /// <returns>用户名列表</returns>
    public List<String> GetAllUsers()
    {
        lock (_lock)
        {
            return new List<String>(_users.Keys);
        }
    }

    /// <summary>密码哈希（SHA256）</summary>
    private static String HashPassword(String password)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(password);
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }
}

/// <summary>用户信息</summary>
public class UserInfo
{
    /// <summary>用户名</summary>
    public String UserName { get; set; } = String.Empty;

    /// <summary>密码哈希</summary>
    public String PasswordHash { get; set; } = String.Empty;

    /// <summary>角色</summary>
    public UserRole Role { get; set; }

    /// <summary>权限列表</summary>
    public List<String> Permissions { get; set; } = [];

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>用户角色</summary>
public enum UserRole
{
    /// <summary>只读用户</summary>
    ReadOnly,

    /// <summary>读写用户</summary>
    ReadWrite,

    /// <summary>管理员</summary>
    Admin
}
