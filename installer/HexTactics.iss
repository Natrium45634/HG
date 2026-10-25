; Установщик «Гекс-тактика» (HexTactics) — Inno Setup 6.
; Сборка: ISCC.exe /DAppVersion=1.0.0 /DSourceDir=<папка экспорта Windows> /DOutputDir=<куда> installer\HexTactics.iss
; CI собирает его на Windows и проверяет: тихая установка, самопроверка установленной игры, удаление (.github/workflows/build.yml).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\build\windows"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{6E3B7C2A-4F0D-4E52-9B8A-5C1D2E7F9A41}
AppName=Гекс-тактика
AppVersion={#AppVersion}
AppVerName=Гекс-тактика {#AppVersion}
AppPublisher=HexTactics
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=HexTactics
VersionInfoDescription=HexTactics setup
DefaultDirName={autopf}\HexTactics
DefaultGroupName=Гекс-тактика
DisableProgramGroupPage=yes
; Для текущего пользователя, без прав администратора; в окне установки можно выбрать «для всех».
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=HexTactics-{#AppVersion}-Setup
SetupIconFile=HexTactics.ico
UninstallDisplayIcon={app}\HexTactics.exe
UninstallDisplayName=Гекс-тактика {#AppVersion}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
CloseApplications=yes
ShowLanguageDialog=auto

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.SafeMode=Гекс-тактика (безопасный режим графики)
english.SafeMode=HexTactics (safe graphics mode)
russian.Guide=Руководство пользователя
english.Guide=User guide (Russian)
russian.UninstallShortcut=Удалить Гекс-тактику
english.UninstallShortcut=Uninstall HexTactics
russian.DeleteUserData=Удалить также ваши данные игры (опубликованный в редакторе контент, настройки, отчёты, точки восстановления)?%n%n%1%n%nЕсли вы собираетесь установить игру снова — нажмите «Нет».
english.DeleteUserData=Also delete your game data (content published in the editor, settings, reports, restore points)?%n%n%1%n%nChoose "No" if you are going to reinstall the game.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\HexTactics.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\HexTactics.pck"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\data_HexTactics_windows_x86_64\*"; DestDir: "{app}\data_HexTactics_windows_x86_64"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "GUIDE_RU.txt"; DestDir: "{app}"; DestName: "Руководство.txt"; Flags: ignoreversion
Source: "HexTactics.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Гекс-тактика"; Filename: "{app}\HexTactics.exe"; WorkingDir: "{app}"; IconFilename: "{app}\HexTactics.ico"
Name: "{group}\{cm:SafeMode}"; Filename: "{app}\HexTactics.exe"; Parameters: "--rendering-driver opengl3"; WorkingDir: "{app}"; IconFilename: "{app}\HexTactics.ico"
Name: "{group}\{cm:Guide}"; Filename: "{app}\Руководство.txt"
Name: "{group}\{cm:UninstallShortcut}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Гекс-тактика"; Filename: "{app}\HexTactics.exe"; WorkingDir: "{app}"; IconFilename: "{app}\HexTactics.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\HexTactics.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,Гекс-тактика}"; Flags: nowait postinstall skipifsilent

[Code]
{ The game keeps the user's data (published content, settings, reports) apart from the program; the uninstaller
  asks before removing it (a silent uninstall keeps it). }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Data := ExpandConstant('{userappdata}\Godot\app_userdata\HexTactics');
  if (not UninstallSilent()) and DirExists(Data) then
    if MsgBox(FmtMessage(CustomMessage('DeleteUserData'), [Data]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(Data, True, True, True);
end;
