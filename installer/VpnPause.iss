#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\out\publish"
#endif

#define AppName "VpnPause"
#define ServiceName "VpnPauseService"

[Setup]
AppId={{6F0E4A6C-5F2E-4C38-9A53-1F6F1D7B3A10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=VpnPause
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\out
OutputBaseFilename=VpnPause-Setup-{#AppVersion}
SetupIconFile=..\assets\VpnPause.ico
UninstallDisplayIcon={app}\VpnPause.exe
CloseApplications=no
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.Autostart=Запускать VpnPause при входе в Windows (значок в трее)
en.Autostart=Start VpnPause at sign-in (tray icon)
ru.LaunchApp=Открыть VpnPause
en.LaunchApp=Open VpnPause
ru.ServiceDesc=VpnPause: блокирует выбранным программам интернет мимо VPN
en.ServiceDesc=VpnPause: blocks selected apps from reaching the internet outside the VPN

[Tasks]
Name: "autostart"; Description: "{cm:Autostart}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\ИНСТРУКЦИЯ.md"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
Type: filesandordirs; Name: "{win}\SystemTemp\.net\VpnPause"
Type: filesandordirs; Name: "{win}\Temp\.net\VpnPause"
Type: filesandordirs; Name: "{localappdata}\Temp\.net\VpnPause"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\VpnPause.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\VpnPause.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VpnPause"; ValueData: """{app}\VpnPause.exe"" --tray"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\VpnPause.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{app}\VpnPause.exe"; Parameters: "--release-all"; Flags: runhidden waituntilterminated; RunOnceId: "ReleaseAll"
Filename: "{app}\VpnPause.exe"; Parameters: "--remove-filters"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFilters"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\VpnPause"

[Code]
function Sc(const Params: String): Integer;
var
  Code: Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM VpnPause.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1500);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Bin: String;
  Code: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    Bin := ExpandConstant('{app}\VpnPause.exe');
    if Sc('query {#ServiceName}') = 0 then
      Sc('config {#ServiceName} binPath= "\"' + Bin + '\" --service" start= auto depend= BFE')
    else
      Sc('create {#ServiceName} binPath= "\"' + Bin + '\" --service" start= auto depend= BFE DisplayName= "VpnPause"');
    Sc('description {#ServiceName} "' + ExpandConstant('{cm:ServiceDesc}') + '"');
    Sc('failure {#ServiceName} reset= 86400 actions= restart/2000/restart/5000/restart/30000');
    Sc('failureflag {#ServiceName} 1');
    Sc('start {#ServiceName}');
    Exec(Bin, '--import-legacy', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM VpnPause.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;
