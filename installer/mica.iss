; Mica installer (Inno Setup 6). Build with build.ps1.
; Installs per-user (no admin needed) to %LOCALAPPDATA%\Programs\Mica.

#define AppName "Mica"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExe "Mica.exe"
#define Bin "..\src\Mica\bin\Release\net462"
#define Repo "https://github.com/iscooliscool/mica"

[Setup]
AppId={{6F3B2E7A-4C1D-4B8E-9A55-7D0C1E3F9B21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Mica
AppPublisherURL={#Repo}
AppSupportURL={#Repo}/issues
AppUpdatesURL={#Repo}/releases
DefaultDirName={localappdata}\Programs\Mica
DefaultGroupName=Mica
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=Mica-{#AppVersion}-Setup
SetupIconFile=..\assets\mica.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=Mica
WizardStyle=modern
WizardSizePercent=100
WizardImageFile=art\wizard-100.bmp,art\wizard-125.bmp,art\wizard-150.bmp,art\wizard-175.bmp,art\wizard-200.bmp,art\wizard-225.bmp,art\wizard-250.bmp
WizardSmallImageFile=art\small-100.bmp,art\small-125.bmp,art\small-150.bmp,art\small-175.bmp,art\small-200.bmp,art\small-225.bmp,art\small-250.bmp
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=force
RestartApplications=no
MinVersion=10.0
ArchitecturesAllowed=x86compatible x64compatible arm64
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Mica
VersionInfoDescription=Mica browser setup
LicenseFile=..\LICENSE
ChangesAssociations=yes

[Messages]
WelcomeLabel1=Welcome to Mica
WelcomeLabel2=Mica is a fast, light browser built to run smoothly on any PC, even older laptops.%n%nThis will install Mica {#AppVersion}. When it's done, Mica opens and gives you a quick tour.
FinishedHeadingLabel=Mica is ready
FinishedLabel=Mica is installed. Open it to pick your look and take the quick tour.
FinishedLabelNoIcons=Mica is installed. Open it to pick your look and take the quick tour.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#Bin}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Bin}\{#AppExe}.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Bin}\Microsoft.Web.WebView2.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Bin}\Microsoft.Web.WebView2.WinForms.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Bin}\runtimes\*"; DestDir: "{app}\runtimes"; Flags: ignoreversion recursesubdirs
Source: "{#Bin}\ui\*"; DestDir: "{app}\ui"; Flags: ignoreversion recursesubdirs
Source: "redist\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWebView2

[InstallDelete]
; old interface files from a previous version
Type: filesandordirs; Name: "{app}\ui"

[Icons]
Name: "{autoprograms}\Mica"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\Mica"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Register Mica as a browser so it can be picked as the default in Windows Settings.
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Mica"; ValueType: string; ValueName: ""; ValueData: "Mica"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Mica\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Mica\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"""
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Mica\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "Mica"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Mica\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "A fast, light browser for any PC"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Mica\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\{#AppExe},0"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Mica\Capabilities\URLAssociations"; ValueType: string; ValueName: "http"; ValueData: "MicaURL"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Mica\Capabilities\URLAssociations"; ValueType: string; ValueName: "https"; ValueData: "MicaURL"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Mica\Capabilities\FileAssociations"; ValueType: string; ValueName: ".htm"; ValueData: "MicaHTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Mica\Capabilities\FileAssociations"; ValueType: string; ValueName: ".html"; ValueData: "MicaHTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Mica\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "MicaHTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Mica\Capabilities\FileAssociations"; ValueType: string; ValueName: ".svg"; ValueData: "MicaHTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Mica\Capabilities\StartMenu"; ValueType: string; ValueName: "StartMenuInternet"; ValueData: "Mica"
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "Mica"; ValueData: "Software\Clients\StartMenuInternet\Mica\Capabilities"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\MicaURL"; ValueType: string; ValueName: ""; ValueData: "Mica URL"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\MicaURL"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\MicaURL\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKCU; Subkey: "Software\Classes\MicaURL\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\MicaHTML"; ValueType: string; ValueName: ""; ValueData: "Mica Document"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\MicaHTML\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKCU; Subkey: "Software\Classes\MicaHTML\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing the Microsoft WebView2 engine (one time only)..."; Check: NeedsWebView2; Flags: waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "Open Mica"; Flags: nowait postinstall skipifsilent
; after a silent auto-update, reopen Mica
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent

[Code]
function WebView2Version(Root: Integer; Key: String): String;
begin
  if not RegQueryStringValue(Root, Key, 'pv', Result) then Result := '';
end;

function NeedsWebView2: Boolean;
var v: String;
begin
  v := WebView2Version(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}');
  if (v = '') or (v = '0.0.0.0') then v := WebView2Version(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}');
  if (v = '') or (v = '0.0.0.0') then v := WebView2Version(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}');
  Result := (v = '') or (v = '0.0.0.0');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
    if MsgBox('Also delete your Mica bookmarks, history and settings?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\Mica'), True, True, True);
end;
