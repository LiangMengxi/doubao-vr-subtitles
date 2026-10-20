#if VER < EncodeVer(6, 7, 0)
  #error Inno Setup 6.7 or newer is required
#endif

#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef ReleaseDir
  #error ReleaseDir is required
#endif
#ifndef VerificationFile
  #error VerificationFile is required
#endif

[Setup]
AppId={{F6AC8595-E870-5E91-9159-0B12C56E0E41}
AppName=豆包VR字幕
AppVersion={#AppVersion}
AppPublisher=凉梦曦
AppPublisherURL=https://github.com/LiangMengxi/doubao-vr-subtitles
DefaultDirName={localappdata}\Programs\DoubaoVR
DefaultGroupName=豆包VR字幕
UsePreviousAppDir=yes
DirExistsWarning=no
UsePreviousTasks=yes
DisableDirPage=no
DisableWelcomePage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#ReleaseDir}
OutputBaseFilename=DoubaoVR-v{#AppVersion}-Setup-x64
SetupIconFile=..\assets\bridge.ico
UninstallDisplayIcon={app}\DoubaoVR.exe
UninstallDisplayName=豆包VR字幕
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=DoubaoVR.exe,DoubaoVR.Desktop.exe
RestartApplications=no
ChangesEnvironment=no
Uninstallable=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=豆包VR字幕安装程序

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\豆包 VR 字幕桥"; Filename: "{app}\DoubaoVR.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\豆包 VR 字幕桥"; Filename: "{app}\DoubaoVR.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\DoubaoVR.exe"; Description: "启动豆包VR字幕"; Flags: nowait postinstall skipifsilent

[Code]
var
  LegacySettings: String;

#include VerificationFile

procedure RememberLegacyDirectory(const Directory: String);
var
  Candidate: String;
begin
  Candidate := AddBackslash(Directory) + 'settings.json';
  if (LegacySettings = '') and FileExists(Candidate) then
    LegacySettings := Candidate;
end;

procedure InitializeWizard;
var
  Shell, Link, Locator, Services, Processes, Proc: Variant;
  Index: Integer;
  Shortcut: String;
begin
  LegacySettings := '';
  if FileExists(ExpandConstant('{localappdata}\DoubaoVR\settings.json')) then Exit;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE Name="DoubaoVR.exe"');
    for Index := 0 to Processes.Count - 1 do begin
      Proc := Processes.ItemIndex(Index);
      if not VarIsNull(Proc.ExecutablePath) then
        RememberLegacyDirectory(ExtractFileDir(Proc.ExecutablePath));
    end;
  except
    Log('Running portable settings discovery unavailable.');
  end;
  Shortcut := ExpandConstant('{userdesktop}\豆包 VR 字幕桥.lnk');
  if (LegacySettings = '') and FileExists(Shortcut) then begin
    try
      Shell := CreateOleObject('WScript.Shell');
      Link := Shell.CreateShortcut(Shortcut);
      RememberLegacyDirectory(ExtractFileDir(Link.TargetPath));
    except
      Log('Portable shortcut settings discovery unavailable.');
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Target, Profile: String;
begin
  if CurStep = ssPostInstall then begin
    VerifyPayload;
    Profile := ExpandConstant('{localappdata}\DoubaoVR');
    Target := AddBackslash(Profile) + 'settings.json';
    if not FileExists(Target) then begin
      if LegacySettings = '' then RememberLegacyDirectory(ExpandConstant('{app}'));
      if LegacySettings <> '' then begin
        ForceDirectories(Profile);
        if not FileCopy(LegacySettings, Target, True) then
          Log('Could not import portable settings; original settings remain untouched.');
      end;
    end;
  end;
end;
