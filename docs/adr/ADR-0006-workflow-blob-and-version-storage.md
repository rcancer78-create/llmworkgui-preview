# ADR-0006: Workflow blob и version storage

- **Статус:** Accepted
- **Дата:** 2026-09-22
- **Задача:** TASK-005 (Phase 0 — discovery и архитектурные контракты)
- **Связанные документы:** `TECHNICAL_SPECIFICATION.md` §2.2, §6.13, §6.14, §6.15, §6.16, §8, §9.3; `ROADMAP.md` Phase 0, Phase 8, Phase 10B; `docs/architecture/THREAT_MODEL.md` §5.4, §5.8; `docs/architecture/DATA_FLOW.md` §4.5-4.6
- **Связанный ADR:** `docs/adr/ADR-0005-secret-storage-windows.md`
- **Артефакты:** `docs/architecture/THREAT_MODEL.md`, `docs/architecture/DATA_FLOW.md`, `tests/LLMWorkGUI.Backends.ContractTests/SecurityAndStorageContractTests.cs`

## Контекст

ТЗ §2.2 требует неизменяемости импортированного workflow: сохранять байты исходного архива и его SHA-256, не изменять импортированный оригинал, исполнять выбранную пользователем active version, а любая адаптация создаёт новую version-кандидат. §6.13 требует сохранить оригинал как immutable blob, хранить manifest отдельно и выполнять preview/draft/adaptation/run на отдельной scratch-копии вне project root и вне blob store. §6.14-6.15 запрещают запись за пределы scratch, требуют повторной проверки hash source blob после операции и привязки начатого run к конкретному version ID. §6.16 задаёт default retention v1 и транзакционный cleanup. §8 разрешает хранить large raw logs и workflow blobs как файлы app data с hash/index в SQLite и требует atomic-запись `temp file → flush → rename`. ROADMAP Phase 8 фиксирует `immutable blob + SHA-256`, `immutable scratch isolation` и `post-operation source hash check`.

## Решение

### 1. Immutable content-addressed blobs

1. Каждая сохранённая version хранится как immutable blob; blob идентифицируется содержимым: `blobId = sha256:<lowercase-hex>`.
2. Хэш вычисляется детерминированно по байтам содержимого (SHA-256); имя файла, путь, время и метаданные в хэш не входят. Одинаковые байты всегда дают одинаковый `blobId` и один и тот же blob (deduplication).
3. Физический путь хранения нормативен: `<app-data>/blobs/sha256/<first2>/<full-hex>`, где `<first2>` — первые два hex-символа хэша.
4. Blob доступен только для чтения. Любое изменение содержимого создаёт новый `blobId`; in-place редактирование запрещено.
5. Запись atomic: temp file в том же томе → flush/fsync → rename в финальный путь. Частично записанный blob не становится видимым.
6. Manifest version (workflow ID/version ID, `originalHash`, source type, entrypoints, declared roles, bindings, compatibility report, creation/activation metadata) хранится отдельно от файлов пользователя в SQLite; `originalHash` — SHA-256 исходного архива, совпадающий с `blobId` исходного blob.
7. Экспорт version byte-identical оригиналу; rollback и активация перемещают только active pointer и не изменяют blobs; version, на которую ссылается начатый run, не удаляется.

### 2. Изоляция scratch вне checkout проекта

1. Preview, draft, adaptation и run выполняются только на копии в изолированном scratch: `<app-data>/scratch/<scope>/<scope-id>`, где scope — `preview`, `draft`, `adaptation` или `run`.
2. Scratch находится **вне project root/checkout** и **вне blob store**; scratch не является workspace проекта и не получает его writable-путь.
3. Adaptation и preview не берут checkout writer lock и не получают project root как writable workspace; source version открывается только для чтения.
4. Legacy entrypoint запускается только в scratch-копии, под checkout writer lock, в едином process tree; source blob при этом не изменяется.
5. Запись за пределы scratch блокируется path traversal guard; после операции выполняется post-operation source hash check, и расхождение hash — блокирующая ошибка.
6. Перенос результатов из scratch в проект возможен только явным действием пользователя (apply), под checkout writer lock и с path guard; импорт никогда не пишет в project root автоматически.

### 3. Path traversal guard

Path traversal guard применяется ко всем операциям импорта, распаковки, копирования, экспорта и cleanup:

