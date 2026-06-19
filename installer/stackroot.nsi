; Stackroot NSIS installer
; Layout: Stackroot.exe (stable launcher) + current.txt + app\{version}\ payload
; Build: makensis /DPRODUCT_VERSION=... /DSTAGE_DIR=... /DINSTALLER_DIR=... /DRELEASE_DIR=... /DDOTNET_DESKTOP_INSTALLER=... stackroot.nsi

!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

!ifndef PRODUCT_VERSION
  !define PRODUCT_VERSION "0.1.0"
!endif
!ifndef PRODUCT_FILE_VERSION
  !define PRODUCT_FILE_VERSION "0.1.0.0"
!endif
!ifndef PRODUCT_PUBLISHER
  !define PRODUCT_PUBLISHER "mngez"
!endif
!ifndef PRODUCT_NAME
  !define PRODUCT_NAME "Stackroot"
!endif
!ifndef STAGE_DIR
  !error "STAGE_DIR is required"
!endif
!ifndef INSTALLER_DIR
  !error "INSTALLER_DIR is required"
!endif
!ifndef RELEASE_DIR
  !error "RELEASE_DIR is required"
!endif
!ifndef DOTNET_DESKTOP_INSTALLER
  !error "DOTNET_DESKTOP_INSTALLER is required (set from installer/dotnet-prereq.ps1 via pack-release.ps1)"
!endif
!ifndef VC_REDIST_INSTALLER
  !error "VC_REDIST_INSTALLER is required (set from installer/vc-redist-prereq.ps1 via pack-release.ps1)"
!endif
!ifndef ICON_PATH
  !define ICON_PATH "..\..\assets\icons\icon.ico"
!endif

!define PRODUCT_EXE "Stackroot.exe"
!define PRODUCT_UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}"
!define PRODUCT_DIR_REGKEY "Software\Microsoft\Windows\CurrentVersion\App Paths\${PRODUCT_EXE}"
!define APP_PAYLOAD_DIR "${STAGE_DIR}\app\${PRODUCT_VERSION}"
!define LEGACY_LAUNCHER_MAX_KB 5120
!define DOTNET_BUNDLE_SOURCE "${INSTALLER_DIR}\prerequisites\${DOTNET_DESKTOP_INSTALLER}"
!define VC_REDIST_BUNDLE_SOURCE "${INSTALLER_DIR}\prerequisites\${VC_REDIST_INSTALLER}"

!if /FileExists "${DOTNET_BUNDLE_SOURCE}"
  !define HAS_DOTNET_BUNDLE
!else
  !warning "Bundled .NET installer not found at compile time: ${DOTNET_BUNDLE_SOURCE}"
!endif

!if /FileExists "${VC_REDIST_BUNDLE_SOURCE}"
  !define HAS_VC_REDIST_BUNDLE
!else
  !warning "Bundled Visual C++ Redistributable not found at compile time: ${VC_REDIST_BUNDLE_SOURCE}"
!endif

!ifndef LAUNCHER_PROTOCOL_VERSION
  !define LAUNCHER_PROTOCOL_VERSION "2"
!endif

!if /FileExists "${STAGE_DIR}\launcher.version"
  !define HAS_STAGE_LAUNCHER_VERSION
!endif

Name "${PRODUCT_NAME}"
OutFile "${RELEASE_DIR}\${PRODUCT_NAME}-Setup-${PRODUCT_VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\${PRODUCT_NAME}"
InstallDirRegKey HKCU "${PRODUCT_DIR_REGKEY}" ""
RequestExecutionLevel user
ShowInstDetails show
; Per-file LZMA (not /SOLID) — a bad byte corrupts one file, not the whole archive.
; Large solid blocks often trigger "Error decompressing data" after partial VM copies.
CRCCheck force
SetCompressor lzma

VIProductVersion "${PRODUCT_FILE_VERSION}"
VIAddVersionKey "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey "LegalCopyright" "Copyright (c) ${PRODUCT_PUBLISHER}"
VIAddVersionKey "FileDescription" "${PRODUCT_NAME} Setup"
VIAddVersionKey "FileVersion" "${PRODUCT_FILE_VERSION}"
VIAddVersionKey "ProductVersion" "${PRODUCT_VERSION}"

