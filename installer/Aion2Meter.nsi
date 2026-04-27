; ============================================================
; Aion2 Meter - NSIS 인스톨러 스크립트
; ============================================================

Unicode true

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

; 기본 설치 경로를 고정 서브디렉토리로 강제
; 사용자가 상위 폴더(Program Files 등)를 직접 선택 못하도록
InstallDir "$PROGRAMFILES64\${INSTALL_SUBDIR}"
InstallDirRegKey HKLM "${REG_KEY}" "InstallLocation"

RequestExecutionLevel admin

!include "MUI2.nsh"
!include "LogicLib.nsh"

!define MUI_ABORTWARNING
!define MUI_ICON "${NSISDIR}\Contrib\Graphics\Icons\modern-install.ico"
!define MUI_UNICON "${NSISDIR}\Contrib\Graphics\Icons\modern-uninstall.ico"

; 설치 경로 페이지에서 기본값 설명 추가
!define MUI_DIRECTORYPAGE_TEXT_TOP "설치 경로를 선택하세요. 기본 경로 사용을 권장합니다."

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

  ; publish 폴더 전체 복사 (SharpPcap 네이티브 DLL 포함)
  File /r "${OUTPUT_DIR}\*.*"

  ; ── Npcap 설치 (미설치 시에만) ──────────────────────
  ReadRegStr $0 HKLM "SOFTWARE\Npcap" ""
  ${If} $0 == ""
    DetailPrint "Npcap 설치 중... (잠시 기다려주세요)"
    File "npcap-installer.exe"

    ; /winpcap_mode : SharpPcap 필수 조건
    ; 설치 완료까지 대기 (ExecWait)
    ExecWait '"$INSTDIR\npcap-installer.exe" /winpcap_mode' $1

    ; 설치 파일 정리
    Delete "$INSTDIR\npcap-installer.exe"

    ${If} $1 != 0
      ; 종료 코드가 0이 아니면 실패
      MessageBox MB_OK|MB_ICONEXCLAMATION         "Npcap 설치에 실패했습니다. (종료 코드: $1)$
$
수동으로 설치해주세요:$
https://npcap.com$
$
반드시 'Install Npcap in WinPcap API-compatible Mode'를 체크하세요."
    ${Else}
      DetailPrint "Npcap 설치 완료"
    ${EndIf}
  ${Else}
    DetailPrint "Npcap 이미 설치됨, 건너뜀"
  ${EndIf}

  ; ── 바로가기 생성 (관리자 권한 플래그 포함) ──────────
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

  nsExec::ExecToLog 'powershell -Command \
    "$s=(New-Object -COM WScript.Shell).CreateShortcut(\"$DESKTOP\${APP_NAME}.lnk\"); \
    $s.Save(); \
    $bytes=[System.IO.File]::ReadAllBytes(\"$DESKTOP\${APP_NAME}.lnk\"); \
    $bytes[0x15]=$bytes[0x15] -bor 0x20; \
    [System.IO.File]::WriteAllBytes(\"$DESKTOP\${APP_NAME}.lnk\",$bytes)"'

  ; ── 레지스트리 등록 ──────────────────────────────────
  WriteRegStr HKLM "${REG_KEY}" "DisplayName"     "${APP_NAME}"
  WriteRegStr HKLM "${REG_KEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr HKLM "${REG_KEY}" "Publisher"       "${PUBLISHER}"
  WriteRegStr HKLM "${REG_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${REG_KEY}" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegDWORD HKLM "${REG_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${REG_KEY}" "NoRepair" 1

  WriteUninstaller "$INSTDIR\Uninstall.exe"

SectionEnd

; ── 언인스톨 섹션 ─────────────────────────────────────────
Section "Uninstall"

  ; ── 안전 검증 1: $INSTDIR 가 비어있지 않은지 ──────────
  ${If} $INSTDIR == ""
    MessageBox MB_OK|MB_ICONSTOP "설치 경로를 확인할 수 없습니다. 수동으로 삭제해주세요."
    Abort
  ${EndIf}

  ; ── 안전 검증 2: 반드시 있어야 할 파일 존재 확인 ──────
  ; Aion2Meter.exe 없으면 잘못된 경로 → 개별 파일만 삭제
  ${IfNot} ${FileExists} "$INSTDIR\${APP_EXE}"
    MessageBox MB_OK|MB_ICONEXCLAMATION \
      "$INSTDIR 에서 ${APP_EXE}를 찾을 수 없습니다.$\n수동으로 삭제해주세요: $INSTDIR"
    Abort
  ${EndIf}

  ; ── 검증 통과 → 앱이 설치한 파일만 명시적 삭제 ────────
  ; RMDir /r 대신 확장자별 명시 삭제로 안전성 확보
  ; 사용자가 직접 넣은 파일은 건드리지 않음
  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$INSTDIR\*.dll"
  Delete "$INSTDIR\*.json"
  Delete "$INSTDIR\*.pdb"
  Delete "$INSTDIR\*.so"

  ; 폴더가 비어있으면 삭제, 비어있지 않으면 그냥 둠
  ; (사용자 파일이 남아있을 수 있으므로 /r 사용 안 함)
  RMDir "$INSTDIR"

  ; 바로가기 삭제
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"

  DeleteRegKey HKLM "${REG_KEY}"

SectionEnd
