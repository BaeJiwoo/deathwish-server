# 서버 테스트 가이드

Windows의 VS Code와 Unity에서 DeathWish 서버를 수동으로 확인하는 방법입니다. 먼저 **서버 실행 → 클라이언트 접속 → 이동 → 두 클라이언트 동기화** 순서로 진행합니다.

## 1. 준비

- VS Code에서 `C:\Users\gunwo\Projects\DeathWish` 폴더를 엽니다.
- **터미널 → 새 터미널**에서 PowerShell 터미널을 만듭니다. 모든 실행 명령은 저장소 루트 기준입니다.
- .NET 10 SDK가 필요합니다. `dotnet --list-sdks`로 `10.0.x` 설치 여부를 확인합니다.
- Unity 프로젝트는 `C:\Users\gunwo\Projects\deathwish-client`이며, 확인한 에디터 버전은 `6000.3.11f1`입니다.
- 이 파일을 연 뒤 `Ctrl+Shift+V`를 누르면 Markdown 미리보기를 볼 수 있습니다.

```powershell
Set-Location C:\Users\gunwo\Projects\DeathWish
```

Unity의 `SyncTestScene`은 미리 배치된 Canvas와 3인칭 카메라를 사용합니다. 업데이트하려면 Unity를 종료하고 9절의 설치 명령을 실행한 뒤 씬을 여세요. 실제 서버 접속과 이동 동기화는 아래 절차로 확인합니다.

## 2. 서버 실행 — 터미널 A

테스트 조건을 방 2개, 30Hz로 맞춘 뒤 실행합니다. 아래 환경변수는 현재 터미널과 여기서 실행한 서버에 적용되며 `.env` 파일은 수정하지 않습니다.

```powershell
$env:SERVER_ROOM_COUNT = '2'
$env:SERVER_TICK_RATE = '30'
dotnet run --project src/DeathWish.Server -- 127.0.0.1 7777
```

정상적으로 실행되면 다음 로그가 나옵니다. 서버 터미널은 그대로 실행해 둡니다.

```text
Listening on 127.0.0.1:7777. Rooms: 2. Tick rate: 30 Hz. Press Ctrl+C to stop.
```

| 항목 | 이 가이드의 테스트 설정 |
| --- | --- |
| 접속 주소 | `127.0.0.1:7777` |
| 사용 가능한 방 | `1`, `2` |
| 월드 갱신 | 초당 30회 |
| 전체 플레이어 스냅숏 전송 | 초당 10회 |
| 최대 이동 속도 | 초당 5m |
| 이동 입력 만료 | 새 입력이 없으면 250ms 후 정지 |

설정 우선순위는 **주소·포트 실행 인자 → 프로세스 환경변수 → `.env` → 코드 기본값**입니다. 설정은 서버 시작 시 읽습니다. `.env`가 없어도 실행되지만, 파일이 있다면 문법 오류가 없어야 합니다.

## 3. 기본 접속·Echo·방 입장 — 터미널 B

VS Code에서 두 번째 PowerShell 터미널을 열고 실행합니다.

```powershell
dotnet run --project mockclient/DeathWish.MockClient.Console -- 127.0.0.1 7777
```

`Connected.`가 나오면 **아래 내용은 PowerShell 명령이 아니라 실행 중인 MockClient에 입력**합니다. 한 줄씩 입력하고 Enter를 누릅니다.

```text
hello
/join 1
```

예상 결과입니다. PlayerId와 ServerTick은 실행마다 달라질 수 있습니다.

```text
Sent: hello
Received: hello
Echo PASS
Join room 1: Success
World ready: PlayerId=..., ServerTick=..., Spawn=(0, 0, 0)
```

- `Connected.`는 TCP 연결 성공입니다.
- `Join room 1: Success` 다음에 `World ready`까지 나와야 월드 입장이 완료된 것입니다.
- Echo는 보낸 클라이언트에게만 돌아옵니다. 다른 클라이언트에게 전달하는 채팅 기능은 아닙니다.
- 이 클라이언트는 `/quit`을 입력해 종료합니다. 다음 단계에서 별도 창으로 다시 실행합니다.

## 4. MockClient WASD 이동 — 별도 콘솔 창

**`/wasd`는 VS Code 통합 터미널과 Windows Terminal 탭에서 지원하지 않습니다.** 입력 포커스를 정확히 구분하기 위해 독립된 Windows Console Host 창을 사용합니다.

