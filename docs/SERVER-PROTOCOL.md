# Протокол сервера OfficeChat

Сервер (`OfficeChat.Server`) хранит аккаунты, всю переписку и файлы и раздаёт события всем
устройствам. Клиенты (Windows, Linux, Android, позже веб и iPhone) работают с ним по HTTPS и WebSocket.
Типы запросов и ответов — в `OfficeChat.Core/Protocol`, общие для сервера и клиентов.

## Соединение

- Порт **45680**, только HTTPS. Сертификат сервер создаёт сам при первом запуске (самоподписанный).
  Клиент запоминает отпечаток (SHA-256) сертификата при первом подключении и дальше доверяет только ему.
- **Поиск в сети**: клиент шлёт широковещательный UDP-пакет на порт **45681**
  `{"app":"OfficeChat/1","type":"server-query"}`, сервер отвечает тому же адресу
  `{"app":"OfficeChat/1","type":"server","name":…,"port":45680,"fingerprint":…,"version":…}`.
  Если сервер не нашёлся — адрес вводится вручную.
- JSON везде в стиле camelCase.

## Аккаунты и вход

Пароль вводится один раз: в ответ на вход устройство получает **ключ устройства** (`token`) и дальше
передаёт его в заголовке `Authorization: Bearer <token>` (в WebSocket из браузера — `?access_token=`).
Ключ бессрочный, пока устройство не вышло или его не отключили. На сервере хранится только хэш ключа.

| Запрос | Что делает |
|---|---|
| `GET /api/server` | имя и версия сервера (без входа) |
| `POST /api/register` | `{login, displayName, password, device:{name, platform}}` → `{token, deviceId, account}` |
| `POST /api/login` | `{login, password, device}` → то же |
| `POST /api/logout` | отключить это устройство |
| `GET /api/me` | свой аккаунт |
| `POST /api/me/password` | `{oldPassword, newPassword}` |
| `PUT /api/me/profile` | `{displayName}` |
| `GET /api/devices`, `DELETE /api/devices/{id}` | свои устройства, отключить устройство |
| `GET /api/accounts` | список сотрудников: `{id, login, displayName, online, lastSeen}` |
| `POST /api/files?name=…` | загрузить файл (тело запроса — содержимое) → `{id, size}` |
| `GET /api/files/{id}` | скачать (только участникам переписки, где файл отправлен) |

Ошибки: код 4xx и `{error, message}`, где `message` — понятный человеку текст по-русски.

Администрирование (только для администраторов): `GET /api/admin/accounts`,
`POST /api/admin/accounts/{id}/reset-password` → `{temporaryPassword}`,
`POST /api/admin/accounts/{id}/block` `{blocked}`, `POST /api/admin/accounts/{id}/admin` `{isAdmin}`.
Первого администратора назначают на самом сервере: `officechat-server admin <логин>`.

## Синхронизация (WebSocket `GET /api/ws`)

У каждого аккаунта на сервере — **журнал событий** с возрастающими номерами `seq`. Всё, что меняет
переписку (сообщение, правка, удаление, прочтение, группа, напоминание, настройки), — событие в журнале
каждого затронутого аккаунта (и самого автора — чтобы увидели его другие устройства).

Кадры — JSON-объекты с полем `op`. Запросы клиента могут нести `rid`; ответ — `{"op":"ok","rid"}` или
`{"op":"error","rid","error","message"}`.

1. Клиент: `{"op":"sync","since":<последний обработанный seq или 0>}`.
2. Сервер присылает `{"op":"event","seq","kind","data"}` по порядку, затем один раз
   `{"op":"synced","seq"}`, а дальше новые события — по мере появления.
3. Клиент подтверждает обработанное: `{"op":"ack","seq"}` — по этому сервер отмечает «доставлено».

Новое устройство начинает с `since: 0` и получает всю историю аккаунта.

### Запросы клиента

| `op` | `data` |
|---|---|
| `send` | сообщение `MessageDto` (Id создаёт клиент — повтор с тем же Id не дублируется) |
| `edit` | `{messageId, text}` — только своё |
| `delete` | `{messageId}` — только своё, удаляется у всех |
| `read` | `{messageIds}` |
| `group.create` | `{id, name, memberIds}` |
| `group.rename` | `{id, name}` |
| `group.add` | `{id, memberIds}` |
| `group.leave` | `{id}` |
| `reminder.put` | `{id, participantIds, json}` — только автор; `json` сервер хранит как есть |
| `reminder.delete` | `{id}` — только автор |
| `reminder.done` | `{id, done}` — автор или участник |
| `settings.put` | `{key, value}` — свои настройки, общие для всех устройств (закрепы, без звука…) |
| `signal` | `{to?, groupId?, type, payload}` — не хранится: «печатает…», ходы в играх |

### События (`kind`)

`message` (`MessageDto`), `edit`, `delete`, `delivered` `{messageIds, accountId}`,
`read` `{messageIds, accountId}`, `group` (`GroupDto` с `action`), `reminder` `{id, creatorId, json}`,
`reminder-delete` `{id}`, `reminder-done` `{id, accountId, done}`, `settings` `{key, value}`.

Без журнала, только тем, кто в сети: `{"op":"presence","accountId","online"}`,
`{"op":"directory","account":{…}}` (новый или переименованный сотрудник),
`{"op":"signal","from","fromName","groupId","type","payload"}`.
