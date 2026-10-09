# Архитектура LLMWorkGUI

LLMWorkGUI — Windows WPF приложение на .NET 10. Оно хранит локальные
маршруты и состояние выполнения, управляет принадлежащими ему процессами
и подключает нативные backend протоколы. Текущее дерево использует schema30;
итоговая приёмка проекта ещё открыта. Результаты прежних release candidates
относятся к их исходникам и пакетам.

## Слои и границы

| Слой | Ответственность |
|---|---|
| Domain | Сущности, состояния, привязки сессий и доменные ограничения |
| Application | Сценарии, routing, workflow и интерфейсы инфраструктуры |
| Infrastructure | SQLite, secrets, процессы, файловое хранение и реализации сервисов |
| Backends.Abstractions | Контракты transport и нативных наблюдений |
| Backends.OpenCode | Managed server, HTTP/SSE, sessions и permissions |
| App | WPF, view models, композиция и пользовательские команды |

[App composition](src/LLMWorkGUI.App/DependencyInjection/AppServiceCollectionExtensions.cs)
соединяет экран с конкретными сервисами. Встроенные проекты LLMGateway входят
в решение; самостоятельное дерево Gateway проверяется отдельно и не получает
автоматически результаты встроенной копии.

## Путь запроса

Пользователь выбирает сохранённый маршрут и создаёт сессию. Приложение проверяет
проект, классификацию данных, актуальность привязки и доступность выбранного
маршрута, сохраняет admission, получает checkout lock, затем проверяет право
отправки непосредственно перед transport. Нативные события обновляют состояние
turn. Неопределённая доставка сохраняет reservation и lock для reconciliation.
Запрос не переносится автоматически в другую модель или новую сессию.

Поколение процесса — локальный идентификатор запуска, выдаваемый supervisor.
Он применяется вместе с владельцем приложения и execution. PID, порт и model
slug не удостоверяют сессию, аккаунт или происхождение ответа. Cursor и OpenCode
передают generation до transport; оставшиеся пути перечислены в
[очереди завершения](PROJECT_REMAINING_WORK.md).

## Возможности модели

[ModelCapabilityEvidence](src/LLMWorkGUI.Application/Providers/ModelCapabilityEvidence.cs)
привязано к модели, аккаунту, источнику и сроку действия. Discovery захватывает
context revisions до операции; store сравнивает их в writer transaction.
Смена identity, auth или credentials отзывает старые данные. Inventory сам
по себе не подтверждает chat, tools, reasoning или размер context.
Codex NativeGateway publisher подключён в completion r7. OpenCode/Cursor ACP publishers пока остаются открытыми.

## Связанные документы

- [Подробная архитектура и исторические checkpoints](docs/architecture/ARCHITECTURE_RELEASE.md).
- [OpenCode transport ADR](docs/adr/ADR-0002-opencode-serve-topology-and-api.md).
- [Cursor ACP ADR](docs/adr/ADR-0003-cursor-acp-lifecycle.md).
- [Хранение](DATA_STORAGE.md), [безопасность](SECURITY.md), [диагностика](DIAGNOSTICS.md).
- [Проверки](TESTING.md) и [фактическая очередь приёмки](PROJECT_REMAINING_WORK.md).

## NativeGateway process admission (completion r6)

Обычная GUI отправка через shipped NativeAdapterBase удерживает checkout token,
создаёт Windows child suspended внутри private Job и передаёт фактический
NativeProcess dispatch authorization. SQLite transaction сохраняет
NativeProcessBound и переводит pending lock generation0 в generation этого
процесса. Gate повторно сверяет маршрут, owner, physical token и durable proof
до resume; prompt stdin начинается после resume. Отказ gate завершает owned
child. Generation не подтверждает native account/model/remote origin.
Compatibility HTTP bridge и custom gateway имеют отдельную ownership boundary;
к ним нельзя приписывать generation несуществующего дочернего процесса.
