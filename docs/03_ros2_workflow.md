# 03 — ROS2 담당 워크플로
 
> **담당: C**
> **최종 산출물**: URDF 패키지, mock/G-code 노드, Unity 브릿지, Moonraker 연동, 통합 launch, 검증 도구
> **선행**: [00_interface_contract.md](00_interface_contract.md) 숙지
> **최우선 과제**: **W1 Day 1~2에 mock URDF + mock 퍼블리셔 + contract_check 푸시.** 지연 시 팀 전체 정지.
 
---
 
## 준비
 
| 항목 | 버전 |
|---|---|
| **ROS2** | **Jazzy** (Ubuntu 24.04) |
| Python | 3.10+ |
| 빌드 | colcon |
 
```bash
sudo apt install ros-jazzy-desktop \
  ros-jazzy-robot-state-publisher ros-jazzy-joint-state-publisher-gui \
  ros-jazzy-xacro ros-jazzy-tf2-tools ros-jazzy-rviz2 \
  python3-colcon-common-extensions
 
mkdir -p ~/voron24-digital-twin/ros2_ws/src && cd ~/voron24-digital-twin/ros2_ws
```
 
팀 전원 `ROS_DOMAIN_ID` 동일 값 사용. `~/.bashrc`에 `export ROS_DOMAIN_ID=42`.
 
---
 
## Step 1 — 패키지 스캐폴딩 (W1 Day 1)
 
```bash
cd src
ros2 pkg create voron24_description --build-type ament_cmake
ros2 pkg create voron24_msgs        --build-type ament_cmake \
     --dependencies std_msgs geometry_msgs builtin_interfaces
ros2 pkg create voron24_gcode       --build-type ament_python \
     --dependencies rclpy sensor_msgs std_msgs voron24_msgs
ros2 pkg create voron24_bringup     --build-type ament_python --dependencies rclpy
ros2 pkg create voron24_moonraker   --build-type ament_python \
     --dependencies rclpy sensor_msgs voron24_msgs
```
 
### 디렉토리
 
```
ros2_ws/src/
├── voron24_description/
│   ├── urdf/
│   │   ├── voron24_params.xacro       ← A 전용 (계약 §4)
│   │   ├── voron24.urdf.xacro         ← C. mock/real 겸용
│   │   └── voron24_macros.xacro       ← C
│   ├── meshes/visual/                 ← A 전용
│   ├── meshes/collision/              ← A 전용
│   ├── rviz/voron24.rviz
│   └── launch/display.launch.py
├── voron24_msgs/msg/
│   ├── PrinterStatus.msg
│   ├── ExtrusionPoint.msg
│   └── PrinterCommand.msg
├── voron24_gcode/voron24_gcode/
│   ├── patterns.py                    ROS 없이 테스트 가능
│   ├── mock_publisher_node.py         ← W1
│   ├── gcode_parser.py
│   ├── gcode_player_node.py
│   └── corexy.py
├── voron24_moonraker/voron24_moonraker/
│   └── moonraker_bridge_node.py
└── voron24_bringup/launch/
    ├── mock.launch.py
    ├── sim.launch.py
    └── real.launch.py
```
 
> **파일 소유권** (계약 §4): `meshes/`와 `voron24_params.xacro`는 A, 나머지는 C. 파일이 겹치지 않으므로 머지 충돌 없음.
 
---
 
## Step 2 — URDF 작성 (W1 Day 1~2) — 최우선
 
### 단일 파일 원칙
 
mock용/real용을 별도 파일로 두면 **조인트 좌표가 중복되어 반드시 드리프트 발생.** 한 벌로 통합하고 `use_meshes` 인자로 형상만 전환 (계약 §4).
 
### 파일 3개 구성
 
**`voron24_params.xacro`** — 모든 치수의 단일 출처. A가 실측값으로 교체할 대상. 초기에는 추정치로 채워둘 것.
 
```xml
<xacro:property name="MEASURED" value="false"/>
 
<xacro:property name="stroke_x" value="0.250"/>
<xacro:property name="stroke_y" value="0.250"/>
<xacro:property name="stroke_z" value="0.250"/>
 
<!-- joint_z : base_link -> z_gantry -->
<xacro:property name="jz_x" value="0.0"/>
<xacro:property name="jz_y" value="0.0"/>
<xacro:property name="jz_z" value="0.2145"/>      <!-- (추정) -->
<!-- ... joint_y, joint_x, nozzle, bed_origin, 질량, mock 박스 치수 ... -->
```
 
