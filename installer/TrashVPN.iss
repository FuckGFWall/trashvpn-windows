#ifndef Payload
  #error Payload missing. Run build-installer.ps1.
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SignedCache
  #error SignedCache missing. Run build-installer.ps1.
#endif

[Setup]
AppId={{40907411-8A11-4539-B4BA-E77645B7A41B}
AppName=TrashVPN
AppVersion={#AppVersion}
AppPublisher=TrashVPN
AppPublisherURL=https://github.com/FuckGFWall/trashvpn-windows
DefaultDirName={autopf}\trashvpn-windows
DefaultGroupName=TrashVPN
DisableDirPage=no
DisableProgramGroupPage=yes
DirExistsWarning=no
AllowNetworkDrive=no
AllowUNCPath=no
SetupIconFile=SetupIcon.ico
UninstallDisplayIcon={app}\SetupIcon.ico
OutputBaseFilename=TrashVPN-{#AppVersion}-Windows-x64-Signed-Setup
SignTool=trashvpn
SignedUninstaller=yes
SignedUninstallerDir={#SignedCache}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1607 (including LTSB) and later, including Windows 11.
MinVersion=10.0.14393
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=no
AppMutex=Local\Xehn.Fluent.Desktop
CloseApplications=no
RestartApplications=no
UsedUserAreasWarning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "chinesetraditional"; MessagesFile: "Languages\ChineseTraditional.isl"
Name: "farsi"; MessagesFile: "Languages\Farsi.isl"

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Excludes: "\data\*,*.pdb"; Flags: ignoreversion recursesubdirs
Source: "{#Payload}\data\core\mihomo.exe"; DestDir: "{app}\seed\core"; Flags: ignoreversion
Source: "{#Payload}\data\core\geoip.metadb"; DestDir: "{app}\seed\core"; Flags: ignoreversion
Source: "{#Payload}\data\core\geosite.dat"; DestDir: "{app}\seed\core"; Flags: ignoreversion
Source: "installed.mode"; DestDir: "{app}"; Flags: ignoreversion
Source: "SetupIcon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Icons]
Name: "{group}\TrashVPN"; Filename: "{app}\Xehn.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\TrashVPN"; Filename: "{app}\Xehn.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Xehn.App.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchTrashVPN}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[CustomMessages]
english.LaunchTrashVPN=Run TrashVPN after closing Setup
english.OwnInstallFolder=Choose an empty local folder dedicated to TrashVPN, or the folder of an existing TrashVPN installation. Links and junctions are not allowed.
english.PurgeConfirm=Permanently remove the TrashVPN installation and all TrashVPN settings, logs, and cached data from this computer?
english.PurgeFailed=Some TrashVPN files could not be removed. Close any other running instances and check these folders:
chinesesimplified.LaunchTrashVPN=关闭安装程序后运行 TrashVPN
chinesesimplified.OwnInstallFolder=请选择一个专用于 TrashVPN 的本地空文件夹，或已有的 TrashVPN 安装文件夹。不能使用链接或目录联接。
chinesesimplified.PurgeConfirm=确定要永久删除此计算机上的 TrashVPN 安装，以及全部 TrashVPN 设置、日志和缓存吗？
chinesesimplified.PurgeFailed=部分 TrashVPN 文件未能删除。请关闭其他运行中的副本，并检查这些文件夹：
chinesetraditional.LaunchTrashVPN=關閉安裝程式後執行 TrashVPN
chinesetraditional.OwnInstallFolder=請選擇專用於 TrashVPN 的本機空資料夾，或現有的 TrashVPN 安裝資料夾。不能使用連結或目錄連接。
chinesetraditional.PurgeConfirm=確定要永久刪除此電腦上的 TrashVPN 安裝，以及所有 TrashVPN 設定、記錄與快取嗎？
chinesetraditional.PurgeFailed=部分 TrashVPN 檔案未能刪除。請關閉其他執行中的副本，並檢查下列資料夾：
farsi.LaunchTrashVPN=پس از بستن نصب‌کننده، TrashVPN اجرا شود
farsi.OwnInstallFolder=یک پوشهٔ محلی خالی مخصوص TrashVPN یا پوشهٔ نصب فعلی آن را انتخاب کنید. پیوند و اتصال پوشه مجاز نیست.
farsi.PurgeConfirm=نصب TrashVPN و همهٔ تنظیمات، گزارش‌ها و داده‌های ذخیره‌شدهٔ TrashVPN برای همیشه از این رایانه حذف شوند؟
farsi.PurgeFailed=برخی فایل‌های TrashVPN حذف نشدند. دیگر نسخه‌های در حال اجرا را ببندید و این پوشه‌ها را بررسی کنید:

[Code]
const
  ReparseAttribute = $400;
  DirectoryAttribute = $10;
  InvalidAttributes = $FFFFFFFF;
  OwnerId = 'TrashVPN.Windows.XEHN';
  DataMarker = '.trashvpn-windows-data';
  InstalledDataFolder = 'trashvpn-windows';
  PreviousDataFolder = 'trashvpn';
  ProfileList = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList';
  AppRunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  AppRunName = 'Xehn.Fluent.Desktop';

var
  RemoveUntracked: Boolean;
  AppWasOwned: Boolean;
  Failures: String;

function GetFileAttributes(Name: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function IsSafeLocalPath(const Value: String): Boolean;
var
  Path, Parent: String;
  Attr: Cardinal;
begin
  Path := RemoveBackslashUnlessRoot(Value);
  Result := False;
  if Length(Path) <= 3 then Exit;
  if (Copy(Path, 2, 2) <> ':\') or (CompareText(ExpandFileName(Path), Path) <> 0) then Exit;
  while Length(Path) > 3 do begin
    if CompareText(ExtractFileName(Path), '.git') = 0 then Exit;
    Attr := GetFileAttributes(Path);
    if (Attr <> InvalidAttributes) and ((Attr and ReparseAttribute) <> 0) then Exit;
    Parent := ExtractFileDir(Path);
    if Parent = Path then Exit;
    Path := Parent;
  end;
  Result := True;
end;

function IsOwned(const Path, Marker: String): Boolean;
var
  Raw: AnsiString;
  Entry: String;
begin
  Entry := AddBackslash(Path) + Marker;
  Result := False;
  if not IsSafeLocalPath(Path) or not IsSafeLocalPath(Entry) then Exit;
  if not LoadStringFromFile(Entry, Raw) then Exit;
  Result := Trim(String(Raw)) = OwnerId;
end;

function IsEmpty(const Path: String): Boolean;
var
  Entry: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Path) + '*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
          Result := False;
          Exit;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Path: String;
begin
  Path := ExpandConstant('{app}');
  Result := CustomMessage('OwnInstallFolder');
  if IsSafeLocalPath(Path) and (not FileExists(Path)) and
     (not DirExists(Path) or IsOwned(Path, 'installed.mode') or IsEmpty(Path)) then
    Result := '';
end;

function HasSafeTree(const Path: String; Depth: Integer): Boolean;
var
  Entry: TFindRec;
begin
  Result := False;
  if Depth > 40 then Exit;
  if FindFirst(AddBackslash(Path) + '*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
          if CompareText(Entry.Name, '.git') = 0 then Exit;
          if (Entry.Attributes and ReparseAttribute) <> 0 then Exit;
          if ((Entry.Attributes and DirectoryAttribute) <> 0) and
             not HasSafeTree(AddBackslash(Path) + Entry.Name, Depth + 1) then Exit;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
  Result := True;
end;

procedure NoteFailure(const Path: String);
begin
  Failures := Failures + #13#10 + Path;
end;

procedure PurgeOwnedData(const Path: String);
begin
  if not IsOwned(Path, DataMarker) then Exit;
  if not HasSafeTree(Path, 0) then begin
    NoteFailure(Path);
    Exit;
  end;
  if not DelTree(Path, True, True, True) then NoteFailure(Path);
end;

procedure PurgeClientData(const LocalPath: String);
begin
  PurgeOwnedData(AddBackslash(LocalPath) + InstalledDataFolder);
  // Only clean the previous name if this client claimed it. An unrelated
  // legacy TrashVPN directory has no marker and remains untouched.
  PurgeOwnedData(AddBackslash(LocalPath) + PreviousDataFolder);
end;

procedure RemoveOwnAutoStart(Root: Integer; const Prefix: String);
var
  Command: String;
begin
  if RegQueryStringValue(Root, Prefix + AppRunKey, AppRunName, Command) and
     (CompareText(Command, '"' + ExpandConstant('{app}\Xehn.App.exe') + '"') = 0) then
    RegDeleteValue(Root, Prefix + AppRunKey, AppRunName);
end;

procedure PurgeProfiles;
var
  Sids: TArrayOfString;
  I: Integer;
  Profile, Local, CurrentLocal, Sid: String;
begin
  CurrentLocal := ExpandConstant('{localappdata}');
  if RemoveUntracked then PurgeClientData(CurrentLocal);
  RemoveOwnAutoStart(HKCU, '');
  if not RegGetSubkeyNames(HKLM64, ProfileList, Sids) then Exit;
  for I := 0 to GetArrayLength(Sids) - 1 do begin
    Sid := Sids[I];
    if (Pos('S-1-5-21-', Sid) <> 1) and (Pos('S-1-12-1-', Sid) <> 1) then Continue;
    if Pos('.bak', Sid) <> 0 then Continue;
    if RemoveUntracked and RegQueryStringValue(HKLM64, ProfileList + '\' + Sid, 'ProfileImagePath', Profile) then begin
      StringChangeEx(Profile, '%SystemDrive%', ExpandConstant('{sd}'), True);
      Local := AddBackslash(Profile) + 'AppData\Local';
      if CompareText(Local, CurrentLocal) <> 0 then
        PurgeClientData(Local);
    end;
    RemoveOwnAutoStart(HKU, Sid + '\');
  end;
end;

function InitializeUninstall(): Boolean;
begin
  AppWasOwned := IsOwned(ExpandConstant('{app}'), 'installed.mode');
  // Normal Inno uninstallation still removes tracked files. Permanent cleanup
  // beyond those files requires an explicit, interactive confirmation.
  RemoveUntracked := AppWasOwned and not UninstallSilent;
  if RemoveUntracked then
    RemoveUntracked := MsgBox(CustomMessage('PurgeConfirm'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
var
  Path: String;
begin
  if Step = usUninstall then
    PurgeProfiles
  else if (Step = usPostUninstall) and RemoveUntracked then begin
    Path := ExpandConstant('{app}');
    if DirExists(Path) and IsSafeLocalPath(Path) and HasSafeTree(Path, 0) then begin
      // The elevated uninstaller runs from a temporary copy. Inno also
      // removes its own files and the empty directory in the normal cleanup.
      if not DelTree(Path, True, True, True) then Log('TrashVPN install directory still in use: ' + Path);
    end else if DirExists(Path) then NoteFailure(Path);
    if Failures <> '' then MsgBox(CustomMessage('PurgeFailed') + Failures, mbError, MB_OK);
  end;
end;
