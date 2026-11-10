<p align="center"><img src="assets/icon.png" width="88" alt="Netlane"></p>

# Netlane для Windows

Лёгкий VPN-клиент для Windows 10 и 11 x64 на базе TrustTunnel. Подключается к собственному серверу по подписке и поддерживает несколько локаций.

<p align="center"><img src="assets/screenshots/windows-client.png" width="760" alt="Главное окно Netlane: навигация слева, состояние, таблица локаций и сессия"></p>

## Скачать и подключиться

1. Скачайте `Netlane-windows-x64.zip` и файл SHA-256 из [релизов](https://github.com/divezh/netlane-windows/releases/latest).
2. Проверьте контрольную сумму: `(Get-FileHash .\Netlane-windows-x64.zip -Algorithm SHA256).Hash` — она должна совпасть с указанной в файле `.sha256`.
3. Распакуйте **весь архив** в отдельную папку и запустите `Netlane.exe` рядом с папкой `engine`.
4. Скопируйте ссылку на подписку, нажмите **«Вставить»**, затем **«Подключиться»**. Ссылку никому не передавайте.

При подключении Windows попросит разрешение администратора. Ссылку на подписку выдаёт администратор сервера. [Подробная инструкция](clients/windows/README.md).

Серверная часть — панель, ноды и установка на VPS — в отдельном репозитории [divezh/netlane](https://github.com/divezh/netlane).

## Возможности

- HTTP/2 (TCP) и HTTP/3 (QUIC/UDP), Anti-DPI, Kill Switch.
- Несколько серверов по одной подписке, обновление подписки, проверка задержки.
- Маршрутизация доменов, IP и приложений, DNS из подписки или личных настроек.
- При выключенном другом VPN доступна выборочная маршрутизация; ограничения описаны в [инструкции](clients/windows/README.md).

Для правил приложений необходима сборка с модифицированным движком (см. ниже). Управление пользователями, нодами и лимитами выполняется отдельно, в панели сервера.

## Сборка

Нужны Windows x64, .NET Framework 4.8 и PowerShell. Из корня репозитория:

```powershell
powershell -ExecutionPolicy Bypass -File clients\windows\build.ps1 -DownloadEngine
```

Эта команда скачивает проверяемый по SHA-256 официальный движок и создаёт архив `artifacts/Netlane-windows-x64.zip`. **Правила по приложениям в этой сборке недоступны.** Скрипт компилирует клиент и выполняет встроенные тесты.

Для сборки с правилами приложений требуются инструменты сборки TrustTunnelClient (MSVC, Python, Conan, CMake, Ninja и make). В окружении MSVC выполните:

```powershell
powershell -ExecutionPolicy Bypass -File clients\windows\build-routed-engine.ps1
powershell -ExecutionPolicy Bypass -File clients\windows\build.ps1 -DownloadEngine -RoutedEngine "$PWD\research\windows-client\cmake-build-msvc-relwithdebinfo\trusttunnel\trusttunnel_client.exe"
```

Для подписи используйте `-Sign -SignThumbprint <отпечаток>`. Движок с патчем подписывается до расчёта хеша; приложение и тесты — до запуска тестов и упаковки. Закрытый ключ храните отдельно от репозитория. [Подробнее о подписи](docs/SIGNING.md).

## Проверка подлинности

Каждый релиз собирается в GitHub Actions из этого репозитория. Для архива публикуется [аттестация происхождения](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations) — подтверждение, что файл собран именно этим workflow из этого коммита:

```powershell
gh attestation verify .\Netlane-windows-x64.zip --repo divezh/netlane-windows
```

После одобрения заявки в [SignPath Foundation](https://signpath.org) `Netlane.exe` и движок дополнительно подписываются бесплатной подписью Authenticode от SignPath Foundation; до этого исполняемые файлы не подписаны. Как это настроено — в [docs/SIGNPATH.md](docs/SIGNPATH.md).

## Безопасность и ограничения

Smart App Control может блокировать неизвестные приложения. Для публичного распространения используйте подпись от доверенного поставщика и проверяйте сборку на чистой Windows. Самоподписанный сертификат предназначен для контролируемых тестов и не гарантирует обход Smart App Control. Не устанавливайте неизвестные корневые сертификаты.

Приложение экспериментальное. О проблемах сообщайте через [Issues](https://github.com/divezh/netlane-windows/issues); не публикуйте ссылки подписки, пароли и журналы с личными данными.

## Лицензии

Собственный код Netlane распространяется под [лицензией MIT](LICENSE). Код TrustTunnel и Wintun распространяются на условиях соответствующих правообладателей, флаги — [flag-icons](https://github.com/lipis/flag-icons). Тексты лицензий зависимостей входят в готовый архив; лицензия флагов также хранится в `clients/windows/licenses/`.
