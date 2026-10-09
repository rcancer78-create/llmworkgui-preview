# Проверки публичного preview

Тесты следует запускать из отдельной копии репозитория на Windows, без параллельного изменения её исходников. Нужны .NET SDK 10.0.401 и Node.js 22. Основная CI-конфигурация: [.github/workflows/ci.yml](.github/workflows/ci.yml).

## Наборы

| Группа | Проект | Границы |
| --- | --- | --- |
| Domain | `tests/LLMWorkGUI.Domain.Tests` | Инварианты и состояния |
| Application | `tests/LLMWorkGUI.Application.Tests` | Сценарии и контракты сервисов |
| Backends.Contract | `tests/LLMWorkGUI.Backends.ContractTests` | Подставные транспорты и протокольные фикстуры |
| Gateway | `tests/LLMGateway.Tests` | HTTP loopback и собственные подставные CLI |
| Integration | `tests/LLMWorkGUI.IntegrationTests` | SQLite, файлы, процессы, DPAPI, тестовые хранилища и установка |
| UI | `tests/LLMWorkGUI.Ui.Tests` | View models, WPF, окна, взаимодействие и визуальные проверки |

UI-набор открывает окна. Для него используйте отдельный тестовый рабочий стол или GitHub runner. Интеграционные проверки создают временные каталоги и собственные дочерние процессы; они не являются чистыми unit-тестами.

```powershell
dotnet restore LLMWorkGUI.sln
dotnet build LLMWorkGUI.sln -c Release --no-restore
dotnet test tests/LLMWorkGUI.Domain.Tests/LLMWorkGUI.Domain.Tests.csproj -c Release --no-build --no-restore
```

Для Application, Backends.Contract и Gateway замените путь проекта по таблице. Не запускайте всю solution без фильтров, если не намерены проверять установленные реальные сервисы.

## Точные исключения стандартного CI

Integration:

```powershell
dotnet test tests/LLMWorkGUI.IntegrationTests/LLMWorkGUI.IntegrationTests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName!~CursorAcpLiveSmokeTests&FullyQualifiedName!~MirasimLiveSmokeTests&Evidence!=ActualOpenCodeNoModel'
```

Исключения сохраняются в исходниках и требуют отдельного подготовленного окружения:

- `CursorAcpLiveSmokeTests` обнаруживает установленный Cursor Agent и выполняет реальный ACP initialize.
- `MirasimLiveSmokeTests` обращается к запущенному Mirasim на loopback.
- `Evidence=ActualOpenCodeNoModel` требует установленный OpenCode и проверяет жизненный цикл реального процесса. Запрос к модели в этом сценарии не предусмотрен.

`CursorAcpLiveStdioBindingTests` использует собственный подставной процесс и остаётся в обычном CI.

UI:

```powershell
dotnet test tests/LLMWorkGUI.Ui.Tests/LLMWorkGUI.Ui.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName!=LLMWorkGUI.Ui.Tests.UiInspectorVisualTests.UiInspector_AllNineteenScreensAcrossThemesAndDpi_ReportsCleanEvidence'
```

Это исключает ровно один метод, а не весь класс или категорию `VisualUi`. Проверки допустимых надписей, обнаружения нежелательной надписи, изменения одного пикселя, остального UI и взаимодействий остаются включены.

## Отдельное сравнение 114 изображений

При ручном запуске workflow выберите `run_visual_baseline`. Отдельная job запускает только:

```text
LLMWorkGUI.Ui.Tests.UiInspectorVisualTests.UiInspector_AllNineteenScreensAcrossThemesAndDpi_ReportsCleanEvidence
```

Тест рендерит 19 экранов в двух темах при трёх DPI и строго сравнивает пиксели с `docs/acceptance/visual-baseline/net10-guide-20261008`. Изображения показывают тестовые данные. Settings-эталоны также содержат фиксированный демонстрационный путь профиля и исторические версии Windows/.NET.

Размеры рендера задаются самим тестом, но вывод версии ОС/runtime и шрифты зависят от машины. Эталоны созданы с .NET 10.0.12 и Windows 10.0.26200; произвольный hosted runner может не совпасть с ними. Ручная job честно завершается ошибкой при различиях, публикует диагностические артефакты и не обновляет эталоны автоматически. Для воспроизводимого прохождения требуется совместимое окружение. Стандартный зелёный CI не означает прохождение этого отдельного сравнения.

## Артефакты и локальные пути

Для каждой стандартной группы CI сохраняет TRX и лог, требует ненулевое число тестов и успешное выполнение всех выбранных случаев. Любая ошибка группы блокирует сборку дистрибутива. Не используйте прошлые результаты как доказательство для изменённого снимка.

Исторические отчёты Phase 0 и TASK-003–005 в `docs/acceptance` сохранены как документы контрактных тестов. Их старые числа тестов, версии и решения по этапам относятся к указанным в отчётах датам, а не к готовности текущего preview.

Старый `scripts/Invoke-ProjectChecks.ps1` сохранён как разработческий инструмент: по умолчанию он включает реальные probes и точное визуальное сравнение, а временные пути ограничивает диском D:. Публичный CI вызывает проекты напрямую с указанными фильтрами и коротким `TEMP/TMP`.

Живые запросы, авторизация внешних клиентов, точное визуальное совпадение и полноценная пользовательская приёмка не подтверждаются офлайн-наборами.
