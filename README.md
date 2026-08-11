# 3DPrinter-onUnity-byROS
 
3D 프린터(Voron 2.4 (250mm))의 Unity ↔ ROS2 디지털 트윈.
 
```
STEP ──▶ 링크별 메시 ──▶ URDF ──▶ Unity (ArticulationBody)
                          ▲            │
                 joint_states│         │/printer/cmd
                          ROS2 (ROS-TCP-Endpoint)
                          ▲
                 G-code 플레이어
```
 
## 문서
 
| 문서 | 대상 | 내용 |
|---|---|---|
| [00_interface_contract.md](docs/00_interface_contract.md) | **전원 필독** | 좌표계, 단위, 링크/조인트 이름, 토픽, 소유권, 검증 |
| [01_cad_workflow.md](docs/01_cad_workflow.md) | A | FreeCAD — STEP → 링크 메시 + 조인트 실측 |
| [02_unity_workflow.md](docs/02_unity_workflow.md) | B | Unity — URDF 임포트, 조인트 구동, 압출 시각화 |
| [03_ros2_workflow.md](docs/03_ros2_workflow.md) | C | ROS2 — URDF, mock/G-code 노드, 브릿지, launch |
 
## 빠른 시작
 
```bash
git clone <repo> && cd 3DPrinter-onUnity-byROS
git lfs install && git lfs pull
 
cd ros2_ws && colcon build --symlink-install && source install/setup.bash
ros2 launch voron24_bringup mock.launch.py
```
 
별도로 Unity 프로젝트(`unity/Voron24Twin`)를 열고 Play.
 
```bash
# 축 방향 검증
ros2 launch voron24_bringup mock.launch.py pattern:=sweep
# A의 메시 적용
ros2 launch voron24_bringup mock.launch.py use_meshes:=true
```
 
## 검증
 
```bash
# 계약 위반 자동 검출 (커밋 전 필수)
python3 tools/contract_check.py --xacro ros2_ws/src/voron24_description/urdf/voron24.urdf.xacro
 
# 통합 게이트
bash tools/smoke_test.sh
 
# 궤적 로직 단독 테스트 (ROS 불요)
cd ros2_ws/src/voron24_gcode && python3 -m voron24_gcode.patterns
```
 
## ROS2 - Unity 연결

`/joint_states` 를 각각 독립 출력. **RViz 는 대조군**으로써 함께 실행 —
Unity 와 비교해서 error 판단.

```
mock_publisher ──/joint_states──┬── robot_state_publisher ──/tf──▶ RViz
                                └── ros_tcp_endpoint(:10000) ──────▶ Unity
```

### 실행

터미널 A.

```bash
cd ros2_ws
source install/setup.bash
ros2 launch voron24_bringup mock.launch.py pattern:=sweep period:=30
```

`pattern:=sweep` 은 축을 하나씩 왕복시켜 축 매핑과 부호를 검증. 디버깅 시
첫 선택. `period` 는 30 이상을 쓴다 — URDF 의 `vel_z` 는 0.05 라 기본값 12 로 sweep 을 돌리면 Z가 0.125 m/s로 요구 제한 속도를 초과.

**launch 출력에서 주의점.**

```
[mock_publisher-2] [INFO]: mock publisher | pattern=sweep rate=50.0Hz period=30.0s
```

노드가 죽어도 launch 는 계속 살아 있으므로 겉보기엔 성공한 것처럼 보일 수 있음.

이후 Unity에서 별도 설정 없이 **Play**. Console에 이 세 줄이 출력되는 지 확인.

```
[JointState] 자기충돌 해제: 콜라이더 4 개, 6 쌍
[JointState] bound 3/3 joints under 'voron24'
[JointState] subscribed to /joint_states
```

RViz 와 Unity 에서 갠트리가 X → Y → Z 순으로 왕복 시 성공.

### 확인 (터미널 B)

```bash
cd ros2_ws && source install/setup.bash
ros2 topic echo /joint_states --once --no-daemon   # 값이 흐르는지
ss -ltn | grep 10000                               # 엔드포인트가 열렸는지
```

`--no-daemon`: `ros2 topic list` 가 무응답이면 노드가 아니라 데몬이 먹통인 경우 — `ros2 daemon stop`

### 자주 쓰는 인자

| 인자 | 기본값 | 용도 |
|---|---|---|
| `pattern:=` | `lissajous` | `home` / `sweep` / `square` / `lissajous` |
| `period:=` | `12.0` | 한 주기 [s] |
| `rviz:=false` | `true` | Unity 만 볼 때. **문제 생기면 다시 켤 것** |
| `unity:=false` | `true` | ROS 파이프라인만 격리 |
| `use_meshes:=true` | `false` | A 의 STL. 메시가 들어오기 전엔 쓰지 말 것 |

## 팀 규칙
 