**`voron24_macros.xacro`** — 관성 계산 + 형상 전환 매크로.
 
```xml
<xacro:macro name="box_inertia" params="m x y z *origin">
  <inertial>
    <xacro:insert_block name="origin"/>
    <mass value="${m}"/>
    <inertia ixx="${m*(y*y+z*z)/12.0}"
             iyy="${m*(x*x+z*z)/12.0}"
             izz="${m*(x*x+y*y)/12.0}"
             ixy="0" ixz="0" iyz="0"/>
  </inertial>
</xacro:macro>
 
<xacro:macro name="link_geometry" params="name use_meshes bx by bz ox:=0 oy:=0 oz:=0 color_name color_rgba">
  <xacro:if value="${use_meshes}">
    <visual>
      <geometry><mesh filename="${MESH_PKG}/visual/${name}.stl" scale="${MESH_SCALE}"/></geometry>
      <material name="${color_name}"><color rgba="${color_rgba}"/></material>
    </visual>
    <collision>
      <geometry><mesh filename="${MESH_PKG}/collision/${name}.stl" scale="${MESH_SCALE}"/></geometry>
    </collision>
  </xacro:if>
  <xacro:unless value="${use_meshes}">
    <visual>
      <origin xyz="${ox} ${oy} ${oz}"/>
      <geometry><box size="${bx} ${by} ${bz}"/></geometry>
      <material name="${color_name}"><color rgba="${color_rgba}"/></material>
    </visual>
    <collision>
      <origin xyz="${ox} ${oy} ${oz}"/>
      <geometry><box size="${bx} ${by} ${bz}"/></geometry>
    </collision>
  </xacro:unless>
</xacro:macro>
```
 
**`voron24.urdf.xacro`** — 링크 트리 본문.
 
```xml
<robot xmlns:xacro="http://www.ros.org/wiki/xacro" name="voron24">
  <xacro:arg name="use_meshes" default="false"/>
  <xacro:property name="use_meshes" value="$(arg use_meshes)"/>
 
  <xacro:include filename="$(find voron24_description)/urdf/voron24_params.xacro"/>
  <xacro:include filename="$(find voron24_description)/urdf/voron24_macros.xacro"/>
 
  <link name="base_link">
    <!-- mock 모드에서는 프레임 외곽(반투명) + 베드(고정!) 2개 visual -->
    ...
    <xacro:box_inertia m="${m_base}" x="${frame_x}" y="${frame_y}" z="${frame_z}">
      <origin xyz="0 0 ${frame_z*0.3}"/>
    </xacro:box_inertia>
  </link>
 
  <link name="z_gantry">
    <xacro:link_geometry name="z_gantry" use_meshes="${use_meshes}"
                         bx="${gantry_x}" by="${gantry_y}" bz="${gantry_z}"
                         color_name="voron_red" color_rgba="0.85 0.27 0.20 1.0"/>
    <xacro:box_inertia m="${m_gantry}" x="${gantry_x}" y="${gantry_y}" z="${gantry_z}">
      <origin xyz="0 0 0"/>
    </xacro:box_inertia>
  </link>
  <joint name="joint_z" type="prismatic">
    <parent link="base_link"/><child link="z_gantry"/>
    <origin xyz="${jz_x} ${jz_y} ${jz_z}"/>
    <axis xyz="0 0 1"/>
    <limit lower="0.0" upper="${stroke_z}" effort="${eff_z}" velocity="${vel_z}"/>
    <dynamics damping="${damp_z}" friction="${fric_z}"/>
  </joint>
 
  <!-- joint_y (z_gantry -> x_beam), joint_x (x_beam -> toolhead) 동일 패턴 -->
 
  <link name="nozzle"/>
  <joint name="joint_nozzle" type="fixed">
    <parent link="toolhead"/><child link="nozzle"/>
    <origin xyz="${noz_x} ${noz_y} ${noz_z}"/>
  </joint>
 
  <!-- 베드가 움직이지 않으므로 z_gantry 가 아니라 base_link 의 자식 -->
  <link name="bed_origin"/>
  <joint name="joint_bed_origin" type="fixed">
    <parent link="base_link"/><child link="bed_origin"/>
    <origin xyz="${bed_x} ${bed_y} ${bed_z}"/>
  </joint>
</robot>
```
 
> **`bed_origin`의 parent를 `z_gantry`로 두는 것이 최빈 오류.** Voron 2.4는 플라잉 갠트리라 베드가 고정. `contract_check.py`가 이 케이스를 명시적으로 검출.
 
