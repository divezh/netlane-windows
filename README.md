<p align="center"><img src="assets/icon.png" width="88" alt="Netlane"></p>

# Netlane для Windows

VPN-клиент для Windows 10 и 11 (x64) на базе [TrustTunnel](https://github.com/TrustTunnel/TrustTunnelClient). Подключается к вашему серверу по ссылке подписки.

<p align="center"><img src="assets/screenshots/windows-client.png" width="760" alt="Главное окно Netlane"></p>

## Установка

1. Скачайте `Netlane-windows-x64.zip` из [последнего релиза](https://github.com/divezh/netlane-windows/releases/latest).
2. Распакуйте архив целиком и запустите `Netlane.exe`.
3. Нажмите «Вставить», чтобы добавить ссылку подписки, затем «Подключиться».

Ссылку выдаёт администратор сервера. Серверная часть находится в [divezh/netlane](https://github.com/divezh/netlane).

## Возможности

- Транспорт HTTP/2 или HTTP/3, автоматический выбор, Anti-DPI, Kill Switch.
- Несколько серверов в одной подписке, проверка пинга.
- Правила для доменов, IP и приложений, собственные DNS.
- Обновления внутри приложения с проверкой SHA-256.

## Безопасность

- Телеметрии нет. Приложение обращается только к вашим серверам и подпискам, к GitHub за обновлениями и к Cloudflare, чтобы показать внешний IP. Показ IP можно отключить в настройках.
- Профили и ссылки хранятся только на этом компьютере и зашифрованы Windows DPAPI.
- Файлы не подписаны Authenticode. Происхождение архива можно проверить так:

  ```powershell
  gh attestation verify .\Netlane-windows-x64.zip --repo divezh/netlane-windows
  ```

## Удаление

Закройте Netlane (значок в трее → «Выход»), затем удалите папку с программой и `%LOCALAPPDATA%\Netlane`.

## Сборка

Нужны Windows x64 и .NET Framework 4.8:

```powershell
powershell -ExecutionPolicy Bypass -File clients\windows\build.ps1 -DownloadEngine
```

Подробнее о сборке и подписи — в [clients/windows](clients/windows/README.md) и [docs/SIGNING.md](docs/SIGNING.md).

## Лицензия

[MIT](LICENSE). Сторонние компоненты (TrustTunnel, Wintun, Twemoji) перечислены в [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
