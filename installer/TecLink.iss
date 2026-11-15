; TecLink: instalador por usuario (no pide permisos de administrador).
#define AppVer "0.9.1"
#ifndef Src
  #define Src "..\publish"
#endif

[Setup]
AppId={{B7C8A6E2-3D0E-4C77-9E64-7E5C5A2A1F10}
AppName=TecLink
AppVersion={#AppVer}
AppPublisher=TechnoCrazy
AppPublisherURL=https://technocrazy.org
AppSupportURL=https://technocrazy.org
AppUpdatesURL=https://github.com/fuchogallery-droid/teclink/releases
AppCopyright=© 2026 TechnoCrazy. Todos los derechos reservados.
AppComments=Comparte mouse, teclado, pantallas, chat y archivos entre PCs.
VersionInfoVersion={#AppVer}.0
VersionInfoCompany=TechnoCrazy
VersionInfoDescription=Instalador de TecLink {#AppVer}
VersionInfoProductName=TecLink
VersionInfoProductVersion={#AppVer}
VersionInfoCopyright=© 2026 TechnoCrazy
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

[UninstallDelete]
; The panel's web cache is TecLink's own: it goes away with the program. The settings (config.json) stay in case it is installed again.
Type: filesandordirs; Name: "{localappdata}\TecLink"

[UninstallRun]
; Best effort: takes away the Firewall permission TecLink asked for (needs administrator rights, so it only works if the uninstaller has them).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""TecLink (sesiones)"""; Flags: runhidden; RunOnceId: "QuitarReglaFirewall"
