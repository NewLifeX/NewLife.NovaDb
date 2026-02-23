using System;
using System.Collections.Generic;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Server;
using Xunit;

namespace XUnitTest.Server;

public class AuthManagerTests
{
    [Fact(DisplayName = "默认创建 admin 用户")]
    public void DefaultAdminUser()
    {
        var auth = new AuthManager();

        Assert.Equal(1, auth.UserCount);

        var admin = auth.GetUser("admin");
        Assert.NotNull(admin);
        Assert.Equal(UserRole.Admin, admin!.Role);
    }

    [Fact(DisplayName = "认证成功")]
    public void AuthenticateSuccess()
    {
        var auth = new AuthManager { Enabled = true };
        var result = auth.Authenticate("admin", "admin");
        Assert.True(result);
    }

    [Fact(DisplayName = "认证失败-密码错误")]
    public void AuthenticateWrongPassword()
    {
        var auth = new AuthManager { Enabled = true };
        var result = auth.Authenticate("admin", "wrong");
        Assert.False(result);
    }

    [Fact(DisplayName = "认证失败-用户不存在")]
    public void AuthenticateNonexistentUser()
    {
        var auth = new AuthManager { Enabled = true };
        var result = auth.Authenticate("unknown", "password");
        Assert.False(result);
    }

    [Fact(DisplayName = "禁用认证时始终通过")]
    public void DisabledAuthAlwaysPasses()
    {
        var auth = new AuthManager { Enabled = false };
        Assert.True(auth.Authenticate("anyone", "anything"));
    }

    [Fact(DisplayName = "创建新用户")]
    public void CreateUser()
    {
        var auth = new AuthManager();
        auth.CreateUser("testuser", "password123", UserRole.ReadWrite);

        Assert.Equal(2, auth.UserCount);

        var user = auth.GetUser("testuser");
        Assert.NotNull(user);
        Assert.Equal(UserRole.ReadWrite, user!.Role);
    }

    [Fact(DisplayName = "创建重名用户抛异常")]
    public void CreateDuplicateUserThrows()
    {
        var auth = new AuthManager();

        var ex = Assert.Throws<NovaException>(() =>
            auth.CreateUser("admin", "password"));
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact(DisplayName = "删除用户")]
    public void DropUser()
    {
        var auth = new AuthManager();
        auth.CreateUser("dropme", "pass");
        Assert.Equal(2, auth.UserCount);

        var result = auth.DropUser("dropme");
        Assert.True(result);
        Assert.Equal(1, auth.UserCount);
    }

    [Fact(DisplayName = "不能删除 admin")]
    public void CannotDropAdmin()
    {
        var auth = new AuthManager();

        var ex = Assert.Throws<NovaException>(() => auth.DropUser("admin"));
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact(DisplayName = "修改密码")]
    public void ChangePassword()
    {
        var auth = new AuthManager { Enabled = true };

        auth.ChangePassword("admin", "newpass");

        Assert.False(auth.Authenticate("admin", "admin"));
        Assert.True(auth.Authenticate("admin", "newpass"));
    }

    [Fact(DisplayName = "修改不存在用户的密码抛异常")]
    public void ChangePasswordNonexistentThrows()
    {
        var auth = new AuthManager();

        var ex = Assert.Throws<NovaException>(() =>
            auth.ChangePassword("nobody", "pass"));
        Assert.Equal(ErrorCode.AuthenticationFailed, ex.Code);
    }

    [Fact(DisplayName = "修改用户角色")]
    public void ChangeRole()
    {
        var auth = new AuthManager();
        auth.CreateUser("user1", "pass", UserRole.ReadOnly);

        auth.ChangeRole("user1", UserRole.Admin);

        var user = auth.GetUser("user1");
        Assert.Equal(UserRole.Admin, user!.Role);
    }

    [Fact(DisplayName = "Admin 拥有全部权限")]
    public void AdminHasAllPermissions()
    {
        var auth = new AuthManager { Enabled = true };

        Assert.True(auth.HasPermission("admin", "db:mydb:read"));
        Assert.True(auth.HasPermission("admin", "table:users:write"));
        Assert.True(auth.HasPermission("admin", "admin:manage"));
    }

    [Fact(DisplayName = "ReadOnly 只允许读")]
    public void ReadOnlyPermissions()
    {
        var auth = new AuthManager { Enabled = true };
        auth.CreateUser("reader", "pass", UserRole.ReadOnly);

        Assert.True(auth.HasPermission("reader", "db:mydb:read"));
        Assert.True(auth.HasPermission("reader", "query:select"));
        Assert.False(auth.HasPermission("reader", "table:users:write"));
    }

    [Fact(DisplayName = "ReadWrite 不允许 admin 操作")]
    public void ReadWritePermissions()
    {
        var auth = new AuthManager { Enabled = true };
        auth.CreateUser("writer", "pass", UserRole.ReadWrite);

        Assert.True(auth.HasPermission("writer", "db:mydb:read"));
        Assert.True(auth.HasPermission("writer", "table:users:write"));
        Assert.False(auth.HasPermission("writer", "admin:manage"));
    }

    [Fact(DisplayName = "禁用认证时权限始终通过")]
    public void DisabledAuthAllPermissions()
    {
        var auth = new AuthManager { Enabled = false };

        Assert.True(auth.HasPermission("anyone", "admin:manage"));
    }

    [Fact(DisplayName = "授予和撤销权限")]
    public void GrantAndRevoke()
    {
        var auth = new AuthManager { Enabled = true };
        auth.CreateUser("custom", "pass", UserRole.ReadOnly);

        auth.Grant("custom", "special:action");
        Assert.True(auth.HasPermission("custom", "special:action"));

        var revoked = auth.Revoke("custom", "special:action");
        Assert.True(revoked);
    }

    [Fact(DisplayName = "获取所有用户名")]
    public void GetAllUsers()
    {
        var auth = new AuthManager();
        auth.CreateUser("user1", "pass1");
        auth.CreateUser("user2", "pass2");

        var users = auth.GetAllUsers();
        Assert.Equal(3, users.Count);
        Assert.Contains("admin", users);
        Assert.Contains("user1", users);
        Assert.Contains("user2", users);
    }

    [Fact(DisplayName = "获取不存在的用户返回 null")]
    public void GetNonexistentUserReturnsNull()
    {
        var auth = new AuthManager();
        Assert.Null(auth.GetUser("nobody"));
    }

    [Fact(DisplayName = "删除不存在的用户返回 false")]
    public void DropNonexistentReturnsFalse()
    {
        var auth = new AuthManager();
        Assert.False(auth.DropUser("nobody"));
    }

    [Fact(DisplayName = "参数校验")]
    public void NullParameterValidation()
    {
        var auth = new AuthManager();

        Assert.Throws<ArgumentNullException>(() => auth.Authenticate(null!, "pass"));
        Assert.Throws<ArgumentNullException>(() => auth.Authenticate("user", null!));
        Assert.Throws<ArgumentNullException>(() => auth.CreateUser(null!, "pass"));
        Assert.Throws<ArgumentNullException>(() => auth.CreateUser("user", null!));
        Assert.Throws<ArgumentNullException>(() => auth.DropUser(null!));
        Assert.Throws<ArgumentNullException>(() => auth.ChangePassword(null!, "pass"));
        Assert.Throws<ArgumentNullException>(() => auth.HasPermission(null!, "perm"));
        Assert.Throws<ArgumentNullException>(() => auth.HasPermission("user", null!));
    }
}
