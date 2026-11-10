# Подпись Windows-клиента через SignPath Foundation

SignPath Foundation выдаёт **бесплатный** сертификат для open-source проектов. Ключ
хранится в HSM SignPath, в репозитории и CI нет закрытого ключа. Сертификат выпущен
на SignPath Foundation: пользователи видят подпись «SignPath Foundation», а не
личный сертификат разработчика.

Требования Foundation:

- публичный репозиторий с OSI-лицензией (у нас MIT);
- сборка выполняется в GitHub Actions на GitHub-hosted runner;
- артефакт сначала загружается как GitHub Actions artifact, потом отправляется в SignPath;
- установлен SignPath GitHub App.

Публичный репозиторий клиента — `divezh/netlane-windows`. Он создан с чистой историей,
без адресов рабочих серверов; приватная история разработки осталась в отдельном репозитории.

## 1. Проверить репозиторий перед заявкой

В репозитории должны быть `LICENSE` (MIT), `THIRD_PARTY_NOTICES.md`,
`.github/workflows/build-and-sign.yml` и `.signpath/artifact-configurations/*.xml`.
Перед каждой публикацией проверяйте, что в коммит не попали секреты и адреса серверов:

```powershell
git log --all --stat -- .env '*.pfx' '*.p12' '*.key'
```

Пока SignPath не настроен, workflow всё равно собирает, тестирует и публикует релиз — без подписи
Authenticode, но с аттестацией происхождения GitHub. Подпись включается сама, как только заданы
переменные и секрет из раздела 3.

## Требования к страницам проекта

На главной странице (README) и на странице загрузки (каждый релиз) должен быть раздел или ссылка
«Code signing policy» со строкой «Free code signing provided by SignPath.io, certificate by SignPath
Foundation», ролями команды и ссылкой на политику конфиденциальности. В README это разделы
«Code signing policy» и «Конфиденциальность»; в описание каждого релиза добавляйте строку:

```
Code signing policy: https://github.com/divezh/netlane-windows#code-signing-policy — free code signing provided by SignPath.io, certificate by SignPath Foundation.
```

Все участники команды должны включить двухфакторную аутентификацию в GitHub и SignPath.

## 2. Подать заявку

1. Откройте https://signpath.org/apply.
2. Укажите публичный репозиторий, лицензию MIT, проект «Netlane Windows client».
3. Опишите сборку: `clients/windows/build.ps1`, Windows, .NET Framework 4.8.
4. После одобрения установите [SignPath GitHub App](https://github.com/apps/signpath)
   на репозиторий.

## 3. Настроить SignPath

В SignPath создайте организацию и проект `Netlane_Windows` (slug будет в
`SIGNPATH_PROJECT_SLUG`). В проекте:

- **Artifact configurations** `engine` и `client` — импортируйте XML из
  `.signpath/artifact-configurations/`.
- **Signing policies** `release-signing` и `test-signing`.
  - `release-signing` — только для тегов `v*` и защищённой ветки;
  - `test-signing` — для `workflow_dispatch` и обычных веток.
- **Trusted Build System** GitHub.com, привязать к проекту.
- Создайте API token для submitter и сохраните как `SIGNPATH_API_TOKEN`.

GitHub repository variables:

| Variable | Значение |
|---|---|
| `SIGNPATH_ORGANIZATION_ID` | ID организации SignPath |
| `SIGNPATH_PROJECT_SLUG` | slug проекта, например `Netlane_Windows` |

GitHub secret:

| Secret | Значение |
|---|---|
| `SIGNPATH_API_TOKEN` | API token submitter |

## 4. Как устроена подпись

`Netlane.exe` при запуске сверяет SHA-256 движка с `EngineSpec`. Поэтому нельзя
сначала собрать клиент, а потом подписать движок: хеш изменится, и клиент откажется
запускать движок.

Workflow делает два прохода:

1. скачивает **неподписанный** patched-движок из публично закреплённого релиза
   `netlane-updates` v0.5.4 (SHA-256 архива проверяется);
2. отправляет движок в SignPath (`engine`);
3. кладёт подписанный движок в `research/...` и собирает клиент — `EngineSpec`
   получает хеш уже подписанного движка;
4. отправляет ZIP клиента в SignPath (`client`) — подписывается `Netlane.exe`,
   подписи движка и `wintun.dll` проверяются;
5. пересобирает финальный ZIP и считает SHA-256.

Артефакт SignPath должен быть ZIP: `upload-artifact` по умолчанию создаёт ZIP,
поэтому корень обеих конфигураций — `<zip-file>`.

## 5. Первый запуск

```text
GitHub → Actions → build-and-sign → Run workflow
```

Первый прогон используйте с `test-signing`: он не требует тега `v*`.
Скачайте artifact `netlane-client-signed`, проверьте:

```powershell
Get-AuthenticodeSignature artifacts\windows\Netlane\Netlane.exe | Format-List
Get-AuthenticodeSignature artifacts\windows\Netlane\engine\trusttunnel_client.exe | Format-List
```

У обоих файлов должен быть `Status = Valid`, подписант — SignPath Foundation.

## 6. Релиз

После успешной проверки опубликуйте релиз на странице Releases → Draft a new release:
тег `vX.Y.Z` (совпадает с `Program.Version`), цель `main`, «Set as the latest release».
Сам по себе push тега сборку не запускает.

Workflow выберет `release-signing`, соберёт и подпишет архив и приложит его к
релизу. Подписанный архив и `latest.json` с его хешем workflow сам выложит в
ветку `updates`, откуда установленные клиенты берут обновления.

## Ограничения

- Подпись SignPath Foundation не делает сборку мгновенно «доверенной» в SmartScreen:
  репутация подписанта общая, но предупреждения возможны, пока хеши не наберут статистику.
- Сертификат Foundation нельзя использовать для проприетарных/закрытых сборок.
- Все jobs workflow должны идти на GitHub-hosted runners.
- Не добавляйте API token в код; только `secrets.SIGNPATH_API_TOKEN`.
- Certum Open Source — не бесплатный вариант (€69/€49); для бесплатной подписи
  используется именно SignPath Foundation.
