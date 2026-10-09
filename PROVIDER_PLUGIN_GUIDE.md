# Провайдеры и плагины LLMWorkGUI

Провайдер объединяет transport и конфигурацию подключения; аккаунт задаёт
credentials и локальную identity. Маршрут связывает provider, account и model.
Конфигурация маршрута не заменяет проверку native доступности.

## Настройка провайдера

Создайте профиль через экран провайдеров, задайте поддерживаемый backend и
endpoint либо путь к локальному клиенту. Credentials вводятся через secret
storage и сохраняются как references. Выполните connection test, обновите
inventory моделей, выберите аккаунт и сохраните маршрут. Не включайте модель
по одному предполагаемому slug.

Для custom providers доступна настройка endpoint, model inventory и дополнительных
headers. Неуспешное discovery не должно заменять сохранённый каталог пустым.
Inventory-only metadata оставляет capabilities неизвестными. Если backend
допускает ручную декларацию, вкладка «Возможности» сохраняет её как UserDefined
с выбранным account/model и сроком действия. Cursor требует per-model discovery;
ручная декларация не разрешает его reasoning/speed/mode options.

## OpenCode plugins

GUI предоставляет inventory настроенных OpenCode plugins через
[IPluginInventoryService](src/LLMWorkGUI.Application/Providers/IPluginInventoryService.cs).
Запись имени в конфигурации не удостоверяет установку, выполнение plugin,
идентичность аккаунта или поддержку переключения.

Managed OpenCode запускается в собственном контексте. Нельзя рассчитывать на
автоматическое наследование пользовательского CLI profile, plugins или auth.
Привязывайте разрешённую конфигурацию явно; используйте фактический native
контракт и версию клиента. Оpaque variant name не превращается автоматически
в `reasoning_effort`.

## Добавление backend

В текущем проекте нет документированного универсального SDK для загрузки
произвольного DLL plugin в GUI. Новый backend требует контрактов в
Backends.Abstractions, реализации lifecycle/discovery и DI composition,
проверок ownership, cancellation, uncertain delivery, permissions и data
classification, затем native acceptance на реальном клиенте.

[Архитектура](ARCHITECTURE.md) · [Безопасность](SECURITY.md) ·
[OpenCode ADR](docs/adr/ADR-0002-opencode-serve-topology-and-api.md) ·
[Cursor ADR](docs/adr/ADR-0003-cursor-acp-lifecycle.md).

## Authenticated model options (completion r7)

Для NativeGateway/Codex на экране возможностей выберите модель и аккаунт,
затем «Получить из нативного клиента». Publisher захватывает local binding и
revisions до native I/O; Codex account/read проверяется до/после model/list в
одной RPC session. Только actual supportedReasoningEfforts становятся scoped
reasoning evidence с TTL10minutes и явным DiscoverySource. Название модели,
static ProviderCapabilities и inventory не подтверждают chat/tools/vision или
context limit. Остальные native clients возвращают unsupported до реализации
их соответствующего публичного discovery-контракта. OpenCode/Cursor ACP
publishers и transport speed/mode options пока не закрыты.
Native source не становится личным manual confirmation пользователя.

Completion r8: NativeGateway/Codex передаёт сохранённый ReasoningEffort в
`codex exec -c model_reasoning_effort=...`. Для этого требуется свежая scoped
capability declaration с точным значением для выбранных account/model;
ручное подтверждение остаётся UserDefined. Catalog, admission, preparation,
process binding и разрешение transport проверяют её повторно. Значение
фиксируется в selected binding, session и Starting lifecycle event. Смена
маршрута, session option или удаление evidence до отправки закрывает выполнение
без prompt. Default null сохраняется; другие native providers не получают
reasoning transport автоматически из общей capability flag.
