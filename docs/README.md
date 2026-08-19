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
 
cd ros2_ws
vcs import src < voron24.repos          # 외부 의존(ROS-TCP-Endpoint) 가져오기
colcon build --symlink-install && source install/setup.bash
ros2 launch voron24_bringup mock.launch.py
```
 
별도로 Unity 프로젝트(`unity/Voron24Twin`)를 열고 Play.
 
```bash
# 축 방향 검증
ros2 launch voron24_bringup mock.launch.py pattern:=sweep
# A의 메시 적용
ros2 launch voron24_bringup mock.launch.py use_meshes:=true
```

STL/G-code 를 돌리려면 통합 진입점인 `sim.launch.py` — 아래
[STL / G-code 재생](#stl--g-code-재생-simlaunchpy) 참조.
 
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

`ros2` CLI 가 통째로 멈추거나 `import rclpy` 가 `ModuleNotFoundError:
rclpy._rclpy_pybind11` 로 죽으면 **데몬이 아니라 `python3` 를 의심할 것.** conda/venv
가 PATH 를 잡고 있으면 ROS2 Jazzy 가 붙어 있는 시스템 python(3.12)이 아닌 다른
인터프리터로 CLI 가 뜬다. `which python3` 로 확인하고 rclpy 를 쓰는 스크립트는
`/usr/bin/python3` 를 명시. `ros2 run` 과 `ros2 topic pub` 은 이 상태에서도 통과할 때가
있어 "노드는 떴는데 토픽이 안 보인다" 로 오진하기 쉽다.

### 자주 쓰는 인자

| 인자 | 기본값 | 용도 |
|---|---|---|
| `pattern:=` | `lissajous` | `home` / `sweep` / `square` / `lissajous` |
| `period:=` | `12.0` | 한 주기 [s] |
| `rviz:=false` | `true` | Unity 만 볼 때. **문제 생기면 다시 켤 것** |
| `unity:=false` | `true` | ROS 파이프라인만 격리 |
| `use_meshes:=true` | `false` | A 의 STL. 메시가 들어오기 전엔 쓰지 말 것 |

## STL / G-code 재생 (`sim.launch.py`)

`mock.launch.py` 가 수식으로 만든 궤적을 보여주는 자리라면, `sim.launch.py` 는 **실제
모델에서 나온 궤적**을 돌리는 통합 진입점. 값의 출처만 `source` 로 갈리고 나머지 그래프는
동일 — 설계 근거는 [04_single_entrypoint.md](docs/04_single_entrypoint.md),
[04b_gcode_pipeline.md](docs/04b_gcode_pipeline.md).

```
source:=manual  manual_publisher  (키보드/패턴)
source:=gcode   gcode_player_node
source:=stl     voron24_slicer ──(SliceModel action)──▶ gcode_player_node
```

```
STL ─▶ PrusaSlicer ─▶ G-code ─▶ gcode_player ─/printer/target─▶ printer_state_node
                                     │                              │
                                /printer/extrusion            /joint_states
                                     └──────────┬─────────────────┘
                                          ros_tcp_endpoint(:10000) ─▶ Unity
```

### 선행 — 슬라이서 설치

```bash
sudo apt install -y prusa-slicer          # noble/universe, 2.7.2
```

`profiles.py` 가 PATH 에서 자동으로 찾음. AppImage 를 쓰면 설치 대신
`export VORON24_SLICER=/경로/PrusaSlicer.AppImage`. 슬라이서가 없어도 launch 는 죽지
않고 슬라이싱 goal 만 `success=false` 로 떨어짐.

프로파일은 `tools/slicer/voron24_250.ini` 한 벌 (베드 250×250, arc fitting 끔,
start/end G-code 비움 — 파서를 단순하게 유지하기 위한 제약. 계약 §2 의 변환 지점을
지키려고 출력은 mm 절대좌표로 고정).

### 실행

```bash
cd ros2_ws && source install/setup.bash

# STL 부터 — 슬라이싱 후 자동 재생
ros2 launch voron24_bringup sim.launch.py source:=stl file:=/abs/path/model.stl speed:=100.0

# 이미 슬라이싱된 G-code 재생
ros2 launch voron24_bringup sim.launch.py source:=gcode file:=/abs/path/a.gcode speed:=100.0

