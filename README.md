# VpnPause

[Русский](#русский) · [English](#english)

## Русский

VpnPause не даёт выбранным программам выйти в интернет мимо VPN. Пока VPN подключён, ChatGPT, Claude, Cursor и другие из списка работают как обычно. Стоило туннелю упасть — их соединения блокируются на уровне Windows Filtering Platform (WFP), и сайт ни разу не увидит ваш настоящий IP. Остальные программы не затрагиваются.

### Как это работает
- Служба `VpnPauseService` (LocalSystem) ставит WFP-фильтры для каждой программы из списка: разрешено через VPN-адаптер и loopback, всё остальное — блок. Фильтры живут в собственном подслое с наивысшим весом и не конфликтуют с kill switch Amnezia/WireGuard.
- VPN-адаптер определяется автоматически (любой туннель: AmneziaWG, Xray `tun2`, WireGuard, OpenVPN, Wintun/TAP) или выбирается вручную.
- Строгий режим (по умолчанию): фильтры остаются, даже если служба упала или ещё не запустилась при загрузке. Обычный режим: фильтры динамические и исчезают вместе со службой.
- Служба каждые 5 секунд сверяет свои фильтры с Windows и восстанавливает их, если их удалили извне или перезапустилась служба BFE.
- Программы узнаются по папке, по пакету Microsoft Store или по exe; только локальные диски (сетевые пути и UNC не принимаются). Служба следит за папками программ: новый exe получает фильтры раньше, чем запустится.
- Менять защиту (список, режим, вкл/выкл) может только администратор — окно спросит подтверждение UAC. Смотреть статус и размораживать процессы может любой пользователь.
- Исключения — имя exe (`helper.exe`) или полный путь к файлу/папке. Короткие куски имени не принимаются.
- Заморозка процессов — отдельная опция, по умолчанию выключена. Включается с задержкой (5 с), ограничена по времени (120 с), не трогает процессы с окнами и буфером обмена. Горячая клавиша Ctrl+Alt+Shift+P отпускает всё.

### Установка
1. Запустить `VpnPause-Setup-x.y.z.exe` (нужны права администратора).
2. Пройти мастер: язык → адаптер («Автоматически» подходит почти всем) → программы → проверка.
3. Значок в трее: зелёный — VPN есть, защищено; красный — VPN нет, программы заблокированы; оранжевый — плюс заморожены процессы; серый — защита выключена или служба не отвечает.

Удаление — через «Установленные приложения». Деинсталлятор снимает фильтры и удаляет службу.

Установщик пока без цифровой подписи, поэтому SmartScreen может показать «Windows защитила ваш компьютер» → «Подробнее» → «Выполнить в любом случае».

### Пресеты
ChatGPT, Codex, Claude, Claude Code, Cursor, VS Code, Opera, Opera GX, Chrome, Edge, Firefox, Telegram, Git и SSH (Git for Windows). Любую другую программу можно добавить из запущенных или через «Обзор».

Claude Code, Codex и Cursor ходят в git и ssh отдельными процессами (`git.exe`, `ssh.exe`). Если это важно, добавьте пресет «Git и SSH» или папку своего git.

Opera: встроенный «VPN» Opera — это прокси внутри браузера, а не системный адаптер. VpnPause считает его отсутствием VPN и блокирует Opera, если системный VPN не подключён.

### Сборка
```powershell
.\build.ps1          # тесты, publish в папку, установщик в .\out
.\build.ps1 -SignPfx cert.pfx   # то же с подписью (пароль в $env:VPNPAUSE_SIGN_PASSWORD)
```
Нужны .NET SDK 8 и Inno Setup 6.

### Командная строка
| Ключ | Что делает |
|---|---|
| `--tray` | запуск свёрнутым в трей |
| `--service` | режим службы (вызывается SCM) |
| `--console` | ядро службы в консоли (отладка, от администратора) |
| `--remove-filters` | снять все фильтры VpnPause (от администратора; фильтры работающей службы снимаются только вместе с ней: сначала `sc stop VpnPauseService`) |
| `--release-all` | разморозить всё, что могло остаться замороженным |
| `--selftest [файл]` | проверка WFP на копии curl (от администратора) |

Файлы: настройки `%ProgramData%\VpnPause\config.json` (+ резервная копия `.bak`), журнал службы `%ProgramData%\VpnPause\logs`, журнал и настройки окна (язык, уведомления) `%LOCALAPPDATA%\VpnPause`. Если `config.json` повреждён, служба берёт `.bak`; если испорчены оба — оставляет уже стоящие фильтры и показывает «настройки повреждены».

### Ограничения
- Exe, который появился вне отслеживаемых папок (программа добавлена одним файлом и обновилась в другое место), может успеть отправить пакеты в первые 1–2 секунды, пока служба не перестроила фильтры.
- DNS-запросы программ идут через системную службу DNS-клиента, а не от имени программы, поэтому фильтр их не блокирует. Сам факт обращения к домену может уйти через провайдера, если у VPN нет своего DNS. IP и содержимое соединений при этом не утекают. Окно предупреждает «DNS идёт мимо VPN», когда это видно.
- Если у VPN-клиента нет своего адаптера (браузерные прокси, расширения), он не считается VPN.
- Только Windows 10/11 x64.

## English

VpnPause stops selected apps from reaching the internet outside your VPN. While the VPN is connected, ChatGPT, Claude, Cursor and the other listed apps work normally. When the tunnel drops, their connections are blocked by the Windows Filtering Platform (WFP), so your real IP is never exposed. Other apps are untouched.

### How it works
- `VpnPauseService` (LocalSystem) installs per-app WFP filters: permit via the VPN adapter and loopback, block everything else. The filters live in their own top-weight sublayer and coexist with Amnezia/WireGuard kill switches.
- The VPN adapter is detected automatically (AmneziaWG, Xray `tun2`, WireGuard, OpenVPN, Wintun/TAP) or chosen manually.
- Strict mode (the default) keeps the filters even when the service is stopped or not yet started at boot. Normal mode uses dynamic filters that vanish with the service.
- Every 5 seconds the service checks its filters against Windows and restores them if something removed them or BFE restarted.
- Apps are matched by folder, Microsoft Store package or exe, local drives only (UNC and network paths are rejected). App folders are watched, so a new exe is filtered before it starts.
- Only administrators can change protection (app list, mode, on/off); the window asks for UAC. Anyone can read the status and release frozen processes.
- Exclusions are an exe name (`helper.exe`) or a full file/folder path; short name fragments are rejected.
- Claude Code, Codex and Cursor run `git.exe` and `ssh.exe` as separate processes; add the "Git and SSH" preset or your git folder if that matters.
- Process freezing is a separate option, off by default: delayed (5 s), time-limited (120 s), never touches processes owning windows or the clipboard. Ctrl+Alt+Shift+P releases everything.

### Install
Run `VpnPause-Setup-x.y.z.exe` as administrator and follow the wizard: language, adapter ("Automatic" fits almost everyone), apps, check. Uninstall from Installed apps; it removes the filters and the service. The installer is not code-signed yet, so SmartScreen may ask: "More info" → "Run anyway".

### Build
`.\build.ps1` runs the tests, publishes to a folder and builds the installer into `.\out` (.NET SDK 8 and Inno Setup 6 required). See the command-line table above; `--remove-filters` cannot remove a running service's filters, stop it first with `sc stop VpnPauseService`.

### Limitations
- An exe that appears outside the watched folders may leak packets during its first 1–2 seconds before the service rebuilds filters.
- DNS lookups go through the Windows DNS Client service, not the app, so they are not blocked. The queried name may reach your ISP when the VPN has no DNS of its own; the app's IP and traffic do not. The window warns "DNS outside VPN" when it can see this.
- VPNs without a system adapter (browser proxies such as Opera's built-in "VPN") are not treated as VPN.
- Windows 10/11 x64 only.
