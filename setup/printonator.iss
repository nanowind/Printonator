; Printonator — Inno Setup wizard script
; Build: ISCC.exe setup\printonator.iss

#define MyAppName "Printonator"
#define MyAppVersion "0.2.3"
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
; Trang "Điều khoản sử dụng" — user bắt buộc bấm "Đồng ý" mới được cài (bảo vệ tác giả khỏi kiện cáo)
LicenseFile=license.txt
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
; Bản phân phối CHÍNH THỨC: x64 (Windows 10/11 64-bit — phổ biến; Windows 11 toàn 64-bit).
; Máy x86 32-bit không được hỗ trợ (apphost .NET mặc định x64).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Yêu cầu Windows 10/11
MinVersion=10.0.17763
; Tự động đóng app đang chạy trước khi ghi đè file (không báo lỗi "file in use").
; KHÔNG dùng AppMutex — Inno sẽ hiện dialog "Setup has detected that Printonator is currently
; running" chờ user bấm OK. Bỏ AppMutex → Inno không detect → không dialog; force tự đóng im
; lặng nếu file bị khóa bởi app đang chạy.
CloseApplications=force

[Languages]
; Vietnamese.isl copy vào repo (setup\Vietnamese.isl) — bản Inno Setup trên CI (choco 6.7.1) KHÔNG ship
; Languages\Vietnamese.isl → tham chiếu `compiler:` fail build. Dùng relative cho hermetic (không phụ
; thuộc thư mục compiler). Default.isl là compiler: (embedded trong ISCC — luôn có).
; ChineseSimplified/Russian/Japanese.isl copy vào repo để hermetic (CI Inno Setup có đủ nhưng không
; phụ thuộc). Name là INTERNAL (tự đặt, không nhất thiết trùng file) — {language} trả về name này,
; nên dùng tên riêng để [Code] GetLangTag map sang culture tag chuẩn.
Name: "vietnamese"; MessagesFile: "Vietnamese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"
Name: "russian"; MessagesFile: "Russian.isl"
Name: "japanese"; MessagesFile: "Japanese.isl"

[Registry]
; Ghi ngôn ngữ người dùng chọn lúc cài để app đọc khi khởi động (HKCU — sở thích user).
; {code:GetLangTag} = hàm [Code] map tên ngôn ngữ Inno → culture tag chuẩn (vi-VN/en-US/zh-CN/ru-RU/ja-JP).
; uninsdeletevalue: gỡ cài KHÔNG để lại value cũ ảnh hưởng lần cài sau.
Root: HKCU; Subkey: "Software\Printonator"; ValueType: string; ValueName: "Language"; ValueData: "{code:GetLangTag}"; Flags: uninsdeletevalue

; T2.8: menu chuột phải "In với Printonator" trên MỌI file (HKCU — không cần admin).
; {cm:ShellMenuPrint} = tên menu theo ngôn ngữ lúc cài (khai báo [CustomMessages] bên dưới).
; uninsdeletekey trên cả 2 key: gỡ cài xóa sạch menu (không để lại cụm shell bẩn).
Root: HKCU; Subkey: "Software\Classes\*\shell\Printonator"; ValueType: string; ValueName: ""; ValueData: "{cm:ShellMenuPrint}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\Printonator"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\Printonator\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" --print ""%1"""; Flags: uninsdeletekey

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
; System.Drawing.Common — engine in PDF bằng ảnh GDI (GdiPrintEngine) dùng PrintDocument/Graphics
Source: "app\System.Drawing.Common.dll"; DestDir: "{app}"; Flags: ignoreversion
; .NET Desktop Runtime KHÔNG gói vào installer (giữ installer NHẸ ~6MB) — download on-demand lúc cài
; khi máy chưa có runtime (xem [Code]). Máy đã có .NET Desktop 8/9/10 → cài thẳng, không cần mạng.

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[CustomMessages]
; Custom message cho 5 ngôn ngữ (mặc định Tiếng Việt — fallback cho các ngôn ngữ không có entry).
; Dùng trong [Code] MsgBox .NET runtime. Các thông báo khác của setup (Next/Browse/Install...) do .isl lo.
DotNetMissing=Không tìm thấy .NET 8 Desktop Runtime
DotNetDownloadQuestion=Máy này chưa có .NET 8 Desktop Runtime
DotNetDownloadDetail=Setup sẽ tải và cài .NET 8 Desktop Runtime (~55MB, cần mạng)
DotNetContinuePrompt=Bấm OK để tiếp tục.
DotNetInstallError=Cài đặt .NET 8 Runtime gặp lỗi (mã %1). Cài thủ công: https://dotnet.microsoft.com/download/dotnet/8.0 rồi chạy lại setup.
DotNetLaunchError=Không khởi động được trình cài đặt .NET 8 Runtime. Cài thủ công: https://dotnet.microsoft.com/download/dotnet/8.0 rồi chạy lại setup.
DotNetDownloadError=Không tải được .NET 8 Desktop Runtime (lỗi mạng hoặc SHA không khớp). Cài thủ công: https://dotnet.microsoft.com/download/dotnet/8.0 rồi chạy lại setup.

