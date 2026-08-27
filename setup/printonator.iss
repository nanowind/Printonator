; Printonator — Inno Setup wizard script
; Build: ISCC.exe setup\printonator.iss

#define MyAppName "Printonator"
#define MyAppVersion "0.1.0"
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
Name: "vietnamese"; MessagesFile: "compiler:Languages\Vietnamese.isl"
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
; .NET 8 Desktop Runtime — GÓI SẴN, self-extract vào {tmp} (dontcopy) để cài tự động nếu thiếu (xem [Code])
Source: "runtime\windowsdesktop-runtime-8.0.30-win-x64.exe"; DestDir: "{tmp}"; Flags: dontcopy nocompression

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// .NET 8 Desktop Runtime (bản x64) được GÓI SẴN trong setup — cài tự động nếu thiếu.
const
  DotNetRuntimeFile = 'windowsdesktop-runtime-8.0.30-win-x64.exe';

function IsDotNet8Installed(): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App') or
            RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  RuntimeExe: String;
  ResultCode: Integer;
begin
  // Cài .NET Desktop Runtime TRƯỚC khi cài app nếu thiếu. Runtime đã được setup gói sẵn
  // (xem [Files] dòng `dontcopy` → được giải nén vào {tmp}, không vào thư mục cài).
  if (CurStep = ssInstall) and (not IsDotNet8Installed()) then
  begin
    MsgBox('Printonator cần .NET 8 Desktop Runtime.' + #13#10 +
           'Setup sẽ tự cài .NET 8 Desktop Runtime (gói sẵn ~60MB, không cần mạng).' + #13#10 +
           'Bấm OK để tiếp tục.',
           mbInformation, MB_OK);
    RuntimeExe := ExpandConstant('{tmp}\' + DotNetRuntimeFile);
    // /norestart để không khởi động lại máy giữa chừng. Bạn bấm It sẽ tự cài im lặng.
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
  end;
end;