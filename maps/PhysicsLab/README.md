# PhysicsLab 맵 데이터

`PhysicsLab.json`은 Blender 씬의 메시 배치를 서버와 Unity에서 같이 읽기 위한 버전 1 형식이다. 원본은 별도 에셋 프로젝트의 `PhysicsLab.blend`다. 아래 명령은 이 저장소 루트에서 실행한다.

```powershell
$blender = 'C:/Program Files/Blender Foundation/Blender 4.2/blender.exe'
$source = '../deathwish-asset/Map/PhysicsLab.blend'
& $blender --background --factory-startup --disable-autoexec $source --python-exit-code 1 --python tools/physics_lab/export_map.py -- --output maps/PhysicsLab/PhysicsLab.json
python tools/physics_lab/verify_map.py maps/PhysicsLab/PhysicsLab.json
```

## 좌표와 형식

Blender에서 지형 가장자리를 수정하면 에셋 저장소의 `Map/build_caution_lines.py`를 열린 맵에서 실행하고 저장한 뒤 다시 내보낸다. 스크립트는 바닥과 벽 가장자리, 경사로 및 발판 바깥과 구멍 안쪽에 폭 36cm의 경고선을 생성한다. Blender의 Material Preview와 Unity에서 같은 간격으로 노랑·검정 사선을 표시한다. JSON의 `objects`는 서버 충돌용이고 `caution`은 화면 표시용이다.

- Blender 4.2.4 LTS에서 추출했다. 활성 씬의 메시 오브젝트를 이름순으로 기록하며, 각 오브젝트의 평가된 메시와 월드 변환을 사용한다.
- 좌표는 미터 단위의 **월드 좌표**다. Blender의 `(X, Y, Z)`를 `(X, Z, Y)`로 바꾸며 씬의 단위 배율을 곱한다. 소비자는 오브젝트 변환이나 축 변환을 다시 적용하지 않는다.
- 각 오브젝트에는 `name`, `vertices`, `triangles`가 있다. 삼각형 세 인덱스는 **해당 오브젝트의** 정점 배열을 0부터 참조한다. 앞에서 보이는 면의 순서는 시계 방향이다. 축 교환과 음수 스케일의 반전을 반영한다.
- 이 파일은 정적 지형용이다. 게임플레이 식별자, 재질, UV, 카메라, 플레이어 스폰은 담지 않는다.

## 검증 기준

바닥 X/Z 범위는 각각 −25~25 m이며, 네 벽은 높이 20 m이고 앞면은 실내를 향한다. 경사로는 Z = 18 m에서 −11 m 방향으로 올라가며, Z = 3.5 m에서 높이 3 m다. 발판 윗면은 6 m, 아랫면은 5.25 m다. 구멍의 X 범위는 약 −20.599~−13.885 m, Z 범위는 −20.95~−15.05 m다. 검증 스크립트는 삼각형을 직접 교차 검사해 구멍 아래의 바닥, 링 중앙 통로와 링 몸체를 확인한다.

동일 원본과 Blender 버전에서 두 번 추출한 UTF-8/LF 결과는 바이트 단위로 일치했다. 기준 출력에는 오브젝트 7개, 정점 616개, 삼각형 1,202개가 있다.

## 서버에서 선택하기

방 번호는 1부터 시작하며 기본적으로 가장 큰 번호가 PhysicsLab이다. `SERVER_ROOM_COUNT`가 5이면 기본값으로 1~4번은 평지, 5번은 PhysicsLab이다. `SERVER_TDM_ROOM_ID`를 가장 큰 방 번호로 설정하면 해당 방은 팀 데스매치 경기장을 사용한다.

빌드에 포함된 JSON을 읽어 오브젝트별로 Bepu 정적 메시 하나를 등록한다. 로컬 좌표로 옮긴 정점과 오브젝트 중심을 함께 사용해 월드 배치를 복원한다. 앞면 순서는 추출 결과를 유지하며 입장 시 원점이 비어 있으면 그곳에, 사용 중이면 주변 바닥에 플레이어의 발을 배치한다. Unity의 SyncTestScene은 입장 응답에 따라 동일한 JSON에서 지형을 표시한다. Play 모드에서 서버에 설정한 방 번호로 접속한 뒤 Capture input을 눌러 WASD로 이동한다.
