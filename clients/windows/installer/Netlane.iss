#ifndef PayloadDir
  #error PayloadDir must point to the complete Windows client bundle
#endif
#ifndef SetupVersion
  #error SetupVersion is required
#endif
#ifndef SetupOutputDir
  #error SetupOutputDir is required
#endif

[Setup]
AppId={{39C1F072-8792-4CF0-A72F-BBA1C428B422}
AppName=Netlane Connect
AppVersion={#SetupVersion}
VersionInfoVersion={#SetupVersion}.0
VersionInfoTextVersion={#SetupVersion}.0
AppPublisher=Netlane
AppPublisherURL=https://github.com/divezh/netlane-windows
AppSupportURL=https://github.com/divezh/netlane-windows/issues
AppUpdatesURL=https://github.com/divezh/netlane-windows/releases
DefaultDirName={localappdata}\Programs\Netlane
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#SetupOutputDir}
OutputBaseFilename=Netlane-Setup-{#SetupVersion}-x64
SetupIconFile=..\Netlane.ico
UninstallDisplayIcon={app}\Netlane.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
SetupLogging=yes

[Languages]
Name: russian; MessagesFile: compiler:Languages\Russian.isl
Name: english; MessagesFile: compiler:Default.isl

[CustomMessages]
russian.DesktopShortcut=Создать ярлык на рабочем столе
english.DesktopShortcut=Create a desktop shortcut
russian.RunNetlane=Запустить Netlane
english.RunNetlane=Launch Netlane
russian.FrameworkRequired=Для Netlane нужен .NET Framework 4.8 или новее. Установите его с https://dotnet.microsoft.com/download/dotnet-framework/net48 и повторите установку.
english.FrameworkRequired=Netlane requires .NET Framework 4.8 or later. Install it from https://dotnet.microsoft.com/download/dotnet-framework/net48 and run Setup again.

[Tasks]
Name: desktopicon; Description: {cm:DesktopShortcut}; Flags: unchecked

[Files]
Source: {#PayloadDir}\Netlane.exe; DestDir: {app}; Flags: ignoreversion
Source: {#PayloadDir}\Netlane.exe.config; DestDir: {app}; Flags: ignoreversion
Source: {#PayloadDir}\engine\trusttunnel_client.exe; DestDir: {app}\engine; Flags: ignoreversion
Source: {#PayloadDir}\engine\wintun.dll; DestDir: {app}\engine; Flags: ignoreversion
Source: {#PayloadDir}\licenses\*; DestDir: {app}\licenses; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: {userprograms}\Netlane Connect; Filename: {app}\Netlane.exe; WorkingDir: {app}
Name: {userdesktop}\Netlane Connect; Filename: {app}\Netlane.exe; WorkingDir: {app}; Tasks: desktopicon

[Run]
Filename: {app}\Netlane.exe; Description: {cm:RunNetlane}; WorkingDir: {app}; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup: Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    SuppressibleMsgBox(CustomMessage('FrameworkRequired'), mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Netlane', Command) then
      if CompareText(Command, '"' + ExpandConstant('{app}\Netlane.exe') + '" --tray') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Netlane');
end;
