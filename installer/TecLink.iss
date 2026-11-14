; TecLink: instalador por usuario (no pide permisos de administrador).
#define AppVer "0.7.8"
#ifndef Src
  #define Src "..\publish"
#endif

[Setup]
AppId={{B7C8A6E2-3D0E-4C77-9E64-7E5C5A2A1F10}
AppName=TecLink
AppVersion={#AppVer}
AppPublisher=TechnoCrazy
AppPublisherURL=https://technocrazy.org
DefaultDirName={autopf}\TecLink
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\TecLink.App\app.ico
UninstallDisplayIcon={app}\TecLink.exe
OutputDir=..\out
OutputBaseFilename=TecLink-Setup-{#AppVer}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el Escritorio"; GroupDescription: "Accesos directos:"
Name: "startup"; Description: "Iniciar TecLink con Windows"; GroupDescription: "Opciones:"; Flags: unchecked

[Files]
Source: "{#Src}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion createallsubdirs

[Icons]
Name: "{autodesktop}\TecLink"; Filename: "{app}\TecLink.exe"; Tasks: desktopicon
Name: "{autoprograms}\TecLink"; Filename: "{app}\TecLink.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TecLink"; ValueData: """{app}\TecLink.exe"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\TecLink.exe"; Description: "Abrir TecLink ahora"; Flags: nowait postinstall skipifsilent