### 즉시 검증
 
```bash
cd ~/voron24-digital-twin/ros2_ws
colcon build --symlink-install && source install/setup.bash
 
# 양쪽 모드 확장 확인
xacro src/voron24_description/urdf/voron24.urdf.xacro use_meshes:=false > /tmp/mock.urdf
xacro src/voron24_description/urdf/voron24.urdf.xacro use_meshes:=true  > /tmp/real.urdf
check_urdf /tmp/mock.urdf
urdf_to_graphiz /tmp/mock.urdf          # 트리 시각화 PDF
 
# 계약 검증
python3 ../tools/contract_check.py --xacro src/voron24_description/urdf/voron24.urdf.xacro
 
# RViz 슬라이더
ros2 launch voron24_description display.launch.py
```
 
RViz 확인 항목:
- `joint_z` ↑ → 갠트리만 상승, **베드 정지**
- `joint_x`, `joint_y` → 툴헤드가 정확한 방향
- 세 조인트 0일 때 노즐이 베드 좌전방 코너
### 커밋 & 통지
 
```bash
git add ros2_ws/src/voron24_description ros2_ws/src/voron24_msgs tools/
git commit -m "feat(ros2): 단일 URDF (mock/real 겸용) + 계약 검증 도구"
git push
```
 
**팀 채널에 "mock URDF 푸시 완료. B는 Unity 임포트 시작 가능" 통지.**
 
---
 
## Step 3 — 계약 검증 도구 (W1 Day 1)
 
`tools/contract_check.py` — 병렬 작업 중 이름·축이 조용히 어긋나는 것을 자동 검출. 통합 시점에 발견하면 원인 추적에 반나절 소요.
 
```bash
python3 tools/contract_check.py --xacro .../voron24.urdf.xacro
python3 tools/contract_check.py --xacro .../voron24.urdf.xacro --use-meshes --check-meshes
```
 
검출: 링크/조인트 이름, parent-child, 축 방향, 스트로크 리밋, `lower=0` 여부, `<inertial>` 누락, 메시 scale, 삼각형 예산, `bed_origin`의 parent, 루트 링크 단일성.
 
종료 코드 0=통과 / 1=위반. pre-commit 훅과 CI에 등록.
 
`tools/smoke_test.sh` — 통합 게이트 확인. 토픽 존재, `/joint_states` 주기, 조인트 이름, `bed_origin → nozzle` TF, TCP 10000 개방.
 
---
 
## Step 4 — Mock 퍼블리셔 (W1 Day 2)
 
### 궤적 로직 분리
 
`voron24_gcode/patterns.py` — **rclpy 의존성 없음.** ROS 환경 없이 단독 검증 가능.
 
```bash
python3 -m voron24_gcode.patterns
```
 
```
[OK ] home       x[0.000,0.000] y[0.000,0.000] z[0.000,0.000] max_step=0.00mm
[OK ] sweep      x[0.010,0.240] y[0.010,0.240] z[0.000,0.250] max_step=2.50mm
[OK ] square     x[0.010,0.240] y[0.010,0.240] z[0.000,0.001] max_step=1.53mm
[OK ] lissajous  x[0.010,0.240] y[0.010,0.240] z[0.010,0.030] max_step=1.20mm
```
 
자체 검증 항목: 전 구간 리밋 내 유지, 20ms당 이동량 10mm 미만(급격한 점프 없음).
 
### 패턴 4종
 
| 패턴 | 용도 |
|---|---|
| `sweep` | 한 축씩 왕복. **축 매핑과 부호 검증. W1에 이것부터 사용** |
| `home` | 전부 0 고정. 홈 자세에서 노즐 위치 확인 |
| `square` | 베드 외곽 사각형. 스트로크 리밋과 원점 확인 |
| `lissajous` | 기본. 궤적이 겹치지 않아 시각적으로 양호 |
 
### 노드
 
`mock_publisher_node.py` — `patterns.py`에 궤적 생성을 위임하고 퍼블리시만 담당.
 
```python
JOINT_NAMES = ['joint_x', 'joint_y', 'joint_z']   # 계약 §3. 변경 금지
 
def tick(self):
    self.t += self.dt
    x, y, z, extruding = self.compute(self.t)
    msg = JointState()
    msg.header.stamp = self.get_clock().now().to_msg()
    msg.name = list(JOINT_NAMES)
    msg.position = [x, y, z]
    self.js_pub.publish(msg)
```
 
