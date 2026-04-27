; ============================================================
; Aion2 Meter - NSIS 인스톨러 스크립트
; makensis /DVERSION=1.0.0 /DOUTPUT_DIR=publish Aion2Meter.nsi
; ============================================================

Unicode true

!define APP_NAME     "Aion2 Meter"
!define APP_EXE      "Aion2Meter.exe"
!define PUBLISHER    "Aion2Meter"
!define REG_KEY      "Software\Microsoft\Windows\CurrentVersion\Uninstall\Aion2Meter"

; GitHub Actions에서 /D 옵션으로 주입
!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!ifndef OUTPUT_DIR
  !define OUTPUT_DIR "publish"
!endif

Name "${APP_NAME} ${VERSION}"
OutFile "Aion2Meter-Setup.exe"
InstallDir "$PROGRAMFILES64\Aion2Meter"
InstallDirRegKey HKLM "${REG_KEY}" "InstallLocation"

; 관리자 권한 필수 (Npcap 설치 + Program Files 쓰기)
RequestExecutionLevel admin

; 모던 UI
!include "MUI2.nsh"
!define MUI_ABORTWARNING
!define MUI_ICON "${NSISDIR}\Contrib\Graphics\Icons\modern-install.ico"
!define MUI_UNICON "${NSISDIR}\Contrib\Graphics\Icons\modern-uninstall.ico"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Korean"

; ── 설치 섹션 ────────────────────────────────────────────
Section "MainSection" SEC01

  SetOutPath "$INSTDIR"

  ; Aion2Meter.exe 복사
  File "${OUTPUT_DIR}\${APP_EXE}"

  ; ── Npcap 설치 (미설치 시에만) ──────────────────────
  ; 레지스트리로 설치 여부 확인
  ReadRegStr $0 HKLM "SOFTWARE\Npcap" ""
  ${If} $0 == ""
    DetailPrint "Npcap 설치 중..."
    File "npcap-installer.exe"
    ; /winpcap_mode: SharpPcap 필수 조건
    ExecWait '"$INSTDIR\npcap-installer.exe" /winpcap_mode' $1
    ${If} $1 != 0
      MessageBox MB_OK|MB_ICONEXCLAMATION \
        "Npcap 설치에 실패했습니다.$\n수동으로 설치해주세요: https://npcap.com"
    ${EndIf}
    Delete "$INSTDIR\npcap-installer.exe"
  ${Else}
    DetailPrint "Npcap 이미 설치됨, 건너뜀"
  ${EndIf}

  ; ── 바로가기 생성 ────────────────────────────────────
  ; 관리자 권한으로 실행되도록 ShellLink에 RunAsAdmin 플래그 설정
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" \
    "$INSTDIR\${APP_EXE}"
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" \
    "$INSTDIR\${APP_EXE}"

  ; 바로가기에 관리자 권한 플래그 설정
  ; (일반 CreateShortcut으로는 안 됨 - ShellLink 직접 조작)
  nsExec::ExecToLog 'powershell -Command \
    "$s=(New-Object -COM WScript.Shell).CreateShortcut(\"$DESKTOP\${APP_NAME}.lnk\"); \
    $s.Save(); \
    $bytes=[System.IO.File]::ReadAllBytes(\"$DESKTOP\${APP_NAME}.lnk\"); \
    $bytes[0x15]=$bytes[0x15] -bor 0x20; \
    [System.IO.File]::WriteAllBytes(\"$DESKTOP\${APP_NAME}.lnk\",$bytes)"'

  ; ── 레지스트리 등록 (프로그램 추가/제거에 표시) ──────
  WriteRegStr HKLM "${REG_KEY}" "DisplayName"     "${APP_NAME}"
  WriteRegStr HKLM "${REG_KEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr HKLM "${REG_KEY}" "Publisher"       "${PUBLISHER}"
  WriteRegStr HKLM "${REG_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${REG_KEY}" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegDWORD HKLM "${REG_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${REG_KEY}" "NoRepair" 1

  ; 언인스톨러 생성
  WriteUninstaller "$INSTDIR\Uninstall.exe"

SectionEnd

; ── 언인스톨 섹션 ─────────────────────────────────────────
Section "Uninstall"

  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"

  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"

  DeleteRegKey HKLM "${REG_KEY}"

SectionEnd
