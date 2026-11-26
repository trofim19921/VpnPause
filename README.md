# VpnPause

[Русский](#русский) · [English](#english)

## Русский

VpnPause не даёт выбранным программам выйти в интернет мимо VPN. Пока VPN подключён, ChatGPT, Claude, Cursor и другие из списка работают как обычно. Стоило туннелю упасть — их соединения блокируются на уровне Windows Filtering Platform (WFP), и сайт ни разу не увидит ваш настоящий IP. Остальные программы не затрагиваются.

### Как это работает
- Служба `VpnPauseService` (LocalSystem) ставит WFP-фильтры для каждой программы из списка: разрешено через VPN-адаптер и loopback, всё остальное — блок. Фильтры живут в собственном подслое с наивысшим весом и не конфликтуют с kill switch Amnezia/WireGuard.
- VPN-адаптер определяется автоматически (любой туннель: AmneziaWG, Xray `tun2`, WireGuard, OpenVPN, Wintun/TAP) или выбирается вручную.
- Обычный режим: фильтры динамические и исчезают вместе со службой — сломать сеть навсегда нельзя. Строгий режим: фильтры остаются, даже если служба упала или ещё не запустилась при загрузке.
- Программы узнаются по папке, по пакету Microsoft Store или по exe; обновления (новые папки версий) подхватываются сами.
- Заморозка процессов — отдельная опция, по умолчанию выключена. Включается с задержкой (5 с), ограничена по времени (120 с), не трогает процессы с окнами и буфером обмена. Горячая клавиша Ctrl+Alt+Shift+P отпускает всё.

### Установка
1. Запустить `VpnPause-Setup-x.y.z.exe` (нужны права администратора).
2. Пройти мастер: язык → адаптер («Автоматически» подходит почти всем) → программы → проверка.
3. Значок в трее: зелёный — VPN есть, защищено; красный — VPN нет, программы заблокированы; оранжевый — плюс заморожены процессы; серый — защита выключена или служба не отвечает.

Удаление — через «Установленные приложения». Деинсталлятор снимает фильтры и удаляет службу.

### Пресеты
ChatGPT, Codex, Claude, Claude Code, Cursor, VS Code, Opera, Opera GX, Chrome, Edge, Firefox, Telegram. Любую другую программу можно добавить из запущенных или через «Обзор».

Opera: встроенный «VPN» Opera — это прокси внутри браузера, а не системный адаптер. VpnPause считает его отсутствием VPN и блокирует Opera, если системный VPN не подключён.

### Сборка
```powershell
.\build.ps1          # тесты, single-file publish, установщик в .\out
.\build.ps1 -SignPfx cert.pfx   # то же с подписью (пароль в $env:VPNPAUSE_SIGN_PASSWORD)
```
Нужны .NET SDK 8 и Inno Setup 6.

### Командная строка
| Ключ | Что делает |
|---|---|
| `--tray` | запуск свёрнутым в трей |
| `--service` | режим службы (вызывается SCM) |
| `--console` | ядро службы в консоли (отладка, от администратора) |
| `--remove-filters` | снять все фильтры VpnPause (от администратора) |
| `--release-all` | разморозить всё, что могло остаться замороженным |
| `--selftest [файл]` | проверка WFP на копии curl (от администратора) |

Файлы: настройки `%ProgramData%\VpnPause\config.json`, журнал службы `%ProgramData%\VpnPause\logs`, журнал окна `%LOCALAPPDATA%\VpnPause\logs`.

### Ограничения
- Новый exe программы (первый запуск после обновления) может успеть отправить пакеты в первые 1–2 секунды, пока служба не перестроила фильтры. Для пресетов файлы известны заранее, поэтому это касается в основном нестандартных программ.
- Если у VPN-клиента нет своего адаптера (браузерные прокси, расширения), он не считается VPN.
- Только Windows 10/11 x64.

## English

VpnPause stops selected apps from reaching the internet outside your VPN. While the VPN is connected, ChatGPT, Claude, Cursor and the other listed apps work normally. When the tunnel drops, their connections are blocked by the Windows Filtering Platform (WFP), so your real IP is never exposed. Other apps are untouched.

### How it works
- `VpnPauseService` (LocalSystem) installs per-app WFP filters: permit via the VPN adapter and loopback, block everything else. The filters live in their own top-weight sublayer and coexist with Amnezia/WireGuard kill switches.
- The VPN adapter is detected automatically (AmneziaWG, Xray `tun2`, WireGuard, OpenVPN, Wintun/TAP) or chosen manually.
- Normal mode uses dynamic filters that vanish with the service, so a crash can never break networking for good. Strict mode keeps the filters even when the service is stopped or not yet started at boot.
- Apps are matched by folder, Microsoft Store package or exe; updates (new version folders) are picked up automatically.
- Process freezing is a separate option, off by default: delayed (5 s), time-limited (120 s), never touches processes owning windows or the clipboard. Ctrl+Alt+Shift+P releases everything.

### Install
Run `VpnPause-Setup-x.y.z.exe` as administrator and follow the wizard: language, adapter ("Automatic" fits almost everyone), apps, check. Uninstall from Installed apps; it removes the filters and the service.

### Build
`.\build.ps1` runs the tests, publishes a single-file exe and builds the installer into `.\out` (.NET SDK 8 and Inno Setup 6 required). See the command-line table above.

### Limitations
- A brand-new app binary may leak packets during its first 1–2 seconds before the service rebuilds filters (preset files are pre-scanned, so this mostly affects custom apps).
- VPNs without a system adapter (browser proxies such as Opera's built-in "VPN") are not treated as VPN.
- Windows 10/11 x64 only.
