; Inno Setup script for the ScreenPlus installer (https://jrsoftware.org/isinfo.php).
; Built by scripts/publish.ps1 -Installer and by CI; see the README. Defines you can pass to ISCC:
;   /DAppVersion=0.1.0   /DArch=x64|arm64   /DSourceDir=<folder with the published app>

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\app-win-" + Arch
#endif

[Setup]
; Never change the AppId: Windows uses it to recognise upgrades and the uninstaller.
AppId={{8F3C2A5E-6B1D-4E7A-9C0F-2D4B6A8E1F37}
AppName=ScreenPlus
AppVersion={#AppVersion}
AppVerName=ScreenPlus {#AppVersion}
AppPublisher=ScreenPlus
AppPublisherURL=https://github.com/Yanguangchen/ScreenPlus
AppSupportURL=https://github.com/Yanguangchen/ScreenPlus/issues
DefaultDirName={autopf}\ScreenPlus
DisableProgramGroupPage=yes
; Installs for the current user without an admin prompt; the first page offers "all users" too.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; Windows 10 version 2004 is the first with cursor-free screen capture.
MinVersion=10.0.19041
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
OutputDir=..\dist
OutputBaseFilename=ScreenPlus-Setup-{#AppVersion}-{#Arch}
SetupIconFile=..\src\ScreenPlus\Assets\AppIcon.ico
UninstallDisplayIcon={app}\ScreenPlus.exe
UninstallDisplayName=ScreenPlus
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
; Offer to close a running ScreenPlus so it can be updated.
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ScreenPlus"; Filename: "{app}\ScreenPlus.exe"
Name: "{autodesktop}\ScreenPlus"; Filename: "{app}\ScreenPlus.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ScreenPlus.exe"; Description: "{cm:LaunchProgram,ScreenPlus}"; Flags: nowait postinstall skipifsilent
