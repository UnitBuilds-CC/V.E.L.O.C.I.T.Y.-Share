; VelocityShare Inno Setup Installer
; Build: ISCC.exe VelocityShare.iss
;
; Packages the self-contained published server as a Windows installer
; with automatic Windows Service registration and firewall configuration.

#define MyAppName "V.E.L.O.C.I.T.Y. Share"
#define MyAppShortName "VelocityShare"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "VelocityShare"
#define MyAppURL "https://github.com/velocityshare/velocityshare"
#define MyAppExeName "VelocityShare.Server.exe"
#define MyServiceName "VelocityShare"

[Setup]
AppId={{E7A3F1B2-4C5D-6E8F-9A0B-1C2D3E4F5A6B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppShortName}
DefaultGroupName={#MyAppShortName}
AllowNoIcons=yes
LicenseFile=
OutputDir=Output
OutputBaseFilename=VelocityShare-{#MyAppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=
DisableProgramGroupPage=yes
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Copy all published server files
Source: "..\publish\VelocityShare.Server\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
; Writable data directory
Name: "{commonappdata}\{#MyAppShortName}"; Permissions: everyone-full

[Icons]
Name: "{group}\{#MyAppShortName} Server"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--urls http://0.0.0.0:5000"
Name: "{group}\{#MyAppShortName} Web UI"; Filename: "http://localhost:5000"
Name: "{group}\Uninstall {#MyAppShortName}"; Filename: "{uninstallexe}"

[Run]
; Install and start the Windows Service
Filename: "sc.exe"; Parameters: "create {#MyServiceName} binPath= ""\""{app}\{#MyAppExeName}\"" --urls http://0.0.0.0:5000"" start= auto DisplayName= ""{#MyAppName} Server"""; Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "description {#MyServiceName} ""Secure peer-to-peer file transfer server with end-to-end encryption"""; Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "start {#MyServiceName}"; Flags: runhidden waituntilterminated
; Open firewall port
Filename: "netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#MyAppName} Server"" dir=in action=allow protocol=TCP localport=5000"; Flags: runhidden waituntilterminated
; Offer to open the web UI
Filename: "http://localhost:5000"; Description: "Open {#MyAppName} Web UI"; Flags: postinstall shellexec skipifsilent unchecked

[UninstallRun]
; Stop and remove the Windows Service
Filename: "sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden waituntilterminated
; Remove firewall rule
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#MyAppName} Server"""; Flags: runhidden waituntilterminated

[Code]
function IsServiceRunning(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('sc.exe', 'query {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Ensure service is stopped before removing files
    if IsServiceRunning() then
    begin
      Exec('sc.exe', 'stop {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(2000);
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  // If upgrading, stop the existing service first
  if IsServiceRunning() then
  begin
    Exec('sc.exe', 'stop {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1000);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    // Reconfigure service binPath to point to actual install directory
    Exec('sc.exe', 'config {#MyServiceName} binPath= "\"{app}\{#MyAppExeName}\" --urls http://0.0.0.0:5000"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