# 수동 조작 (기본값)
ros2 launch voron24_bringup sim.launch.py
```

`file:=` 은 노드 파라미터가 아니라 `load_gcode` 한 발 — 파일 진입점을 둘로 늘리지
않기 위함. `source:=stl` 은 그 한 발을 `job_starter` 가 대신 쏨.

| 인자 | 기본값 | 용도 |
|---|---|---|
| `source:=` | `manual` | `manual` / `gcode` / `stl` |
| `file:=` | `''` | `source` 가 gcode/stl 일 때 **절대경로** |
| `speed:=` | `10.0` | 재생 배속. 실행 중에는 `set_speed` 로 |
| `max_step:=` | `10.0` | 틱당 헤드 이동 상한 [mm]. `0` 이면 무제한 |
| `unity:=` | `build` | `build`(리눅스 플레이어 기동) / `editor`(엔드포인트만) / `none` |
| `pattern:=` | `none` | `source:=manual` 일 때만 의미 있음 |
| `use_meshes:=` | `false` | A 의 STL |

**배속은 필수에 가깝다.** 머그 한 개가 1x 로 4.9 시간. 배속은 시간축만 늘리고 줄이며
궤적 형상과 `ExtrusionPoint` 열은 배속과 무관하게 동일.

**다만 배속은 원하는 만큼 나오지 않는다 — 그리고 그게 정상이다.** 좌표는 50Hz 로
나가므로 배속을 올린 만큼 한 틱의 이동량이 커진다. 100x 면 틱당 40~120mm 씩 건너뛰게
되고, 그건 더 이상 궤적이 아니라 순간이동이라 Unity 의 ArticulationBody 가 목표를
따라가지 못한 채 뒤에 처져 헤맨다("점 사이를 안 가고 영점 근처에서 튄다"의 정체).
`max_step:=` 이 그 상한이고 기본값 `10.0` 은 vel_xy(500mm/s) × 0.02s — 화면 위의 헤드가
기계 자신의 최대속도보다 빨리 움직이지 않게 하는 값이다. 상한에 걸리면 플레이어가

```
[WARN] 틱당 이동 상한(10mm)에 걸림 — 요청 100x 대신 약 8.0x 로 재생함
```

처럼 실제 배속을 찍는다. 연속성과 압축은 맞바꾸는 관계이고 이 모델의 하한은 경로 총
길이가 정한다 — 머그의 경로가 1.12km 라 10mm/tick(=500mm/s)로는 아무리 서둘러도 37 분.
더 줄이려면 `max_step:=` 을 올리는 대신(헤드가 튄다) 인필·레이어를 줄여 경로 자체를
짧게 슬라이싱하는 쪽이 맞다. `max_step:=0` 은 상한 해제 — 궤적 형상만 보고 헤드 움직임은
버릴 때만.

Unity 를 Editor 에서 직접 Play 할 거면 `unity:=editor` (launch 는 엔드포인트만 띄움).

### 슬라이싱할 STL 준비

시뮬레이션 전용이라 재료·온도는 무의미하고 **치수와 단위만** 맞으면 됨. 다만 CAD/DCC
export 물은 이 둘로 자주 막힘.

| 증상 | 원인 | 확인 |
|---|---|---|
| `There is an object with no extrusions in the first layer` | 바운딩 박스가 실제 형상보다 훨씬 큼. Blender 등에서 **바닥 평면·백드롭이 같이 export** 되면 베드 맞춤이 본체를 뭉갬 | 바운딩 박스 대비 정점 분포 확인 |
| 모델이 먼지만 하거나 베드를 넘침 | **단위가 mm 가 아님.** Blender 기본 단위 그대로 나온 경우 | `--scale` 또는 export 시 mm 지정 |

원본을 고칠 수 없으면 슬라이서 인자로 우회 가능 — `--scale`, `--scale-to-fit X,Y,Z`,
`--center 125,125` (베드 중앙), `--rotate-x`.

슬라이싱만 따로 돌려보는 것이 가장 빠른 분리 진단.

```bash
prusa-slicer --export-gcode --load tools/slicer/voron24_250.ini \
  --center 125,125 -o /tmp/out.gcode /abs/path/model.stl
