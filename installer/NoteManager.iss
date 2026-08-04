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

#ifndef Doc2MdVersion
  #error Doc2MdVersion must be supplied by build-installer.ps1
#endif

#ifndef Doc2MdVersionMajor
  #error Doc2MdVersionMajor must be supplied by build-installer.ps1
#endif

#ifndef Doc2MdVersionMinor
  #error Doc2MdVersionMinor must be supplied by build-installer.ps1
#endif

#ifndef Doc2MdVersionRevision
  #error Doc2MdVersionRevision must be supplied by build-installer.ps1
#endif

#ifndef Doc2MdVersionBuild
  #error Doc2MdVersionBuild must be supplied by build-installer.ps1
#endif

#ifndef Doc2MdInstallerName
  #error Doc2MdInstallerName must be supplied by build-installer.ps1
#endif

#ifndef Doc2MdInstallerUrl
  #error Doc2MdInstallerUrl must be supplied by build-installer.ps1
#endif

#ifndef Doc2MdInstallerSha256
  #error Doc2MdInstallerSha256 must be supplied by build-installer.ps1
#endif

#define AppName "NoteManager"
#define AppPublisher "Taskscape Ltd"
#define AppExeName "NoteManager.exe"
#define Doc2MdCliExeName "DOC2MD.Cli.exe"

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

[Code]
var
  Doc2MdDownloadPage: TDownloadWizardPage;

{ Keep the install directory aligned with the Document Conversion plugin contract. }
function GetDoc2MdInstallDirectory(): String;
begin
  Result := ExpandConstant('{autopf}\Taskscape\DOC2MD');
end;

{ Return the fixed CLI path used to validate installed DOC2MD versions. }
function GetDoc2MdCliPath(): String;
begin
  Result := GetDoc2MdInstallDirectory() + '\{#Doc2MdCliExeName}';
end;

{ Detect the pinned DOC2MD version or a newer compatible installation. }
function IsRequiredDoc2MdInstalled(): Boolean;
var
  InstalledVersion: Int64;
begin
  if not GetPackedVersion(GetDoc2MdCliPath(), InstalledVersion) then
  begin
    Result := False;
    Log('A versioned DOC2MD CLI was not found at ' + GetDoc2MdCliPath() + '.');
    Exit;
  end;

  Result := ComparePackedVersion(
    InstalledVersion,
    PackVersionComponents(
      {#Doc2MdVersionMajor},
      {#Doc2MdVersionMinor},
      {#Doc2MdVersionRevision},
      {#Doc2MdVersionBuild})) >= 0;

  if Result then
    Log('DOC2MD {#Doc2MdVersion} or newer is already installed at the required location.')
  else
    Log('DOC2MD {#Doc2MdVersion} must be installed at ' + GetDoc2MdInstallDirectory() + '.');
end;

{ Retain the nested installer log after the parent setup temporary files are removed. }
function GetDoc2MdSetupLogPath(): String;
begin
  Result := ExpandConstant(
    '{commonappdata}\Taskscape\NoteManager\InstallerLogs\DOC2MD-Setup.log');
end;

{ Download the build-pinned release asset and verify its GitHub SHA-256 digest. }
function DownloadDoc2MdInstaller(): String;
begin
  Result := '';
  Doc2MdDownloadPage.Clear;
  Doc2MdDownloadPage.Add(
    '{#Doc2MdInstallerUrl}',
    '{#Doc2MdInstallerName}',
    '{#Doc2MdInstallerSha256}');
  Doc2MdDownloadPage.Show;
  try
    try
      Doc2MdDownloadPage.Download;
      Log('The DOC2MD installer download and SHA-256 verification succeeded.');
    except
      if Doc2MdDownloadPage.AbortedByUser then
        Result := 'The DOC2MD installer download was cancelled.'
      else
        Result := Format(
          'Unable to download DOC2MD {#Doc2MdVersion} from GitHub: %s', [GetExceptionMessage]);
    end;
  finally
    Doc2MdDownloadPage.Hide;
  end;
end;

{ Create the prerequisite progress page used by interactive installations. }
procedure InitializeWizard;
begin
  Doc2MdDownloadPage := CreateDownloadPage(
    'Downloading DOC2MD',
    'Setup is downloading the Document Conversion dependency.',
    nil);
  Doc2MdDownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

{ Install DOC2MD before NoteManager files are copied and fail closed on errors. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  InstallerPath: String;
  InstallerParameters: String;
  SetupLogPath: String;
  ResultCode: Integer;
begin
  Result := '';
  NeedsRestart := False;
  if IsRequiredDoc2MdInstalled() then
    Exit;

  Result := DownloadDoc2MdInstaller();
  if Result <> '' then
    Exit;

  InstallerPath := ExpandConstant('{tmp}\{#Doc2MdInstallerName}');
  SetupLogPath := GetDoc2MdSetupLogPath();
  if not ForceDirectories(ExtractFileDir(SetupLogPath)) then
  begin
    Result := 'Unable to create the NoteManager installer log directory.';
    Exit;
  end;

  InstallerParameters :=
    '/VERYSILENT /SUPPRESSMSGBOXES /SP- /NORESTART ' +
    '/RESTARTEXITCODE=3010 /NORESTARTAPPLICATIONS /TASKS="" ' +
    '/DIR=' + AddQuotes(GetDoc2MdInstallDirectory()) + ' ' +
    '/LOG=' + AddQuotes(SetupLogPath);

  Log('Installing DOC2MD silently at ' + GetDoc2MdInstallDirectory() + '.');
  if not Exec(
    InstallerPath,
    InstallerParameters,
    ExpandConstant('{tmp}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Result := Format(
      'Unable to start the DOC2MD installer: %s', [SysErrorMessage(ResultCode)]);
    Exit;
  end;

  if ResultCode = 3010 then
  begin
    NeedsRestart := True;
    Result := 'DOC2MD requires Windows to restart. Restart Windows, then run NoteManager Setup again.';
    Exit;
  end;

  if ResultCode <> 0 then
  begin
    Result := Format('The DOC2MD installer failed with exit code %d.', [ResultCode]);
    Exit;
  end;

  if not IsRequiredDoc2MdInstalled() then
  begin
    Result :=
      'DOC2MD Setup completed, but DOC2MD.Cli.exe was not installed at ' +
      GetDoc2MdCliPath() + '. Review ' + SetupLogPath + ' before retrying.';
    Exit;
  end;

  Log('DOC2MD was installed and verified before NoteManager installation.');
end;
