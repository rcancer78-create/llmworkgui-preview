# ADR-0009: Per-user installer и канал обновлений

**Статус:** Accepted (Phase 12, Milestone 12C)
**Дата:** 2026-09-25
**Связано:** ТЗ §9.4, ROADMAP Phase 12, ADR-0001 (runtime baseline), ADR-0007 (star-cliproxy), ADR-0008 (Mirasim)

## 1. Контекст

ТЗ §9.4 требует, чтобы Release 1.0 поставлялся per-user Windows x64 installer-ом;
предпочтительный формат — подписанный MSIX, а при доказанной несовместимости с управлением
child processes или protocol transports допускается подписанный bootstrapper/installer с ADR.
Приложение поставляется self-contained, portable build допускается только как диагностический
артефакт. Auto-update приложения не входит в v1; installer обязан поддерживать upgrade и
rollback без удаления пользовательской БД. Внешние CLI не включаются в пакет и не обновляются
installer-ом.

## 2. Решение

Release Candidate 1.0 поставляется как **per-user self-contained bootstrapper/installer на
PowerShell** (`scripts/Install-LLMWorkGUI.ps1`, `Update-LLMWorkGUI.ps1`,
`Rollback-LLMWorkGUI.ps1`, `Uninstall-LLMWorkGUI.ps1` + модуль
`LLMWorkGUI.Packaging.psm1`), который:

1. распаковывает/копирует self-contained дистрибутив (`dotnet publish -r win-x64
   --self-contained true`, helper `scripts/Publish-LLMWorkGUI.ps1`) в
   `%LOCALAPPDATA%\Programs\LLMWorkGUI`;
2. не требует UAC elevation, не пишет в глобальный реестр и не изменяет внешние CLI;
3. создаёт ярлыки Start Menu и Desktop;
4. выполняет атомарный upgrade с точкой отката (`<install>.rollback`) и сохранением
   `%LOCALAPPDATA%\LLMWorkGUI` (SQLite, settings, сессии) без изменений;
5. выполняет rollback из точки отката и uninstall с опциональным удалением app-data
   (`-RemoveUserData`).

Пользовательские данные отделены от бинарных файлов: installer никогда не удаляет и не
перезаписывает app-data, кроме явного `-RemoveUserData`.
Rollback возвращает бинарные файлы и сохраняет текущую SQLite без downgrade. Если новая
версия уже повысила schema, старая версия может отказать при открытии такой БД; для её
запуска требуется отдельное явное восстановление совместимого backup согласно ADR-0011.
Lifecycle-проверка RC проверяет upgrade/schema 23→27, binary rollback с сохранением 27,
затем явный restore совместимого schema 23 и запуск старого приложения. Это не автоматический
schema rollback и не гарантия совместимости любой предыдущей версии.

## 3. Обоснование отказа от MSIX в v1

- **Управление child processes.** Продукт запускает и удерживает долгоживущие
  process-tree (managed `opencode serve`, `cursor-agent acp`, `star-cliproxy`) с
  bounded process-tree termination, перенаправлением stdin/stdout/stderr и run
  directories вне checkout. MSIX-контейнеризация добавляет виртуализацию файловой
  системы/реестра и ограничения на запуск внешних непакетированных исполняемых файлов,
  что усложняет (а на части конфигураций делает непредсказуемым) запуск внешних CLI.
- **Protocol transports.** ACP/JSON-RPC stdio и loopback HTTP требуют стабильных путей
  run-directory и loopback-сокетов, а также доступа к пользовательским внешним CLI; это
  проверяется live smoke-тестами вне MSIX-контейнера.
- **Self-contained и per-user.** ТЗ допускает self-contained поставку; per-user
  bootstrap-installer покрывает это напрямую без обязательной установки сертификата
  доверия пакета.
- **Управляемость в RC.** Скриптовый installer прозрачен, версионируем, тестируется
  интеграционными тестами на изолированном временном профиле (9 тестов жизненного цикла)
  и не требует инфраструктуры подписи пакетов на этапе RC.

## 4. Последствия

- Release Candidate распространяется как подписываемый в будущем bootstrapper; до подписи
  скрипты запускаются с `-ExecutionPolicy Bypass` и должны поставляться вместе с
  контрольными хэшами артефактов. Подпись bootstrapper/артефактов — обязательный шаг перед
  GA (не входит в v1 scope по auto-update).
- Auto-update приложения отсутствует: обновление выполняется явным `Update-LLMWorkGUI.ps1`
  с точкой отката. Это соответствует §9.4.
- Portable build не является поддерживаемым способом поставки и допускается только как
  диагностический артефакт.
- Внешние CLI остаются ответственностью пользователя; installer не включает, не обновляет
  и не модифицирует их.
- Резервный путь: если в будущем MSIX-упаковка докажет совместимость с child process и
  protocol transport контурами, возможен отдельный MSIX-канал без изменения разделения
  app-data/бинарных файлов, введённого этим ADR.

## 5. Альтернативы

| Альтернатива | Причина отказа |
|---|---|
| MSIX (подписанный) | Ограничения контейнера на запуск внешних CLI и process-tree управление; риск для loopback/stdio transports |
| Machine-wide MSI (per-machine) | Требует elevation и противоречит требованию per-user без UAC |
| Portable ZIP без installer-а | Не создаёт ярлыки, не управляет upgrade/rollback; допускается только как diagnostic artifact |
| Auto-update service | Исключён ТЗ из v1; требует отдельного канала доверия |
