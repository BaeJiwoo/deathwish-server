# PhysicsLab 맵 데이터

`PhysicsLab.json`은 Blender 씬의 메시 배치를 서버와 Unity에서 같이 읽기 위한 버전 1 형식이다. 원본은 별도 에셋 프로젝트의 `PhysicsLab.blend`다. 아래 명령은 이 저장소 루트에서 실행한다.

```powershell
$blender = 'C:/Program Files/Blender Foundation/Blender 4.2/blender.exe'
$source = '../deathwish-asset/PhysicsLab.blend'
& $blender --background --factory-startup --disable-autoexec $source --python-exit-code 1 --python tools/physics_lab/export_map.py -- --output maps/PhysicsLab/PhysicsLab.json
python tools/physics_lab/verify_map.py maps/PhysicsLab/PhysicsLab.json
```

## 좌표와 형식

- Blender 4.2.4 LTS에서 추출했다. 활성 씬의 메시 오브젝트를 이름순으로 기록하며, 각 오브젝트의 평가된 메시와 월드 변환을 사용한다.
- 좌표는 미터 단위의 **월드 좌표**다. Blender의 `(X, Y, Z)`를 `(X, Z, Y)`로 바꾸며 씬의 단위 배율을 곱한다. 소비자는 오브젝트 변환이나 축 변환을 다시 적용하지 않는다.
- 각 오브젝트에는 `name`, `vertices`, `triangles`가 있다. 삼각형 세 인덱스는 **해당 오브젝트의** 정점 배열을 0부터 참조한다. 앞에서 보이는 면의 순서는 시계 방향이다. 축 교환과 음수 스케일의 반전을 반영한다.
- 이 파일은 정적 지형용이다. 게임플레이 식별자, 재질, UV, 카메라, 플레이어 스폰은 담지 않는다.

## 검증 기준

바닥 X/Z 범위는 각각 −25~25 m이며, 네 벽은 높이 20 m이고 앞면은 실내를 향한다. 경사로는 Z = 18 m에서 −11 m 방향으로 올라가며, Z = 3.5 m에서 높이 3 m다. 발판 윗면은 6 m, 아랫면은 5.25 m다. 구멍의 X 범위는 약 −20.599~−13.885 m, Z 범위는 −20.95~−15.05 m다. 검증 스크립트는 삼각형을 직접 교차 검사해 구멍 아래의 바닥, 링 중앙 통로와 링 몸체를 확인한다.

동일 원본과 Blender 버전에서 두 번 추출한 UTF-8/LF 결과는 바이트 단위로 일치했다. 기준 출력에는 오브젝트 7개, 정점 616개, 삼각형 1,202개가 있다.
