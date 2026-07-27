; NoteManager desktop installer
; -----------------------------------------------------------------------------
; Compiled by build-installer.ps1, which supplies the optimized dotnet publish
; directory, product version, target architecture, and output filename.
; -----------------------------------------------------------------------------

#ifndef PublishDir
  #define PublishDir "publish"
#endif

#ifndef OutputDir
  #define OutputDir "Output"
#endif

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef VersionInfoVersion
  #define VersionInfoVersion "1.0.0.0"
#endif

#ifndef InstallerArchitecture
  #define InstallerArchitecture "x64compatible"
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "NoteManager-1.0.0-win-x64-Setup"
#endif

#define AppName "NoteManager"
#define AppPublisher "Taskscape Ltd"
#define AppExeName "NoteManager.exe"

[Setup]
AppId={{8C46DB0F-726E-4B2A-AFD3-9EE601DE30B8}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\Taskscape\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed={#InstallerArchitecture}
ArchitecturesInstallIn64BitMode={#InstallerArchitecture}
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#AppExeName}
VersionInfoVersion={#VersionInfoVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; \
    GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; \
    Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; \
    Flags: nowait postinstall skipifsilent
