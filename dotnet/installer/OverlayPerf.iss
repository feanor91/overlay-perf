; Script Inno Setup pour l'installeur Windows d'OverlayPerf.
; Compiler avec : ISCC.exe OverlayPerf.iss
; Prerequis : "..\publish\OverlayPerf.exe" doit deja exister (dotnet publish -c Release -o publish).

#define MyAppName "OverlayPerf"
#define MyAppVersion "0.1.1"
#define MyAppPublisher "feanor91"
#define MyAppURL "https://github.com/feanor91/overlay-perf"
#define MyAppExeName "OverlayPerf.exe"

[Setup]
AppId={{6B6F3F3E-6C3C-4B1B-9C7E-1B7B3E9C8F2A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; L'exe lui-meme exige deja les droits administrateur (app.manifest) pour PresentMon
; et LibreHardwareMonitor : l'installeur les demande aussi, par coherence et pour
; pouvoir ecrire dans Program Files.
PrivilegesRequired=admin
OutputDir=output
OutputBaseFilename=OverlayPerf-Setup-{#MyAppVersion}
SetupIconFile=..\OverlayPerf\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\..\LICENSE
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startupicon"; Description: "Lancer OverlayPerf au demarrage de Windows"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\OverlayPerf.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
; OverlayPerf.exe exige les droits administrateur (app.manifest) : un raccourci dans le
; dossier Demarrage ne peut pas s'auto-elever, Windows bloque son lancement sans la
; moindre invite ni erreur (l'app reste "Active" dans le Gestionnaire des taches mais ne
; demarre jamais). Une tache planifiee "executer avec les privileges les plus eleves",
; declenchee a l'ouverture de session, est le mecanisme standard de contournement :
; l'elevation est deja actee par la tache elle-meme, aucune invite UAC n'apparait.
Filename: "{sys}\schtasks.exe"; Parameters: "/create /tn ""OverlayPerf"" /tr ""{app}\{#MyAppExeName}"" /sc onlogon /rl highest /f"; Tasks: startupicon; Flags: runhidden

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /tn ""OverlayPerf"" /f"; Flags: runhidden; RunOnceId: "DeleteStartupTask"

[UninstallDelete]
; Config, jeton et journaux sont dans %LOCALAPPDATA%\overlay : laisses en place a la
; desinstallation (ce ne sont pas des fichiers de l'installeur, et l'utilisateur peut
; reinstaller sans perdre son appairage telephone/config).