1. Путь из недоверенного источника считается относительным от базового каталога; база канонизируется один раз.
2. Запрещены: пустой путь, `..`-сегменты, `.`-сегмент как выход за базу, rooted/абсолютные пути, drive-relative пути, UNC пути, управляющие символы и NUL.
3. Запрещён NTFS alternate data stream: символ `:` допускается только как разделитель буквы диска, но drive-relative и самостоятельные ADS-имена отклоняются.
4. После нормализации итоговый полный путь обязан находиться строго внутри базового каталога с учётом границы разделителя; на Windows сравнение выполняется case-insensitive.
5. Symlink/reparse point внутри импортируемого дерева отклоняются; Zip Slip, symlink и decompression bomb проверки не отключаются даже при override лимитов.
6. Default import limits v1: ZIP до 100 MiB, распакованный package до 500 MiB, не более 10 000 файлов, один файл до 20 MiB, compression ratio одного entry не более 100:1.

### 4. Retention categories и транзакционный cleanup

1. Default retention v1: project/session/workflow metadata и пользовательские messages — до явного удаления; raw backend events — 30 дней; process/server logs — 14 дней; diagnostic bundles — 7 дней; quota snapshots — 90 дней (downsampling старше 30 дней); health/audit transitions — 180 дней; immutable workflow versions — до явного удаления.
2. Retention настраивается раздельно по категориям; категория immutable workflow versions не удаляется, пока version является active или referenced любым run.
3. Cleanup транзакционен: удаление записей индекса и файлов выполняется согласованно, не оставляет «висячих» ссылок и не ломает session index; прерванный cleanup безопасно повторяется.
4. Каждый cleanup оставляет audit record без чувствительного payload: категория, количество, время, инициатор (retention policy или пользователь).
5. Cleanup не удаляет active/referenced artifacts и не изменяет content-addressed blobs, на которые есть ссылки.

## Последствия

- БД backup и export не содержат больших бинарных данных: blobs лежат в app data, в SQLite — hash/index и manifest.
- Deduplication бесплатна: повторный импорт тех же байтов не создаёт новый blob, но создаёт новый version ID при сохранении draft.
- Hash — первичный ключ целостности: любая операция чтения/экспорта может перепроверить `originalHash` без доверия к времени изменения файла.
- Phase 8 может реализовать import/preview/draft/adaptation/rollback поверх этого контракта без изменения blobs.
- Path traversal guard становится общей библиотекой Infrastructure с unit-тестами, а не локальной проверкой в UI.

## Риски и ограничения

- **GC blobs.** Удаление unreferenced blob требует reference counting и отложено до Phase 8; до этого blobs хранятся immutable, что увеличивает дисковый след.
- **Symlink/reparse races.** TOCTOU при распаковке снижается открытием файлов с проверкой типа, но окончательная гарантия зависит от ФС; race-сценарии покрываются тестами Phase 8.
- **Case sensitivity.** Windows case-insensitive, но экспорт/импорт на case-sensitive ФС (сетевые шары) требует явной нормализации; сравнение путей на Windows выполняется case-insensitive.
- **Retention vs. audit.** Транзакционный cleanup и audit взаимозависимы: ошибка в порядке операций может временно оставить файл без записи индекса; cleanup обязан быть идемпотентным.
- **Legacy entrypoint.** Entrypoint может писать вне scratch по своему усмотрению; ограничение обеспечивается тем, что ему передаётся только scratch-копия, а source blob и project root остаются недоступными на запись.

## Альтернативы

- **Хранить workflow как обычные файлы в project root** — отклонено: нарушает §6.13 и допускает изменение оригинала.
- **Mutable version с номером ревизии** — отклонено: не гарантирует byte-identical rollback и ломает post-operation hash check.
- **Хранить blobs внутри SQLite BLOB-колонок** — отклонено: раздувает БД и backup, противоречит §8 («large workflow blobs можно хранить как файлы с hash/index в SQLite»).
- **Scratch внутри project root (`.llmworkgui/`)** — отклонено: нарушает изоляцию, попадает в git status/diff, может быть затронут инструментами проекта и writer lock.
- **Zip Slip/symlink проверки отключаемы через override** — запрещено §6.13: override может поднять лимиты, но не отключает проверки.
- **Удаление blobs по времени без учёта ссылок** — отклонено: нарушает требование сохранности active/referenced version.
