; Printonator — Inno Setup wizard script
; Build: ISCC.exe setup\printonator.iss

#define MyAppName "Printonator"
#define MyAppVersion "0.1.3"
#define MyAppPublisher "Phuc Nguyen"
#define MyAppExeName "Printonator.UI.exe"
#define MyAppURL "https://github.com/nanowind/Printonator"

[Setup]
AppId={{A1B2C3D4-1234-5678-9ABC-DEF012345678}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Cần quyền admin: install vào Program Files + tự cài .NET Desktop Runtime (runtime installer phải chạy elevated).
PrivilegesRequired=admin
OutputDir=.
OutputBaseFilename=printonator-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\Printonator.UI\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
; Bản phân phối CHÍNH THỨC: x64 (Windows 10/11 64-bit — phổ biến; Windows 11 toàn 64-bit).
; Máy x86 32-bit không được hỗ trợ (apphost .NET mặc định x64).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Yêu cầu Windows 10/11
MinVersion=10.0.17763

[Languages]
; Vietnamese.isl copy vào repo (setup\Vietnamese.isl) — bản Inno Setup trên CI (choco 6.7.1) KHÔNG ship
; Languages\Vietnamese.isl → tham chiếu `compiler:` fail build. Dùng relative cho hermetic (không phụ
; thuộc thư mục compiler). Default.isl là compiler: (embedded trong ISCC — luôn có).
Name: "vietnamese"; MessagesFile: "Vietnamese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "app\Printonator.UI.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "app\Printonator.UI.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "app\Printonator.UI.pdb"; DestDir: "{app}"; Flags: ignoreversion
Source: "app\Printonator.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "app\Printonator.Core.pdb"; DestDir: "{app}"; Flags: ignoreversion
Source: "app\Printonator.Spool.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "app\Printonator.Spool.pdb"; DestDir: "{app}"; Flags: ignoreversion
Source: "app\Printonator.UI.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "app\Printonator.UI.deps.json"; DestDir: "{app}"; Flags: ignoreversion
; Các dependency Windows/WinRT — Windows.Data.Pdf (PDF slicing) cần bộ SDK runtime này
Source: "app\Microsoft.Windows.SDK.NET.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "app\WinRT.Runtime.dll"; DestDir: "{app}"; Flags: ignoreversion
; .NET Desktop Runtime KHÔNG gói vào installer (giữ installer NHẸ ~6MB) — download on-demand lúc cài
; khi máy chưa có runtime (xem [Code]). Máy đã có .NET Desktop 8/9/10 → cài thẳng, không cần mạng.

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// .NET Desktop Runtime (bản x64) KHÔNG gói trong installer — download on-demand từ dot.net lúc cài,
// chỉ khi máy CHƯA có runtime. Máy đã có .NET Desktop 8/9/10 (app có RollForward=Major) → cài thẳng.
const
  DotNetRuntimeUrl    = 'https://dotnetcli.blob.core.windows.net/dotnet/WindowsDesktop/8.0.30/windowsdesktop-runtime-8.0.30-win-x64.exe';
  DotNetRuntimeFile   = 'windowsdesktop-runtime-8.0.30-win-x64.exe';
  // Bắt buộc khớp file thật trên CDN dot.net — release.yml verify hằng số này trước mỗi release.
  DotNetRuntimeSha256 = '8bd710afa5de396c9eb2a3b68d00279b7b9aca372a2443e9acd4f48ffdef3f2d';

// Kiểm tra đã có .NET Desktop Runtime hay chưa — đọc khoá release từ registry (cách chuẩn của .NET).
// App có RollForward=Major → bất kỳ WindowsDesktop.App bản 8+ nào cũng chạy được, không cần bản cụ thể.
function IsDotNet8Installed(): Boolean;
begin
  Result := RegKeyExists(HKLM,
             'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App');
  if Result then Exit;
  Result := RegKeyExists(HKLM,
             'SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  RuntimeExe: String;
  ResultCode: Integer;
begin
  // Cài .NET Desktop Runtime TRƯỚC khi cài app nếu thiếu. DownloadTemporaryFile tải về {tmp} (có
  // SHA-256 verify + progress bar trong setup); chạy ở ssInstall nên nếu abort thì chưa có file nào
  // được cài — không để lại nửa-cài. /VERYSILENT → skip (không hiện dialog giữa chừng).
  if (CurStep = ssInstall) and (not IsDotNet8Installed()) then
  begin
    if WizardSilent then
    begin
      Log('Printonator: máy thiếu .NET Desktop Runtime — bỏ qua download trong chế độ silent (cần cài sẵn .NET).');
      Exit;
    end;

    if MsgBox('Máy này chưa có .NET 8 Desktop Runtime.' + #13#10 +
              'Setup sẽ tải và cài .NET 8 Desktop Runtime (~55MB, cần mạng).' + #13#10 +
              'Bấm OK để tiếp tục.',
              mbInformation, MB_OKCANCEL) <> IDOK then
      Abort();

    try
      DownloadTemporaryFile(DotNetRuntimeUrl, DotNetRuntimeFile, DotNetRuntimeSha256, nil);
      RuntimeExe := ExpandConstant('{tmp}\' + DotNetRuntimeFile);
      // /norestart để không khởi động lại máy giữa chừng. Lỗi cài runtime → CẢNH BÁO + link cài tay,
      // KHÔNG abort (user có thể tự cài .NET sau; lỗi này không nên chặn cả quá trình cài app).
      if Exec(RuntimeExe, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      begin
        if ResultCode <> 0 then
          MsgBox('Cài đặt .NET 8 Runtime gặp lỗi (mã ' + IntToStr(ResultCode) + ').' + #13#10 +
                 'Cài thủ công: https://dotnet.microsoft.com/download/dotnet/8.0 rồi chạy lại setup.',
                 mbError, MB_OK);
      end
      else
        MsgBox('Không khởi động được trình cài đặt .NET 8 Runtime.' + #13#10 +
               'Cài thủ công: https://dotnet.microsoft.com/download/dotnet/8.0 rồi chạy lại setup.',
               mbError, MB_OK);
    except
      MsgBox('Không tải được .NET 8 Desktop Runtime (lỗi mạng hoặc SHA không khớp).' + #13#10 +
             'Cài thủ công: https://dotnet.microsoft.com/download/dotnet/8.0 rồi chạy lại setup.',
             mbError, MB_OK);
      Abort();
    end;
  end;
end;