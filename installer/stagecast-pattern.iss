; Stagecast Pattern installer (Inno Setup 6). Build with scripts/installer-pattern.sh.
; The app is self-contained (.NET 8 is inside) and uses the video encoder that comes with Windows, so there are no prerequisites to download.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\publish-pattern"
#endif
#define AppName "Stagecast Pattern"
#define AppExe "StagecastPattern.exe"

[Setup]
AppId={{B3E7A1D9-52C4-4F08-9A6E-1D8C0F37B254}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Stagecast
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\dist
OutputBaseFilename=StagecastPattern-Setup-{#AppVersion}
SetupIconFile=..\src\Stagecast.Pattern\pattern.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "id"; MessagesFile: "Indonesian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
id.TaskDesktop=Buat pintasan di &Desktop
id.RunApp=Jalankan Stagecast Pattern
id.RemoveData=Hapus juga pengaturan Stagecast Pattern di folder %1?%n%nPilih Tidak bila Anda akan memasang ulang dan ingin pengaturan tetap ada.
en.TaskDesktop=Create a &desktop shortcut
en.RunApp=Run Stagecast Pattern
en.RemoveData=Also delete the Stagecast Pattern settings in %1?%n%nChoose No if you will reinstall and want to keep them.

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:RunApp}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{localappdata}\StagecastPattern');
    if DirExists(Data) then
      if SuppressibleMsgBox(FmtMessage(CustomMessage('RemoveData'), [Data]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;
