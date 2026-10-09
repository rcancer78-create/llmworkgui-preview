# LLM Work GUI — Data Flow

- **Статус:** Accepted
- **Дата:** 2026-09-22
- **Задача:** TASK-005 (Phase 0 — discovery и архитектурные контракты)
- **Нормативные ссылки:** `TECHNICAL_SPECIFICATION.md` §2.1, §4.1-4.2, §6.1-6.2, §6.4-6.5, §6.7-6.8, §6.9, §6.11-6.13, §6.16, §8, §9.3, §10; `ROADMAP.md` Phase 0
- **Связанные документы:** `docs/architecture/THREAT_MODEL.md`, `docs/adr/ADR-0005-secret-storage-windows.md`, `docs/adr/ADR-0006-workflow-blob-and-version-storage.md`

## 1. Назначение

Документ описывает нормативные потоки данных Level 0 и Level 1, классификацию каждого потока, точки redaction и границы изоляции. Обозначения процессов (`P1`–`P6`), хранилищ (`DS1`–`DS4`) и потоков (`F1`–`F15`) используются как стабильные идентификаторы в тестах и последующих ADR.

## 2. Акторы, процессы и хранилища

| ID | Элемент | Тип | Описание |
|---|---|---|---|
| `U1` | User | External entity | Пользователь GUI; единственный источник approvals и API keys |
| `EXT1` | Provider endpoints | External entity | Внешние OpenAI-compatible/Codex/AGY/Cursor endpoints |
| `OS1` | Windows secret storage | External data store | Windows Credential Manager / DPAPI CurrentUser |
| `P1` | UI | Process | WPF views/view-models, secure input fields, отображение redacted данных |
| `P2` | App Core | Process | Application Services: каталоги, Routing Engine, Session Manager, Workflow Service, Quota/Health |
| `P3` | Supervisor | Process | Process Supervisor + Execution Watchdog: lifecycle, pipes, cancellation, normalization |
| `EXT2` | Mirasim host | External entity | Пользовательский HTTP host, managed instance либо внешний сервис; собственный native lifecycle требует отдельной приёмки |
| `P4` | Managed Process | Process | `opencode serve`, `star-cliproxy` с owned Codex/AGY provider subprocesses, `cursor-agent acp`, optional supervised legacy entrypoint |
| `DS1` | SQLite | Data store | Метаданные, состояния, история, индексы, secret URN references |
| `DS2` | Blob store | Data store | Immutable content-addressed workflow blobs (SHA-256) |
| `DS3` | Isolated scratch | Data store | Preview/draft/adaptation/run scratch вне project root и blob store |
| `DS4` | Logs и audit | Data store | Structured logs, health/audit transitions, search index, diagnostic bundles (только redacted) |

## 3. Level 0 — контекстная диаграмма

```text
                    prompts, approvals, API keys, project paths
        +---------+ -------------------------------------------> +---------------------------+
        |   U1    |                                              |                           |
        |  User   | <------------------------------------------- |     LLM Work GUI          |
        +---------+     chat/run, route, quota, health, diffs    |  (P1 UI + P2 App Core +   |
                                                                 |        P3 Supervisor)     |
        +---------+                                              |                           |
        |  OS1    | <--- secret writes (Credential Manager) ---- |                           |
        | Windows | ---> secret reads (только в момент send) ---> |                           |
        | secret  |                                              +-------------+-------------+
        | storage |                                                            |
        +---------+                                                prompts, events, cancel
                                                                               |
                                       +---------------------------------------+-------------------------------+
                                       |                                                                       |
                                       v                                                                       v
                         +---------------------------+                             +-------------------------------+
                         |  P4 Managed Process       |                             |  EXT1 Provider endpoints      |
                         | (opencode/star-cliproxy/ | <-------------------------> |  (HTTPS; data-class gated)    |
                         |       cursor-agent)      |                             |                               |
                         +-------------+-------------+       HTTPS / loopback      +-------------------------------+
                                       |
                                       v
                         +---------------------------------------------------------------+
                         |  Local Storage                                                |
                         |  DS1 SQLite | DS2 Blob store | DS3 Scratch | DS4 Redacted logs |
                         +---------------------------------------------------------------+

        P2 App Core <--- F15: HTTP turn/watch/cancel/reconcile ---> EXT2 Mirasim host
```

Level 0 правила:

1. Секреты пересекают границу только между `OS1` и процессами `P2`/`P3`; в prompts, SQLite и логи значение не попадает.
2. Данные наружу (`EXT1` и `EXT2`) уходят только после data classification gate и только в рамках закреплённого route.
3. Управляемый процесс получает рабочую копию checkout/scratch, но не получает секреты в argv.

Mirasim (`EXT2`) получает HTTP turn/watch/cancel/reconcile от App Core через backend adapter. Поток ограничен явно выбранным host и закреплённым route, проходит classification/egress gate. HTTP cancel acknowledgement не подтверждает native terminal исход. Ответы нормализуются и редактируются до audit/UI. Host может быть внешним: он не становится owned process только по настройке адреса.

