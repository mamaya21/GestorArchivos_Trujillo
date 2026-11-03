#define AppName "Gestor de Archivos"
#define AppVersion "1.0.0"
#define AppPublisher "Estudio Jurídico Trujillo"
#define ServerUrl "https://archivos.juridicotrujillo.com"
#define ServiceName "GestorArchivosSync"

[Setup]
AppId={{A02E431C-2CF9-4C68-BF17-72E5B4D2A620}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\Gestor de Archivos
DefaultGroupName=Gestor de Archivos
OutputDir=..\publish\Installer\Output
OutputBaseFilename=GestorArchivos-Instalador-x64
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
WizardResizable=yes
DisableWelcomePage=no
UninstallDisplayIcon={app}\Desktop\GestorArchivos.Desktop.Windows.exe
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
Source: "..\publish\Installer\Agent\*"; DestDir: "{app}\Agent"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\publish\Installer\Selector\*"; DestDir: "{app}\Selector"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\publish\Installer\Desktop\*"; DestDir: "{app}\Desktop"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Gestor de Archivos"; Filename: "{app}\Desktop\GestorArchivos.Desktop.Windows.exe"
Name: "{group}\Selector de sincronización"; Filename: "{app}\Selector\GestorArchivos.Sync.Selector.Windows.exe"
Name: "{autodesktop}\Gestor de Archivos"; Filename: "{app}\Desktop\GestorArchivos.Desktop.Windows.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Crear acceso directo en el escritorio"; GroupDescription: "Accesos directos:"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "GestorArchivosSelectorAgent"; ValueData: """{app}\Selector\GestorArchivos.Sync.Selector.Windows.exe"" --agent"; Flags: uninsdeletevalue

[Code]
const
  ServiceKey = '{#ServiceName}';
  WebUrl = '{#ServerUrl}';

function RunSC(Params: String): Boolean;
var
  ExitCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE,
    ewWaitUntilTerminated, ExitCode) and (ExitCode = 0);
end;

procedure ReplaceInFile(FileName, OldText, NewText: String);
var
  Content: AnsiString;
  Updated: String;
begin
  if not LoadStringFromFile(FileName, Content) then
    RaiseException('No se pudo leer ' + FileName);
  Updated := String(Content);
  StringChangeEx(Updated, OldText, NewText, True);
  if not SaveStringToFile(FileName, AnsiString(Updated), False) then
    RaiseException('No se pudo actualizar ' + FileName);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  AgentConfig, DesktopConfig, ServiceExe: String;
begin
  if CurStep <> ssPostInstall then Exit;

  AgentConfig := ExpandConstant('{app}\Agent\appsettings.json');
  DesktopConfig := ExpandConstant('{app}\Desktop\desktopsettings.json');
  ServiceExe := ExpandConstant('{app}\Agent\GestorArchivos.Sync.Windows.exe');

  ReplaceInFile(AgentConfig, 'http://localhost:5015', WebUrl);
  ReplaceInFile(DesktopConfig, 'http://localhost:5173/', WebUrl + '/');

  { El selector usa esta variable para validar el Origin del navegador. }
  RegWriteStringValue(HKLM,
    'SYSTEM\CurrentControlSet\Control\Session Manager\Environment',
    'GESTOR_ARCHIVOS_WEB_ORIGIN', WebUrl);

  { Una reinstalación debe actualizar el servicio existente. }
  RunSC('stop "' + ServiceKey + '"');
  RunSC('delete "' + ServiceKey + '"');
  Sleep(1500);

  if not RunSC('create "' + ServiceKey + '" binPath= "' +
    '"' + ServiceExe + '"' + '" start= auto DisplayName= "Gestor Archivos Sync"') then
    RaiseException('No se pudo registrar el servicio de sincronización.');

  if not RunSC('start "' + ServiceKey + '"') then
    RaiseException('No se pudo iniciar el servicio de sincronización.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RunSC('stop "' + ServiceKey + '"');
    RunSC('delete "' + ServiceKey + '"');
  end;
end;
