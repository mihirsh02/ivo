; Ivo installer (Inno Setup 6). Build: installer\build.ps1, which publishes the app to dist\app and compiles this
; into dist\Ivo\IvoSetup-<version>.exe; with /DApp=Portal it installs Ivo Portal instead, as dist\Ivo Portal\IvoPortalSetup-<version>.exe
; (build.ps1 sets the folder with /O; built by hand, it lands in dist).
; Installs to Program Files (one admin prompt), the same place as IvoSetup-<version>.msi. Either installer replaces
; the other's install first, so there's only ever one Ivo. Install and upgrade never touch your chats and settings
; (%AppData%\Ivo, or %AppData%\Ivo Portal); only a manual uninstall offers to delete them, and only if you tick yes.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef App
  #define App "Ivo"
#endif
#if App == "Portal"
  #define AppName "Ivo Portal"
  #define AppId "{B47529B4-032D-4F4E-873A-165EC91CB352}"
  #define MsiUpgradeCode "{87122A19-F7A0-44ED-9C49-338AAA277C90}"
  #define SetupName "IvoPortalSetup"
  #define AppIcon "..\Portal\portal.ico"
  #define Art "art-portal"
  #define Blurb "Ivo Portal: chat, calls and screen sharing, person to person."
  #define Opener "Open it from the Start menu."
  #define DataWords "chats, contacts and settings"
#else
  #define AppName "Ivo"
  #define AppId "{6E7A3C1D-4B2F-4C8E-9A51-7D0B3E2F9A64}"
  #define MsiUpgradeCode "{6E7A3C1D-4B2F-4C8E-9A51-7D0B3E2F9A65}"
  #define SetupName "IvoSetup"
  #define AppIcon "..\Assets\ivo.ico"
  #define Art "art"
  #define Blurb "Ivo is your AI desk on Windows: chats with any model, agents that work in your code, a planner and a morning briefing, all in one quiet window."
  #define Opener "Open it from the Start menu, or press Ctrl+Shift+Space from anywhere."
  #define DataWords "chats, settings, API keys, skills, memories"
#endif
#define AppExe AppName + ".exe"

