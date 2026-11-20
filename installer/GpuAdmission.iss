#ifndef AppVersion
#define AppVersion "0.0.0-dev"
#endif
#ifndef PublishDir
#define PublishDir "..\out"
#endif
[Setup]
AppId=GpuAdmission
AppName=GPU Admission
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\GPU Admission
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=GPU-Admission-Setup-x64
SetupIconFile=..\assets\GpuAdmission.ico
UninstallDisplayIcon={app}\GpuAdmissionTray.exe
WizardStyle=modern
CloseApplications=no
RestartApplications=no

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "root.txt"; Flags: recursesubdirs ignoreversion
Source: "..\config.example.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\assets\GpuAdmission.png"; DestDir: "{app}\assets"; Flags: ignoreversion
Source: "..\native\README.md"; DestDir: "{app}\native"; Flags: ignoreversion
Source: "..\integrations\gpu_admission_client.py"; DestDir: "{app}\integrations"; Flags: ignoreversion
Source: "..\integrations\README.md"; DestDir: "{app}\integrations"; Flags: ignoreversion

[Run]
Filename: "{app}\GpuAdmissionTray.exe"; Parameters: "--status"; Description: "Open GPU Admission setup"; Flags: postinstall nowait skipifsilent

[Icons]
Name: "{userprograms}\GPU Admission"; Filename: "{app}\GpuAdmissionTray.exe"; Parameters: "--status"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "GPU Admission"; Flags: dontcreatekey uninsdeletevalue
