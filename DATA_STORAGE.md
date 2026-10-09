# Хранение данных LLMWorkGUI

Рабочие данные хранятся отдельно от checkout и установленных binaries.
Смена версии приложения и восстановление данных — отдельные операции;
binary rollback не понижает SQLite schema.

## SQLite и файлы

По умолчанию используется `%LOCALAPPDATA%\LLMWorkGUI` и файл `llmworkgui.db`.
[StorageOptions](src/LLMWorkGUI.Application/Configuration/StorageOptions.cs)
позволяет задать корень и имя БД. SQLite также может иметь WAL/SHM файлы;
копирование только основного файла работающего приложения не заменяет backup.
Текущие миграции находятся в
[Data/Migrations](src/LLMWorkGUI.Infrastructure/Data/Migrations).

| Данные | Где и зачем |
|---|---|
| Profiles, accounts, models, routes | SQLite; настройки и локальные references |
| Sessions, executions, requests, events | SQLite; admission, состояния и аудит |
| ProjectLocks | SQLite; владение checkout с application/execution/generation |
| ModelCapabilities | SQLite; scoped evidence, provenance, freshness и context revisions |
| Workflow packages и versions | SQLite metadata и неизменяемые content blobs |
| Process logs и diagnostic artifacts | App data/run directories, вне checkout |
| Secrets | Credential Manager или DPAPI; БД хранит references |

[WorkflowBlobStore](src/LLMWorkGUI.Infrastructure/Storage/WorkflowBlobStore.cs)
адресует данные по `sha256:<64 lowercase hex>` и хранит их в
`blobs/sha256/<первые два символа>/<hash>`. При чтении проверяются длина и hash;
открытый verified handle защищает bytes до закрытия. Повтор открытия ограничен
случаем временного Windows sharing violation.

## Backup, restore и retention

Перед обновлением со сменой схемы сохраните совместимый backup остановленного
приложения. Для rollback используйте backup версии, которую запускаете.
Инсталлятор не должен удалять пользовательские данные при обычном обновлении.
Порядок lifecycle приведён в [release guide](docs/README_RELEASE.md).

Backup, integrity check, diagnostic preview/export и retention доступны через
Settings & Diagnostics. Restore предоставлен service API; отдельная команда
restore в этом экране ещё не реализована. Принудительное удаление удерживаемых
locks или active execution rows может разрушить evidence о доставке.

[Диагностика и recovery](DIAGNOSTICS.md) · [Threat model](docs/architecture/THREAT_MODEL.md).

Completion r6 добавляет audit event NativeProcessBound: processGeneration,
applicationInstanceId и nativeIdentityConfirmed=false, без prompt/ответа.
В одной writer transaction pending ProjectLock generation0 заменяется generation
созданного suspended OS child. MarkRunning требует соответствия lock и binding
event. Schema30 не меняется. Lifecycle replay читает только свои EventKind;
служебный binding event не становится отображаемым состоянием выполнения.

Completion r7 дополняет versioned ModelCapabilityEvidence JSON nullable
DiscoverySource. Legacy payloads остаются читаемыми без этого поля; schema30
не меняется. Authenticated Codex publisher сохраняет exact reasoning options
для одного model/account и source/TTL10minutes, с captured context revisions.
User declarations очищают DiscoverySource и получают UserDefined. Discovery
не пишет credentials, native account identity payload или model output.

Completion r8 использует существующее Sessions.ReasoningEffort и дополняет
NativeGatewayLifecycle payload полем reasoningEffort, включая JSON null для
default. Starting event хранит исходное значение admission; dispatch сравнивает
его с session/route/context. Schema30 не меняется. Lifecycle replay старых
событий продолжает работать; старые незавершённые исполнения требуют recovery,
а не повторной отправки.