[Setup]
AppId={#StringChange(AppId, "{", "{{")}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Ivo
AppComments={#Blurb}
DefaultDirName={autopf}\{#AppName}
UsePreviousAppDir=yes
DisableDirPage=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename={#SetupName}-{#AppVersion}
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
; Ivo's look: dark, Windows 11 controls, the title bar styled too, no divider lines; Ivo's canvas colour with its soft
; violet / cyan glow behind every page (tools\installer-art\make_art.py draws all the art)
WizardStyle=modern dark windows11 hidebevels includetitlebar
WizardSizePercent=110
WizardBackColor=#12171C
WizardBackImageFile={#Art}\back.png
WizardImageBackColor=#0B0F12
; the welcome / finish art at 100 / 125 / 150 / 200%, and the corner icon: just the crystal, no tile behind it
WizardImageFile={#Art}\wizard-100.bmp,{#Art}\wizard-125.bmp,{#Art}\wizard-150.bmp,{#Art}\wizard-200.bmp
WizardSmallImageFile={#Art}\small-100.png,{#Art}\small-125.png,{#Art}\small-150.png,{#Art}\small-200.png
WizardImageStretch=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} setup
VersionInfoCompany=Ivo
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
#ifdef Signed
; build.ps1 passes /DSigned with the IvoSign tool: Inno then signs the setup and unins000.exe, which it writes at install time
SignTool=IvoSign
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
; Ivo's own words on each page
SetupWindowTitle=Install {#AppName}
WelcomeLabel1=Welcome to {#AppName}
WelcomeLabel2={#Blurb}%n%nThis installs {#AppName} {#AppVersion}. If {#AppName} is already here, your chats and settings stay as they are.
SelectDirDesc=Where should {#AppName} live?
SelectDirLabel3={#AppName} goes in this folder. Your chats and settings live in your own profile, not here.
SelectTasksDesc=A couple of choices
SelectTasksLabel2=You can change both later in {#AppName}'s settings.
ReadyLabel1=Ready to install {#AppName}
ReadyLabel2a=Click Install to put {#AppName} on this PC.
InstallingLabel=Putting {#AppName} in place…
FinishedHeadingLabel={#AppName} is ready
FinishedLabelNoIcons={#AppName} is installed. {#Opener}
FinishedLabel={#AppName} is installed. {#Opener}
ClickFinish=Click Finish to close setup.

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup"; Description: "&Start {#AppName} with Windows (it waits in the tray)"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; just installed: on its next start the app asks whether to keep an account it finds from before (and clears this)
Root: HKCU; Subkey: "Software\Ivo\{#AppName}"; ValueType: dword; ValueName: "JustInstalled"; ValueData: 1
; the same per-user entry Settings → Appearance → "Start with Windows" uses, so the two stay in step
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --tray"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Open {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM ""{#AppExe}"" /F"; Flags: runhidden; RunOnceId: "StopApp"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// Before installing: remove the app installed from the .msi or by Ivo's own installer, so there's only ever one copy.
// (Neither uninstall touches its %AppData% folder: the data question is only asked in a manual, non-silent uninstall.)
function MsiEnumRelatedProducts(upgradeCode: String; reserved, index: Cardinal; productCode: String): Cardinal;
  external 'MsiEnumRelatedProductsW@msi.dll stdcall';

// Updating, not installing, when this PC already has it (from this setup, the .msi or Ivo's own installer): the window,
// the welcome, the button and the progress say so
var Updating: Boolean;
procedure InitializeWizard;
begin
  Updating := RegKeyExists(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1')
           or RegKeyExists(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppName}')
           or (MsiEnumRelatedProducts('{#MsiUpgradeCode}', 0, 0, StringOfChar(' ', 38)) = 0);
  if Updating then
  begin
    WizardForm.Caption := 'Update {#AppName}';
    WizardForm.WelcomeLabel1.Caption := 'Update {#AppName}';
    WizardForm.WelcomeLabel2.Caption := 'This puts {#AppName} {#AppVersion} in place of the version you have. Your chats and settings stay as they are.';
    WizardForm.FinishedHeadingLabel.Caption := '{#AppName} is updated';
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if not Updating then exit;
  if CurPageID = wpReady then
  begin
    WizardForm.NextButton.Caption := 'Update';
    WizardForm.PageNameLabel.Caption := 'Ready to update {#AppName}';
    WizardForm.ReadyLabel.Caption := 'Click Update to put the new {#AppName} in place.';
  end
  else if CurPageID = wpInstalling then
  begin
    WizardForm.PageNameLabel.Caption := 'Updating {#AppName}';
    WizardForm.PageDescriptionLabel.Caption := 'Putting the new version in place…';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var code, cmd: String; rc: Integer;
begin
  Result := '';
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM "{#AppExe}" /F', '', SW_HIDE, ewWaitUntilTerminated, rc);
  code := StringOfChar(' ', 38);
  while MsiEnumRelatedProducts('{#MsiUpgradeCode}', 0, 0, code) = 0 do
    if not Exec('msiexec.exe', '/x ' + code + ' /qn /norestart', '', SW_HIDE, ewWaitUntilTerminated, rc) or (rc <> 0) then
    begin
      Result := 'Couldn''t remove the {#AppName} installed from the .msi (error ' + IntToStr(rc) + '). Uninstall it in Settings > Apps, then run this again.';
      exit;
    end;
  // Ivo's own installer (IvoInstall-<version>.exe) registers under the app's name; its quiet uninstall keeps your data
  if RegQueryStringValue(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppName}', 'QuietUninstallString', cmd) then   // no window: --quiet
  begin
    if not Exec(ExpandConstant('{cmd}'), '/C "' + cmd + '"', '', SW_HIDE, ewWaitUntilTerminated, rc) or (rc <> 0) then
    begin
      Result := 'Couldn''t remove the existing {#AppName} install (error ' + IntToStr(rc) + '). If endpoint protection removed its uninstaller, ask your IT team to restore it, then run setup again.';
      exit;
    end;
  end;
end;

// Remove the startup entry even if it was turned on later from inside the app, and offer to delete the user's data.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
    data := ExpandConstant('{userappdata}\{#AppName}');
    if DirExists(data) and (not UninstallSilent) then
      // kept unless you say otherwise: there's no cloud copy, so this folder is the only way back to the same app
      if MsgBox('Keep your {#AppName} data ({#DataWords}) on this PC?' + #13#10 + #13#10 +
                'Install {#AppName} again and everything is exactly as you left it. There''s no cloud copy: if you remove it, it''s gone for good.' + #13#10 + #13#10 + data,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDNO then
        DelTree(data, True, True, True);
  end;
end;
