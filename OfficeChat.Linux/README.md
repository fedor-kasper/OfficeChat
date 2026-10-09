# OfficeChat для Linux

Linux-версия OfficeChat на [Avalonia UI](https://avaloniaui.net/). Протокол тот же, что у Windows-версии:
компьютеры на Linux и Windows видят друг друга и переписываются, передают изображения и файлы (до 1 ГБ),
играют в крестики-нолики.

Сетевая часть, история, игра, изображения и лог — **общие файлы** из `../Services` и `../Models`
(подключаются ссылками, не копируются). Здесь только интерфейс и то, что зависит от системы:
трей, автозапуск, меню приложений, защита от второго запуска.

Проверено на Debian/Linux Mint (x86_64).

## Сборка AppImage

Нужен .NET 10 SDK (только для сборки) и `curl`:

```bash
cd OfficeChat.Linux
./build-appimage.sh
```

Получится `dist/OfficeChat-<версия>-x86_64.AppImage` (~40 МБ). .NET и все библиотеки внутри —
на компьютерах пользователей ничего ставить не нужно.

## Установка у пользователя

```bash
chmod +x OfficeChat-1.0.0-x86_64.AppImage
./OfficeChat-1.0.0-x86_64.AppImage
```

При первом запуске программа спросит имя и сама:
- добавит себя в **меню приложений** (со значком) — дальше запускайте оттуда;
- включит **автозапуск при входе** в систему (сразу в трей) — галочка в «Настройках».

Файл AppImage лучше положить в постоянное место (например, `~/Applications/`) до первого запуска:
пункт меню и автозапуск ссылаются на то место, откуда его запустили.

Если AppImage не запускается с ошибкой про FUSE: `sudo apt install fuse3`
(или запуск без FUSE: `./OfficeChat-…AppImage --appimage-extract-and-run`).

## Трей

Значок в трее работает в Cinnamon (Linux Mint), MATE, Xfce, KDE.
В GNOME (Debian по умолчанию) нужно расширение «AppIndicator and KStatusNotifierItem Support»
(`sudo apt install gnome-shell-extension-appindicator`, затем включить в «Расширениях»).
Без трея крестик всё равно прячет окно, а повторный запуск программы из меню его показывает.

## Сеть и брандмауэр

Порты те же: **UDP 45678** (поиск компьютеров) и **TCP 45679** (сообщения).
В Linux Mint брандмауэр (`ufw`) по умолчанию выключен. Если он включён — разрешите локальную сеть:

```bash
sudo ufw allow from 192.168.0.0/16 to any port 45678 proto udp
sudo ufw allow from 192.168.0.0/16 to any port 45679 proto tcp
```

(подставьте свою подсеть).

## Данные

- `~/.config/OfficeChat/` — настройки, история (`history.db`), изображения (`images/`), файлы (`files/`), логи (`logs/`)
- `~/.local/share/applications/officechat.desktop` — пункт меню
- `~/.config/autostart/officechat.desktop` — автозапуск

## Разработка

```bash
cd OfficeChat.Linux
dotnet run
```

Проект не входит в `OfficeChat.slnx` (Windows-решение) и не попадает в сборку Windows-версии.
