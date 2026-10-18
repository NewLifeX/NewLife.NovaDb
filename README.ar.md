# NewLife.NovaDb - قاعدة بيانات هجينة مضمّنة/شبكية


![GitHub License](https://img.shields.io/github/license/NewLifeX/NewLife.NovaDb?logo=github)
![NuGet Downloads](https://img.shields.io/nuget/dt/NewLife.NovaDb?logo=nuget)
![NuGet](https://img.shields.io/nuget/v/NewLife.NovaDb?logo=nuget)
![NuGet prerelease](https://img.shields.io/nuget/vpre/NewLife.NovaDb?label=dev%20nuget&logo=nuget)
[![test](https://github.com/NewLifeX/NewLife.NovaDb/actions/workflows/test.yml/badge.svg?branch=master)](https://github.com/NewLifeX/NewLife.NovaDb/actions/workflows/test.yml)
![platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-blue)
[![contributors](https://img.shields.io/github/contributors/NewLifeX/NewLife.NovaDb?logo=github)](https://github.com/NewLifeX/NewLife.NovaDb/graphs/contributors)
<!-- lang:start -->
🌐 [中文](README.md) | [繁體中文](README.zh-TW.md) | [English](README.en.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md) | [한국어](README.ko.md)
<!-- lang:end -->

> قد تتأخر هذه الترجمة عن النسخة الصينية.

NewLife.NovaDb قاعدة بيانات هجينة متوسطة إلى كبيرة بـ C#، تجمع محركات علائقية وسلاسل زمنية وطوابير رسائل وKV، مع وضعي التضمين والشبكة والنسخ المتماثل.

## التثبيت

```bash
dotnet add package NewLife.NovaDb
```

## البدء السريع

```csharp
using var conn = new NovaConnection("Data Source=data;Mode=Embedded");
conn.Open();
```

## الميزات الرئيسية

- تغطية واسعة لإصدارات البروتوكولات والصيغ الشائعة
- إنتاجية عالية وزمن استجابة منخفض، مُثبت على نطاق الإنتاج
- تنفيذ مُدار بالكامل دون تبعيات أصلية

## التوثيق

> 📖 التوثيق الكامل (بالصينية): [مستندات المستودع](Doc)

## الترخيص

MIT License © 2002-2026 فريق NewLife

[GitHub](https://github.com/NewLifeX/NewLife.NovaDb) | [Website](https://newlifex.com)
