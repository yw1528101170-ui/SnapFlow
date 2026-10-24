; SnapFlow (瞬流) 官方安装程序脚本
; 架构: x64 | 权限: 最低权限 (当前用户 Programs 目录，免 UAC 管理员提权)

#define MyAppName "SnapFlow 瞬流"
#define MyAppEnglishName "SnapFlow"
; 版本号由 build.ps1 通过 /DMyAppVersion 传入；仅当未传入时才回退到默认值
; （回退值需与 src/Jietu/Jietu.csproj 的 <Version> 手动保持一致）
#ifndef MyAppVersion
  #define MyAppVersion "1.1.3"
#endif
#define MyAppPublisher "SnapFlow"
#define MyAppExe "SnapFlow.exe"
#define MyAppId "{7C4E9A21-6B18-4F0D-9A55-3E8D1C0B6F42}"

[Setup]
AppId={{#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright © 2026 {#MyAppPublisher}
AppMutex=Local\SnapFlow.SingleInstance
DefaultDirName={localappdata}\Programs\SnapFlow
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=SnapFlow-Setup-v{#MyAppVersion}
SetupIconFile=..\src\Jietu\Assets\App.ico
UninstallDisplayIcon={app}\{#MyAppExe},0
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} 安装向导
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCopyright=Copyright © 2026 {#MyAppPublisher}

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.StartupOnBoot=开机自动启动 SnapFlow
chinesesimplified.LaunchProgram=启动 %1
en.StartupOnBoot=Start %1 when Windows starts
en.LaunchProgram=Launch %1

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "{cm:StartupOnBoot,{#MyAppName}}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; 注意：这里只会写注册表。应用启动时由 StartupService.Reconcile 把 Run 键里的实际状态
; 对账回用户设置文件（settings.json），因此安装时勾选后首次打开设置页即显示为已开启。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SnapFlow"; ValueData: """{app}\{#MyAppExe}"""; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "SnapFlow"; Flags: dontcreatekey uninsdeletevalue
; 旧版本（Jietu）残留的自启动值：卸载时一并删除，避免删除程序后每次登录还尝试启动旧 exe。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Jietu"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  // 卸载流程中用户是否选择同时删除用户数据。
  DeleteUserData: Boolean;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Prompt, Nl: string;
  DataDirs: array of string;
  AppDir, DefaultDir: string;
  I: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Nl := #13#10;
    if ActiveLanguage = 'chinesesimplified' then
      Prompt := '是否同时删除用户数据？' + Nl + Nl
        + '选择“是”将永久删除以下内容（无法恢复）：' + Nl
        + '  · 历史记录与截图（%LOCALAPPDATA%\SnapFlow）' + Nl
        + '  · 旧版本遗留的数据目录（%APPDATA%\SnapFlow、%APPDATA%\Jietu）' + Nl
        + '  · 设置文件（含已保存的配置）' + Nl + Nl
        + '选择“否”仅卸载程序本体，用户数据保留，重装后可继续使用。'
    else
      Prompt := 'Also delete your user data?' + Nl + Nl
        + 'Choosing Yes will permanently remove (cannot be undone):' + Nl
        + '  · History and screenshots (%LOCALAPPDATA%\SnapFlow)' + Nl
        + '  · Legacy data folders from older versions (%APPDATA%\SnapFlow, %APPDATA%\Jietu)' + Nl
        + '  · Settings (including saved configuration)' + Nl + Nl
        + 'Choosing No uninstalls the program only; your data is kept for a future reinstall.';

    // 默认保留数据（默认按钮=否）：误点卸载后重装仍能找回历史记录。
    // SuppressibleMsgBox：/SILENT、/VERYSILENT 静默卸载时不弹窗，自动按默认按钮“否”保留数据。
    DeleteUserData := SuppressibleMsgBox(Prompt, mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
    if not DeleteUserData then
      exit;

    // 现行数据目录（历史库/截图/设置/日志）→ 迁移暂存残留 → 两个旧版本遗留数据目录
    SetArrayLength(DataDirs, 4);
    DataDirs[0] := ExpandConstant('{localappdata}\SnapFlow');
    DataDirs[1] := ExpandConstant('{localappdata}\SnapFlow.migrating');
    DataDirs[2] := ExpandConstant('{userappdata}\SnapFlow');
    DataDirs[3] := ExpandConstant('{userappdata}\Jietu');
    for I := 0 to GetArrayLength(DataDirs) - 1 do
    begin
      if DirExists(DataDirs[I]) then
      begin
        Log('卸载清理用户数据: ' + DataDirs[I]);
        DelTree(DataDirs[I], True, True, True);
      end;
    end;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    // 兜底清扫：卸载器只删除自己记录过的文件；刚装就卸时 Defender 实时扫描会短暂
    // 锁住部分 DLL，静默卸载便跳过它们，留下整目录残留。默认安装目录是应用专属的，
    // 卸载后剩余内容都属本应用，直接整目录清除；用户自定义目录不在此列，避免误删。
    AppDir := ExpandConstant('{app}');
    DefaultDir := ExpandConstant('{localappdata}\Programs\SnapFlow');
    if (CompareText(AppDir, DefaultDir) = 0) and DirExists(AppDir) then
    begin
      Log('清扫安装目录残留: ' + AppDir);
      DelTree(AppDir, True, True, True);
    end;
  end;
end;
