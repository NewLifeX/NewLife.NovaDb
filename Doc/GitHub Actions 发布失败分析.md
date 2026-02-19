# GitHub Actions 发布失败分析

## 问题概述

发布工作流在执行 `dotnet nuget push` 时失败，错误信息：
```
Required argument missing for option: '--api-key'.
error: Missing value for option 'api-key'
```

## 详细分析

### 1. 失败日志分析

从 GitHub Actions 运行日志（Run ID: 22165080683, Job ID: 64090643760）中可以看到：

```
2026-02-19T01:42:44.8259113Z ##[debug]....Evaluating secrets:
2026-02-19T01:42:44.8259415Z ##[debug]....=> Object
2026-02-19T01:42:44.8259661Z ##[debug]....Evaluating String:
2026-02-19T01:42:44.8259887Z ##[debug]....=> 'nugetKey'
2026-02-19T01:42:44.8260181Z ##[debug]..=> null
```

**关键发现**：`secrets.nugetKey` 的值为 `null`，导致最终执行的命令变成：
```bash
dotnet nuget push ./out/*.nupkg --skip-duplicate --source https://api.nuget.org/v3/index.json --api-key 
```

`--api-key` 后面没有值，因此 dotnet CLI 报错。

### 2. 工作流配置对比

**NewLife.NovaDb 的 publish-beta.yml**：
```yaml
- name: Publish
  run: |
    dotnet nuget push ./out/*.nupkg --skip-duplicate --source https://api.nuget.org/v3/index.json --api-key ${{ secrets.nugetKey }}
```

**NewLife.MQTT 的 publish-beta.yml**：
```yaml
- name: Publish
  run: |
    dotnet nuget push ./out/*.nupkg --skip-duplicate --source https://api.nuget.org/v3/index.json --api-key ${{ secrets.nugetKey }}
```

两者使用的语法**完全相同**，都是 `${{ secrets.nugetKey }}`。

### 3. 根本原因

问题的根本原因是：**NewLife.NovaDb 仓库是私有仓库（private: true）**

从 API 响应中可以看到：
```json
"repository": {
  "private": true,
  ...
}
```

#### GitHub Secrets 访问权限说明

GitHub 的 Organization Secrets 有两种访问级别：
1. **Public repositories only**（仅公共仓库）
2. **All repositories**（所有仓库）或 **Selected repositories**（选定的仓库）

**分析**：
- NewLife.MQTT 应该是**公共仓库**，可以访问设置为 "Public repositories only" 的 Organization Secret
- NewLife.NovaDb 是**私有仓库**，无法访问设置为 "Public repositories only" 的 Organization Secret
- 因此即使两个仓库使用相同的语法 `secrets.nugetKey`，NewLife.NovaDb 获取不到该 secret

## 解决方案

有以下几种解决方案：

### 方案 1：修改 Organization Secret 的访问权限（推荐）

在 GitHub Organization 设置中，将 `nugetKey` secret 的访问权限从 "Public repositories only" 改为：
- **"All repositories"**（所有仓库都可以访问）
- 或 **"Selected repositories"**，并将 NewLife.NovaDb 添加到允许列表中

**操作路径**：
1. 访问 https://github.com/organizations/NewLifeX/settings/secrets/actions
2. 找到 `nugetKey` secret
3. 点击 "Edit"
4. 在 "Repository access" 部分，选择 "All repositories" 或将 "NewLife.NovaDb" 添加到选定仓库列表
5. 保存更改

### 方案 2：在仓库级别添加 Secret

在 NewLife.NovaDb 仓库设置中单独添加 `nugetKey` secret：

**操作路径**：
1. 访问 https://github.com/NewLifeX/NewLife.NovaDb/settings/secrets/actions
2. 点击 "New repository secret"
3. Name: `nugetKey`
4. Value: （NuGet API Key）
5. 点击 "Add secret"

**注意**：此方案需要在每个私有仓库中单独配置，维护成本较高。

### 方案 3：将仓库改为公共（不推荐）

如果项目可以开源，将 NewLife.NovaDb 改为公共仓库，但这需要考虑业务和安全需求。

## 验证方法

修改配置后，可以通过以下方式验证：
1. 手动触发 workflow：访问 https://github.com/NewLifeX/NewLife.NovaDb/actions/workflows/publish-beta.yml，点击 "Run workflow"
2. 查看日志确认 `secrets.nugetKey` 不再为 null
3. 确认 NuGet 包成功推送

## 总结

- **问题**：私有仓库无法访问仅对公共仓库开放的 Organization Secret
- **推荐方案**：修改 Organization Secret 的访问权限，允许私有仓库访问
- **临时方案**：在仓库级别单独配置 Secret
