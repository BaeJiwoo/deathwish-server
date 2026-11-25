# 팀 데스매치 PoC 경기장

`TeamDeathmatch.blend`는 32 m × 38 m 경기장의 Blender 원본이다. 경기장 내부의 바닥 범위는 X = −16~16 m, Z = −19~19 m다. 양 끝에 팀별 스폰 5개와 엄폐물이 있다. 동쪽에는 아치와 외다리, 서쪽에는 블록 점프 코스가 있다. Blender에서는 `ArenaGeometry`의 메시와 `TeamSpawns`의 Empty를 편집한다.

기존 스폰과 구조물 좌표를 유지한 채 바닥과 경계 벽을 사방으로 4 m씩 확장했다. X = ±14 m의 양쪽 통로와 Z = ±17 m의 끝 통로를 연결해 경기장 외곽을 한 바퀴 돌 수 있다.

## 동쪽 물리 검증 경로

| 지형 | 월드 좌표와 구조 | 확인할 동작 |
| --- | --- | --- |
| `EastArch` | 중심 X = 4.5 m, Z = −7.7~−6.8 m. 원호형 개구부 폭 2.6 m, 중심 천장 높이 2.72 m | 가운데로 왕복하고, 기둥에 비스듬히 접근하거나 천장 아래에서 점프한다. |
| `EastNarrowBridge` | X = 3.975~5.025 m. Z = −6~−2 m에서 올라가고, Z = −2~4 m에서 높이 2.2 m를 유지한다. Z = 4~8 m에서 바닥으로 내려간다. | 경사로와 발판 접합부를 통과하고, 발판에서 정지하거나 옆으로 떨어져 착지한다. |

외다리는 폭 1.05 m인 연속 메시다. 발판에는 난간이 없고, Z = 0 m 부근에서는 다리 옆으로 떨어져 바닥에 착지하거나 높이 1.95 m의 발판 아래로 지나갈 수 있다.

## 서쪽 블록 점프 코스

서버 기본 설정인 이동 속도 5 m/s, 중력 9.81 m/s², 30 Hz에서 점프를 측정했다. 발의 위치를 기준으로 같은 높이에 착지할 때 **6.167 m**, 0.4 m 높은 곳에 착지할 때 **5.833 m** 이동했다. 점프 최고점은 이륙 높이에서 1.804 m였다.

서쪽 X = −10.4 m에서 남쪽 램프로 올라가 북쪽으로 점프한다. 발판 사이의 거리는 마주 보는 두 가장자리 기준이다.

| 발판 | 상판 높이 | 직전 발판과 사이 간격 |
| --- | --- | --- |
| 1 | 0.75 m | 진입 램프 |
| 2 | 1.00 m | 2.10 m |
| 3 | 1.35 m | 3.00 m |
| 4 | 1.15 m | 4.00 m |
| 5 | 1.40 m | 4.65 m |

첫 발판은 진입 램프와, 마지막 발판은 하차 램프와 이어진다. 짧은 점프에서는 공중에서 전진 입력을 놓아 착지 지점을 맞출 수 있다. 발판 사이로 떨어지면 경기장 바닥에 착지한다.

## 공유 JSON 다시 만들기

Blender 5.2.3 LTS로 다음 명령을 저장소 루트에서 실행한다.

```powershell
$blender = 'C:/Program Files/Blender Foundation/Blender 5.2/blender.exe'
& $blender --background --factory-startup --disable-autoexec maps/TeamDeathmatch/TeamDeathmatch.blend --python-exit-code 1 --python tools/physics_lab/export_map.py -- --map-id 2 --output maps/TeamDeathmatch/TeamDeathmatch.json
python -B tools/physics_lab/verify_team_deathmatch.py maps/TeamDeathmatch/TeamDeathmatch.json
```

서버는 `TeamDeathmatch.json`의 메시를 BepuPhysics에 등록하고 Unity는 같은 파일의 메시를 화면에 표시한다. 생성 JSON을 함께 갱신하고, 설치된 Unity 리소스와 서버 빌드 입력의 SHA-256이 같은지 확인한다.

## 버전 2 형식

- 지형은 [PhysicsLab의 좌표 계약](../PhysicsLab/README.md#좌표와-형식)을 따른다. 모든 위치는 미터 단위 월드 좌표이며 Blender의 `(X, Y, Z)`를 `(X, Z, Y)`로 변환한다. 삼각형 앞면 순서는 시계 방향이다.
- `mapId`는 팀 데스매치를 나타내는 `2`다. `spawns`의 각 항목은 Empty의 이름인 `id`, 정수 사용자 속성인 `team`, 발 기준 월드 좌표인 `position`, 수평 단위 벡터인 `forward`를 담는다.
- `TeamSpawns`는 팀별 Empty를 5개씩 포함한다. 팀 1은 빨강, 팀 2는 파랑이며, Empty의 로컬 +Y를 진행 방향으로 사용한다. 내보내기는 스폰을 팀과 이름 순서로 정렬한다.
- 검증 스크립트는 확장된 경계와 외곽 통로, 엄폐·기존 통로·스폰, 아치·외다리, 서쪽 블록 발판의 높이와 간격 및 바닥 착지 면을 검사한다.