`voron24_msgs` 미빌드 상태에서도 `/joint_states`만으로 동작하도록 import를 try/except로 보호. B가 msg 생성 전에도 Unity 연결 검증 가능.
 
```bash
ros2 run voron24_gcode mock_publisher
ros2 run voron24_gcode mock_publisher --ros-args -p pattern:=sweep
ros2 run voron24_gcode mock_publisher --ros-args -p rate:=100.0 -p period:=8.0
```
 
---
 
## Step 5 — Unity 브릿지 (W1 Day 2)
 
```bash
cd ~/voron24-digital-twin/ros2_ws/src
git clone -b main-ros2 https://github.com/Unity-Technologies/ROS-TCP-Endpoint.git
cd .. && colcon build --packages-select ros_tcp_endpoint && source install/setup.bash
```
 
`voron24_bringup/launch/mock.launch.py`:
 
```python
def generate_launch_description():
    desc_pkg = FindPackageShare('voron24_description')
    urdf = PathJoinSubstitution([desc_pkg, 'urdf', 'voron24.urdf.xacro'])
    robot_description = Command(['xacro ', urdf,
                                 ' use_meshes:=', LaunchConfiguration('use_meshes')])
    return LaunchDescription([
        DeclareLaunchArgument('use_meshes', default_value='false'),
        DeclareLaunchArgument('pattern', default_value='lissajous'),
        DeclareLaunchArgument('rviz', default_value='true'),
        DeclareLaunchArgument('unity', default_value='true'),
 
        Node(package='robot_state_publisher', executable='robot_state_publisher',
             parameters=[{'robot_description': robot_description,
                          'publish_frequency': 50.0}]),
        Node(package='voron24_gcode', executable='mock_publisher',
             parameters=[{'pattern': LaunchConfiguration('pattern')}]),
        Node(package='ros_tcp_endpoint', executable='default_server_endpoint',
             condition=IfCondition(LaunchConfiguration('unity')),
             parameters=[{'ROS_IP': '0.0.0.0', 'ROS_TCP_PORT': 10000}]),
        Node(package='rviz2', executable='rviz2',
             condition=IfCondition(LaunchConfiguration('rviz')),
             arguments=['-d', PathJoinSubstitution([desc_pkg, 'rviz', 'voron24.rviz'])]),
    ])
```
 
```bash
ros2 launch voron24_bringup mock.launch.py
ros2 launch voron24_bringup mock.launch.py pattern:=sweep      # 축 검증
ros2 launch voron24_bringup mock.launch.py unity:=false        # 엔드포인트 없이
ros2 launch voron24_bringup mock.launch.py use_meshes:=true    # W4
```
 
### W1 통합 게이트
 
```bash
# 터미널 1
ros2 launch voron24_bringup mock.launch.py
# 터미널 2
bash tools/smoke_test.sh
```
 
RViz와 Unity에서 **동시에** 같은 움직임이 보이면 통과.
 
---
 
## Step 6 — 커스텀 메시지 (W2)
 
계약 §6의 정의를 그대로 작성.
 
`CMakeLists.txt`:
 
```cmake
find_package(rosidl_default_generators REQUIRED)
find_package(std_msgs REQUIRED)
find_package(geometry_msgs REQUIRED)
 
rosidl_generate_interfaces(${PROJECT_NAME}
  "msg/PrinterStatus.msg"
  "msg/ExtrusionPoint.msg"
  "msg/PrinterCommand.msg"
  DEPENDENCIES std_msgs geometry_msgs
)
```
 
`package.xml`:
 
```xml
<buildtool_depend>rosidl_default_generators</buildtool_depend>
<exec_depend>rosidl_default_runtime</exec_depend>
<member_of_group>rosidl_interface_packages</member_of_group>
```
 
> **msg 변경 시 B가 Unity에서 C# 재생성 필요.** 계약 §6 변경 = PR + 3인 승인. **W2 내 확정할 것.**
 
---
 
## Step 7 — G-code 파서 (W2~W3)
 
`gcode_parser.py` — Marlin/Klipper 공통 서브셋.
 
지원: `G0/G1`, `G28`, `G90/G91`, `M82/M83`, `G92`, 슬라이서 레이어 주석(`;LAYER:`, `;LAYER_CHANGE`, `;AFTER_LAYER_CHANGE`).
 
미지원: `G2/G3`(원호), `G29`(베드 레벨링), 아크 보간.
 
```python
@dataclass
class Move:
    x: float; y: float; z: float; e: float
    feed: float                # mm/min
    extruding: bool
    layer: int
    dist: float = 0.0          # mm
    dur:  float = 0.0          # s
```
 
