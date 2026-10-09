# ADR-0011: .NET 10 и обновление SQLite bundle

- Статус: реализовано в рабочем дереве; полная регрессия и новый RC ещё проверяются.
- Дата: 2026-10-04.
- Область: runtime/build baseline из ADR-0001; CLI-протоколы этот ADR не меняет.

## Решение

ТЗ §4.1 задаёт .NET 10. Историческая причина использования .NET 8 — отсутствие SDK 10 на машине — устранена: официальный SDK 10.0.401 установлен отдельно, без замены системного SDK 8.0.417. SHA-512 ZIP проверен по Microsoft releases.json. Локальная копия: `artifacts/project-closure-20261004/net10/sdk-10.0.401`.

`global.json` фиксирует SDK 10.0.401, разрешает только patch roll-forward и запрещает prerelease. Библиотеки и обычные тесты используют `net10.0`; приложение WPF, UI-тесты и два WPF harness — `net10.0-windows`. Вспомогательные скрипты используют обновлённые пути; Invoke-ProjectChecks включает все шесть test projects, включая LLMGateway.Tests. Внешний D:/work/LLMGateway и прежние RC остаются на своих исходных targets.

Поставка остаётся self-contained win-x64. Наличие SDK нужно для сборки и тестов, но не для запуска self-contained дистрибутива. Текущий архив RC .2 не изменяется задним числом: переход войдёт в новый неизменяемый кандидат после проверок. Schema rollback по-прежнему требует совместимого backup; смена runtime не понижает схему БД.

## Зависимости SQLite

Первый restore SDK 10 обнаружил NU1903 у транзитивного SQLitePCLRaw.lib.e_sqlite3 2.1.6 (CVE-2025-6965). Предупреждение не подавлялось. Infrastructure явно использует SQLitePCLRaw.bundle_e_sqlite3 2.1.13, сохраняя API Microsoft.Data.Sqlite 8.0.8. Фактическая загруженная win-x64 библиотека сообщает SQLite 3.53.3. NuGet-аудит всех 19 проектов, включая транзитивные зависимости, не сообщил известных уязвимых пакетов. Это результат конкретного аудита, а не независимая security acceptance.

## Воспроизведение

Можно установить SDK 10.0.401 обычным способом либо использовать проверенную локальную копию в текущем терминале PowerShell:

```powershell
$taskDotnetRoot = (Resolve-Path ./artifacts/project-closure-20261004/net10/sdk-10.0.401).Path
$env:DOTNET_ROOT = $taskDotnetRoot
$env:DOTNET_ROOT_X64 = $taskDotnetRoot
$env:PATH = $taskDotnetRoot + [IO.Path]::PathSeparator + $env:PATH
dotnet --version
./scripts/Invoke-ProjectChecks.ps1
```

Эти переменные действуют только в текущем процессе терминала. Машинный PATH и установленный .NET 8 не меняются. Для проверок агента есть эквивалентный `artifacts/project-closure-20261004/net10/run.py`, который задаёт окружение только дочерней команде.

## Доказательства и оставшаяся проверка

Release solution build: 0 warnings/errors. На .NET 10 пройдено 210 affected adaptation/activation/scratch/database-migration tests. Первый полный прогон шести проектов: 5490/5491, единственный failure UiInspector сравнивает runtime-текст и рендеринг net10 с эталонами net8; 114 captures без clipped/leaks. Новый успешный полный прогон ещё требуется; старые результаты .NET 8 не выдаются за результаты нового runtime. После него требуются новый self-contained publish и изолированные package/lifecycle/UI проверки.

Локальные receipts: `net10/installation.json`, `net10/sqlite-runtime.json`, `net10/package-vulnerabilities.json`, `net10/build-r3.log` под `artifacts/project-closure-20261004`.

Продолжение05.10.2026: исправлен реальный empty-provider layout дефект, затем просмотрены48runtime differences в18comparison pages. Прежние эталоны сохранены, новая именованная ревизия .NET10 с exact hashes; exact pixel assertion и one-pixel negative test не ослаблялись. UiInspectorVisualTests4/4 PASS; fresh full-net10-r2 **5726/5726 PASS**, inputDrift=[],114captures/clipped0/leaks0/changedPixels0. D054 CODE/BASELINE METADATA PASS относится к исходникам и метаданным, не PNG pixels. Первый .NET10 RC прошёл isolated lifecycle/schema15→23 и package-identical Integration156/156, но package UI выявил stale editor binding. Исправлен IsBusy→CanConfigureProvider notification; RED1/1→GREEN13/13. Следующий full-net10-r3 **5727/5727 PASS**, drift=[]; D055 PART_VERDICT PASS. Новый RC1.0.0-rc.20261005.2:571files, self-contained10.0.12/schema23, isolated lifecycle PASS, package Integration156/156, UI129/0/1 на10точных DLL; source/package/installer drift=[]; release/physical/owner acceptance OPEN. [Визуальный отчёт](../work/NET10_VISUAL_BASELINE_20261005.md).

Источники: [Microsoft .NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), [официальные release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json), [описание CVE](https://github.com/advisories/GHSA-2m69-gcr7-jv3q), [SQLitePCLRaw bundle 2.1.13](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3/2.1.13).
