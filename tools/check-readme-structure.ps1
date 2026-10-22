# README 多语言结构校验：章节数、代码块数量与语言标记、表格行数与英文版一致
# 不校验具体行数（各翻译版可能多出“译文可能过期”提示行）
#
# 用法：
#   pwsh -File tools/check-readme-structure.ps1
#   pwsh -File tools/check-readme-structure.ps1 -Root D:\src\MyLib
param(
    [String]$Root = (Split-Path -Parent $PSScriptRoot),
    [String]$Config = "readme-languages.json",
    [String]$Baseline = "README.md"
)

$ErrorActionPreference = "Stop"

$configPath = Join-Path $Root $Config
if (!(Test-Path -LiteralPath $configPath)) { throw "未找到语言配置：$configPath" }
$langs = @((Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json).languages)
if ($langs.Count -eq 0) { throw "$Config 未配置任何语种" }

function Get-ReadmeStats([String]$path) {
    $lines = Get-Content -LiteralPath $path -Encoding UTF8
    $fenceLangs = [System.Collections.Generic.List[String]]::new()
    $inFence = $false
    foreach ($line in $lines) {
        if ($line -match '^```') {
            if (!$inFence) {
                $inFence = $true
                $fenceLangs.Add(($line -replace '^```', '').Trim())
            }
            else {
                $inFence = $false
            }
        }
    }

    return [pscustomobject]@{
        Headings   = @($lines | Where-Object { $_ -match '^#{1,4}\s' }).Count
        Fences     = @($lines | Where-Object { $_ -match '^```' }).Count
        TableRows  = @($lines | Where-Object { $_ -match '^\|' }).Count
        FenceLangs = ($fenceLangs -join ',')
    }
}

$baselinePath = Join-Path $Root $Baseline
if (!(Test-Path -LiteralPath $baselinePath)) { throw "未找到基准 README：$baselinePath" }
$expected = Get-ReadmeStats $baselinePath

$errors = 0
foreach ($lang in $langs) {
    if ($lang.file -eq $Baseline) { continue }

    $path = Join-Path $Root $lang.file
    if (!(Test-Path -LiteralPath $path)) {
        Write-Host "✗ $($lang.file)：文件不存在" -ForegroundColor Red
        $errors++
        continue
    }

    $actual = Get-ReadmeStats $path
    $diffs = @()
    if ($actual.Headings -ne $expected.Headings) { $diffs += "章节数 $($actual.Headings) != $($expected.Headings)" }
    if ($actual.Fences -ne $expected.Fences) { $diffs += "代码围栏 $($actual.Fences) != $($expected.Fences)" }
    if ($actual.TableRows -ne $expected.TableRows) { $diffs += "表格行 $($actual.TableRows) != $($expected.TableRows)" }
    if ($actual.FenceLangs -ne $expected.FenceLangs) { $diffs += "代码块语言标记序列不一致" }

    if ($diffs.Count -gt 0) {
        Write-Host "✗ $($lang.file)：$($diffs -join '；')" -ForegroundColor Red
        $errors++
    }
    else {
        Write-Host "✓ $($lang.file)（$($actual.Headings) 章节 / $($actual.Fences) 围栏 / $($actual.TableRows) 表格行）"
    }
}

if ($errors -gt 0) {
    Write-Host "`nREADME 结构校验未通过：$errors 个文件与 $Baseline 不一致。" -ForegroundColor Red
    exit 1
}

Write-Host "README 结构校验通过：$($langs.Count) 个语种与 $Baseline 一致（$($expected.Headings) 章节 / $($expected.Fences) 围栏 / $($expected.TableRows) 表格行）。" -ForegroundColor Green