## 4. Level 1 — декомпозиция

```text
 U1 ──F1──> P1 UI ──F2──> P2 App Core ──F4──> DS1 SQLite (metadata + URN references)
                ^              │  │  │
                │              │  │  └──F5──> OS1 Windows secret storage
                │              │  │
                │              │  └──F12──> DS2 Blob store (immutable SHA-256 blobs)
                │              │
                │              └──F13──> DS3 Isolated scratch (preview/draft/adapt/run)
                │
                │              P2 ──F5b──> P3 Supervisor ──F6──> P4 Managed Process
                │                                ^                    │
                │                                └────F7──────────────┘
                │                                     stdout/JSON-RPC/ACP
                │
                └──F9── P1 <──F8── P3 (normalized events после redaction)

 P4 <──F6──> EXT1 Network (loopback server / HTTPS provider endpoints)
 P3 ──F10──> DS4 Logs/audit/search (redacted)
 P2 ──F11──> DS4 audit (routing decisions, approvals, health transitions)
 P2 ──F14──> DS3 cleanup / retention (transactional)
 P2 <──F15──> EXT2 Mirasim HTTP host (pinned route; ack ≠ native terminal)
```

### 4.1. Потоки секретов (F1, F4, F5, F5b)

1. `F1`: API key вводится через защищённое поле UI (`PasswordBox`-семантика) и не отображается после сохранения; UI передаёт значение в App Core один раз, без логирования и без буфера обмена.
2. `F4`: App Core пишет в SQLite только `SecretReference` вида `urn:llmworkgui:secret:openai-api-key` и metadata (kind, provider profile, timestamps). Значение секрета через эту границу не проходит.
3. `F5`: App Core сохраняет/читает значение через Windows Credential Manager или DPAPI CurrentUser; исходное значение существует только в памяти процесса.
4. `F5b`: непосредственно перед запросом к provider значение передаётся managed process scoped environment конкретного child process. Передача секрета в command line/argv запрещена.
5. Redaction применяется до записи в `DS1`/`DS4`, до config preview и до diagnostic bundle. Допустимы opaque `[REDACTED]`/`***REDACTED***` и typed `[REDACTED:<kind>]` согласно ADR-0010; placeholder повторно не преобразуется.

### 4.2. Потоки промптов (F2, F6, F8)

1. Промпт формируется в `P2` из пользовательского ввода, выбранного route и данных проекта; классификация наследуется от проекта.
2. Каждый запрос получает `clientRequestId` и hash нормализованного payload; `requestedRoute` фиксируется до отправки.
3. Перед выходом на `EXT1` проверяется eligibility gate: route enabled, auth valid, model/reasoning/speed/mode поддерживаются, route не quarantined, quota gate, concurrency slot, `data classification ≤ maxDataClass`.
4. `Restricted` fragment отправляется только после отдельного preview/подтверждения.
5. Ответ provider (`F7`) нормализуется, проверяется на `observedRoute` и проходит redaction до записи; расхождение завершает execution как `RouteMismatch`.

### 4.3. Потоки SSE-событий (F5b, F6, F7, F8, F9)

1. `opencode serve` публикует SSE на loopback-порту; `star-cliproxy` публикует OpenAI-compatible SSE и владеет Codex/AGY subprocess; Cursor ACP отдаёт streaming updates по stdio.
2. Codex account context передаётся owned proxy/CLI process через отдельный `CODEX_HOME`; AGY profile выбирается `agy-profile` до proxy execution под общим lock. OpenCode в этих потоках не участвует.
3. Supervisor нормализует события в `ExecutionEvent` и сохраняет raw/normalized связь; malformed event даёт health class `malformed protocol/event` и не исполняется.
4. Redaction выполняется до записи в `DS1`/`DS4` и до отображения в `P1`.
5. События идут в UI через bounded очередь; списки виртуализируются, память не растёт бесконечно.
6. Cancellation передаётся в managed process, но не отменяет уже возможную доставку prompt: outcome может стать `Ambiguous`.

### 4.4. Потоки approvals (F7, F2, F8, F11)

1. Backend присылает permission request (`F7`); adapter переводит native kind в нормализованный kind.
2. `P2` показывает исходный запрос и нормализованное объяснение в `P1`; неизвестный kind — `UnknownHighRisk`.
3. Пользователь выбирает из ответов, подтверждённых для конкретного backend (`F2`); adapter передаёт только поддержанный ответ. OpenCode использует `once`/`reject`; execution-wide allow показывается только при доказанном native mapping и не доступен для UnknownHighRisk/HighRiskDestructive.
4. Persistent approval rule создаётся только через отдельный экран и содержит backend/provider scope, project/path scope, operation, срок действия и автора; создание/изменение/удаление записывается в `DS4` (`F11`).
5. Автоматический `auto approve` по умолчанию выключен.

### 4.5. Потоки workflow blobs (F12, F13, F14)