1. **계약 문서(00) 변경은 PR + 3인 승인.** 여기가 흔들리면 병렬 작업 붕괴
2. **파일 소유권** (계약 §4): A는 `voron24_params.xacro` + `meshes/`, B는 `unity/`, C는 URDF 본문 + launch + 나머지 ROS2 패키지
3. **Mock 우선**: 상대 산출물 대기 금지. 교체는 마지막에
4. **커밋 전 `contract_check.py`** — pre-commit 훅 등록 권장

## 대상 기종 요약
 
Voron 2.4 R2 / 250mm — **CoreXY + 플라잉 갠트리**. 베드 고정, 갠트리가 Z로 승강.
 
```
base_link (프레임 + 베드 + 전장)
└── z_gantry   prismatic Z
    └── x_beam prismatic Y
        └── toolhead prismatic X
            └── nozzle (fixed, TCP)
bed_origin (fixed to base_link) ← G-code (0,0,0)
```
 
## 라이선스
 
Voron 2.4 CAD 원본은 [VoronDesign/Voron-2](https://github.com/VoronDesign/Voron-2) (GPLv3).
파생물 배포 시 라이선스 및 상표 정책 확인 필수 — [00 §12](docs/00_interface_contract.md) 참조.

## ROS 조작 노드와 Unity 연동 구조

Unity, ROS 조작 노드, ROS는 내부 코드를 직접 공유하지 않고 ROS 메시지로 연결합니다.
Unity는 명령을 보내고 상태를 표시하며, 조작 노드는 명령을 처리하고 상태를 발행합니다.

```text
Unity Engine
  → /printer/cmd
  → ROS 조작 노드
  → /joint_states
  → Unity Engine
```

전체 흐름은 다음과 같습니다.

```text
Unity UI / Keyboard
  → PrinterCommandPublisher.cs
  → /printer/cmd
  → ROS 조작 노드
  → /joint_states
  → JointStateSubscriber.cs
  → Unity 3D Printer Model
```

### 구성별 역할

| 구성 | 역할 |
|---|---|
| Unity Engine | 3D 프린터 표시, 버튼·키 입력, 상태 시각화 |
| ROS 조작 노드 | `jog`, `home`, `stop`, G-code 명령 처리 |
| ROS | 토픽 통신, 메시지 전달, 상태 관리 |

Unity는 프린터 동작 로직을 직접 수행하지 않습니다. Unity UI에서 받은 입력은
명령 메시지로 보내고, 실제 좌표 갱신 결과는 ROS의 상태 메시지를 통해 받습니다.

### Unity → ROS 명령

Unity의 `PrinterCommandPublisher.cs`는 `/printer/cmd` 토픽을 발행합니다.
권장 명령 메시지 구조는 다음과 같습니다.

```text
string command
float32[] args
string payload
```

| 동작 | `command` | `args` | `payload` |
|---|---|---|---|
| X축 1 mm 이동 | `jog` | `[1.0, 0.0, 0.0]` | `""` |
| 원점 복귀 | `home` | `[]` | `XYZ` |
| 정지 | `stop` | `[]` | `""` |

`PrinterCommandPublisher.cs`는 버튼 클릭 또는 키 입력 시 메시지를 만들고
`/printer/cmd`로 publish합니다.

### ROS 조작 노드

예를 들어 `voron24_controller` 또는 `printer_command_node`는
`/printer/cmd`를 구독하여 명령을 해석합니다. 이 노드는 현재 X/Y/Z 좌표를
계산하고 `joint_x`, `joint_y`, `joint_z`를 갱신한 뒤 `/joint_states`를 발행합니다.

예를 들어 Unity가 `jog`와 `[10.0, 0.0, 0.0]`을 보내면, 조작 노드는
10 mm를 0.010 m로 변환하여 X 좌표에 더합니다.

```text
name:     ['joint_x', 'joint_y', 'joint_z']
position: [0.010, 0.000, 0.000]
```

### ROS → Unity 상태

Unity의 `JointStateSubscriber.cs`는 `/joint_states`를 구독해 프린터 모델에
반영합니다.

| ROS Joint | Unity Object | 의미 |
|---|---|---|
| `joint_x` | `toolhead` | 노즐/툴헤드 X축 이동 |
| `joint_y` | `x_beam` | X빔의 Y축 이동 |
| `joint_z` | `z_gantry` | 갠트리 Z축 이동 |

따라서 Unity 버튼을 눌렀을 때 `toolhead.transform.position`을 바로 변경하지
않습니다. 반드시 아래의 상태 순환을 거쳐 모델을 움직입니다.

```text
Unity 버튼 → /printer/cmd → ROS 처리 → /joint_states → Unity 이동
```

### ROS 없이 테스트할 때

ROS가 실행되지 않는 개발 환경에서는 `LocalMockDriver` 또는 Offline Mock이
`JointStateSubscriber.SetTargetExternal()`을 호출해 Unity 모델을 움직일 수
있습니다. 이는 단독 테스트용이며, 최종 연동에서는 ROS 조작 노드가 상태의
기준(source of truth)이 되어야 합니다.
