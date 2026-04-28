; ============================================================
; Aion2 Meter - NSIS Installer Script
; makensis /DVERSION=1.0.0 /DOUTPUT_DIR=C:\path\to\publish Aion2Meter.nsi
; ============================================================

; No Unicode directive - use ASCII/English only to avoid encoding issues

!define APP_NAME       "Aion2 Meter"
!define APP_EXE        "Aion2Meter.exe"
!define PUBLISHER      "Aion2Meter"
!define INSTALL_SUBDIR "Aion2Meter"
!define REG_KEY        "Software\Microsoft\Windows\CurrentVersion\Uninstall\Aion2Meter"

!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!ifndef OUTPUT_DIR
  !define OUTPUT_DIR "publish"
!endif

Name "${APP_NAME} ${VERSION}"
OutFile "Aion2Meter-Setup.exe"
InstallDir "$PROGRAMFILES64\${INSTALL_SUBDIR}"
InstallDirRegKey HKLM "${REG_KEY}" "InstallLocation"

RequestExecutionLevel admin

!include "MUI2.nsh"
!include "LogicLib.nsh"

!define MUI_ABORTWARNING
!define MUI_ICON "${NSISDIR}\Contrib\Graphics\Icons\modern-install.ico"
!define MUI_UNICON "${NSISDIR}\Contrib\Graphics\Icons\modern-uninstall.ico"

!define MUI_WELCOMEPAGE_TITLE "Welcome to Aion2 Meter ${VERSION} Setup"
!define MUI_WELCOMEPAGE_TEXT "This will install Aion2 Meter on your computer.$\r$\n$\r$\nNpcap will be installed automatically if not already present.$\r$\n$\r$\nClick Next to continue."
!define MUI_DIRECTORYPAGE_TEXT_TOP "Choose the folder to install Aion2 Meter. Default path is recommended."
!define MUI_FINISHPAGE_TEXT "Aion2 Meter has been installed.$\r$\n$\r$\nRun as Administrator for packet capture to work."

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Korean"

; ── Install Section ──────────────────────────────────────
Section "MainSection" SEC01

  SetOutPath "$INSTDIR"

  ; Copy all publish folder contents (includes SharpPcap native DLLs)
  File /r "${OUTPUT_DIR}\*.*"

  ; ── Install Npcap (only if not already installed) ──────
  ReadRegStr $0 HKLM "SOFTWARE\Npcap" ""
  ${If} $0 == ""
    DetailPrint "Installing Npcap..."
    File "npcap-installer.exe"
    ExecWait '"$INSTDIR\npcap-installer.exe" /winpcap_mode' $1
    Delete "$INSTDIR\npcap-installer.exe"
    ${If} $1 != 0
      MessageBox MB_OK|MB_ICONEXCLAMATION "Npcap installation failed (code: $1).$\n$\nPlease install manually: https://npcap.com$\n$\nMake sure to check: Install Npcap in WinPcap API-compatible Mode"
    ${Else}
      DetailPrint "Npcap installed successfully."
    ${EndIf}
  ${Else}
    DetailPrint "Npcap already installed, skipping."
  ${EndIf}

  ; ── Create Shortcuts ───────────────────────────────────
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

  ; Set Run As Administrator flag on desktop shortcut
  nsExec::ExecToLog 'powershell -Command "$s=(New-Object -COM WScript.Shell).CreateShortcut(\"$DESKTOP\${APP_NAME}.lnk\"); $s.Save(); $bytes=[System.IO.File]::ReadAllBytes(\"$DESKTOP\${APP_NAME}.lnk\"); $bytes[0x15]=$bytes[0x15] -bor 0x20; [System.IO.File]::WriteAllBytes(\"$DESKTOP\${APP_NAME}.lnk\",$bytes)"'

  ; ── Registry ───────────────────────────────────────────
  WriteRegStr HKLM "${REG_KEY}" "DisplayName"     "${APP_NAME}"
  WriteRegStr HKLM "${REG_KEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr HKLM "${REG_KEY}" "Publisher"       "${PUBLISHER}"
  WriteRegStr HKLM "${REG_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${REG_KEY}" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegDWORD HKLM "${REG_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${REG_KEY}" "NoRepair" 1

  WriteUninstaller "$INSTDIR\Uninstall.exe"

SectionEnd

; ── Uninstall Section ────────────────────────────────────
Section "Uninstall"

  ${If} $INSTDIR == ""
    MessageBox MB_OK|MB_ICONSTOP "Cannot determine install path. Please uninstall manually."
    Abort
  ${EndIf}

  ${IfNot} ${FileExists} "$INSTDIR\${APP_EXE}"
    MessageBox MB_OK|MB_ICONEXCLAMATION "${APP_EXE} not found in $INSTDIR.$\nPlease delete manually: $INSTDIR"
    Abort
  ${EndIf}

  ; Delete installed files by extension (safe - no RMDir /r)
  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$INSTDIR\*.dll"
  Delete "$INSTDIR\*.json"
  Delete "$INSTDIR\*.pdb"

  ; Remove folder only if empty
  RMDir "$INSTDIR"

  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"

  DeleteRegKey HKLM "${REG_KEY}"

SectionEnd
