#ifndef AppVersion
#define AppVersion "0.0.0-dev"
#endif
[Setup]
AppId=GpuAdmission
AppName=GPU Admission
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\GPU Admission
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
OutputDir=..\dist
OutputBaseFilename=GPU-Admission-Setup-x64
SetupIconFile=..\assets\GpuAdmission.ico
UninstallDisplayIcon={app}\GpuAdmissionTray.exe
WizardStyle=modern
CloseApplications=no
RestartApplications=no

[Files]
Source: "..\out\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "..\config.example.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\GPU Admission"; Filename: "{app}\GpuAdmissionTray.exe"; Parameters: "--status"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "GPU Admission"; Flags: dontcreatekey uninsdeletevalue
