; Stagecast installer (Inno Setup 6). Build with scripts/installer.sh.
; Prerequisites: the app is self-contained (.NET 8 is inside), so only the WebView2 Runtime is required (downloaded when missing);
; the NDI Runtime (about 10 MB, official, signed by Vizrt) is downloaded and installed silently when it is missing.
#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\publish"
#endif
#define AppName "Stagecast"
#define AppExe "Stagecast.exe"
#define WebView2Guid "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"

[Setup]
AppId={{6D2C8F54-3B0E-4C7A-9E51-5A1B7C0D4E32}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Stagecast
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\dist
OutputBaseFilename=Stagecast-Setup-{#AppVersion}
SetupIconFile=..\src\Stagecast.App\stagecast.ico
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
id.PrereqTitle=Pemeriksaan prasyarat
id.PrereqSubtitle=Stagecast membutuhkan komponen berikut.
id.PrereqNet=.NET 8: sudah termasuk di dalam aplikasi, tidak perlu dipasang.
id.PrereqWebViewOk=WebView2 Runtime: sudah terpasang (versi %1).
id.PrereqWebViewMissing=WebView2 Runtime: BELUM terpasang. Akan diunduh dari Microsoft (sekitar 2 MB) dan dipasang otomatis.
id.PrereqNdiOk=NDI Runtime: sudah terpasang.
id.PrereqNdiMissing=NDI Runtime: BELUM terpasang. Akan diunduh dari ndi.video (sekitar 10 MB, resmi dari NDI) dan dipasang otomatis. Dibutuhkan untuk output NDI.
id.PrereqInternet=Pengunduhan memerlukan koneksi internet.
id.TaskDesktop=Buat pintasan di &Desktop
id.TaskGroup=Komponen tambahan:
id.DownloadingWebView=Mengunduh WebView2 Runtime...
id.DownloadingNdi=Mengunduh NDI Runtime...
id.InstallingWebView=Memasang WebView2 Runtime...
id.InstallingNdi=Memasang NDI Runtime...
id.NdiNotSigned=Berkas NDI Runtime yang diunduh tidak memiliki tanda tangan resmi Vizrt dan tidak akan dipasang. Lanjutkan memasang Stagecast tanpa NDI Runtime? Output NDI tidak akan bisa dinyalakan.
id.NdiFailed=NDI Runtime tidak berhasil dipasang (kode %1). Lanjutkan memasang Stagecast tanpa NDI Runtime? Output NDI tidak akan bisa dinyalakan sampai runtime dipasang.
id.DownloadFailed=Pengunduhan %1 gagal:%n%n%2%n%nLanjutkan memasang Stagecast tanpa komponen ini? Aplikasi mungkin tidak berjalan sampai komponen itu dipasang.
id.WebViewFailed=WebView2 Runtime tidak berhasil dipasang (kode %1). Lanjutkan memasang Stagecast tanpa komponen ini? Aplikasi tidak akan berjalan sampai WebView2 Runtime dipasang.
id.RunApp=Jalankan Stagecast
id.RemoveData=Hapus juga pengaturan, data login, dan log Stagecast di folder %1?%n%nPilih Tidak bila Anda akan memasang ulang dan ingin tetap login.
en.PrereqTitle=Prerequisite check
en.PrereqSubtitle=Stagecast needs the following components.
en.PrereqNet=.NET 8: included in the application, nothing to install.
en.PrereqWebViewOk=WebView2 Runtime: already installed (version %1).
en.PrereqWebViewMissing=WebView2 Runtime: NOT installed. It will be downloaded from Microsoft (about 2 MB) and installed automatically.
en.PrereqNdiOk=NDI Runtime: already installed.
en.PrereqNdiMissing=NDI Runtime: NOT installed. It will be downloaded from ndi.video (about 10 MB, official from NDI) and installed automatically. Needed for the NDI output.
en.PrereqInternet=Downloading needs an internet connection.
en.TaskDesktop=Create a &desktop shortcut
en.TaskGroup=Additional components:
en.DownloadingWebView=Downloading WebView2 Runtime...
en.DownloadingNdi=Downloading the NDI Runtime...
en.InstallingWebView=Installing WebView2 Runtime...
en.InstallingNdi=Installing the NDI Runtime...
en.NdiNotSigned=The downloaded NDI Runtime file does not carry a valid Vizrt signature and will not be installed. Continue installing Stagecast without the NDI Runtime? The NDI output will not be available.
en.NdiFailed=The NDI Runtime could not be installed (code %1). Continue installing Stagecast without it? The NDI output will not be available until the runtime is installed.
en.DownloadFailed=Downloading %1 failed:%n%n%2%n%nContinue installing Stagecast without it? The application may not run until it is installed.
en.WebViewFailed=The WebView2 Runtime could not be installed (code %1). Continue installing Stagecast without it? The application will not run until the WebView2 Runtime is installed.
en.RunApp=Run Stagecast
en.RemoveData=Also delete Stagecast settings, login data and logs in %1?%n%nChoose No if you will reinstall and want to stay logged in.

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
const
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  NdiRuntimeUrl = 'https://downloads.ndi.tv/SDK/NDI_SDK/NDI%206%20Runtime.exe';

var
  PrereqPage: TOutputMsgWizardPage;
  DownloadPage: TDownloadWizardPage;

function WebView2Version(): String;
var
  V: String;
begin
  Result := '';
  if ExpandConstant('{param:SimulateWebView2Missing|0}') = '1' then Exit; // test switch: behave as if the runtime were missing
  if RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{#WebView2Guid}', 'pv', V) or
     RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{#WebView2Guid}', 'pv', V) then
    if (V <> '') and (V <> '0.0.0.0') then Result := V;
end;

function WebView2Missing(): Boolean;
begin
  Result := WebView2Version() = '';
end;

function NdiInstalled(): Boolean;
begin
  if ExpandConstant('{param:SimulateNdiMissing|0}') = '1' then begin Result := False; Exit; end;
  Result :=
    (GetEnv('NDI_RUNTIME_DIR_V6') <> '') or (GetEnv('NDI_RUNTIME_DIR_V5') <> '') or
    FileExists(ExpandConstant('{commonpf64}\NDI\NDI 6 Runtime\v6\Processing.NDI.Lib.x64.dll')) or
    FileExists(ExpandConstant('{commonpf64}\NDI\NDI 6 Tools\Runtime\Processing.NDI.Lib.x64.dll')) or
    FileExists(ExpandConstant('{commonpf64}\NDI\NDI 5 Runtime\Processing.NDI.Lib.x64.dll')) or
    FileExists(ExpandConstant('{commonpf64}\NDI\NDI 5 Tools\Runtime\Processing.NDI.Lib.x64.dll'));
end;

function NdiMissing(): Boolean;
begin
  Result := not NdiInstalled();
end;

procedure InitializeWizard();
var
  Text: String;
begin
  PrereqPage := CreateOutputMsgPage(wpWelcome, CustomMessage('PrereqTitle'), CustomMessage('PrereqSubtitle'), '');
  Text := CustomMessage('PrereqNet') + #13#10#13#10;
  if WebView2Missing() then Text := Text + CustomMessage('PrereqWebViewMissing')
  else Text := Text + FmtMessage(CustomMessage('PrereqWebViewOk'), [WebView2Version()]);
  Text := Text + #13#10#13#10;
  if NdiMissing() then Text := Text + CustomMessage('PrereqNdiMissing') else Text := Text + CustomMessage('PrereqNdiOk');
  if WebView2Missing() or NdiMissing() then Text := Text + #13#10#13#10 + CustomMessage('PrereqInternet');
  PrereqPage.MsgLabel.Caption := Text;
  PrereqPage.MsgLabel.AutoSize := False;
  PrereqPage.MsgLabel.Width := PrereqPage.SurfaceWidth;
  PrereqPage.MsgLabel.Height := PrereqPage.SurfaceHeight;
  PrereqPage.MsgLabel.WordWrap := True;
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

// Downloads Url to FileName in the temporary folder. Returns True when the file is there; otherwise asks whether to carry on.
function Fetch(const What, Url, FileName, CaptionKey: String; var Abort: Boolean): Boolean;
begin
  Result := False;
  DownloadPage.Clear;
  DownloadPage.Add(Url, FileName, '');
  DownloadPage.SetText(CustomMessage(CaptionKey), '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      Result := True;
    except
      if DownloadPage.AbortedByUser then
        Abort := True
      else if SuppressibleMsgBox(FmtMessage(CustomMessage('DownloadFailed'), [What, GetExceptionMessage]), mbError, MB_YESNO, IDYES) = IDNO then
        Abort := True;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Abort: Boolean;
begin
  Result := '';
  Abort := False;

  if WebView2Missing() then
  begin
    if Fetch('WebView2 Runtime', WebView2Url, 'MicrosoftEdgeWebview2Setup.exe', 'DownloadingWebView', Abort) then
    begin
      WizardForm.StatusLabel.Caption := CustomMessage('InstallingWebView');
      if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, Code) then
        Code := -1;
      if (Code <> 0) and (ExpandConstant('{param:SimulateWebView2Missing|0}') <> '1') and WebView2Missing() then
        if SuppressibleMsgBox(FmtMessage(CustomMessage('WebViewFailed'), [IntToStr(Code)]), mbError, MB_YESNO, IDYES) = IDNO then Abort := True;
    end;
    if Abort then Result := SetupMessage(msgSetupAborted);
    if Abort then Exit;
  end;

  if NdiMissing() then
  begin
    if Fetch('NDI Runtime', NdiRuntimeUrl, 'NDI 6 Runtime.exe', 'DownloadingNdi', Abort) then
    begin
      WizardForm.StatusLabel.Caption := CustomMessage('InstallingNdi');
      // Only run it when it carries a valid Vizrt signature: the file comes from the internet.
      if not Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "$s = Get-AuthenticodeSignature -LiteralPath ''' + ExpandConstant('{tmp}\NDI 6 Runtime.exe') + '''; if ($s.Status -eq ''Valid'' -and $s.SignerCertificate.Subject -like ''*Vizrt*'') { exit 0 } else { exit 1 }"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
        Code := 1;
      if Code <> 0 then
      begin
        if SuppressibleMsgBox(CustomMessage('NdiNotSigned'), mbError, MB_YESNO, IDYES) = IDNO then Abort := True;
      end
      else
      begin
        if not Exec(ExpandConstant('{tmp}\NDI 6 Runtime.exe'), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code) then
          Code := -1;
        if (Code <> 0) and (ExpandConstant('{param:SimulateNdiMissing|0}') <> '1') and NdiMissing() then
          if SuppressibleMsgBox(FmtMessage(CustomMessage('NdiFailed'), [IntToStr(Code)]), mbError, MB_YESNO, IDYES) = IDNO then Abort := True;
      end;
    end;
    if Abort then Result := SetupMessage(msgSetupAborted);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Data := ExpandConstant('{localappdata}\Stagecast');
  if DirExists(Data) then
    if SuppressibleMsgBox(FmtMessage(CustomMessage('RemoveData'), [Data]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
      DelTree(Data, True, True, True);
end;
