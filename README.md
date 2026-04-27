# Aion2 Meter

아이온2 전투 분석을 위한 DPS 미터기

## 사용자 설치

[**최신 버전 다운로드**](https://github.com/wjsskagur/aion2meter/releases/latest) → `Aion2Meter-Setup.exe` 실행

- Npcap 자동 설치 포함
- 관리자 권한 자동 요청
- 별도 런타임 설치 불필요

---

## 개발자 가이드

### 레포지토리 구조

```
aion2meter/
├── .github/
│   └── workflows/
│       └── build.yml          ← GitHub Actions (자동 빌드/배포)
├── Aion2Meter/                ← C# 소스
│   ├── Models/
│   ├── Services/
│   │   ├── PacketCaptureService.cs
│   │   ├── PacketParserService.cs   ← 패킷 분석 후 수정 필요
│   │   ├── UpdateCheckerService.cs
│   │   └── ...
│   ├── ViewModels/
│   ├── Views/
│   └── Aion2Meter.csproj
├── installer/
│   └── Aion2Meter.nsi         ← NSIS 인스톨러 스크립트
└── build.bat                  ← 로컬 빌드용
```

### 로컬 빌드

```batch
build.bat
```

### 배포 방법

버전 태그를 push하면 자동으로 빌드 + Setup.exe + Release가 생성됩니다.

```bash
git tag v1.0.0
git push origin v1.0.0
```

Actions 탭에서 진행 확인 → 완료되면 Releases에 `Aion2Meter-Setup.exe` 자동 업로드

---

## 패킷 분석 가이드

`PacketParserService.cs`의 OpCode와 오프셋을 실제 값으로 수정 필요.

Wireshark로 아이온2 트래픽 캡처 후 반복 패턴 분석 → 수정 후 태그 push → 자동 배포.

---

## 주의사항

- 게임사 공식 API 아님, 패킷 분석 기반
- 패킷 암호화 적용 시 동작 불가
- 관리자 권한 실행 필수
