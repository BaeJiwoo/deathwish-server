# DeathWish

**게임 서버** · [Unity 클라이언트](https://github.com/BaeJiwoo/deathwish-client) · [게임 에셋](https://github.com/BaeJiwoo/deathwish-assets)

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

## 1. 서버 실행

.NET 10 SDK가 필요합니다. 기본 주소는 `127.0.0.1:7777`이며, 아래 로컬 PhysicsLab 예제에서는 서버, Unity, MockClient 모두 `127.0.0.1:17779`를 사용합니다. 저장소 루트에서 실행하세요.

```powershell
$env:SERVER_ROOM_COUNT = '2'
$env:SERVER_TICK_RATE = '30'
dotnet run --project src/DeathWish.Server -- 127.0.0.1 17779
```

`.env` 또는 프로세스 환경변수에 `SERVER_TICK_RATE=60`을 설정하면 서버 재시작 후 모든 방의 목표 틱 레이트가 60Hz로 바뀝니다. 기본값은 30Hz이며, 자세한 설정은 [실행 문서](docs/Protocol.md#run)를 참조합니다.

`Ctrl+C`로 서버를 종료합니다. 잘못된 인자나 포트 바인딩 실패는 표준 오류에 출력되며 종료 코드는 1입니다.

## 2. MockClient 실행

서버를 켜 둔 채 **서버 저장소 루트의 두 번째 PowerShell**에서 실행합니다. Windows에서 WASD 조작을 하려면 다음 런처로 독립 콘솔 창을 엽니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\mockclient\Start-MockClient.ps1 -Port 17779
```

새로 열린 **MockClient 창**에서 한 줄씩 입력합니다.

```text
/join 2
/wasd
```

`World ready`가 뜨면 입장이 끝납니다. WASD로 이동하고 좌우 방향키로 방향을 바꿉니다. Esc로 명령 모드에 돌아간 뒤 `/quit`으로 종료합니다.

VS Code 터미널이나 Linux에서 Echo와 이동 패킷을 확인할 때는 아래 명령으로 실행합니다. 접속 후 `hello`를 입력하면 `Echo PASS`가 출력됩니다. 이어서 `/join 2`, `/move 1 0`을 차례로 입력하면 이동 입력을 한 번 보냅니다.

```powershell
dotnet run --project mockclient/DeathWish.MockClient.Console -- 127.0.0.1 17779
```

## 3. Unity 클라이언트 실행

1. Unity Hub에서 별도 저장소 `deathwish-client`의 루트(`Assets`, `Packages`, `ProjectSettings`가 있는 폴더)를 **6000.3.11f1**로 엽니다. 점프·레이캐스트 확인에는 양쪽 저장소의 `feat/jump-and-raycast` 버전을 사용합니다. 이 클라이언트에는 실행에 필요한 DLL이 포함되어 있습니다.
2. Project 창에서 `Assets/DeathWishMovement/Scenes/SyncTestScene.unity`를 열고 **Play**를 누릅니다.
3. Game 뷰에서 Host `127.0.0.1`, Port `17779`, Room `2`를 확인하고 **Connect and join**을 누릅니다. `Connected | PhysicsLab`과 증가하는 Tick을 확인합니다.
4. **Capture input**을 누릅니다. WASD는 이동, 마우스는 시점 회전, Space는 점프입니다. 좌클릭하면 카메라 방향의 레이캐스트 결과가 상태창과 Unity Console에 표시됩니다. Esc로 커서를 해제하고 **Disconnect**로 접속을 종료합니다.

MockClient도 같은 방에 남아 있으면 Unity에 **Players: 2**가 표시됩니다. MockClient를 움직여 노란색 상대 캡슐의 이동을 확인합니다. 파란색은 본인입니다. 방 개수를 바꾸면 가장 높은 번호의 방이 PhysicsLab입니다. 서버를 기본 포트 `7777`로 기동했다면 MockClient의 포트와 Unity의 Port 입력값도 `7777`로 맞춥니다.

기존 Unity 프로젝트의 DLL과 스크립트를 갱신할 때는 에디터를 닫고 서버 루트에서 다음 명령을 사용합니다. `-UnityProject`에는 실제 클라이언트 루트 경로를 지정합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\integrations\Unity\Install.ps1 -UnityProject ..\deathwish-client
```

두 클라이언트의 동기화·방 분리·예외 상황은 [테스트 가이드](docs/Testing.md), 설치 파일과 패키지 복구는 [Unity 연동 가이드](integrations/Unity/README.md)에 정리했습니다.

## 개발 진행

서버는 방별 이동·중력·충돌·점프를 계산하고 스냅숏을 전송합니다. Unity는 수신한 위치로 플레이어를 표시하며, 좌클릭 레이캐스트의 명중 대상·거리 또는 빗나감은 서버가 판정해 요청자에게 돌려줍니다.

단계별 구현 항목과 리뷰 기준은 [구현 계획](docs/ImplementationPlan.md), 코딩 규칙은 [AGENTS.md](AGENTS.md)에 기록합니다. 각 단계를 구현·검증한 뒤 코드 리뷰를 거쳐 다음 단계로 진행합니다.

장기 개발 목표는 다음과 같습니다.

- **서버 권한 기반 전투:** 서버가 이동, 사격, 피격 등 주요 게임 상태와 전투 결과를 판정하는 시스템 구축
- **매치메이킹:** 플레이어의 매치 참가 요청부터 게임 서버 입장까지 이어지는 시스템 구현
- **플레이 데이터 수집 및 추적:** 게임 플레이 기록을 수집하고, 향후 데이터 분석 파이프라인으로 확장