```

X 없이 막히면 앞에 `xvfb-run -a` 한 겹. `test/` 는 `.gitignore` 대상이라 실험용
STL/G-code 를 두는 자리로 씀 (`*.stl`/`*.gcode` 가 LFS 대상이라 실수로 올리면 무거움).

### 슬라이서 없이 / ROS 없이

파서와 보간기는 rclpy 를 import 하지 않으므로 단독으로 돈다. 슬라이서 설치 전에도
파이프라인 절반은 검증 가능.

```bash
python3 tools/make_test_gcode.py square -o /tmp/sq.gcode --layers 20   # 사각형/원통 생성
cd ros2_ws/src/voron24_gcode
python3 -m voron24_gcode.gcode_parser /tmp/sq.gcode    # 세그먼트 덤프
python3 -m voron24_gcode.motion       /tmp/sq.gcode    # 50Hz 시간축 궤적
```

### 플레이어 단독 확인

상태 노드 없이 플레이어만 띄워도 검증됨. **구독 토픽이 `/printer/cmd` 가 아니라
`/printer/playback`** 인 것에 주의 — 상태 노드가 걸러 넘기는 스트림 계열만 받는다.

```bash
ros2 run voron24_gcode gcode_player
ros2 topic pub --once /printer/playback voron24_msgs/PrinterCommand \
  "{command: 'load_gcode', payload: '/abs/path/a.gcode'}"
ros2 topic echo /printer/target --once --no-daemon
```

정상이면 `/printer/target` 이 50Hz, `name=[joint_x, joint_y, joint_z]`, **단위 m**
(계약 §2 의 mm→m 변환은 여기 publish 직전 한 번뿐).

### 헤드가 영점과 G-code 좌표 사이를 오갈 때 — 앞 실행의 고아 노드

**증상**: 재생하면 헤드가 경로를 따라가지 않고 영점(또는 어떤 고정된 좌표)으로 돌아갔다
G-code 좌표로 갔다를 반복한다. 배속을 `1.0` 으로 낮춰도 그대로다.

**원인**: 같은 토픽에 발행자가 둘이다. `/joint_states` 는 상태 노드가, `/printer/target`
은 값 소스 하나가 단독으로 소유하는 것이 전제인데, 앞 실행의 노드가 살아남으면 두 흐름이
50Hz 씩 번갈아 도착하고 받는 쪽은 그때그때 온 값을 그대로 쓴다. 새로 띄운 플레이어는
파일 맨 앞 `(0,0,0)` 에서 출발하므로 그 사이를 오가는 것이 "영점으로 돌아간다" 로 보인다.
**궤적 데이터는 멀쩡하므로 G-code 나 보간기를 아무리 봐도 안 나온다.**

터미널을 닫거나 Ctrl+C 가 launch 에 안 먹으면 노드만 고아(PPID=1)로 남아 몇 시간이고
계속 쏜다. `/printer/playback` 은 그 고아에게도 도달하므로 `load_gcode` 를 쏘면 둘이
같이 재생을 시작한다.

```bash
/usr/bin/python3 tools/topic_probe.py 3     # 발행자 수 + 값이 흐르는지
ps -ef | grep -E 'printer_state_node|gcode_player|mock_publisher' | grep -v grep
pkill -f voron24_                           # 확인 후 정리
```

`topic_probe.py` 가 "Unity 문제인가 ROS 문제인가" 를 가른다. 읽는 법:

| 결과 | 뜻 |
|---|---|
| 발행자 **0** 개 | launch 가 안 떴거나 `ROS_DOMAIN_ID` 가 다르다 |
| 발행자 **2** 개 이상 | 고아 노드. 위 `pkill` |
| 발행자 1 개, **값 고정** | 상태 노드가 `manual` 모드 — `load_gcode` 가 도달하지 않았다 |
| 발행자 1 개, **값이 흐름** | ROS 는 정상. 여기부터는 Unity/엔드포인트 문제 |

두 노드가 2 초마다 스스로 감시하므로 로그에 이렇게 뜬다:

```
[ERROR] /printer/target 에 다른 값 소스가 1 개 더 있음 — 값 소스는 배타 선택이다.
```

`ROS_DOMAIN_ID` 가 다르면 서로 안 보인다는 점도 같이 본다. 도메인을 바꿔 가며 테스트했다면
**앞 도메인의 고아는 지금 셸에서 `ros2 node list` 에 안 잡힌다** — `ps` 로 봐야 한다.

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