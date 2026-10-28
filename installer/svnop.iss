; svnop インストーラ (Inno Setup)。installer\build.ps1 から AppVersion / SourceDir を渡してビルドする
; ユーザ単位 (管理者権限不要) で %LOCALAPPDATA%\Programs\svnop に入れる

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\app"
#endif

[Setup]
AppId={{8C1F2E6A-4B5D-4E2F-9A7C-3D1B6E0F2A11}
AppName=svnop
AppVersion={#AppVersion}
AppVerName=svnop {#AppVersion}
AppPublisher=svnop
DefaultDirName={userpf}\svnop
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=svnop-setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=svnop
UninstallDisplayIcon={app}\svnopw.exe
ChangesEnvironment=yes
CloseApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"

[CustomMessages]
en.TaskTsvn=Set up for TortoiseSVN (start the agent at logon and now)
ja.TaskTsvn=TortoiseSVN 向けに設定する (ログオン時と今すぐ agent を起動する)
en.TaskMsys2=Set up for msys2 svn (gpg-agent and pinentry)
ja.TaskMsys2=msys2 の svn 向けに設定する (gpg-agent と pinentry)
en.TaskPath=Add svnop to PATH
ja.TaskPath=PATH に svnop を追加する
en.VaultTitle=1Password vault
ja.VaultTitle=1Password のボールト
en.VaultDesc=Choose the vault for Subversion credentials.
ja.VaultDesc=Subversion の認証情報を入れるボールトを指定します。
en.VaultPrompt=Vault name (empty = op's default vault). Create the vault in 1Password beforehand.
ja.VaultPrompt=ボールト名 (空なら op の既定のボールト)。ボールトは事前に 1Password で作成してください。
en.NoRuntime=.NET 10 Desktop Runtime is not installed. svnop needs it.%n%nOpen the download page?
ja.NoRuntime=.NET 10 デスクトップ ランタイムが入っていません。svnop の実行に必要です。%n%nダウンロードページを開きますか?
en.NoOp=The 1Password CLI (op) was not found.%n%nInstall it with "winget install AgileBits.1Password.CLI" and turn on "Integrate with 1Password CLI" in the 1Password app (Settings > Developer). Then run "svnop setup tortoisesvn".
ja.NoOp=1Password CLI (op) が見つかりません。%n%n「winget install AgileBits.1Password.CLI」で導入し、1Password アプリの 設定 > 開発者 で「1Password CLI と連携」を有効にしてから、「svnop setup tortoisesvn」を実行してください。
en.SetupRunning=Configuring svnop...
ja.SetupRunning=svnop を設定しています...
en.OpenReadme=Open the README
ja.OpenReadme=README を開く
en.ReadmeFile=README.en.md
ja.ReadmeFile=README.md

[Tasks]
Name: "tsvn"; Description: "{cm:TaskTsvn}"
Name: "msys2"; Description: "{cm:TaskMsys2}"; Check: MsysExists; Flags: unchecked
Name: "path"; Description: "{cm:TaskPath}"

[Files]
; README / LICENSE は build.ps1 が SourceDir にコピーしている
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: path; Check: NeedsAddPath(ExpandConstant('{app}'))

[Run]
Filename: "{app}\svnop.exe"; Parameters: "setup tortoisesvn {code:VaultArg}"; StatusMsg: "{cm:SetupRunning}"; Flags: runhidden waituntilterminated; Tasks: tsvn
Filename: "{app}\svnop.exe"; Parameters: "setup msys2"; StatusMsg: "{cm:SetupRunning}"; Flags: runhidden waituntilterminated; Tasks: msys2
Filename: "{app}\{cm:ReadmeFile}"; Description: "{cm:OpenReadme}"; Flags: postinstall shellexec skipifsilent unchecked

[UninstallRun]
Filename: "{app}\svnop.exe"; Parameters: "setup --remove msys2"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveMsys2"; Check: MsysExists
Filename: "{app}\svnop.exe"; Parameters: "setup --remove tortoisesvn"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveTsvn"

[Code]
var
  VaultPage: TInputQueryWizardPage;

function MsysExists: Boolean;
begin
  Result := FileExists('C:\msys64\usr\bin\bash.exe');
end;

{ .NET 10 デスクトップ ランタイムの有無 (shared\Microsoft.WindowsDesktop.App\10.*) }
function HasDesktopRuntime: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

function FindOp: Boolean;
begin
  Result := FileExists(ExpandConstant('{localappdata}\Microsoft\WinGet\Links\op.exe'))
    or (FileSearch('op.exe', GetEnv('PATH')) <> '');
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not HasDesktopRuntime then
  begin
    if MsgBox(CustomMessage('NoRuntime'), mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
end;

{ 既存の config.json からボールト名を拾って初期値にする (無ければ SVN) }
function CurrentVault: String;
var
  S: AnsiString;
  P, Q: Integer;
  Text: String;
begin
  Result := 'SVN';
  if LoadStringFromFile(ExpandConstant('{userappdata}\svnop\config.json'), S) then
  begin
    Text := String(S);
    P := Pos('"vault": "', Text);
    if P > 0 then
    begin
      Text := Copy(Text, P + 10, MaxInt);
      Q := Pos('"', Text);
      if Q > 0 then
        Result := Copy(Text, 1, Q - 1);
    end;
  end;
end;

procedure InitializeWizard;
begin
  VaultPage := CreateInputQueryPage(wpSelectTasks, CustomMessage('VaultTitle'), CustomMessage('VaultDesc'), '');
  VaultPage.Add(CustomMessage('VaultPrompt'), False);
  VaultPage.Values[0] := CurrentVault;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = VaultPage.ID) and not WizardIsTaskSelected('tsvn');
end;

function VaultArg(Param: String): String;
begin
  if Trim(VaultPage.Values[0]) = '' then
    Result := ''
  else
    Result := '--vault "' + Trim(VaultPage.Values[0]) + '"';
end;

{ 上書きインストールの前に動いている agent を止める (exe を置き換えられるように) }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\svnop.exe')) then
  begin
    Exec(ExpandConstant('{app}\svnop.exe'), 'agent --stop', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('tsvn') and not FindOp then
    MsgBox(CustomMessage('NoOp'), mbInformation, MB_OK);
end;

{ PATH (HKCU\Environment) の操作 }
function NeedsAddPath(Dir: String): Boolean;
var
  OrigPath: String;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OrigPath) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(OrigPath) + ';') = 0;
end;

procedure RemovePath(Dir: String);
var
  OrigPath: String;
  P: Integer;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OrigPath) then
    exit;
  P := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(OrigPath) + ';');
  if P = 0 then
    exit;
  Delete(OrigPath, P - 1, Length(Dir) + 1);
  if (Length(OrigPath) > 0) and (OrigPath[1] = ';') then
    Delete(OrigPath, 1, 1);
  RegWriteExpandStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OrigPath);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemovePath(ExpandConstant('{app}'));
end;
