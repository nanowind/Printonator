; Printonator — Inno Setup wizard script
; Build: ISCC.exe setup\printonator.iss

#define MyAppName "Printonator"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Printonator Project"
#define MyAppExeName "Printonator.UI.exe"
#define MyAppURL "https://github.com/printonator/printonator"

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
Source: "app\Printonator.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "app\Printonator.UI.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "app\Printonator.UI.deps.json"; DestDir: "{app}"; Flags: ignoreversion
; Kiểm tra .NET 8 runtime — cài nếu thiếu (xem [Code])

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Kiểm tra .NET 8 Desktop Runtime đã cài chưa
function IsDotNet8Installed(): Boolean;
var
  key: String;
begin
  key := 'SOFTWARE\Microsoft\NET Core Setup\NDP\v4\Full\';
  // Đơn giản: kiểm tra registry .NET 8 runtime
  Result := RegKeyExists(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App') or
            RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Mở trang .NET 8 download nếu thiếu (user tự cài — đơn giản & an toàn)
    if not IsDotNet8Installed() then
      MsgBox('Printonator cần .NET 8 Desktop Runtime.' + #13#10 +
             'Vui lòng cài từ: https://dotnet.microsoft.com/download/dotnet/8.0' + #13#10 +
             '(chọn ".NET Desktop Runtime 8.x"), rồi chạy lại Printonator.',
             mbInformation, MB_OK);
  end;
end;