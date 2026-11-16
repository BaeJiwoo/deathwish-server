# DeathWish

**Server-Authoritative 방식의 FPS 데스매치 게임 서버 개발 프로젝트입니다.**

이 저장소에서는 C# 게임 서버를 개발합니다. Unity PC 클라이언트는 별도 프로젝트에서 개발하며, 바이너리 패킷 명세에 따라 서버와 통신합니다.

오버워치와 같은 하이퍼 슈팅 게임의 서버를 직접 구현하고자 시작했습니다. 서버가 게임 상태와 전투 결과를 판정하는 구조를 구축하고, 매치메이킹과 게임 플레이 데이터 수집까지 구현하는 것을 목표로 합니다.

## 프로젝트 개요

| 항목 | 내용 |
| --- | --- |
| 장르 | FPS 데스매치 |
| 목표 플랫폼 | PC |
| 초기 서버 기술 스택 | C#, .NET, DotNetty, TCP |
| 서버 실행 환경 | Windows, Linux |
| 연동 클라이언트 | Unity PC 클라이언트, 별도 프로젝트 |
| 향후 검토 기술 | ASP.NET Core, MySQL, Redis |
| 프로젝트 목표 | Server-Authoritative 슈팅 게임 시스템 구축 |
| 프로젝트 링크 | [BaeJiwoo/DeathWish](https://github.com/BaeJiwoo/DeathWish) |

## 서버 실행

.NET 10 SDK가 필요합니다. 기본 주소는 `127.0.0.1:7777`이며, IP 주소와 포트를 인자로 지정할 수 있습니다.

```powershell
dotnet run --project src/DeathWish.Server -- 127.0.0.1 7777
```

`.env` 또는 프로세스 환경변수에 `SERVER_TICK_RATE=60`을 설정하면 서버 재시작 후 모든 방의 목표 틱 레이트가 60Hz로 바뀝니다. 기본값은 30Hz이며, 자세한 설정은 [실행 문서](docs/Protocol.md#run)를 참조합니다.

`Ctrl+C`로 서버를 종료합니다. 잘못된 인자나 포트 바인딩 실패는 표준 오류에 출력되며 종료 코드는 1입니다.

## 개발 진행

Stage 7 snapshots and WASD clients are implemented and awaiting review. Unity's SyncTestScene displays local server coordinates on its preplaced Canvas, shows players in a third-person Game view, and keeps Scene-view Gizmos for diagnostics. Run the installer to update the Unity client. Follow the [server test guide](docs/Testing.md) for VS Code, MockClient and Unity checks.

단계별 구현 항목과 리뷰 기준은 [구현 계획](docs/ImplementationPlan.md), 코딩 규칙은 [AGENTS.md](AGENTS.md)에 기록합니다. 각 단계를 구현·검증한 뒤 코드 리뷰를 거쳐 다음 단계로 진행합니다.

장기 개발 목표는 다음과 같습니다.

- **서버 권한 기반 전투:** 서버가 이동, 사격, 피격 등 주요 게임 상태와 전투 결과를 판정하는 시스템 구축
- **매치메이킹:** 플레이어의 매치 참가 요청부터 게임 서버 입장까지 이어지는 시스템 구현
- **플레이 데이터 수집 및 추적:** 게임 플레이 기록을 수집하고, 향후 데이터 분석 파이프라인으로 확장

## WASD movement test

Manual steps, expected results and troubleshooting: [서버 테스트 가이드](docs/Testing.md).

Console `/join 1` → `/wasd`; Unity setup and manual verification: [movement guide](integrations/Unity/README.md).

Unity opening error `Char: 65279`: rerun `integrations/Unity/Install.ps1` with Unity closed; it repairs the UTF-8 BOM and updates the client. Console WASD now requires a standalone Console Host window; use `mockclient/Start-MockClient.ps1` instead of a VS Code/Windows Terminal tab.
