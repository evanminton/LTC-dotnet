; Inno Setup 6 script for LTC Studio + ltc CLI.
; Built by publish.ps1, which passes AppVersion, SourceDir, OutputDir, OutputBase and Arch.
; Manual build (after ./publish.ps1 -NoInstaller):  ISCC.exe installer\LtcStudio.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\Release\win-x64\LtcStudio"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif
#ifndef OutputBase
  #define OutputBase "LtcStudio-" + AppVersion + "-win-x64-setup"
#endif
#ifndef Arch
  #define Arch "x64compatible"
#endif

#define AppName "LTC Studio"
#define AppExe  "LtcStudio.exe"

[Setup]
AppId={{CCF9078D-D408-4D8C-9101-245D854B4244}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Evan Minton
AppComments=Live SMPTE ST 12-1 Linear Time Code reader/generator and ltc command-line utility
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install by default (no admin); the user can choose "all users".
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
#if Arch != ""
ArchitecturesAllowed={#Arch}
ArchitecturesInstallIn64BitMode={#Arch}
#endif
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBase}
SetupIconFile={#SourceDir}\appicon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile={#SourceDir}\LICENSE
ChangesEnvironment=yes
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "addtopath";   Description: "Add the &ltc command-line tool to PATH"; GroupDescription: "Command line:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";           Filename: "{app}\{#AppExe}"
Name: "{group}\README";               Filename: "{app}\README.md"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";     Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
function EnvKeyRoot: Integer;
begin
  if IsAdminInstallMode then Result := HKEY_LOCAL_MACHINE else Result := HKEY_CURRENT_USER;
end;

function EnvKeyPath: String;
begin
  if IsAdminInstallMode then
    Result := 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
  else
    Result := 'Environment';
end;

function PathContains(const Paths, Dir: String): Boolean;
begin
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0;
end;

procedure AddToPath(const Dir: String);
var Paths: String;
begin
  if not RegQueryStringValue(EnvKeyRoot, EnvKeyPath, 'Path', Paths) then Paths := '';
  if PathContains(Paths, Dir) then exit;
  if (Paths <> '') and (Copy(Paths, Length(Paths), 1) <> ';') then Paths := Paths + ';';
  RegWriteExpandStringValue(EnvKeyRoot, EnvKeyPath, 'Path', Paths + Dir);
end;

procedure RemoveFromPath(const Dir: String);
var Paths: String; P: Integer;
begin
  if not RegQueryStringValue(EnvKeyRoot, EnvKeyPath, 'Path', Paths) then exit;
  Paths := ';' + Paths + ';';
  P := Pos(';' + Uppercase(Dir) + ';', Uppercase(Paths));
  if P = 0 then exit;
  Delete(Paths, P, Length(Dir) + 1);
  Paths := Copy(Paths, 2, Length(Paths) - 2);
  RegWriteExpandStringValue(EnvKeyRoot, EnvKeyPath, 'Path', Paths);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddToPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromPath(ExpandConstant('{app}'));
end;
