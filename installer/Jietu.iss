; SnapFlow (瞬流) 官方安装程序脚本
; 架构: x64 | 权限: 最低权限 (当前用户 Programs 目录，免 UAC 管理员提权)

#define MyAppName "SnapFlow 瞬流"
#define MyAppEnglishName "SnapFlow"
; 版本号由 build.ps1 通过 /DMyAppVersion 传入；仅当未传入时才回退到默认值
; （回退值需与 src/Jietu/Jietu.csproj 的 <Version> 手动保持一致）
#ifndef MyAppVersion
  #define MyAppVersion "1.1.6"
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
  // 卸载确认页上的“同时删除用户数据”勾选框与说明文字；静默卸载时不创建（保持 nil，一律保留数据）。
  DeleteDataCheckBox: TNewCheckBox;
  DeleteDataHintLabel: TNewStaticText;
  // 卸载确认页上的“卸载”按钮。
  UninstallConfirmButton: TNewButton;
  // 卸载流程中用户是否选择同时删除用户数据。
  DeleteUserData: Boolean;

procedure InitializeUninstallProgressForm();
var
  IsCN: Boolean;
  OrigName, OrigDesc: string;
  BaseLeft, BaseTop: Integer;
begin
  // /SILENT、/VERYSILENT 无人值守卸载：不显示确认选项，一律保留用户数据（与旧版弹窗的静默行为一致）。
  if UninstallSilent then
    exit;

  IsCN := (ActiveLanguage = 'chinesesimplified');

  // ProgressBar 位于 InnerNotebook 的页面内，其 Left/Top 是页面坐标，换算成窗体客户区坐标用于对齐排版。
  BaseLeft := UninstallProgressForm.InnerNotebook.Left + UninstallProgressForm.ProgressBar.Left;
  BaseTop := UninstallProgressForm.InnerNotebook.Top
    + UninstallProgressForm.ProgressBar.Top + UninstallProgressForm.ProgressBar.Height;

  // “同时删除用户数据”选项：放在进度条下方的空白区，卸载全程可见。
  DeleteDataCheckBox := TNewCheckBox.Create(UninstallProgressForm);
  DeleteDataCheckBox.Parent := UninstallProgressForm;
  DeleteDataCheckBox.SetBounds(BaseLeft, BaseTop + ScaleY(20),
    UninstallProgressForm.ClientWidth - BaseLeft * 2, ScaleY(17));
  if IsCN then
    DeleteDataCheckBox.Caption := '同时删除用户数据（历史记录、截图与设置）'
  else
    DeleteDataCheckBox.Caption := 'Also delete user data (history, screenshots and settings)';
  // 默认不勾选（保留数据）：误点卸载后重装仍能找回历史记录。
  DeleteDataCheckBox.Checked := False;

  DeleteDataHintLabel := TNewStaticText.Create(UninstallProgressForm);
  DeleteDataHintLabel.Parent := UninstallProgressForm;
  DeleteDataHintLabel.SetBounds(BaseLeft + ScaleX(20),
    BaseTop + ScaleY(20) + ScaleY(17) + ScaleY(6),
    UninstallProgressForm.ClientWidth - BaseLeft * 2 - ScaleX(20), ScaleY(60));
  DeleteDataHintLabel.AutoSize := False;
  DeleteDataHintLabel.WordWrap := True;
  DeleteDataHintLabel.ShowAccelChar := False;
  DeleteDataHintLabel.Font.Color := clGrayText;
  if IsCN then
    DeleteDataHintLabel.Caption := '勾选后将永久删除以下内容（无法恢复）：历史记录与截图（%LOCALAPPDATA%\SnapFlow）、'
      + '旧版本遗留的数据目录（%APPDATA%\SnapFlow、%APPDATA%\Jietu）以及设置文件。'
      + '不勾选则仅卸载程序本体，用户数据保留，重装后可继续使用。'
  else
    DeleteDataHintLabel.Caption := 'If checked, the following will be permanently removed and cannot be undone: '
      + 'history and screenshots (%LOCALAPPDATA%\SnapFlow), legacy data folders from older versions '
      + '(%APPDATA%\SnapFlow, %APPDATA%\Jietu), and your settings. '
      + 'Leave unchecked to uninstall the program only; your data is kept for a future reinstall.';

  // “卸载”按钮置于“取消”左侧。窗体先以模态方式等用户确认，点“卸载”后引擎才开始删除文件：
  // 否则窗体一显示引擎立即开始卸载，用户来不及勾选。
  UninstallConfirmButton := TNewButton.Create(UninstallProgressForm);
  UninstallConfirmButton.Parent := UninstallProgressForm;
  UninstallConfirmButton.SetBounds(
    UninstallProgressForm.CancelButton.Left - UninstallProgressForm.CancelButton.Width - ScaleX(10),
    UninstallProgressForm.CancelButton.Top,
    UninstallProgressForm.CancelButton.Width,
    UninstallProgressForm.CancelButton.Height);
  if IsCN then
    UninstallConfirmButton.Caption := '卸载'
  else
    UninstallConfirmButton.Caption := 'Uninstall';
  UninstallConfirmButton.ModalResult := mrOk;
  UninstallConfirmButton.Default := True;

  // 确认阶段借用窗体本身：改写标题文案、隐藏进度控件，并把“取消”按钮设为可关闭。
  OrigName := UninstallProgressForm.PageNameLabel.Caption;
  OrigDesc := UninstallProgressForm.PageDescriptionLabel.Caption;
  UninstallProgressForm.PageNameLabel.Caption := '卸载 {#MyAppName}';
  if IsCN then
    UninstallProgressForm.PageDescriptionLabel.Caption :=
      '确认后点击“卸载”开始移除程序；点击“取消”退出，不做任何更改。'
  else
    UninstallProgressForm.PageDescriptionLabel.Caption :=
      'Click Uninstall to remove the program, or Cancel to quit without changing anything.';
  UninstallProgressForm.StatusLabel.Visible := False;
  UninstallProgressForm.ProgressBar.Visible := False;
  UninstallProgressForm.CancelButton.Enabled := True;
  UninstallProgressForm.CancelButton.ModalResult := mrCancel;

  if UninstallProgressForm.ShowModal = mrOk then
  begin
    // 恢复引擎托管的界面状态，随后的卸载进度由引擎负责刷新。
    UninstallProgressForm.PageNameLabel.Caption := OrigName;
    UninstallProgressForm.PageDescriptionLabel.Caption := OrigDesc;
    UninstallProgressForm.StatusLabel.Visible := True;
    UninstallProgressForm.ProgressBar.Visible := True;
    UninstallProgressForm.CancelButton.Enabled := False;
    UninstallProgressForm.CancelButton.ModalResult := mrNone;
    UninstallConfirmButton.Visible := False;
  end
  else
    Abort; // 用户点了“取消”或关闭窗口：终止卸载，不做任何删除（引擎对 Abort 静默处理，不弹错误框）。
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDirs: array of string;
  AppDir, DefaultDir: string;
  I: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // 交互卸载：读取确认页勾选框；静默卸载：未创建勾选框（nil），一律保留数据。
    DeleteUserData := Assigned(DeleteDataCheckBox) and DeleteDataCheckBox.Checked;
    if Assigned(DeleteDataCheckBox) then
    begin
      // 选项已读取生效，锁定控件，避免卸载中途切换勾选造成误解。
      DeleteDataCheckBox.Enabled := False;
      DeleteDataHintLabel.Enabled := False;
    end;
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
