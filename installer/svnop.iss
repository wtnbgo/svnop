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
en.VaultNote=During setup, 1Password will ask you to approve access by svnop (once or twice: to check the vault, and for the first sync by the agent). Please approve it in the 1Password window.
ja.VaultNote=設定の途中で、svnop からのアクセスを許可するか確認する 1Password の承認画面が 1〜2 回表示されます (ボールトの確認と、agent の最初の同期のため)。1Password の画面で承認してください。
en.VaultPrompt=Vault name (empty = op's default vault). Create the vault in 1Password beforehand.
ja.VaultPrompt=ボールト名 (空なら op の既定のボールト)。ボールトは事前に 1Password で作成してください。
en.NoRuntime=.NET 10 Desktop Runtime is not installed. svnop needs it.%n%nOpen the download page?
ja.NoRuntime=.NET 10 デスクトップ ランタイムが入っていません。svnop の実行に必要です。%n%nダウンロードページを開きますか?
en.NoOp=The 1Password CLI (op) was not found.%n%nInstall it with "winget install AgileBits.1Password.CLI" and turn on "Integrate with 1Password CLI" in the 1Password app (Settings > Developer). Then run "svnop setup tortoisesvn".
ja.NoOp=1Password CLI (op) が見つかりません。%n%n「winget install AgileBits.1Password.CLI」で導入し、1Password アプリの 設定 > 開発者 で「1Password CLI と連携」を有効にしてから、「svnop setup tortoisesvn」を実行してください。
en.CacheTitle=Credential cache
ja.CacheTitle=認証キャッシュ
en.CacheDesc=Save passwords in TortoiseSVN's credential cache (%APPDATA%\Subversion)?
ja.CacheDesc=TortoiseSVN の認証キャッシュ (%APPDATA%\Subversion) にパスワードを保存しますか?
en.CacheNote=TortoiseSVN's command-line svn.exe cannot be auto-filled, so it can only be used without typing passwords when they are saved in the cache. msys2 svn works either way. You can change this later from Settings in the tray menu.
ja.CacheNote=TortoiseSVN のコマンドライン版 (svn.exe) は自動入力できないため、パスワード入力なしで使うにはキャッシュへの保存が必要です。msys2 の svn はどちらでも動きます。あとでトレイメニューの「設定」から変更できます。
en.CacheKeep=Save (recommended) - TortoiseSVN and svn.exe authenticate from the cache; missing entries are auto-filled and saved
ja.CacheKeep=保存する (おすすめ) - TortoiseSVN も svn.exe もキャッシュで認証。キャッシュに無いときは自動入力して保存
en.CacheNone=Do not save - no passwords on disk; TortoiseSVN is auto-filled every time, svn.exe asks every time
ja.CacheNone=保存しない - ディスクにパスワードを残さない。TortoiseSVN は毎回自動入力、svn.exe は毎回入力が必要
en.SyncTitle=Sync at logon
ja.SyncTitle=ログオン時の同期
en.SyncDesc=Keep the credential cache up to date with 1Password.
ja.SyncDesc=認証キャッシュを 1Password の内容に揃えます。
en.SyncOption=Sync the cache from 1Password at logon (password changes in 1Password are applied automatically)
ja.SyncOption=ログオン時に 1Password からキャッシュを同期する (1Password で変えたパスワードが自動で反映される)
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
Filename: "{app}\svnop.exe"; Parameters: "setup tortoisesvn {code:VaultArg} --cache {code:CacheArg} --sync-on-start {code:SyncArg} --language {language}"; StatusMsg: "{cm:SetupRunning}"; Flags: runhidden waituntilterminated; Tasks: tsvn
Filename: "{app}\svnop.exe"; Parameters: "setup msys2"; StatusMsg: "{cm:SetupRunning}"; Flags: runhidden waituntilterminated; Tasks: msys2
Filename: "{app}\{cm:ReadmeFile}"; Description: "{cm:OpenReadme}"; Flags: postinstall shellexec skipifsilent unchecked

[UninstallRun]
Filename: "{app}\svnop.exe"; Parameters: "setup --remove msys2"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveMsys2"; Check: MsysExists
Filename: "{app}\svnop.exe"; Parameters: "setup --remove tortoisesvn"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveTsvn"

[Code]
var
  VaultPage: TInputQueryWizardPage;
  CachePage: TInputOptionWizardPage;
  SyncPage: TInputOptionWizardPage;

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

{ 既存の config.json から文字列の設定値を拾う (無ければ Default) }
function ConfigValue(Key, Default: String): String;
var
  S: AnsiString;
  P, Q: Integer;
  Text: String;
begin
  Result := Default;
  if LoadStringFromFile(ExpandConstant('{userappdata}\svnop\config.json'), S) then
  begin
    Text := String(S);
    P := Pos('"' + Key + '": ', Text);
    if P > 0 then
    begin
      Text := Copy(Text, P + Length(Key) + 4, MaxInt);
      if Copy(Text, 1, 1) = '"' then
      begin
        Text := Copy(Text, 2, MaxInt);
        Q := Pos('"', Text);
      end
      else
        Q := Pos(',', Text);
      if Q > 0 then
        Result := Trim(Copy(Text, 1, Q - 1));
    end;
  end;
end;

procedure InitializeWizard;
begin
  VaultPage := CreateInputQueryPage(wpSelectTasks, CustomMessage('VaultTitle'), CustomMessage('VaultDesc'), CustomMessage('VaultNote'));
  VaultPage.Add(CustomMessage('VaultPrompt'), False);
  VaultPage.Values[0] := ConfigValue('vault', 'SVN');

  CachePage := CreateInputOptionPage(VaultPage.ID, CustomMessage('CacheTitle'), CustomMessage('CacheDesc'),
    CustomMessage('CacheNote'), True, False);
  CachePage.Add(CustomMessage('CacheKeep'));
  CachePage.Add(CustomMessage('CacheNone'));
  if ConfigValue('cacheMode', 'keep') = 'none' then
    CachePage.SelectedValueIndex := 1
  else
    CachePage.SelectedValueIndex := 0;

  SyncPage := CreateInputOptionPage(CachePage.ID, CustomMessage('SyncTitle'), CustomMessage('SyncDesc'), '', False, False);
  SyncPage.Add(CustomMessage('SyncOption'));
  SyncPage.Values[0] := ConfigValue('syncOnStart', 'true') <> 'false';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if (PageID = VaultPage.ID) or (PageID = CachePage.ID) or (PageID = SyncPage.ID) then
    Result := not WizardIsTaskSelected('tsvn');
  { 保存しない方針なら同期の設定は無意味 }
  if (PageID = SyncPage.ID) and (CachePage.SelectedValueIndex = 1) then
    Result := True;
end;

function CacheArg(Param: String): String;
begin
  if CachePage.SelectedValueIndex = 1 then
    Result := 'none'
  else
    Result := 'keep';
end;

function SyncArg(Param: String): String;
begin
  if SyncPage.Values[0] then
    Result := 'true'
  else
    Result := 'false';
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
