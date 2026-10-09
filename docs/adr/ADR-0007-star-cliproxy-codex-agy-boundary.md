# ADR-0007 — star-cliproxy как граница Codex/AGY

- **Статус:** Accepted for Phase 5R implementation
- **Дата:** 2026-09-23
- **Заменяет:** части ADR-0002/ADR-0004, допускающие Codex или AGY через OpenCode; переходный direct-AGY transport TASK-032
- **Источник:** решение пользователя от 2026-09-23

## Контекст

OpenCode уже реализован как самостоятельный backend и остаётся полезен для OpenCode-native и пользовательских OpenAI-compatible providers. Он не должен быть transport, plugin host или account switcher для Codex и AGY.

`star-cliproxy` предоставляет локальный OpenAI-compatible endpoint и запускает Codex/AGY CLI как subprocess providers. Публичная документация подтверждает наличие provider blocks Codex/AGY, streaming и Codex CLI/app-server modes, но не доказывает требуемую LLMWorkGUI multi-account изоляцию на Windows. Поэтому конкретная схема процессов и передачи environment должна быть доказана spike до product wiring.

## Решение

1. Codex и AGY интегрируются только через отдельный adapter `star-cliproxy`. Их маршрутизация через OpenCode, OpenCode plugins или OpenCode provider config запрещена.
2. OpenCode сохраняется отдельным backend для собственных и пользовательских providers. Удаление OpenCode целиком не требуется.
3. AGY account selection выполняется только через `agy-profile`: `list/current/switch`, без `-Force`, `next`, `random`, чтения или копирования credentials. Switch сериализуется общим account/writer lock и запрещён при живом AGY execution.
4. Codex account selection выполняется выбором отдельного абсолютного `CODEX_HOME` для каждого account context. Глобальные `HOME` и `USERPROFILE` не изменяются; credential-файлы между каталогами не копируются. Каждый каталог создаётся/авторизуется пользователем независимо.
5. Account context immutable на время session/execution. Его изменение требует новой backend session; старые native session IDs не переносятся между аккаунтами.
6. До live-spike не предполагается, что один процесс `star-cliproxy` умеет безопасно обслуживать несколько `CODEX_HOME` или переключаемых AGY profiles. Допустимая fail-closed topology — отдельный managed proxy instance/config/loopback port на account context.
7. Секреты proxy (`ADMIN_TOKEN`, `PROXY_API_KEY`) хранятся через существующий secret store и передаются без command-line/log exposure. Raw payload/debug capture по умолчанию выключен либо проходит redaction.
8. Успех execution требует evidence фактических provider, account context, model и native session. Недоказанный account остаётся opaque/ManualOnly; несовпадение даёт `RouteMismatch`.

## Уточнение §8 (Phase 10): что именно нужно, чтобы наблюдение стало `Routes.Id`

§8 требует evidence, но не говорит, каких именно полей в хранилище не хватает, чтобы наблюдение шлюза превратилось в уникальный сохранённый `Routes.Id`. Шлюз сообщает native provider, native account, native model и session. Ни один из них сейчас не разрешается в строку `Routes`, поэтому read-only канал ревью (`star-cliproxy-review-readonly`) отказывает для **каждого** маршрута и объявляет `SupportsRoute` ложным. Чтобы §8 стал выполнимым, нужны все четыре пункта:

1. **Native provider id на `ProviderProfiles`.** Имя провайдера, которое сообщает шлюз, и наш идентификатор профиля — разные пространства имён; колонки, сопоставляющей их, нет.
2. **Стабильный идентификатор аккаунта, отличный от пути Codex и от метки профиля AGY.** Сейчас аккаунт опознаётся тем, где он лежит (`CODEX_HOME`, профиль `agy-profile`). Это адрес хранения, а не идентичность: два сохранённых профиля могут указывать на один путь, а путь может быть переназначен. Аккаунт, опознаваемый по расположению, не доказывает, какой аккаунт ответил.
3. **Сопоставление native model id на сохранённую модель.** Имя модели, которое вернул шлюз, не является `Models.ProviderModelId` и без явного отображения не разрешается в него.
4. **Уникальность ключа маршрута.** Либо размерности режима маршрута (reasoning effort, speed mode, execution mode) входят в наблюдаемый ключ, либо шлюз сообщает route key, который приложение наблюдает напрямую. Без этого две сохранённые строки `Routes` могут предъявить одну и ту же пару provider/account/model, а общая у трёх строк пара не идентифицирует ни одну из них.

Пока эти четыре пункта не существуют, угадывание соответствия означало бы наблюдение, которого не было: колонка `ObservedRouteId` у execution, привязка reviewer-execution evidence и гейт перехода затем читали бы выдумку. Отказ — единственный честный ответ, и он назван поимённо в `StarCliProxyReviewReadOnlyChannel.MissingNativeRouteIdentityReason` и в тексте отказа, который видит оператор.

## Последствия

- Ближайший этап — Phase 5R до Cursor Phase 6.
- Уже реализованные Routing Engine, quota domain, dashboard и process supervision переиспользуются.
- `AgyProfileAccountBridge` может быть сохранён как account-context component, но direct `AgyProcessRunner` не остаётся конечным transport после миграции.
- OpenCode plugin filtering остаётся защитным deny-boundary до удаления всех Codex/AGY OpenCode configuration paths.
- Capability matrix получает новый namespace `starcliproxy.*`; значения остаются `Unknown` до live evidence.
- Read-only канал ревью назначенной модели (`workflow.review.readonly`) существует и зарегистрирован в продуктовой композиции, но поддерживает ни один маршрут: гейт модели остаётся закрытым, ни одно исполнение ревьюера не пишется, ни один вердикт не создаётся. Отказ называет четыре недостающие идентичности из раздела выше.

## Источники

- https://github.com/starhunt/star-cliproxy
- https://github.com/starhunt/star-cliproxy/blob/main/config.example.yaml
- https://github.com/haclongkim/agy-profile
