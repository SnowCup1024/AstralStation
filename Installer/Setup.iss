#ifndef FileListPath
  #error FileListPath is required
#endif
#ifndef OutputPath
  #error OutputPath is required
#endif
#ifndef DisplayVersion
  #error DisplayVersion is required
#endif
#ifndef FileVersion
  #error FileVersion is required
#endif
#ifndef AppIcon
  #error AppIcon is required
#endif

[Setup]
AppId=AstralStation
AppName=星间驿站
AppVersion=Alpha {#DisplayVersion}
AppVerName=星间驿站 Alpha {#DisplayVersion}
VersionInfoVersion={#FileVersion}
DefaultDirName={code:InitialDirectory}
UsePreviousAppDir=yes
DisableDirPage=no
DisableWelcomePage=no
DisableReadyPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
AllowRootDirectory=no
AllowUNCPath=no
AppendDefaultDirName=no
OutputDir={#OutputPath}
OutputBaseFilename=AstralStation-Alpha-{#DisplayVersion}-Setup
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\AstralStation.exe
UninstallDisplayName=星间驿站
WizardStyle=modern
Compression=lzma2/normal
SolidCompression=no
CloseApplications=yes
RestartApplications=no
AppMutex=Local\AstralStation.SteamLaunch.4732690
SetupMutex=Local\AstralStation.Setup

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

#include FileListPath

[Registry]
Root: HKCU; Subkey: "Software\AstralStation"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\AstralStation"; ValueType: string; ValueName: "InstallVersion"; ValueData: "{#DisplayVersion}"; Flags: uninsdeletevalue

[Icons]
Name: "{userprograms}\星间驿站\星间驿站"; Filename: "{app}\AstralStation.exe"
Name: "{userprograms}\星间驿站\卸载星间驿站"; Filename: "{uninstallexe}"

[Code]
var
  PreviousDirectory: String;
  DeleteUserData: Boolean;
  ClosingProcesses: array of THandle;
  CloseRequestFailed: Boolean;
  InstallMode, PreviousVersion: String;
  CheckedFiles, ChangedFiles: TStringList;

function NeedsFile(Relative, Expected: String): Boolean;
var
  Filename: String;
begin
  Filename := ExpandConstant('{app}\') + Relative;
  if CheckedFiles.IndexOf(Filename) >= 0 then begin
    Result := ChangedFiles.IndexOf(Filename) >= 0;
    Exit;
  end;
  Result := True;
  if FileExists(Filename) then
    try
      Result := CompareText(GetSHA256OfFile(Filename), Expected) <> 0;
    except
      Log('Unable to compare file; let the installer repair it: ' + Filename);
    end;
  CheckedFiles.Add(Filename);
  if Result then begin ChangedFiles.Add(Filename); Log('Install changed or missing file: ' + Relative); end
  else Log('Keep identical file: ' + Relative);
end;

function GetFileAttributes(Name: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function EnumWindows(Callback, Param: NativeInt): BOOL;
  external 'EnumWindows@user32.dll stdcall';
function GetProp(Window: HWND; Name: String): THandle;
  external 'GetPropW@user32.dll stdcall';
function GetWindowThreadProcessId(Window: HWND; var ProcessId: Cardinal): Cardinal;
  external 'GetWindowThreadProcessId@user32.dll stdcall';
function OpenProcess(Access: Cardinal; Inherit: BOOL; ProcessId: Cardinal): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function QueryFullProcessImageName(Process: THandle; Flags: Cardinal; Path: String; var Size: Cardinal): BOOL;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

function RequestWindowExit(Window: HWND; Param: NativeInt): BOOL;
var
  ProcessId, Size: Cardinal;
  Process: THandle;
  Path: String;
  Count: Integer;
begin
  Result := True;
  if GetProp(Window, 'AstralStation.UpgradeProtocol') = 0 then Exit;
  GetWindowThreadProcessId(Window, ProcessId);
  Process := OpenProcess($1000 or $100000, False, ProcessId);
  if Process = 0 then Exit;
  Size := 32768;
  SetLength(Path, Size);
  if QueryFullProcessImageName(Process, 0, Path, Size) then begin
    SetLength(Path, Size);
    if CompareText(Path, ExpandConstant('{app}\AstralStation.exe')) = 0 then begin
      { The existing upgrade protocol saves running tasks and bypasses close-to-tray. }
      if not PostMessage(Window, RegisterWindowMessage('AstralStation.Upgrade'), 0, 0) then
        CloseRequestFailed := True;
      Count := GetArrayLength(ClosingProcesses);
      SetArrayLength(ClosingProcesses, Count + 1);
      ClosingProcesses[Count] := Process;
      Exit;
    end;
  end;
  CloseHandle(Process);
end;

function PrepareApplication(Uninstalling: Boolean): String;
var
  I, Code: Integer;
  Command: String;
begin
  Result := '';
  if CheckForMutexes('Local\AstralStation.SteamLaunch.4732690') then begin
    Result := 'Steam 等待进程仍在运行，请结束游戏或取消启动等待后重试。';
    Exit;
  end;
  CloseRequestFailed := False;
  SetArrayLength(ClosingProcesses, 0);
  try
    if not EnumWindows(CreateCallback(@RequestWindowExit), 0) then CloseRequestFailed := True;
    for I := 0 to GetArrayLength(ClosingProcesses) - 1 do
      if WaitForSingleObject(ClosingProcesses[I], 10000) <> 0 then CloseRequestFailed := True;
  finally
    for I := 0 to GetArrayLength(ClosingProcesses) - 1 do CloseHandle(ClosingProcesses[I]);
    SetArrayLength(ClosingProcesses, 0);
  end;
  if CloseRequestFailed then begin
    Result := '星间驿站尚未退出。请完成或关闭程序内的弹窗，检查记录保存错误后重试；不会强制结束游戏或程序。';
    Exit;
  end;
  if Uninstalling then Command := '--installer-uninstall' else Command := '--installer-prepare';
  if FileExists(ExpandConstant('{app}\AstralStation.exe')) then
    if not Exec(ExpandConstant('{app}\AstralStation.exe'), Command,
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      Result := '后台尚未安全退出，请检查程序权限和记录保存情况后重试。';
end;

function InitialDirectory(Param: String): String;
var
  Registered: String;
begin
  Result := '';
  { The standard AppId registration also covers a missing executable needing repair. }
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\AstralStation_is1', 'InstallLocation', Registered) then
    if DirExists(Registered) then begin Result := Registered; Exit; end;
  if RegQueryStringValue(HKCU, 'Software\AstralStation', 'InstallDir', Result) then
    if not FileExists(AddBackslash(Result) + 'AstralStation.exe') and
      not FileExists(AddBackslash(Result) + 'unins000.exe') then Result := '';
end;

function SameDirectory(Left, Right: String): Boolean;
begin
  Result := (Left <> '') and (Right <> '') and
    (CompareText(RemoveBackslashUnlessRoot(ExpandFileName(Left)), RemoveBackslashUnlessRoot(ExpandFileName(Right))) = 0);
end;

procedure DetectInstallMode(Directory: String);
var
  Registered: String;
  Installed, Current: Int64;
begin
  InstallMode := '全新安装'; PreviousVersion := '';
  if Directory = '' then Exit;
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\AstralStation_is1', 'InstallLocation', Registered) and SameDirectory(Directory, Registered) then
    RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\AstralStation_is1', 'DisplayVersion', PreviousVersion);
  if (PreviousVersion = '') and RegQueryStringValue(HKCU, 'Software\AstralStation', 'InstallDir', Registered) and SameDirectory(Directory, Registered) then
    RegQueryStringValue(HKCU, 'Software\AstralStation', 'InstallVersion', PreviousVersion);
  if Pos('Alpha ', PreviousVersion) = 1 then Delete(PreviousVersion, 1, 6);
  if PreviousVersion <> '' then begin
    if StrToVersion(PreviousVersion, Installed) and StrToVersion('{#DisplayVersion}', Current) then begin
      if ComparePackedVersion(Installed, Current) < 0 then InstallMode := '更新'
      else if ComparePackedVersion(Installed, Current) = 0 then InstallMode := '修复安装'
      else InstallMode := '禁止降级';
    end else InstallMode := '修复现有安装';
  end else if SameDirectory(Directory, PreviousDirectory) or
    (FileExists(AddBackslash(Directory) + 'AstralStation.exe') and FileExists(AddBackslash(Directory) + 'unins000.exe')) then
    InstallMode := '修复现有安装';
end;

function InstallDescription: String;
begin
  Result := InstallMode;
  if PreviousVersion <> '' then Result := Result + '：Alpha ' + PreviousVersion + ' → Alpha {#DisplayVersion}';
  Result := Result + #13#10 + '只安装新增、内容有变化或需要修复的文件，相同文件保持原样。';
end;

function InitializeSetup: Boolean;
begin
  Result := not WizardSilent;
  if not Result then
    MsgBox('请交互式运行安装器，选择安装目录并确认后安装。', mbError, MB_OK);
end;

procedure DirectoryChanged(Sender: TObject);
begin
  CheckedFiles.Clear; ChangedFiles.Clear;
  if WizardForm.CurPageID = wpSelectDir then
    WizardForm.NextButton.Enabled := Trim(WizardForm.DirEdit.Text) <> '';
end;

procedure InitializeWizard;
begin
  CheckedFiles := TStringList.Create; CheckedFiles.Sorted := True;
  ChangedFiles := TStringList.Create; ChangedFiles.Sorted := True;
  PreviousDirectory := InitialDirectory('');
  DetectInstallMode(PreviousDirectory);
  { Never use /DIR or a framework fallback as a first-install selection. }
  WizardForm.DirEdit.Text := PreviousDirectory;
  WizardForm.DirEdit.OnChange := @DirectoryChanged;
  WizardForm.WelcomeLabel2.Caption := InstallDescription + #13#10#13#10 + WizardForm.WelcomeLabel2.Caption;
end;

procedure DeinitializeSetup;
begin
  if CheckedFiles <> nil then CheckedFiles.Free;
  if ChangedFiles <> nil then ChangedFiles.Free;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectDir then DirectoryChanged(nil);
  if CurPageID = wpReady then begin
    if InstallMode = '更新' then WizardForm.NextButton.Caption := '更新(&U)'
    else if Pos('修复', InstallMode) = 1 then WizardForm.NextButton.Caption := '修复(&R)';
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := InstallDescription + NewLine + NewLine + MemoDirInfo + NewLine + MemoGroupInfo + MemoTasksInfo;
end;

function InsideOrEqual(Path, Root: String): Boolean;
begin
  Path := AddBackslash(ExpandFileName(Path));
  Root := AddBackslash(ExpandFileName(Root));
  Result := CompareText(Copy(Path, 1, Length(Root)), Root) = 0;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Selected: String;
begin
  Result := True;
  if CurPageID <> wpSelectDir then Exit;
  Selected := Trim(WizardForm.DirEdit.Text);
  if Selected = '' then begin
    MsgBox('请手动选择安装目录。', mbError, MB_OK);
    Result := False;
  end else if InsideOrEqual(Selected, ExpandConstant('{localappdata}\AstralStation')) or
    InsideOrEqual(ExpandConstant('{localappdata}\AstralStation'), Selected) then begin
    MsgBox('安装目录不能覆盖本地数据目录或其上级目录，请选择单独的程序文件夹。', mbError, MB_OK);
    Result := False;
  end else if (PreviousDirectory <> '') and
    (CompareText(RemoveBackslashUnlessRoot(ExpandFileName(Selected)),
      RemoveBackslashUnlessRoot(ExpandFileName(PreviousDirectory))) <> 0) then begin
    MsgBox('更新请使用已有安装目录。若要更换位置，请先卸载并保留数据，再重新安装。', mbInformation, MB_OK);
    Result := False;
  end;
  if Result then begin
    DetectInstallMode(Selected);
    if InstallMode = '禁止降级' then begin
      MsgBox('已安装 Alpha ' + PreviousVersion + '，不能使用旧版安装器覆盖新版。', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  LegacyDirectory: String;
begin
  Result := '';
  DetectInstallMode(ExpandConstant('{app}'));
  if InstallMode = '禁止降级' then begin Result := '检测到更新版本，已停止安装以避免降级。'; Exit; end;
  if RegQueryStringValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\SnowCup1024.AstralStation',
    'InstallLocation', LegacyDirectory) and
    FileExists(AddBackslash(LegacyDirectory) + 'current\AstralStation.exe') then begin
    Result := '请先卸载旧版一键安装程序，并保留数据，然后再安装此版本。';
    Exit;
  end;
  Result := PrepareApplication(False);
  { Recheck after the application has completed its shutdown/handoff. }
  CheckedFiles.Clear; ChangedFiles.Clear;
end;

function ChooseUninstallData: Boolean;
var
  Form: TSetupForm;
  Heading, Details, KeepDetails, DeleteDetails: TNewStaticText;
  KeepRadio, DeleteRadio: TNewRadioButton;
  ConfirmButton, CancelButton: TNewButton;
begin
  DeleteUserData := False;
  Form := CreateCustomForm(ScaleX(510), ScaleY(285), False, False);
  try
    Form.Caption := '卸载星间驿站 · 本地数据';
    Heading := TNewStaticText.Create(Form);
    Heading.Parent := Form;
    Heading.SetBounds(ScaleX(20), ScaleY(16), ScaleX(470), ScaleY(26));
    Heading.Caption := '卸载后如何处理本地数据？';
    Heading.Font.Style := [fsBold];
    Details := TNewStaticText.Create(Form);
    Details.Parent := Form;
    Details.SetBounds(ScaleX(20), ScaleY(48), ScaleX(470), ScaleY(40));
    Details.AutoSize := False; Details.WordWrap := True;
    Details.Caption := '默认数据目录：' + #13#10 + ExpandConstant('{localappdata}\AstralStation');
    KeepRadio := TNewRadioButton.Create(Form);
    KeepRadio.Parent := Form;
    KeepRadio.SetBounds(ScaleX(20), ScaleY(98), ScaleX(470), ScaleY(24));
    KeepRadio.Caption := '保留数据（推荐）'; KeepRadio.Checked := True;
    KeepDetails := TNewStaticText.Create(Form);
    KeepDetails.Parent := Form;
    KeepDetails.SetBounds(ScaleX(40), ScaleY(125), ScaleX(450), ScaleY(28));
    KeepDetails.Caption := '保留任务、设置、运行历史、配置备份和自定义启动脚本。';
    DeleteRadio := TNewRadioButton.Create(Form);
    DeleteRadio.Parent := Form;
    DeleteRadio.SetBounds(ScaleX(20), ScaleY(160), ScaleX(470), ScaleY(24));
    DeleteRadio.Caption := '同时删除本地数据';
    DeleteDetails := TNewStaticText.Create(Form);
    DeleteDetails.Parent := Form;
    DeleteDetails.SetBounds(ScaleX(40), ScaleY(187), ScaleX(450), ScaleY(36));
    DeleteDetails.AutoSize := False; DeleteDetails.WordWrap := True;
    DeleteDetails.Caption := '删除上述默认目录中的全部数据，无法恢复。自定义数据目录保留，游戏与 Steam 文件不会删除。';
    ConfirmButton := TNewButton.Create(Form);
    ConfirmButton.Parent := Form;
    ConfirmButton.SetBounds(ScaleX(304), ScaleY(244), ScaleX(90), ScaleY(28));
    ConfirmButton.Caption := '继续卸载'; ConfirmButton.ModalResult := mrOk;
    ConfirmButton.Default := True;
    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form;
    CancelButton.SetBounds(ScaleX(402), ScaleY(244), ScaleX(88), ScaleY(28));
    CancelButton.Caption := '取消'; CancelButton.ModalResult := mrCancel; CancelButton.Cancel := True;
    Form.ActiveControl := KeepRadio;
    Result := Form.ShowModal = mrOk;
    if Result and DeleteRadio.Checked then begin
      Result := MsgBox('确认永久删除默认目录中的任务、设置、历史、备份和自定义脚本？',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
      DeleteUserData := Result;
    end;
  finally
    Form.Free;
  end;
end;

function InitializeUninstall: Boolean;
var
  Error: String;
begin
  { Silent uninstall can never opt in to data deletion. }
  DeleteUserData := False;
  if UninstallSilent then Result := True
  else Result := ChooseUninstallData;
  if Result then begin
    Error := PrepareApplication(True);
    Result := Error = '';
    if not Result then MsgBox(Error, mbError, MB_OK);
  end;
end;

function ContainsLink(const Root: String): Boolean;
var
  Entry: TFindRec;
  Child: String;
begin
  Result := (GetFileAttributes(Root) and $400) <> 0;
  if Result then Exit;
  if FindFirst(AddBackslash(Root) + '*', Entry) then
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
          Child := AddBackslash(Root) + Entry.Name;
          if (Entry.Attributes and $400) <> 0 then Result := True
          else if (Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then Result := ContainsLink(Child);
          if Result then Exit;
        end;
      until not FindNext(Entry);
    finally FindClose(Entry); end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDirectory: String;
begin
  if (CurUninstallStep <> usPostUninstall) or not DeleteUserData then Exit;
  DataDirectory := ExpandConstant('{localappdata}\AstralStation');
  if not DirExists(DataDirectory) then Exit;
  if ContainsLink(DataDirectory) then begin
    MsgBox('数据目录含有符号链接，已保留数据，请手动处理。', mbInformation, MB_OK);
    Exit;
  end;
  if not DelTree(DataDirectory, True, True, True) then
    MsgBox('部分数据仍被占用，已保留未删除内容，请手动检查。', mbInformation, MB_OK);
end;