> 가감속을 무시하므로 예상 시간이 실제보다 짧게 산출. 시각화 목적에는 충분. 정확한 시간이 필요하면 슬라이서의 `;TIME:` 주석을 파싱할 것.
 
### CoreXY 변환
 
`corexy.py` — 모터 풀리 회전 시각화용. 기구학과 무관.
 
```python
def xy_to_ab(x_mm, y_mm):
    return x_mm + y_mm, x_mm - y_mm
 
def ab_to_xy(a_mm, b_mm):
    return 0.5 * (a_mm + b_mm), 0.5 * (a_mm - b_mm)
 
def mm_to_rad(mm):
    return mm * (2.0 * math.pi / MM_PER_REV)      # GT2 20T -> 40mm/rev
```
 
`python3 corexy.py`로 정변환/역변환 왕복 검증.
 
---
 
## Step 8 — G-code 플레이어 노드 (W3)
 
`gcode_player_node.py`
 
퍼블리시: `/joint_states` (50Hz), `/printer/status` (5Hz), `/printer/extrusion` (이벤트)
구독: `/printer/cmd`
 
핵심 파라미터:
 
| 파라미터 | 기본값 | 설명 |
|---|---|---|
| `gcode_path` | `''` | G-code 절대경로 |
| `rate` | `50.0` | 퍼블리시 주기 [Hz] |
| `speed_scale` | `1.0` | 재생 배속 |
| `extrusion_width` | `0.42` | 압출 폭 [mm] |
| `layer_height` | `0.20` | 레이어 높이 [mm] |
 
```bash
ros2 run voron24_gcode gcode_player --ros-args \
  -p gcode_path:=/path/benchy.gcode -p speed_scale:=20.0
```
 
> **`speed_scale` 20~100배가 데모 필수 조건.** 실시간(1x)이면 벤치 하나에 1시간 소요.
 
압출 궤적은 `bed_origin` 프레임 기준 m 단위로 퍼블리시:
 
```python
ep.header.frame_id = 'bed_origin'
ep.position = Point(x=x*MM, y=y*MM, z=z*MM)      # MM = 1e-3
```
 
`/printer/cmd`의 `jog`는 **args 단위가 mm** (계약 §6 SI 예외).
 
---
 
## Step 9 — 실제 메시 전환 (W4)
 
A가 `voron24_params.xacro`를 갱신하고 메시를 푸시하면 **C가 할 일은 검증뿐.** URDF 본문 수정 불요 (계약 §4).
 
```bash
git pull
python3 tools/contract_check.py \
  --xacro src/voron24_description/urdf/voron24.urdf.xacro \
  --use-meshes --check-meshes
 
ros2 launch voron24_description display.launch.py use_meshes:=true
```
 
검증 항목:
- [ ] `joint_z=0`에서 노즐이 베드에 접촉
- [ ] `joint_x=0.25`에서 툴헤드가 X빔 우측 끝
- [ ] `bed_origin` 프레임이 베드 좌전방 코너
- [ ] `ros2 run tf2_tools view_frames`로 트리 확인
- [ ] `voron24_params.xacro`의 `MEASURED="true"`
---
 
## Step 10 — Moonraker 연동 (W5, 실기 디지털 트윈)
 
Voron은 대부분 Klipper 사용 → **Moonraker WebSocket API**로 실시간 상태 수신.
 
```python
ws.send(json.dumps({
    "jsonrpc": "2.0", "id": 1,
    "method": "printer.objects.subscribe",
    "params": {"objects": {
        "toolhead": ["position", "homed_axes"],
        "extruder": ["temperature", "target"],
        "heater_bed": ["temperature", "target"],
        "print_stats": ["state", "filename", "info"],
        "display_status": ["progress"],
    }}}))
```
 
> **`toolhead.position`은 이미 카티전 좌표(mm).** CoreXY 역변환 불요.
 
### 안전 인터록 — 쓰기 방향은 별도 검증 후 활성화
 
읽기 전용으로 시작할 것. Unity → 실기 명령(`/printer/cmd` → Moonraker `printer.gcode.script`)은 다음 조건 충족 후에만:
 
- 소프트 리밋 검사 — 0~250 범위 밖 거부
- `/emergency_stop` 토픽 구독 → 즉시 `M112` 전송
- 온도 미달 시 압출 명령 차단
- `enable_write` 파라미터 기본값 `false`
- WebSocket 재연결 로직 (`on_close` 핸들러)
---
 
