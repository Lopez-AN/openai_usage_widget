; Build with build-installer.ps1. Keep AppId stable across releases.
#define AppName "OpenAI Usage Widget"
#define AppId "{628B1FA2-D00D-4EA7-9A15-5D897AD74F73}"
#define AppExeName "OpenAIUsageWidget.exe"

#ifndef AppVersion
  #error AppVersion is required. Use build-installer.ps1.
#endif
#ifndef PublishDir
  #error PublishDir is required. Use build-installer.ps1.
#endif
#ifndef InstallerOutputDir
  #error InstallerOutputDir is required. Use build-installer.ps1.
#endif

[Setup]
AppId={{#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\OpenAIUsageWidget
DisableDirPage=yes
UsePreviousAppDir=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
; Do not allow switching to an all-users/elevated installation.
PrivilegesRequiredOverridesAllowed=
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#InstallerOutputDir}
OutputBaseFilename=OpenAIUsageWidget-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
; Match App.xaml.cs. Ask the user to exit from the tray, never kill it.
AppMutex=Local\OpenAIUsageWidget-{username}
CloseApplications=no
RestartApplications=no
UsePreviousTasks=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "autostart"; Description: "Iniciar el widget al entrar en Windows"; GroupDescription: "Inicio automático (solo este usuario):"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "OpenAIUsageWidget"; ValueData: """{app}\{#AppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart
; Also log cleanup when unchecked: the app can enable this value later.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "OpenAIUsageWidget"; Flags: deletevalue uninsdeletevalue; Tasks: not autostart

[Run]
Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Description: "Abrir {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  AutostartInitialized: Boolean;

function HasTaskOverride: Boolean;
var
  I: Integer;
  Argument: String;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    Argument := Uppercase(ParamStr(I));
    if (Pos('/TASKS=', Argument) = 1) or (Pos('/MERGETASKS=', Argument) = 1) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Command: String;
begin
  { Tasks do not exist yet in InitializeWizard. Apply this only after the tasks
    page is created, once, so going Back does not overwrite the user's choice. }
  if (CurPageID <> wpSelectTasks) or AutostartInitialized then
    Exit;
  AutostartInitialized := True;

  { On upgrades, honor changes made in the app instead of old installer tasks.
    A first installation defaults to enabled; explicit CLI tasks take priority. }
  if not HasTaskOverride and
     RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1') then
  begin
    if not RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
      'OpenAIUsageWidget', Command) or (Trim(Command) = '') then
      WizardSelectTasks('!autostart');
  end;
end;
