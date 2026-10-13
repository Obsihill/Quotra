#ifndef AppVersion
#define AppVersion "1.0.0"
#endif
#ifndef AppFileVersion
#define AppFileVersion "1.0.0.0"
#endif
#ifndef PublishDir
  #error PublishDir must point to the self-contained win-x64 publish directory.
#endif

[Setup]
AppId={{B8ED5F53-6679-4C2C-B6D4-7C6B59AC4B6E}
AppName=Quotra
AppVersion={#AppVersion}
AppVerName=Quotra {#AppVersion}
AppPublisher=Quotra
DefaultDirName={localappdata}\Programs\Quotra
DefaultGroupName=Quotra
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
CloseApplications=yes
RestartApplications=no
OutputBaseFilename=Quotra-Setup-{#AppVersion}-win-x64
SetupIconFile=..\..\src\Quotra.WinUI\Assets\Quotra.ico
UninstallDisplayIcon={app}\QuotraWinUI.exe
VersionInfoVersion={#AppFileVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoCompany=Quotra
VersionInfoDescription=Quotra installer
WizardStyle=modern
Compression=lzma2
SolidCompression=yes

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면 바로 가기 만들기"; GroupDescription: "추가 작업:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Quotra"; Filename: "{app}\QuotraWinUI.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Quotra"; Filename: "{app}\QuotraWinUI.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Quotra"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\QuotraWinUI.exe"; Description: "Quotra 실행"; Flags: nowait postinstall skipifsilent