## Step 11 — 통합 launch (W4~W5)
 
`sim.launch.py`:
 
```bash
ros2 launch voron24_bringup sim.launch.py gcode:=/path/benchy.gcode speed:=30.0
```
 
`real.launch.py`:
 
```bash
ros2 launch voron24_bringup real.launch.py moonraker_host:=192.168.0.50
```
 
---
 
## 검증 & 디버깅
 
```bash
ros2 topic list
ros2 topic hz /joint_states              # 50 Hz 근처
ros2 topic echo /joint_states --once
ros2 run tf2_tools view_frames           # frames.pdf
ros2 run tf2_ros tf2_echo bed_origin nozzle
ros2 param list /gcode_player
ros2 topic pub --once /printer/cmd voron24_msgs/PrinterCommand "{command: 'pause'}"
```
 
### 체크리스트
 
- [ ] `check_urdf` 통과
- [ ] `contract_check.py` 통과 (mock/real 양쪽)
- [ ] `python3 -m voron24_gcode.patterns` ALL PASS
- [ ] `/joint_states` 50 Hz 안정
- [ ] `tf2_echo bed_origin nozzle` 값이 G-code XYZ와 일치 (mm→m)
- [ ] `speed_scale=50`에서도 노드 지연 없음
- [ ] Unity 연결 시 RViz와 자세 동일
- [ ] `/printer/extrusion` 발행량이 폭주하지 않음
---
 
## 산출물 요약
 
| 경로 | 설명 | 소비자 |
|---|---|---|
| `urdf/voron24.urdf.xacro` | 링크 트리 (mock/real 겸용) | B |
| `urdf/voron24_macros.xacro` | 관성·형상 매크로 | — |
| `urdf/voron24_params.xacro` | 초기 추정치 제공 → 이후 A가 관리 | A |
| `launch/display.launch.py` | RViz 확인 | A, C |
| `rviz/voron24.rviz` | RViz 설정 | 전원 |
| `msg/*.msg` | 커스텀 메시지 | B (C# 생성) |
| `patterns.py` | 궤적 로직 (단독 테스트) | — |
| `mock_publisher_node.py` | 더미 퍼블리셔 | B |
| `gcode_parser.py` / `gcode_player_node.py` | G-code 재생 | B |
| `corexy.py` | CoreXY 변환 | — |
| `moonraker_bridge_node.py` | 실기 브릿지 | — |
| `bringup/launch/*.launch.py` | 통합 실행 | 전원 |
| `tools/contract_check.py` | 계약 검증 | **전원 / CI** |
| `tools/smoke_test.sh` | 통합 게이트 | 전원 |
 
---
 
## 참고: `voron24_description/CMakeLists.txt`
 
```cmake
cmake_minimum_required(VERSION 3.8)
project(voron24_description)
find_package(ament_cmake REQUIRED)
install(DIRECTORY urdf meshes launch rviz
        DESTINATION share/${PROJECT_NAME})
ament_package()
```
 
---
 
## 트러블슈팅
 
| 증상 | 원인 / 대응 |
|---|---|
| Unity 연결 실패 | `ROS_IP:=0.0.0.0`, `sudo ufw allow 10000`, WSL 포트 포워딩 |
| `ROS_DOMAIN_ID` 불일치 | 팀 전원 동일 값. `~/.bashrc` 등록 |
| xacro 에러 | `xacro file.xacro` 단독 실행해 메시지 확인 |
| RViz에 메시 미표시 | `package://` 경로. `colcon build` 후 `source` 재실행. `install(DIRECTORY meshes ...)` 확인 |
| **`/joint_states` 충돌** | `joint_state_publisher_gui`와 `mock_publisher` 동시 실행 시 상호 덮어씀. **하나만 실행** (`display.launch.py`=gui, `mock.launch.py`=mock_publisher) |
| TF에 링크 누락 | robot_state_publisher가 해당 조인트 position 미수신. `msg.name` 오타 확인 |
| 커스텀 msg import 실패 | `colcon build --packages-select voron24_msgs` 후 `source` |
| `speed_scale` 상향 시 끊김 | `advance()` while 루프가 프레임당 처리량 초과. 프레임당 이동 수 상한 설정 |
| Moonraker 연결 단절 | `websocket-client` 재연결 로직, `on_close` 핸들러 |
| `contract_check` bed_origin 오류 | parent를 `z_gantry`로 지정한 상태. Voron 2.4는 베드 고정 |