; ==== Bản dịch theo ngôn ngữ (prefix = internal Name trong [Languages]: english/chinesesimp/russian/japanese).
;      Vi = mặc định ở trên. Inno tự chọn entry đúng ngôn ngữ khi hiển thị CustomMessage. ====
english.DotNetDownloadQuestion=This computer doesn't have .NET 8 Desktop Runtime installed.
english.DotNetDownloadDetail=Setup will download and install .NET 8 Desktop Runtime (~55MB, requires internet).
english.DotNetContinuePrompt=Press OK to continue.
english.DotNetInstallError=Failed to install .NET 8 Runtime (code %1). Install manually: https://dotnet.microsoft.com/download/dotnet/8.0 then run setup again.
english.DotNetLaunchError=Could not launch the .NET 8 Runtime installer. Install manually: https://dotnet.microsoft.com/download/dotnet/8.0 then run setup again.
english.DotNetDownloadError=Failed to download .NET 8 Desktop Runtime (network error or SHA mismatch). Install manually: https://dotnet.microsoft.com/download/dotnet/8.0 then run setup again.
chinesesimp.DotNetDownloadQuestion=此电脑尚未安装 .NET 8 桌面运行时。
chinesesimp.DotNetDownloadDetail=安装程序将下载并安装 .NET 8 桌面运行时（约 55MB，需要联网）。
chinesesimp.DotNetContinuePrompt=点击“确定”继续。
chinesesimp.DotNetInstallError=安装 .NET 8 运行时失败（代码 %1）。请手动安装：https://dotnet.microsoft.com/download/dotnet/8.0 然后重新运行安装程序。
chinesesimp.DotNetLaunchError=无法启动 .NET 8 运行时安装程序。请手动安装：https://dotnet.microsoft.com/download/dotnet/8.0 然后重新运行安装程序。
chinesesimp.DotNetDownloadError=无法下载 .NET 8 桌面运行时（网络错误或校验不一致）。请手动安装：https://dotnet.microsoft.com/download/dotnet/8.0 然后重新运行安装程序。
russian.DotNetDownloadQuestion=На этом компьютере не установлена среда выполнения .NET 8 Desktop Runtime.
russian.DotNetDownloadDetail=Программа установки загрузит и установит .NET 8 Desktop Runtime (~55 МБ, требуется интернет).
russian.DotNetContinuePrompt=Нажмите OK для продолжения.
russian.DotNetInstallError=Не удалось установить .NET 8 Runtime (код %1). Установите вручную: https://dotnet.microsoft.com/download/dotnet/8.0 и запустите установку заново.
russian.DotNetLaunchError=Не удалось запустить установщик .NET 8 Runtime. Установите вручную: https://dotnet.microsoft.com/download/dotnet/8.0 и запустите установку заново.
russian.DotNetDownloadError=Не удалось загрузить .NET 8 Desktop Runtime (ошибка сети или несоответствие SHA). Установите вручную: https://dotnet.microsoft.com/download/dotnet/8.0 и запустите установку заново.
japanese.DotNetDownloadQuestion=.NET 8 デスクトップ ランタイムがインストールされていません。
japanese.DotNetDownloadDetail=セットアップが .NET 8 デスクトップ ランタイム（約55MB、インターネット接続が必要）をダウンロードしてインストールします。
japanese.DotNetContinuePrompt=続行するには [OK] を押してください。
japanese.DotNetInstallError=.NET 8 ランタイムのインストールに失敗しました（コード %1）。手動でインストールしてください：https://dotnet.microsoft.com/download/dotnet/8.0 の後、セットアップを再実行してください。
japanese.DotNetLaunchError=.NET 8 ランタイムのインストーラーを起動できませんでした。手動でインストールしてください：https://dotnet.microsoft.com/download/dotnet/8.0 の後、セットアップを再実行してください。
japanese.DotNetDownloadError=.NET 8 デスクトップ ランタイムをダウンロードできませんでした（ネットワークエラーまたは SHA 不一致）。手動でインストールしてください：https://dotnet.microsoft.com/download/dotnet/8.0 の後、セットアップを再実行してください。

; T2.8: tên menu chuột phải "In với Printonator" (dùng trong [Registry] — theo ngôn ngữ lúc cài).
ShellMenuPrint=In với Printonator
english.ShellMenuPrint=Print with Printonator
chinesesimp.ShellMenuPrint=用 Printonator 打印
russian.ShellMenuPrint=Печать через Printonator
japanese.ShellMenuPrint=Printonatorで印刷

[Code]
// === Map tên ngôn ngữ Inno (Name trong [Languages]) → culture tag chuẩn .NET để app đọc registry.
//     {language} constant trả về Name tự đặt (vd "vietnamese"), KHÔNG phải culture tag → map thủ công. ===
function GetLangTag(Param: String): String;
begin
  if ActiveLanguage = 'vietnamese' then Result := 'vi-VN'
  else if ActiveLanguage = 'chinesesimp' then Result := 'zh-CN'
  else if ActiveLanguage = 'russian' then Result := 'ru-RU'
  else if ActiveLanguage = 'japanese' then Result := 'ja-JP'
  else Result := 'en-US';  // english + fallback
end;

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

    if MsgBox(CustomMessage('DotNetDownloadQuestion') + #13#10 +
              CustomMessage('DotNetDownloadDetail') + #13#10 +
              CustomMessage('DotNetContinuePrompt'),
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
          MsgBox(FmtMessage(CustomMessage('DotNetInstallError'), [IntToStr(ResultCode)]),
                 mbError, MB_OK);
      end
      else
        MsgBox(CustomMessage('DotNetLaunchError'), mbError, MB_OK);
    except
      MsgBox(CustomMessage('DotNetDownloadError'), mbError, MB_OK);
      Abort();
    end;
  end;
end;