1. Импорт ZIP/папки проходит archive safety: path traversal, symlinks, абсолютные пути, лимиты размера/числа файлов/compression ratio.
2. Оригинал сохраняется в `DS2` как immutable content-addressed blob `sha256:<lowercase-hex>`; путь хранения — `blobs/sha256/<first2>/<full-hex>`; запись atomic: temp file → flush → rename.
3. Manifest (workflow ID/version ID, original hash, source type, entrypoints, roles, bindings) хранится отдельно в `DS1`; version с одним и тем же содержимым не дублируется.
4. Draft/candidate всегда создаёт новый immutable version ID; active pointer переставляется только после явного подтверждения; rollback перемещает только pointer и не изменяет blobs.
5. Preview, draft, adaptation и run работают на копии в `DS3`; после операции hash source blob проверяется повторно (`F14`).

### 4.6. Изолированный scratch и cleanup (F13, F14)

1. `DS3` располагается вне project root/checkout и вне blob store (`DS2`); каталог создаётся на каждый scope (preview/draft/adapt/run) и удаляется по retention.
2. Adaptation не получает project root как writable workspace и не берёт checkout writer lock; source version открывается только для чтения.
3. Legacy entrypoint запускается только в scratch-копии, под checkout writer lock и в едином process tree.
4. Path traversal prevention применяется ко всем операциям с `DS3`: `..`-сегменты, абсолютные пути вне базового каталога, drive-relative/UNC пути и NTFS alternate data streams запрещены.
5. Cleanup транзакционен: не удаляет active/referenced workflow version, не ломает session index и оставляет audit record без чувствительного payload.

## 5. Таблица потоков

| ID | From | To | Данные | Класс | Контроль |
|---|---|---|---|---|---|
| `F1` | U1 | P1 | prompt, project path, approval answer, API key (secure field) | PrivateSource / Secrets | Ввод не логируется; API key сразу передаётся в P2 |
| `F2` | P1 | P2 | команды, выбор route, ответы approvals | PrivateSource | UI не запускает процессы и не пишет в SQLite |
| `F4` | P2 | DS1 | metadata, состояния, history, secret URN references | PrivateSource | Секреты в таблицах запрещены; только reference |
| `F5` | P2 | OS1 | запись/чтение secret value | Secrets | Credential Manager / DPAPI CurrentUser; значение только в памяти |
| `F5b` | P2 | P3 | scoped environment для child process | Secrets | Секрет не в argv; environment только нужному процессу |
| `F6` | P3 | P4 | spawn, stdin/JSON-RPC/ACP, cancel | PrivateSource | Управляемый spawn, loopback/stdin, без секретов в командной строке |
| `F7` | P4 | P3 | stdout, SSE, ACP updates, permission requests | PrivateSource | Нормализация, schema validation, redaction до записи |
| `F8` | P3 | P2 | normalized events, observed route evidence, terminal outcome | PrivateSource | `requestedRoute`/`observedRoute`, `RouteMismatch` |
| `F9` | P2 | P1 | live status, route/quota/health, approvals, diffs | PrivateSource | Только redacted данные; paging/virtualization |
| `F10` | P3 | DS4 | process lifecycle, protocol errors, redacted raw events | PrivateSource | Append-only; redaction до записи |
| `F11` | P2 | DS4 | routing decisions, health transitions, approvals, quota refresh | PrivateSource | Correlation IDs; audit без секретов |
| `F12` | P2 | DS2 | immutable workflow blob + SHA-256 | PrivateSource | Content-addressed, read-only, atomic write |
| `F13` | P2 | DS3 | preview/draft/adaptation/run файлы | PrivateSource | Scratch вне project root и blob store; path traversal guard |
| `F14` | P2 | DS2/DS3/DS4 | retention cleanup, post-operation hash check | PrivateSource | Транзакционный cleanup; active/referenced version не удаляется |

## 6. Изоляция и redaction: сводные инварианты

1. **Redaction до записи.** Диагностические потоки в `DS1`/`DS4` и диагностический UI проходят redaction pipeline. Границы распознаваемых текстовых и структурированных форматов описаны в `SECURITY_GUIDE.md`; фильтр не доказывает очистку произвольной сериализации или кодированного секрета.
2. **Секреты и ссылки.** Ключи, сохранённые приложением, находятся в Windows secret storage; в metadata хранится URN reference. Распознанные секреты в диагностике заменяются placeholder. Передача секрета нативному клиенту использует память процесса и его поддерживаемый механизм авторизации; это не утверждение об отсутствии копий в CLR, внешнем клиенте или любых пользовательских исходниках.
3. **Scratch изолирован.** `DS3` не пересекается с project root/checkout и `DS2`; запись за пределы scratch блокируется path traversal guard.
4. **Blobs неизменяемы.** Любая новая ревизия — новый content-addressed blob; существующий blob доступен только для чтения.
5. **Network gate.** Наружу уходят только данные, разрешённые классификацией проекта и выбранным route; loopback-only по умолчанию.
6. **Audit.** Routing decisions, approvals, health transitions и lock ownership сохраняются с correlation IDs; audit не содержит секретов и чувствительного payload.