VS Code의 터미널 B에서 아래 명령으로 지원되는 창을 엽니다. 런처는 기본 주소 `127.0.0.1:7777`로 접속합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\mockclient\Start-MockClient.ps1
```

새 콘솔 창에서 한 줄씩 입력합니다.

```text
/join 1
/wasd
```

| 조작 | 확인할 결과 |
| --- | --- |
| W / S | 앞으로 / 뒤로 이동하며 좌표 변경 |
| A / D | 좌우 이동하며 좌표 변경 |
| 왼쪽 / 오른쪽 방향키 | 이동 기준 방향인 `Yaw` 변경 |
| W+D 등 대각선 입력 | 정상 이동, 회전하면서 눌러도 연결 유지 |
| 모든 이동 키 놓기 | 좌표가 멈춤 |
| 다른 창 클릭 | 이동 정지, 다른 창에서 WASD를 입력해도 이동하지 않음 |
| Esc | 정지 후 명령 입력 모드로 복귀 |
| 명령 모드에서 `/quit` | 연결 종료 |

WASD 모드에는 서버에서 받은 위치가 아래 형태로 표시됩니다.

```text
Tick ...: (X, Y, Z)  Players=1  Yaw=...
```

움직이지 않아도 Tick은 증가해야 합니다. 다른 테스트 클라이언트가 없다면 Players는 1입니다. 이 창을 연결한 상태로 두고 Unity 테스트를 이어갑니다.

## 5. Unity 접속과 이동

1. `deathwish-client` 프로젝트에서 `Assets/DeathWishMovement/Scenes/SyncTestScene.unity`를 엽니다. Canvas와 UI가 Play 전부터 Hierarchy에 있어야 합니다. 다른 빈 씬에는 UI가 자동 생성되지 않습니다.
2. Play를 누르고 Game 뷰의 접속 UI에 아래 값을 입력합니다.
3. **Connect and join**을 누릅니다.
4. 접속 메시지와 증가하는 Tick, **My server position (m)**의 X/Y/Z를 확인합니다.
5. Game 뷰에서 **Capture input**을 누릅니다. 마우스로 시점을 회전하고 WASD로 시점 기준 이동을 합니다.
6. Esc를 눌러 마우스를 해제하고 이동이 멈추는지 확인합니다.

| UI 항목 | 입력값 |
| --- | --- |
| Host | `127.0.0.1` |
| Port | `7777` |
| Room | `1` |

정상 결과:

- Game 뷰에는 초록색 본인 캡슐과 청록색 상대 캡슐, 격자 바닥이 표시됩니다. **My server position (m)**에는 본인의 서버 좌표가 나옵니다.
- Scene 뷰에서 본인은 초록색 `ME`, 상대는 청록색 `PLAYER` Gizmo로 표시됩니다. 각 표식에는 Player ID와 좌표가 붙습니다.
- WASD를 누르면 본인 캡슐과 좌표가 바뀌고 카메라가 뒤에서 따라갑니다. 마우스 좌우·상하로 카메라 시점이 바뀝니다.
- Console에 새 오류가 발생하지 않습니다.
- MockClient도 방 1에 접속해 있다면, 마우스를 해제한 UI에서 `Players: 2`가 보입니다.

플레이어 위치는 최신 서버 스냅숏을 그대로 표시합니다. 물리·충돌·점프는 포함하지 않습니다. Scene 뷰 Gizmo를 켜면 좌표와 ID를 추가로 확인할 수 있습니다.

## 6. 두 클라이언트 동기화·방 분리

Unity와 MockClient가 모두 방 1에 들어온 상태에서 진행합니다. 두 클라이언트는 같은 원점에서 시작하므로 먼저 한쪽을 이동해야 겹쳐 있는 캡슐을 구분할 수 있습니다.

| 순서 | 수행 방법 | 정상 결과 |
| --- | --- | --- |
| 1 | Unity에서 Esc로 마우스 해제 | 접속 UI에서 Players 2 확인 |
| 2 | MockClient 창을 클릭하고 WASD 모드에서 W를 약 2초 누른 뒤 놓기 | MockClient 좌표 변경, Unity Game 뷰의 청록색 캡슐 위치 변경 |
| 3 | MockClient에서 이동 키를 누르다가 다른 창으로 포커스 이동 | 입력이 정지하고, 다음 스냅숏들이 반영된 뒤 상대 캡슐도 멈춤 |
| 4 | MockClient에서 Esc, 이후 `/quit` | Unity의 상대 캡슐이 사라지고 Players 1로 변경 |
| 5 | 런처로 MockClient를 다시 열고 `/join 1`, `/wasd` | 상대 캡슐이 다시 생기고 양쪽 Players 2 |
| 6 | Unity에서 **Disconnect**, Room을 `2`로 바꾸고 **Connect and join** | Unity는 방 2, MockClient는 방 1에 각각 Players 1 |
| 7 | 방 1의 MockClient 이동 | 방 2의 Unity에 해당 캡슐이 나타나지 않음 |

한 연결에서 바로 다른 방으로 이동하는 기능은 없습니다. 방을 바꾸려면 연결을 끊고 다시 접속합니다. 두 화면의 Tick이 순간적으로 다르게 보이는 것은 수신·화면 갱신 시점 차이일 수 있습니다. 같은 틱의 스냅숏 일치 여부는 자동 테스트에서도 검사합니다.

## 7. 예외 상황 확인

MockClient의 명령 모드에서 수행합니다. WASD 모드였다면 Esc를 먼저 누릅니다. 아래 방 관련 결과는 서버를 방 2개로 실행했을 때 기준입니다.

| 상황 | 수행 방법 | 정상 결과 |
| --- | --- | --- |
| 존재하지 않는 방 | 새 연결에서 `/join 99` | `RoomNotFound`, 연결은 유지 |
| 방 입장 전 이동 | 새 연결에서 `/move 1 0` | 먼저 방에 입장하라는 안내 |
| 중복 입장 | 방 1 입장 후 다시 `/join 1` | `Success`, 기존 PlayerId 유지, 플레이어 수 증가 없음 |
| 연결 유지 상태에서 방 변경 | 방 1 입장 후 `/join 2` | `AlreadyInAnotherRoom`, 기존 방 유지 |
| 범위를 벗어난 방향 | `/move 2 0` | 클라이언트에서 입력 범위 안내, 전송하지 않음 |
| 한 번만 이동 입력 전송 | `/move 1 0` 후 추가 입력 없이 대기 | 같은 방의 Unity에서 잠깐 이동한 뒤 정지 |
| 명시적 정지 | `/move 0 0` | 이동 정지 |
| 서버 종료 | 서버 터미널 A에서 `Ctrl+C` | 클라이언트에서 연결 종료 감지 |

`/move`는 입력을 한 번만 보내므로 계속 이동하지 않습니다. 좌표도 일반 명령 모드에 매번 출력하지 않으므로 같은 방의 Unity로 관찰하거나 `/wasd`로 돌아가 좌표를 확인합니다. 잘못된 패킷을 서버가 직접 거부하는지는 자동 테스트에서 확인합니다.

서버 종료 시 `World loop stopped. Ticks: ...` 로그를 확인합니다. 이후 같은 서버 실행 명령으로 재시작해 포트가 정상적으로 다시 열리는지도 확인합니다. 클라이언트는 다시 접속해야 합니다.

## 8. 자동 테스트

새 PowerShell 터미널에서 실행합니다. 테스트 실행 파일이 필요한 서버와 임시 포트를 자체적으로 생성하므로 수동 서버를 먼저 켤 필요는 없습니다.

```powershell
dotnet run --project tests/DeathWish.ConnectionTests
$LASTEXITCODE
```

| 종료 코드 | 의미 |
| --- | --- |
| `0` | 자동 테스트 전체 통과 |
| `2` | TCP 권한 제한으로 네트워크 테스트 일부 생략. 전체 통과로 기록하지 않음 |
| 그 외 | 실패. 출력된 예외·빌드 오류 확인 |

주요 검증 범위는 패킷 분할·병합과 잘못된 입력, 방 입장·퇴장, 틱 기반 이동·입력 만료, 스냅숏·방 분리, 느린 연결의 출력 제한, 실제 TCP 접속과 서버 종료입니다. 이 저장소는 실행형 테스트 프로그램을 사용하므로 위의 `dotnet run` 명령으로 수행합니다.

Unity 연동 파일을 변경했을 때 추가로 확인합니다.

```powershell
dotnet build tests/DeathWish.UnityCompileChecks
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Verify-UnityInstall.ps1
```

- UnityCompileChecks는 설치된 `6000.3.11f1` API로 C#을 컴파일합니다. Play 모드나 실제 렌더링 검증을 대신하지 않습니다.
- Verify-UnityInstall은 별도의 임시 프로젝트에서 설치·복구를 검사합니다. 실제 `deathwish-client`에 적용하는 명령이 아닙니다.

## 9. 문제가 생겼을 때

| 증상 | 확인할 항목 |
| --- | --- |
| 접속 거부 또는 연결 시간 초과 | 서버의 Listening 로그, IP·포트 일치 여부. 클라이언트 접속·입장은 5초 제한 |
| 서버 포트 바인딩 실패 | 같은 주소·포트의 서버를 중복 실행했는지 확인. 기존 서버는 해당 터미널에서 Ctrl+C로 종료 |
| `Server configuration error` | `.env` 문법과 유효한 설정값 확인. 방 개수·틱 레이트는 양수 |
| VS Code에서 `/wasd` 거부 | `mockclient/Start-MockClient.ps1`로 독립 콘솔 실행 |
| Tick 0 / Players 0 유지 | TCP 연결뿐 아니라 Connect and join 또는 `/join 1`의 월드 입장까지 성공했는지 확인 |
| 접속했지만 Unity에서 움직이지 않음 | Capture input 클릭, Game 뷰 포커스, Active Input Handling이 Input Manager (Old) 또는 Both인지 확인 |
| 상대 플레이어가 보이지 않음 | 같은 방에 접속했는지 확인하고 한쪽을 원점에서 이동. 마우스로 카메라 시점을 돌리거나 Scene 뷰 Gizmos로 상대 좌표 확인 |
| 빈 씬에서 UI가 나오지 않음 | `Assets/DeathWishMovement/Scenes/SyncTestScene.unity` 열기. Canvas와 버튼은 씬에 미리 저장되어 있음 |
| Unity `Char: 65279` 또는 `inputlegacy` 패키지를 찾을 수 없음 | 아래 manifest 복구 명령 실행 |
| 알 수 없는 패킷·구버전 DLL 오류 | 서버와 클라이언트를 같은 소스 버전으로 빌드하고 Unity 설치 스크립트 재실행 |
| `NU1900` | NuGet 취약점 정보 조회 경고. 빌드 실패 여부와 자동 테스트 종료 코드를 별도로 확인 |
| `SKIP TCP lifecycle tests` | 실행 환경에서 TCP가 허용되지 않음. 로컬에서 다시 실행해 종료 코드 0 확인 |

Unity를 종료한 상태에서, manifest의 두 가지 알려진 오류만 복구할 때:

```powershell
powershell -ExecutionPolicy Bypass -File .\integrations\Unity\Repair-Manifest.ps1 -UnityProject ..\deathwish-client
```

프로토콜·Core DLL이나 Unity 스크립트를 수정한 뒤 연동 파일까지 갱신할 때:

```powershell
powershell -ExecutionPolicy Bypass -File .\integrations\Unity\Install.ps1 -UnityProject ..\deathwish-client
```

## 10. 수동 확인 체크리스트

실제로 확인한 항목만 체크합니다.

- [ ] 서버가 지정한 주소·포트에서 실행된다.
- [ ] Echo PASS와 World ready를 확인했다.
- [ ] MockClient WASD 이동·회전·정지가 정상이다.
- [ ] Unity 씬에 Play 전부터 Canvas·입력칸·버튼이 있다.
- [ ] Unity 접속 후 본인의 X/Y/Z, Player ID, Tick이 표시된다.
- [ ] WASD로 위치가 바뀌고, 마우스로 시점을 돌리면 3인칭 카메라가 회전한다.
- [ ] 같은 방의 두 클라이언트에 Players 2가 표시된다.
- [ ] 상대 이동과 접속 종료가 다른 클라이언트에 반영된다.
- [ ] 다른 방의 플레이어는 보이지 않는다.
- [ ] 포커스가 없을 때와 입력 갱신이 없을 때 이동이 멈춘다.
- [ ] 서버 종료를 클라이언트가 감지하고, 서버 재실행·재접속도 성공한다.
- [ ] 자동 테스트가 생략 없이 종료 코드 0으로 완료된다.

## 11. 서버 권위 걷기 애니메이션

서버와 Unity 클라이언트의 `codex/server-authoritative-animation` 브랜치로 접속합니다. Unity의 **Use Server Animation**은 기본으로 활성화되며 일반 MockClient 접속도 같은 방에 참여할 수 있습니다. 서버가 실제 수평 이동으로 방향과 걷기 위상을 계산하므로 움직임이 멈추거나 벽에 막히면 캐릭터가 정지 자세로 표시됩니다.

`python3 tools/animation/bake_walk.py --check`로 60 Hz 걷기 테이블의 재현성을 확인할 수 있습니다. 전체 ConnectionTests는 프레임 경계, 기존 접속과 애니메이션 접속 혼용, 지연 입력 만료, 세계 좌표 히트박스를 검사합니다.

Unity 배치 검증은 별도 클라이언트 복사본의 `Assets/Editor`에 `ServerAnimationSceneCheck.cs`와 `ServerAnimationPoseReferenceCheck.cs`를 넣고 `-executeMethod DeathWish.Client.Editor.ServerAnimationSceneCheck.Run`으로 실행합니다. `DW_ANIMATION_TEST_PORT`에 방 1을 사용할 서버의 로컬 TCP 포트를 지정하면 실제 접속과 원격 관절 이동도 검증합니다.

관련 문서 [프로토콜](Protocol.md), [구현 계획](ImplementationPlan.md), [Unity 연동 상세](../integrations/Unity/README.md), [개발 기록](devlog/README.md).
