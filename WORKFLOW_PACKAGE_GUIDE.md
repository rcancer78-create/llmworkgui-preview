# Пакеты workflow LLMWorkGUI

Импорт сохраняет исходный пакет как неизменяемую версию. Адаптация создаёт
кандидата отдельно; исходные bytes и настройки не исправляются автоматически.
Импорт и обнаружение entrypoint сами по себе не запускают скрипты.

## Форматы и ограничения

Импорт поддерживает ZIP и каталог. Корневой `workflow.json` либо `manifest.json`
считается формальным manifest. Если его нет, parser ищет документацию
README.md, SKILL.md, WORKFLOW.md или INSTRUCTIONS.md и известные entrypoints
эвристически. Такой discovery не удостоверяет исполнимость или безопасность.

Актуальные [лимиты](src/LLMWorkGUI.Application/Workflows/WorkflowImportLimits.cs):
архив100 MiB, суммарная распаковка500 MiB, до10000 записей, один файл20 MiB,
compression ratio до100. Manifest отдельно ограничен4 MiB.
[SafeArchiveValidator](src/LLMWorkGUI.Infrastructure/Workflows/SafeArchiveValidator.cs)
проверяет пути, collisions, symlinks и размеры до использования данных.

## Manifest

Parser читает `id`, `version`, `name`, `entrypoints`, `roles`, `bindings`
и `creationMetadata`, включая определённые в коде aliases. Entrypoints задаются
массивом относительных путей либо объектов с `path`. Пример описания:

```json
{
  "id": "example-documents",
  "version": "1",
  "name": "Документы проекта",
  "entrypoints": ["README.md"],
  "roles": []
}
```

Этот пример — metadata для импорта, не обещание готового executable workflow.
Полный поддерживаемый синтаксис проверяйте по
[WorkflowManifestParser](src/LLMWorkGUI.Infrastructure/Workflows/WorkflowManifestParser.cs)
и отдельному declarative graph validator. Не смешивайте manifest пакета
с execution plan и approval policy.

## Адаптация и активация

Выберите импортированную версию и совместимый маршрут. Capability mappings
проверяются по scoped текущим данным; неизвестные возможности не разрешают
сопоставление. Внешняя модель получает только разрешённый контекст. Кандидат
имеет отдельный hash, review и историю. Активация проверяет текущие bindings,
ссылки и предусмотренные gates; model verdict не заменяет owner acceptance.

Ошибку blob integrity не исправляйте изменением сохранённых bytes. Восстановите
пакет из проверенного backup либо импортируйте новую версию с новым hash.

[Хранение](DATA_STORAGE.md) · [Проверки](TESTING.md) · [Безопасность](SECURITY.md).
