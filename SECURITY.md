# Безопасность LLMWorkGUI

Этот документ описывает границы доверия и порядок сообщения о дефектах.
Текущий проект ещё проходит приёмку. Зелёные тесты и отдельный model review
не являются общей security acceptance.

## Границы доверия

Workflow, файлы проекта, model output и нативные события — входные данные.
Они не предоставляют разрешения на выполнение команд, изменение маршрута,
доступ к секретам или автоматическое одобрение инструментов.
Подробные сценарии угроз и меры защиты приведены в
[threat model](docs/architecture/THREAT_MODEL.md) и
[security guide](docs/architecture/SECURITY_GUIDE.md).

Проект имеет классификацию PublicSource, PrivateSource или Restricted.
Отправка зависит от сохранённой политики маршрута и провайдера. Secret данные
не должны попадать в prompt, trace или diagnostic export. Возможности backend
и принадлежность ответа требуют отдельных наблюдений; имя модели не даёт
такого подтверждения.

## Credentials и процессы

GUI использует secret references, Windows Credential Manager и DPAPI fallback.
Не сохраняйте рабочие API keys в репозитории или appsettings.json.
Secret payloads, native auth-файлы и пользовательские profiles не входят
в пакеты исходников для ревью.

Supervisor управляет созданными им process trees. Managed OpenCode получает
изолированную конфигурацию и окружение. Неопределённый результат transport
не разрешает повтор запроса или снятие удерживаемой блокировки. Manual recovery
должен учитывать фактическую сессию и процесс; один HTTP abort acknowledgement
не доказывает завершения remote inference.

## Сообщение о дефекте

До определения публичного security contact сообщайте владельцу проекта
приватно. Укажите конкретную версию/manifest, затронутый сценарий, ожидаемое
и фактическое поведение и минимальное воспроизведение. Используйте синтетические
credentials и обезличенный диагностический пакет. Не публикуйте живые ключи,
auth-файлы, содержимое чужих проектов или сырые model traces.

Если ошибка связана с неопределённой доставкой, сохраните receipt и состояние
для reconciliation; не отправляйте запрос повторно ради воспроизведения.

[Диагностический export](DIAGNOSTICS.md) · [Проверки](TESTING.md) ·
[Актуальные ограничения](PROJECT_REMAINING_WORK.md).

Completion r6: обычный GUI NativeGateway dispatch связывает journal с реальным
owned suspended child до исполнения его кода/prompt stdin. Gate проверяет
physical checkout token и durable process binding; отказ commit/cancellation
не возобновляет ребёнка. Локальное завершение подтверждается private Job и root
process; это не доказательство прекращения внешней операции модели. Arbitrary
public adapter subclass не получает transport authorization capability; friend
assembly fixtures явно являются тестовыми двойниками. HTTP bridge имеет
отдельный контракт и не получает синтетическое process generation.

Completion r7: authenticated Codex options используют отдельный trusted adapter
contract и headerless Codex RPC dialect. Strict JSON-RPC2.0 остаётся default
для других клиентов. Native account/read проверяется до/после model/list;
local model/account revisions захватываются до I/O и сверяются в writer
transaction. Смена API-key value при прежнем env name инвалидирует gateway
observation; fingerprint не сохраняется в SQLite, diagnostics или review packet.
DiscoverySource не переносится в user declarations. Native reported evidence
не означает личное ручное подтверждение и не подтверждает response origin.

Completion r8: Codex reasoning option фиксируется при admission и сравнивается
с request, route, session и Starting audit event перед native dispatch.
Scoped evidence должно оставаться Supported и действующим до разрешения
transport; изменённое или удалённое подтверждение останавливает suspended
child до resume/prompt. Эта проверка параметра не подтверждает upstream model
identity и не расширяет права native клиента.
