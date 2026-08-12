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

### 통합 실행: Unity → RViz → echo 메시지 대조

목적: 하나의 mock publisher가 만든 `/joint_states`를 Unity, RViz, 터미널 echo에서
차례대로 검증한다. 세 화면이 같은 메시지를 보는지 확인하는 절차이며, 각 단계가
통과해야 다음 단계로 진행한다.

**사전 조건**

- ROS 2 배포판은 이 환경의 `jazzy`를 사용한다.
- Unity 프로젝트는 Windows 로컬 폴더에서 연다. 예: `C:\Users\user\Desktop\ksy\Voron24Twin`
- Unity `Robotics > ROS Settings`: Protocol=`ROS 2`, IP=`127.0.0.1`, Port=`10000`.
- 이전 launch가 남아 있으면 해당 터미널에서 `Ctrl+C`로 먼저 종료한다.

#### 1. ROS + RViz를 시작한다 — 터미널 A

```bash
cd ~/3DPrinter-onUnity-byROS/ros2_ws
source /opt/ros/jazzy/setup.bash
source install/setup.bash
ros2 launch voron24_bringup mock.launch.py pattern:=sweep period:=30.0
```

이 명령은 아래를 한 번에 시작한다.

```text
mock_publisher           -> /joint_states (50 Hz)
robot_state_publisher    -> /tf
ros_tcp_endpoint         -> TCP 0.0.0.0:10000
rviz2                    -> RobotModel 표시
```

터미널 A에서 다음 두 로그가 나온 뒤 계속 유지되어야 한다. `mock_publisher`가 죽으면
launch 창은 남아 있어도 Unity와 RViz는 움직이지 않는다.

```text
mock publisher | pattern=sweep rate=50.0Hz period=30.0s
Starting server on 0.0.0.0:10000
```

RViz가 열리면 갠트리가 **X → Y → Z** 순으로 한 축씩 왕복하는지 본다. 이 단계에서
움직이지 않으면 Unity를 열지 말고 터미널 A의 `mock_publisher` 오류부터 해결한다.

#### 2. 원본 ROS 메시지를 확인한다 — 터미널 B

```bash
cd ~/3DPrinter-onUnity-byROS/ros2_ws
source /opt/ros/jazzy/setup.bash
source install/setup.bash
ros2 topic echo /joint_states --once --no-daemon
```

아래 구조가 출력되어야 한다. `position`은 sweep 단계에 따라 변하므로 숫자는 달라도 된다.

```yaml
name:
- joint_x
- joint_y
- joint_z
position: [<x>, <y>, <z>]
```

출력되지 않으면 ROS publisher 문제다. 터미널 A에서 `mock_publisher`가 살아 있는지 확인하고,
필요하면 A를 `Ctrl+C`로 종료한 뒤 1단계 명령을 다시 실행한다.

#### 3. Unity를 연결한다 — Windows Unity

1. `Voron24Twin` 프로젝트를 열고 Console의 기존 메시지를 `Clear`한다.
2. **Play**를 누른다.
3. Game 뷰에 `ROS: CONNECTED`와 IP `127.0.0.1:10000`이 표시되는지 확인한다.
4. Unity 모델도 RViz와 동일하게 **X → Y → Z** 순으로 움직이는지 확인한다.

Unity Console에는 최소한 다음 로그가 보여야 한다.

```text
[JointState] bound 3/3 joints under 'voron24'
[JointState] subscribed to /joint_states
[JointState] first message: names=[joint_x, joint_y, joint_z] ...
```

#### 4. 세 출력의 결과를 대조한다

| 관찰 결과 | 판정 / 다음 조치 |
|---|---|
| echo 없음, RViz/Unity 정지 | mock publisher가 종료됨. 터미널 A 오류 확인 |
| echo 있음, RViz 정지 | `robot_state_publisher` 또는 URDF/조인트 이름 문제 |
| echo와 RViz 정상, Unity `DISCONNECTED` | Unity ROS Settings의 ROS 2·`127.0.0.1`·`10000` 확인 |
| echo와 RViz 정상, Unity만 축/방향 다름 | `JointStateSubscriber`의 링크 매핑 또는 축 부호 확인 |
| 세 곳 모두 X → Y → Z로 이동 | ROS → RViz → TCP → Unity 통합 성공 |

Windows PowerShell에서 `Test-NetConnection 127.0.0.1 -Port 10000`가 `True`라면,
Windows Unity → WSL ROS 연결에는 WSL mirrored 모드가 필요 없다.

#### 종료

Unity에서 Play를 멈춘 뒤 터미널 A에서 `Ctrl+C`를 누른다. 다음 실행 전에 이전 launch를
겹쳐 실행하지 않는다. 같은 이름의 endpoint가 중복되면 로그와 연결 상태 판단이 어려워진다.
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
