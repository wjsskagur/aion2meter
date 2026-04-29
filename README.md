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

---


## 주의사항

- 게임사 공식 API 아님, 패킷 분석 기반
- 관리자 권한 실행 필수
