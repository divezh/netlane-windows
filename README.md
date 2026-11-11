<p align="center"><img src="assets/icon.png" width="80" alt="Netlane"></p>

# Netlane для Windows

VPN-клиент на базе [TrustTunnel](https://github.com/TrustTunnel/TrustTunnelClient): подписки, наборы правил для сайтов и программ, Kill Switch. Windows 10 и 11 (x64). Версия для Mac — [netlane-macos](https://github.com/divezh/netlane-macos).

<p align="center"><img src="assets/screenshots/windows-client.png" width="760" alt="Главное окно Netlane"></p>

## Установка

Скачайте **[Netlane-windows-x64.zip](https://github.com/divezh/netlane-windows/releases/latest/download/Netlane-windows-x64.zip)**, распакуйте целиком и запустите `Netlane.exe`. Затем «Вставить» → ссылка подписки от администратора → «Подключиться».

Файлы не подписаны Authenticode. Если SmartScreen предупредит, нажмите «Подробнее» → «Выполнить в любом случае». Происхождение архива проверяется так:

```powershell
gh attestation verify .\Netlane-windows-x64.zip --repo divezh/netlane-windows
```

## Полезное

```powershell
Get-Content "$env:LOCALAPPDATA\Netlane\engine.log" -Tail 50 -Wait   # журнал VPN
Get-Content "$env:LOCALAPPDATA\Netlane\update.log"                  # журнал обновлений
explorer "$env:LOCALAPPDATA\Netlane"                                # настройки и подписки (зашифрованы DPAPI)

# другой VPN занял маршрут по умолчанию и Netlane не подключается (-Restore — вернуть как было)
powershell -ExecutionPolicy Bypass -File .\clients\windows\fix-other-vpn.ps1
```

Правило программы — имя файла, например `chrome.exe`, или полный путь к нему. В панели те же правила действуют и на Mac: там `Google Chrome.exe` означает процесс `Google Chrome` вместе с его Helper-процессами.

**Удаление:** значок в трее → «Выход», затем удалите папку с программой и `%LOCALAPPDATA%\Netlane`. Если был включён автозапуск:

```powershell
Remove-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run -Name Netlane -ErrorAction SilentlyContinue
```

## Сборка

Клиент: C#, WinForms, .NET Framework 4.8, без NuGet.

```powershell
powershell -ExecutionPolicy Bypass -File clients\windows\build.ps1 -DownloadEngine
```

Так собирается клиент с официальным движком, в нём не работают правила программ и DNS через VPN для всех запросов. Полный движок — TrustTunnel 1.1.11 с патчами из `clients/windows/upstream` (`0001` — правила программ и Kill Switch, `0003` — DNS через VPN). Его собирает Actions (`build-engine-ci.ps1`), а локально — `build-routed-engine.ps1` в окружении MSVC с Conan, CMake, Ninja и NASM. После этого `build.ps1` сам возьмёт собранный движок.

Релиз: поднять `Program.Version` и AssemblyVersion, добавить раздел в CHANGELOG, опубликовать релиз `vX.Y.Z`. Actions соберёт архив и обновит канал обновлений.

[MIT](LICENSE) · [сторонние компоненты](THIRD_PARTY_NOTICES.md) · об уязвимостях пишите приватно через [Security advisories](https://github.com/divezh/netlane-windows/security/advisories/new)
