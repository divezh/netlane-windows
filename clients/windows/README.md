# Netlane: сборка Windows-клиента

Клиент написан на C#, WinForms под .NET Framework 4.8 и собирается без NuGet. Движок — TrustTunnel Client, сетевой адаптер — Wintun.

## Обычная сборка

```powershell
powershell -ExecutionPolicy Bypass -File clients\windows\build.ps1 -DownloadEngine
```

Скрипт скачивает официальный движок, проверяет его по SHA-256, компилирует клиент, запускает встроенные тесты и создаёт `artifacts/Netlane-windows-x64.zip`. В этой сборке не работают правила для приложений.

## Сборка с правилами для приложений

Нужны MSVC, Python, Conan, CMake, Ninja и make. Команды запускайте в окружении MSVC:

```powershell
powershell -ExecutionPolicy Bypass -File clients\windows\build-routed-engine.ps1
powershell -ExecutionPolicy Bypass -File clients\windows\build.ps1 -DownloadEngine -RoutedEngine "$PWD\research\windows-client\cmake-build-msvc-relwithdebinfo\trusttunnel\trusttunnel_client.exe"
```

Патч движка находится в `upstream/`.

## Подпись

Ключ `build.ps1 -Sign -SignThumbprint <отпечаток>` подписывает `Netlane.exe` и собственный движок до тестов и упаковки. Подробности — в [docs/SIGNING.md](../../docs/SIGNING.md).

## Релизы

Публикация релиза на GitHub с тегом `vX.Y.Z` запускает сборку в Actions. Тег должен совпадать с `Program.Version`. Workflow прикладывает к релизу архив, SHA-256 и аттестацию происхождения, а также обновляет канал обновлений в ветке `updates`.

## Другой VPN рядом

Если другой VPN занял маршрут по умолчанию, клиент всё равно подключится. При этом работает только режим «Весь трафик»; выборочная маршрутизация работает, когда другой VPN выключен.
