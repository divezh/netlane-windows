# Сторонние компоненты Netlane

Netlane распространяется по лицензии MIT (`LICENSE`). В клиент входят сторонние
компоненты; их лицензии перечислены ниже.

## TrustTunnel Client

Движок `engine/trusttunnel_client.exe` — TrustTunnel Client, Copyright AdGuard Software
Limited, лицензия Apache License 2.0.

- https://github.com/TrustTunnel/TrustTunnelClient
- Полный текст лицензии: `licenses/LICENSE.txt` в собранном архиве.

В публичной сборке используется собственная сборка движка с патчем маршрутизации по
приложениям. Исходники патча и инструкция сборки: https://github.com/divezh/netlane-windows
(каталог `upstream`).

## Wintun

`engine/wintun.dll` — Wintun, Copyright WireGuard LLC. Распространяется по
Prebuilt Binaries License; полный текст: `licenses/WINTUN_LICENSE.txt` в собранном
архиве.

- https://www.wintun.net/

## Twemoji

Флаги стран в интерфейсе — графика Twemoji, Copyright 2020 Twitter, Inc and other
contributors, лицензия Creative Commons Attribution 4.0 International (CC-BY 4.0).

- https://github.com/twitter/twemoji
- Полный текст: `licenses/FLAG_ICONS_LICENSE.txt` в собранном архиве.

## CLDR

Названия стран в выборе флага основаны на данных CLDR (Unicode Consortium),
лицензия Unicode License. Названия стран — производная таблица в
`clients/windows/CountryNames.cs`.

## .NET Framework

Клиент собран для .NET Framework 4.8. Сборка не включает runtime .NET Framework:
он является компонентом Windows и распространяется Microsoft по условиям Windows.
