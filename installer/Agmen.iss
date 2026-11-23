#ifndef AppSource
  #error Supply /DAppSource=<published Agmen directory>
#endif
#ifndef AppVersion
  #error Supply /DAppVersion=<Agmen version>
#endif

[Setup]
AppId=Agmen
AppName=Agmen
AppVersion={#AppVersion}
AppPublisher=Agmen
AppPublisherURL=https://github.com/cxbxmxcx/agmen
DefaultDirName={localappdata}\Programs\Agmen
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
AllowNoIcons=yes
OutputBaseFilename=Agmen-Setup-x64
UninstallDisplayIcon={app}\Agmen.exe
VersionInfoVersion={#AppVersion}
WizardStyle=modern
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "startup"; Description: "Start Agmen when I sign in"

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Agmen"; Filename: "{app}\Agmen.exe"; Parameters: "--workspace"; Check: not WizardNoIcons
Name: "{userprograms}\Uninstall Agmen"; Filename: "{uninstallexe}"; Check: not WizardNoIcons
Name: "{userstartup}\Agmen"; Filename: "{app}\Agmen.exe"; Tasks: startup

[Run]
Filename: "{app}\Agmen.exe"; Parameters: "--workspace"; Description: "Open Agmen"; Flags: nowait postinstall skipifsilent

[Code]
function AppInUse(): Boolean;
var
  Stream: TFileStream;
  FileName: String;
  Attempt: Integer;
begin
  Result := False;
  FileName := ExpandConstant('{app}\Agmen.exe');
  if not FileExists(FileName) then Exit;
  // A process or scanner can briefly retain the executable after the last workspace exits.
  for Attempt := 1 to 13 do begin
    try
      Stream := TFileStream.Create(FileName, fmOpenReadWrite or fmShareDenyNone);
      Stream.Free;
      Exit;
    except
      if (Attempt = 1) or (Attempt = 13) then Log('Agmen executable write probe: ' + GetExceptionMessage());
    end;
    if Attempt < 13 then Sleep(250);
  end;
  Result := True;
end;

function InUseMessage(): String;
begin
  Result := 'Agmen.exe is in use or read-only in ' + ExpandConstant('{app}') + '.' + #13#10 +
    'Finish your work, exit each workspace running from this installation using its tray menu, and try again.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if AppInUse() then Result := InUseMessage();
end;

function InitializeUninstall(): Boolean;
var
  Status: Integer;
begin
  Result := not AppInUse();
  if not Result then begin
    SuppressibleMsgBox(InUseMessage(), mbError, MB_OK, IDOK);
    Exit;
  end;
  if ExpandConstant('{param:KEEPAGENTCONFIG|0}') = '1' then Exit;
  if Exec(ExpandConstant('{app}\Agmen.exe'), '--check-uninstall-connections', ExpandConstant('{app}'),
          SW_HIDE, ewWaitUntilTerminated, Status) and (Status = 12) then begin
    SuppressibleMsgBox('Kimi Code or Antigravity still points to this copy. Open Connect agents in Agmen, disconnect it, then exit all its workspaces and uninstall again.',
      mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  LinkPath: String;
  Shell, Link: Variant;
begin
  if (CurStep <> ssPostInstall) or WizardIsTaskSelected('startup') then Exit;
  LinkPath := ExpandConstant('{userstartup}\Agmen.lnk');
  if not FileExists(LinkPath) then Exit;
  Shell := CreateOleObject('WScript.Shell');
  Link := Shell.CreateShortcut(LinkPath);
  if CompareText(Link.TargetPath, ExpandConstant('{app}\Agmen.exe')) = 0 then DeleteFile(LinkPath);
end;