!define MUI_ABORTWARNING
!define MUI_ICON "${ICON_PATH}"
!define MUI_UNICON "${ICON_PATH}"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${PRODUCT_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${PRODUCT_NAME}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; nsExec runs hidden — no flashing cmd/powershell windows.
!macro DefineCloseStackrootFunctions PREFIX
Function ${PREFIX}IsStackrootRunning
  ClearErrors
  nsExec::ExecToStack 'tasklist /FI "IMAGENAME eq ${PRODUCT_EXE}" /NH'
  Pop $0
  Pop $1
  StrLen $2 $1
  IntCmp $2 0 ${PREFIX}not_running
  StrCpy $3 $1 1
  StrCmp $3 "I" ${PREFIX}not_running
  StrCpy $3 $1 12
  StrCmp $3 "${PRODUCT_EXE}" ${PREFIX}running ${PREFIX}not_running
  ${PREFIX}running:
    Push 1
    Return
  ${PREFIX}not_running:
    Push 0
FunctionEnd

Function ${PREFIX}EnsureStackrootClosed
  ; Silent kill first (no prompt). Handles tray-only instances.
  nsExec::Exec 'taskkill /IM ${PRODUCT_EXE} /T /F'
  Sleep 2000

  ${PREFIX}check_again:
  Call ${PREFIX}IsStackrootRunning
  Pop $0
  ${If} $0 == 0
    Return
  ${EndIf}

  MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION \
    "${PRODUCT_NAME} is still running in the background.$\n$\nRight-click the tray icon and choose Quit, then click Retry." \
    /SD IDRETRY IDCANCEL ${PREFIX}user_abort IDRETRY ${PREFIX}check_again

  ${PREFIX}user_abort:
    Abort
FunctionEnd
!macroend

!insertmacro DefineCloseStackrootFunctions ""
!insertmacro DefineCloseStackrootFunctions "un."

Function GetSetupPowerShell
  ${If} ${RunningX64}
    IfFileExists "$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe" 0 setup_ps_system32
      StrCpy $R8 "$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
      Return
    setup_ps_system32:
  ${EndIf}
  StrCpy $R8 "$WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
FunctionEnd

Function EnsureDotNetDesktopRuntime
  DetailPrint "Checking .NET 8 Desktop Runtime (required for Stackroot)..."
  SetOutPath "$PLUGINSDIR"
  File "${INSTALLER_DIR}\Ensure-DotNetDesktopRuntime.ps1"
  File "${INSTALLER_DIR}\dotnet-prereq.ps1"

  Call GetSetupPowerShell

!ifdef HAS_DOTNET_BUNDLE
  DetailPrint "Preparing bundled .NET Desktop Runtime fallback..."
  CreateDirectory "$PLUGINSDIR\prerequisites"
  SetOutPath "$PLUGINSDIR\prerequisites"
  File "${DOTNET_BUNDLE_SOURCE}"
  nsExec::ExecToLog '"$R8" -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\Ensure-DotNetDesktopRuntime.ps1" -BundledInstallerPath "$PLUGINSDIR\prerequisites\${DOTNET_DESKTOP_INSTALLER}"'
!else
  nsExec::ExecToLog '"$R8" -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\Ensure-DotNetDesktopRuntime.ps1"'
!endif
  Pop $0
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP \
      ".NET 8 Desktop Runtime is required but could not be installed.$\n$\nInstall it from dotnet.microsoft.com/download/dotnet/8.0 and run setup again."
    Abort
  ${EndIf}
FunctionEnd

Function EnsureVcRedist
  DetailPrint "Checking Visual C++ Redistributable (required for PHP on Windows)..."
  SetOutPath "$PLUGINSDIR"
  File "${INSTALLER_DIR}\Ensure-VcRedist.ps1"
  File "${INSTALLER_DIR}\vc-redist-prereq.ps1"

  Call GetSetupPowerShell

  nsExec::ExecToLog '"$R8" -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\Ensure-VcRedist.ps1" -CheckOnly'
  Pop $0
  ${If} $0 == 0
    Return
  ${EndIf}

!ifdef HAS_VC_REDIST_BUNDLE
  DetailPrint "Preparing bundled Visual C++ Redistributable fallback (for PHP)..."
  CreateDirectory "$PLUGINSDIR\prerequisites"
  SetOutPath "$PLUGINSDIR\prerequisites"
  File "${VC_REDIST_BUNDLE_SOURCE}"
  nsExec::ExecToLog '"$R8" -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\Ensure-VcRedist.ps1" -BundledInstallerPath "$PLUGINSDIR\prerequisites\${VC_REDIST_INSTALLER}"'
!else
  nsExec::ExecToLog '"$R8" -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\Ensure-VcRedist.ps1"'
!endif
  Pop $0
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP \
      "Microsoft Visual C++ 2015-2022 Redistributable (x64) is required for PHP on Windows but could not be installed.$\n$\nDownload it from aka.ms/vs/17/release/vc_redist.x64.exe and run setup again."
    Abort
  ${EndIf}
FunctionEnd

Function CleanupLegacyInstallRoot
  ; 0.1/0.2 left self-contained runtime + app DLLs in the install root. The thin launcher
  ; must not sit beside hostfxr.dll — apphost loads the wrong host and fails with ".NET required".
  IfFileExists "$INSTDIR\hostfxr.dll" legacy_markers
  IfFileExists "$INSTDIR\coreclr.dll" legacy_markers
  IfFileExists "$INSTDIR\Stackroot.dll" legacy_markers
    Return

  legacy_markers:
  DetailPrint "Removing legacy install files from the install root..."
  SetOutPath "$PLUGINSDIR"
  File "${INSTALLER_DIR}\Cleanup-LegacyInstallRoot.ps1"
  Call GetSetupPowerShell
  nsExec::ExecToLog '"$R8" -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\Cleanup-LegacyInstallRoot.ps1" -InstallDir "$INSTDIR"'
  Pop $0
  ${If} $0 != 0
    DetailPrint "Warning: legacy cleanup returned exit code $0."
  ${EndIf}
FunctionEnd

Function ShouldInstallPinnedLauncher
  ; Push 1 = install/replace, 0 = keep existing launcher with matching protocol.

  IfFileExists "$INSTDIR\${PRODUCT_EXE}" exe_exists
    DetailPrint "Pinned launcher: first install."
    Push 1
    Return

  exe_exists:
  ${GetSize} "$INSTDIR\${PRODUCT_EXE}" "/S=0K" $0 $1 $2
  IntCmp $0 ${LEGACY_LAUNCHER_MAX_KB} check_protocol check_protocol replace_launcher

  check_protocol:
  IfFileExists "$INSTDIR\Stackroot.deps.json" 0 launcher_no_split_deps
    Goto replace_launcher
  launcher_no_split_deps:
  IfFileExists "$INSTDIR\Stackroot.dll" 0 launcher_no_launcher_dll
    Goto replace_launcher
  launcher_no_launcher_dll:
  IfFileExists "$INSTDIR\Stackroot.Launcher.dll" 0 launcher_check_version
    Goto replace_launcher
  launcher_check_version:
  IfFileExists "$INSTDIR\launcher.version" 0 replace_launcher
    ClearErrors
    FileOpen $0 "$INSTDIR\launcher.version" r
    IfErrors replace_launcher
    FileRead $0 $1
    FileClose $0
    StrCpy $3 $1 1
    StrCmp $3 "${LAUNCHER_PROTOCOL_VERSION}" keep_launcher replace_launcher

  replace_launcher:
    DetailPrint "Pinned launcher: replacing (missing, legacy, or protocol mismatch)."
    Push 1
    Return

  keep_launcher:
    DetailPrint "Pinned launcher: keeping existing."
    Push 0
    Return
FunctionEnd

Section "Install" SecInstall
  SectionIn RO
  Call EnsureStackrootClosed
  Call EnsureDotNetDesktopRuntime
  Call EnsureVcRedist
  Call CleanupLegacyInstallRoot

  ; Pinned launcher — keep across upgrades unless legacy self-contained or protocol changed.
  Call ShouldInstallPinnedLauncher
  Pop $R9
  IntCmp $R9 1 install_pinned_launcher keep_pinned_launcher keep_pinned_launcher
  install_pinned_launcher:
    DetailPrint "Installing pinned Stackroot launcher..."
    SetOutPath "$INSTDIR"
    SetOverwrite on
    Delete "$INSTDIR\Stackroot.Launcher.dll"
    Delete "$INSTDIR\Stackroot.Launcher.deps.json"
    Delete "$INSTDIR\Stackroot.Launcher.runtimeconfig.json"
    Delete "$INSTDIR\Stackroot.dll"
    Delete "$INSTDIR\Stackroot.deps.json"
    Delete "$INSTDIR\Stackroot.runtimeconfig.json"
    File /oname=${PRODUCT_EXE} "${STAGE_DIR}\${PRODUCT_EXE}"
!ifdef HAS_STAGE_LAUNCHER_VERSION
    File /oname=launcher.version "${STAGE_DIR}\launcher.version"
!endif
    Goto launcher_done
  keep_pinned_launcher:
    DetailPrint "Keeping pinned Stackroot launcher."
  launcher_done:

  DetailPrint "Installing app payload ${PRODUCT_VERSION}..."
  SetOutPath "$INSTDIR\app\${PRODUCT_VERSION}"
  File /r "${APP_PAYLOAD_DIR}\*"

  DetailPrint "Updating active version..."
  FileOpen $0 "$INSTDIR\current.txt" w
  FileWrite $0 "${PRODUCT_VERSION}"
  FileClose $0

  CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
  CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk" "$INSTDIR\${PRODUCT_EXE}"
  CreateShortCut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\${PRODUCT_EXE}"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "${PRODUCT_DIR_REGKEY}" "" "$INSTDIR\${PRODUCT_EXE}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "DisplayIcon" "$INSTDIR\${PRODUCT_EXE}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "${PRODUCT_UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${PRODUCT_UNINST_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  Call un.EnsureStackrootClosed
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk"
  RMDir "$SMPROGRAMS\${PRODUCT_NAME}"

  Delete "$INSTDIR\Uninstall.exe"
  Delete "$INSTDIR\current.txt"
  Delete "$INSTDIR\launcher.version"
  Delete "$INSTDIR\Stackroot.Launcher.dll"
  Delete "$INSTDIR\Stackroot.Launcher.deps.json"
  Delete "$INSTDIR\Stackroot.Launcher.runtimeconfig.json"
  Delete "$INSTDIR\Stackroot.dll"
  Delete "$INSTDIR\Stackroot.deps.json"
  Delete "$INSTDIR\Stackroot.runtimeconfig.json"
  RMDir /r "$INSTDIR\app"
  Delete "$INSTDIR\${PRODUCT_EXE}"
  RMDir /r "$INSTDIR"

  DeleteRegKey HKCU "${PRODUCT_UNINST_KEY}"
  DeleteRegKey HKCU "${PRODUCT_DIR_REGKEY}"
SectionEnd
