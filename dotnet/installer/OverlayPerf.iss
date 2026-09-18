; Script Inno Setup pour l'installeur Windows d'OverlayPerf.
; Compiler avec : ISCC.exe OverlayPerf.iss
; Prerequis : "..\publish\OverlayPerf.exe" doit deja exister (dotnet publish -c Release -o publish).

#define MyAppName "OverlayPerf"
#define MyAppVersion "0.1.4"
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

[Files]
Source: "..\publish\OverlayPerf.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; runascurrentuser : avec "postinstall", Inno Setup deselave par defaut le programme lance
; (retour aux droits de l'utilisateur d'origine, cf. doc officielle) - exactement l'inverse de
; ce qu'il faut pour OverlayPerf.exe qui exige l'administrateur (app.manifest). Sans ce
; drapeau, le lancement en fin d'installation echoue silencieusement (aucune invite UAC,
; aucune erreur, l'appli ne demarre pas).
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser
; Nettoyage best-effort d'une tache planifiee "OverlayPerf" laissee par une 0.1.1
; installee avec l'option de demarrage automatique (retiree depuis : ne fonctionnait
; pas de facon fiable et n'avait pas a etre activee sans le demander explicitement).
; Silencieux si la tache n'existe pas.
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /tn ""OverlayPerf"" /f"; Flags: runhidden

[InstallDelete]
; Raccourci mort laisse par l'installeur 0.1.0 (case "demarrer avec Windows") : un
; raccourci dans ce dossier ne peut pas elever OverlayPerf.exe, Windows bloquait son
; lancement sans la moindre invite ni erreur.
Type: files; Name: "{userstartup}\{#MyAppName}.lnk"

[UninstallDelete]
; Config, jeton et journaux sont dans %LOCALAPPDATA%\overlay : laisses en place a la
; desinstallation (ce ne sont pas des fichiers de l'installeur, et l'utilisateur peut
; reinstaller sans perdre son appairage telephone/config).
