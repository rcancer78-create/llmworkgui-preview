# Диагностика LLMWorkGUI

Settings & Diagnostics предоставляет preview/export, backup, integrity check,
ручной recovery и retention. Используйте эти операции с сохранением состояния
неопределённых запросов: повтор model turn может создать второе выполнение.

## Сбор диагностики

Зафиксируйте версию приложения, backend/client version, время и выбранный
маршрут, затем конкретный шаг и видимый результат. Перед export проверьте
redaction preview. Передавайте только разрешённый диагностический пакет;
не прикладывайте live API keys, auth-файлы и сырые traces из пользовательского
профиля. Подробнее: [diagnostics guide](docs/architecture/DIAGNOSTICS_GUIDE.md).

## Разбор результата

| Наблюдение | Следующий шаг |
|---|---|
| Нет process generation либо процесс retired | Создать подтверждённую новую сессию; сохранить неотправленный prompt |
| Native inventory не содержит точной модели | Обновить каталог в том же profile и проверить model/account scope |
| Capability Unknown или Stale | Обновить discovery либо явно подтвердить допустимую ручную декларацию |
| Ambiguous или retained lock | Выполнить reconciliation по фактическому execution; не повторять turn автоматически |
| Не подтверждён terminal или cleanup | Сохранить ownership/evidence и проверить точный принадлежащий приложению процесс |
| SQLite integrity или blob hash failure | Сохранить ошибку, проверить совместимый backup; не редактировать bytes вручную |

Запущенный process, exit0, session ID и текст модели «готово» имеют разные
значения. Отмечайте requested identity, native-reported metadata и независимое
подтверждение отдельно. Наличие старого release PASS не удостоверяет текущее дерево.

## Восстановление

Проверьте backup перед обновлением схемы. Binary rollback требует совместимой
БД; понижение schema не выполняется. Restore доступен service API, а не отдельной
командой текущего diagnostic экрана. Не удаляйте active locks вручную ради
разблокировки composer.

[Release lifecycle](docs/README_RELEASE.md) · [Хранение](DATA_STORAGE.md) ·
[Порядок тестов](TESTING.md) · [Актуальная очередь](PROJECT_REMAINING_WORK.md).
