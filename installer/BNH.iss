; BrentNET Client Installer
; Built for BrentCorp / BrentNET alpha testing

#define MyAppName "BrentNET Client"
#define MyAppVersion "0.2.11"
#define MyAppPublisher "BrentCorp Holdings Inc."
#define MyAppURL "https://brentnet.net"
#define MyAppExeName "BrentNETHelper.exe"
#define MyAppProcessName "BrentNETHelper.exe"
#define MyAppTaskName "BrentNET Client"
#define MyLegacyTaskName "BrentNET Helper"

[Setup]
AppId={{4CF15B87-FDEF-4610-9522-56347F57616F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

DefaultDirName={autopf}\BrentNET Client
DefaultGroupName={#MyAppName}

UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin

OutputDir={#SourcePath}..\dist\windows
OutputBaseFilename=BrentNETClientSetup-v0.2.11

Compression=lzma2
SolidCompression=yes
WizardStyle=modern

CloseApplications=yes
RestartApplications=no

SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; \
    Description: "{cm:CreateDesktopIcon}"; \
    GroupDescription: "{cm:AdditionalIcons}"; \
    Flags: unchecked

Name: "autostart"; \
    Description: "Start BrentNET Client when Windows starts"; \
    GroupDescription: "Startup options:"

[Files]
Source: "{#SourcePath}..\helper\bin\Release\net8.0-windows\win-x64\publish\{#MyAppExeName}"; \
    DestDir: "{app}"; \
    Flags: ignoreversion restartreplace

Source: "{#SourcePath}..\helper\routes.json"; \
    DestDir: "{app}"; \
    Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; \
    Filename: "{app}\{#MyAppExeName}"

Name: "{autodesktop}\{#MyAppName}"; \
    Filename: "{app}\{#MyAppExeName}"; \
    Tasks: desktopicon

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
    Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ""$ErrorActionPreference='SilentlyContinue'; Unregister-ScheduledTask -TaskName '{#MyLegacyTaskName}' -Confirm:$false; Unregister-ScheduledTask -TaskName '{#MyAppTaskName}' -Confirm:$false; $exe='{app}\{#MyAppExeName}'; $action=New-ScheduledTaskAction -Execute $exe; $trigger=New-ScheduledTaskTrigger -AtLogOn; $principal=New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest; Register-ScheduledTask -TaskName '{#MyAppTaskName}' -Action $action -Trigger $trigger -Principal $principal -Force"""; \
    Flags: runhidden waituntilterminated; \
    Tasks: autostart

Filename: "{app}\{#MyAppExeName}"; \
    Description: "Launch BrentNET Client"; \
    Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
    Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ""Unregister-ScheduledTask -TaskName '{#MyAppTaskName}' -Confirm:$false -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName '{#MyLegacyTaskName}' -Confirm:$false -ErrorAction SilentlyContinue"""; \
    Flags: runhidden waituntilterminated; \
    RunOnceId: "RemoveBrentNETScheduledTasks"

[Code]
function IsProcessRunning(const ProcessName: String): Boolean;
var
  ResultCode: Integer;
begin
  Result :=
    Exec(
      ExpandConstant('{cmd}'),
      '/C tasklist /FI "IMAGENAME eq ' + ProcessName +
      '" 2>NUL | find /I "' + ProcessName + '" >NUL',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode
    ) and (ResultCode = 0);
end;

procedure StopBrentNETClient();
var
  ResultCode: Integer;
  Attempts: Integer;
begin
  if not IsProcessRunning('{#MyAppProcessName}') then
  begin
    Exit;
  end;

  Log('A running BrentNET Client instance was detected.');

  Exec(
    ExpandConstant('{cmd}'),
    '/C taskkill /IM "{#MyAppProcessName}" /T',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode
  );

  Attempts := 0;

  while IsProcessRunning('{#MyAppProcessName}') and (Attempts < 10) do
  begin
    Sleep(500);
    Attempts := Attempts + 1;
  end;

  if IsProcessRunning('{#MyAppProcessName}') then
  begin
    Log('BrentNET Client did not close gracefully. Forcing termination.');

    Exec(
      ExpandConstant('{cmd}'),
      '/C taskkill /F /IM "{#MyAppProcessName}" /T',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode
    );

    Sleep(1000);
  end;

  if IsProcessRunning('{#MyAppProcessName}') then
  begin
    MsgBox(
      'BrentNET Client is still running and could not be closed.' + #13#10 + #13#10 +
      'Please close it manually, then run the installer again.',
      mbError,
      MB_OK
    );

    Abort;
  end;

  Log('BrentNET Client was closed successfully.');
end;

procedure RemoveLegacyShortcuts();
begin
  DeleteFile(
    ExpandConstant('{autoprograms}\BrentNET Helper.lnk')
  );

  DeleteFile(
    ExpandConstant('{autodesktop}\BrentNET Helper.lnk')
  );
end;

procedure RemoveExistingScheduledTasks();
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ' +
    '""Unregister-ScheduledTask -TaskName ''{#MyLegacyTaskName}'' ' +
    '-Confirm:$false -ErrorAction SilentlyContinue; ' +
    'Unregister-ScheduledTask -TaskName ''{#MyAppTaskName}'' ' +
    '-Confirm:$false -ErrorAction SilentlyContinue""',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode
  );
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';

  StopBrentNETClient();
  RemoveExistingScheduledTasks();
  RemoveLegacyShortcuts();
end;