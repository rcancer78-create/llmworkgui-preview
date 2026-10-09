# ADR-0001: Runtime и CLI baseline

- **Статус:** исторический Phase-0 baseline; runtime-решение заменено [ADR-0011](ADR-0011-net10-runtime-and-sqlite-bundle.md) от 04.10.2026. CLI snapshot ниже сохранён.
- **Дата:** 2026-09-22
- **Задача:** TASK-001 (Phase 0 — discovery и архитектурные контракты)
- **Связанные документы:** `TECHNICAL_SPECIFICATION.md` §4.1, §4.3, §5, §15; `ROADMAP.md` Phase 0

## Контекст

ТЗ §4.1 требует C# и актуальный LTS .NET, доступный на старте реализации; целевой baseline — `.NET 10`.
При этом ТЗ §5 фиксирует capability snapshot окружения от 2026-09-22, а ROADMAP Phase 0 требует определить
minimum supported Windows и .NET runtime до написания продуктовой интеграции.

Фактическая проверка окружения в рамках TASK-001 показала:

- установлен только .NET SDK `8.0.417` (LTS); .NET 10 SDK отсутствует;
- ОС — Windows 11 Pro x64 (10.0.26200);
- OpenCode, Cursor Agent и AGY CLI доступны как внешние prerequisites.

## Решение

1. **Target Framework:** `net8.0` для всех библиотек и тестовых проектов; `net8.0-windows` для
   `LLMWorkGUI.App` (WPF) и `LLMWorkGUI.Ui.Tests`. Значения централизованы в `Directory.Build.props`;
   WPF/UI-проекты переопределяют TFM локально.
2. **Платформа:** Windows 10/11 x64; WPF; x64.
3. **Runtime baseline:** .NET 8 (LTS, SDK `8.0.417`) — единственный доступный на старте реализации LTS,
   что соответствует формулировке §4.1 «актуальный LTS .NET, доступный на старте реализации».
4. **CLI baseline** (наблюдение, не вечный контракт §5): версии ниже фиксируются как исходная точка
   capability discovery. Adapter'ы обязаны проверять фактические версии/capabilities при старте и
   переходить в `UnsupportedVersion` при отсутствии обязательного контракта; silent fallback запрещён.
5. **Тестовый инструментарий:** xUnit `2.5.3`, `Microsoft.NET.Test.Sdk` `17.8.0`,
   `xunit.runner.visualstudio` `2.5.3` (совместимы с net8.0/net8.0-windows).

## Фактические версии окружения (evidence)

| Компонент | Версия | Источник проверки | Роль в продукте |
|---|---|---|---|
| .NET SDK | 8.0.417 LTS | `dotnet --list-sdks` | Runtime baseline, сборка и тесты |
| ОС | Windows 11 Pro x64 (10.0.26200) | `Win32_OperatingSystem` | Поддерживаемая платформа Windows 10/11 x64 |
| OpenCode | 1.18.31 | `opencode --version` | Основной backend transport (`serve`-first) |
| Cursor Agent | 2026.09.15-d2fe57e | `cursor-agent --version` | Нативный backend через `cursor-agent acp` |
| Codex CLI | 0.155.0-alpha.9.2 | ТЗ §5 capability snapshot от 2026-09-22; в PATH на момент TASK-001 не обнаружен | Не штатный backend; только диагностический контекст |
| AGY CLI | 1.1.23 | `agy --version` | Provider CLI для `star-cliproxy`; account selection через `agy-profile` (ADR-0007) |

## Обоснование выбора net8.0 / net8.0-windows

- На машине установлен и доступен только .NET SDK 8.0.417 LTS; целевой `.NET 10` из ТЗ в окружении
  отсутствует, поэтому недоступен для фактической сборки и тестов Phase 0.
- Формулировка §4.1 допускает «актуальный LTS .NET, доступный на старте реализации», и требует
  зафиксировать minimum supported runtime в Phase 0 — это и делается данным ADR.
- `net8.0-windows` необходим для WPF (`LLMWorkGUI.App`) и UI-тестов; библиотеки остаются на `net8.0`
  и не зависят от Windows-специфичных API.
- `Directory.Build.props` централизует TFM, поэтому последующий retarget на `net10.0-windows` при
  установке .NET 10 SDK — точечное изменение, оформляемое отдельным ADR/change proposal.

## Последствия

- `LLMWorkGUI.Domain` не зависит от WPF, SQLite и внешних CLI (§15); зависимости слоёв заданы ссылками
  проектов: Application → Domain, Infrastructure → Domain/Application, Backends.* → Domain/Abstractions,
  App — composition root.
- Release-поставка остаётся self-contained Windows x64 (§4.3); смена TFM не меняет архитектурные границы.
- Версии CLI из таблицы используются только как baseline discovery; продуктовая логика не привязывается
  к конкретным версиям (§5).

## Риски и ограничения

- Поддержка .NET 8 LTS завершается 2026-11-10. Установка .NET 10 SDK и retarget на `net10.0-windows`
  должны быть рассмотрены до release 1.0 отдельным ADR.
- Codex CLI не обнаружен в PATH на момент TASK-001; это не блокирует Phase 0, так как прямой Codex CLI
  не является штатным backend продукта (§2.1) и не входит в обязательные discovery-результаты.
- Зафиксированные версии CLI являются наблюдением от 2026-09-22, а не API-контрактом (§18).
