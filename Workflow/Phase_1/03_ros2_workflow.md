# 03 — ROS2 담당 워크플로

> **담당자: C**
> **최종 산출물**: `ros2_ws/src/` — URDF 패키지, G-code 재생 노드, Unity 브릿지, Moonraker 연동, 통합 launch
> **선행 조건**: [00_interface_contract.md](00_interface_contract.md) 숙지
> **가장 중요한 역할**: **W1 Day 1~2에 mock URDF와 사인파 퍼블리셔를 올려서 A와 B의 작업을 언블록하는 것.** 이게 늦으면 팀 전체가 멈춥니다.

---

## 준비

| 항목 | 버전 |
|---|---|
| **ROS2** | **Humble** (Ubuntu 22.04) 또는 **Jazzy** (24.04) |
| Python | 3.10+ |
| 빌드 | colcon |

```bash
sudo apt install ros-humble-desktop \
  ros-humble-robot-state-publisher ros-humble-joint-state-publisher-gui \
  ros-humble-xacro ros-humble-tf2-tools ros-humble-rviz2 \
  python3-colcon-common-extensions

mkdir -p ~/voron24-digital-twin/ros2_ws/src && cd ~/voron24-digital-twin/ros2_ws
```

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
│   ├── CMakeLists.txt
│   ├── package.xml
│   ├── urdf/
│   │   ├── voron24_mock.urdf.xacro      ← W1 (C 작성)
│   │   ├── voron24.urdf.xacro           ← W4 (C 작성, A의 실측값 사용)
│   │   ├── voron24.gazebo.xacro         (선택)
│   │   └── inertial_macros.xacro
│   ├── meshes/                          ← A 담당 영역. C는 건드리지 않음
│   │   ├── visual/
│   │   └── collision/
│   ├── rviz/voron24.rviz
│   └── launch/display.launch.py
├── voron24_msgs/msg/
│   ├── PrinterStatus.msg
│   ├── ExtrusionPoint.msg
│   └── PrinterCommand.msg
├── voron24_gcode/voron24_gcode/
│   ├── gcode_parser.py
│   ├── gcode_player_node.py
│   ├── mock_publisher_node.py           ← W1
│   └── corexy.py
├── voron24_moonraker/voron24_moonraker/
│   └── moonraker_bridge_node.py
└── voron24_bringup/
    ├── launch/
    │   ├── mock.launch.py
    │   ├── sim.launch.py
    │   └── real.launch.py
    └── config/*.yaml
```

> **파일 소유권 규칙**: `meshes/`는 A만, `urdf/`와 `launch/`는 C만 수정합니다. 같은 패키지를 공유하지만 파일이 겹치지 않으므로 머지 충돌이 안 납니다.

---

## Step 2 — Mock URDF (W1 Day 1~2) ★ 최우선

박스만으로 계약 §3의 링크 트리를 그대로 구현합니다. 좌표는 대략값이어도 무방 — **이름과 구조가 계약과 일치하는 게 전부**입니다.

`voron24_description/urdf/inertial_macros.xacro`:

```xml
<?xml version="1.0"?>
<robot xmlns:xacro="http://www.ros.org/wiki/xacro">
  <xacro:macro name="box_inertia" params="m x y z *origin">
    <inertial>
      <xacro:insert_block name="origin"/>
      <mass value="${m}"/>
      <inertia ixx="${m*(y*y+z*z)/12}" iyy="${m*(x*x+z*z)/12}" izz="${m*(x*x+y*y)/12}"
               ixy="0" ixz="0" iyz="0"/>
    </inertial>
  </xacro:macro>
</robot>
```

`voron24_description/urdf/voron24_mock.urdf.xacro`:

```xml
<?xml version="1.0"?>
<robot xmlns:xacro="http://www.ros.org/wiki/xacro" name="voron24">
  <xacro:include filename="$(find voron24_description)/urdf/inertial_macros.xacro"/>

  <!-- Voron 2.4 250mm. 플라잉 갠트리 CoreXY. 베드는 base_link 고정. -->
  <xacro:property name="stroke" value="0.250"/>
  <xacro:property name="z_home" value="0.2145"/>   <!-- A 실측으로 교체 -->

  <link name="base_link">
    <visual>
      <origin xyz="0 0 0.2325"/>
      <geometry><box size="0.350 0.350 0.465"/></geometry>
      <material name="frame"><color rgba="0.6 0.62 0.65 0.25"/></material>
    </visual>
    <visual>  <!-- 베드 (고정!) -->
      <origin xyz="0 0 0.060"/>
      <geometry><box size="0.250 0.250 0.008"/></geometry>
      <material name="bed"><color rgba="0.15 0.15 0.18 1"/></material>
    </visual>
    <collision>
      <origin xyz="0 0 0.2325"/>
      <geometry><box size="0.350 0.350 0.465"/></geometry>
    </collision>
    <xacro:box_inertia m="12.0" x="0.35" y="0.35" z="0.465">
      <origin xyz="0 0 0.15"/>
    </xacro:box_inertia>
  </link>

  <!-- Z: 갠트리 전체가 승강 -->
  <link name="z_gantry">
    <visual>
      <geometry><box size="0.330 0.330 0.030"/></geometry>
      <material name="gantry"><color rgba="0.85 0.3 0.2 1"/></material>
    </visual>
    <collision><geometry><box size="0.330 0.330 0.030"/></geometry></collision>
    <xacro:box_inertia m="2.2" x="0.33" y="0.33" z="0.03"><origin xyz="0 0 0"/></xacro:box_inertia>
  </link>
  <joint name="joint_z" type="prismatic">
    <parent link="base_link"/><child link="z_gantry"/>
    <origin xyz="0 0 ${z_home}"/>
    <axis xyz="0 0 1"/>
    <limit lower="0" upper="${stroke}" effort="200" velocity="0.05"/>
    <dynamics damping="5.0" friction="2.0"/>
  </joint>

  <!-- Y: X빔이 앞뒤로 -->
  <link name="x_beam">
    <visual>
      <geometry><box size="0.320 0.030 0.030"/></geometry>
      <material name="beam"><color rgba="0.2 0.5 0.85 1"/></material>
    </visual>
    <collision><geometry><box size="0.320 0.030 0.030"/></geometry></collision>
    <xacro:box_inertia m="0.9" x="0.32" y="0.03" z="0.03"><origin xyz="0 0 0"/></xacro:box_inertia>
  </link>
  <joint name="joint_y" type="prismatic">
    <parent link="z_gantry"/><child link="x_beam"/>
    <origin xyz="0 ${-stroke/2} 0.012"/>
    <axis xyz="0 1 0"/>
    <limit lower="0" upper="${stroke}" effort="50" velocity="0.5"/>
    <dynamics damping="0.5" friction="0.2"/>
  </joint>

  <!-- X: 툴헤드 -->
  <link name="toolhead">
    <visual>
      <origin xyz="0 0.01 -0.025"/>
      <geometry><box size="0.055 0.060 0.090"/></geometry>
      <material name="th"><color rgba="0.9 0.9 0.9 1"/></material>
    </visual>
    <collision>
      <origin xyz="0 0.01 -0.025"/>
      <geometry><box size="0.055 0.060 0.090"/></geometry>
    </collision>
    <xacro:box_inertia m="0.45" x="0.055" y="0.06" z="0.09">
      <origin xyz="0 0.01 -0.025"/>
    </xacro:box_inertia>
  </link>
  <joint name="joint_x" type="prismatic">
    <parent link="x_beam"/><child link="toolhead"/>
    <origin xyz="${-stroke/2} 0 0"/>
    <axis xyz="1 0 0"/>
    <limit lower="0" upper="${stroke}" effort="50" velocity="0.5"/>
    <dynamics damping="0.5" friction="0.2"/>
  </joint>

  <!-- TCP -->
  <link name="nozzle"/>
  <joint name="joint_nozzle" type="fixed">
    <parent link="toolhead"/><child link="nozzle"/>
    <origin xyz="0 0.0085 -0.052"/>
  </joint>

  <!-- G-code 원점 (베드 좌전방). base_link 에 고정 -->
  <link name="bed_origin"/>
  <joint name="joint_bed_origin" type="fixed">
    <parent link="base_link"/><child link="bed_origin"/>
    <origin xyz="-0.125 -0.125 0.064"/>
  </joint>
</robot>
```

### 즉시 검증

```bash
cd ~/voron24-digital-twin/ros2_ws
colcon build --symlink-install && source install/setup.bash

# 문법 체크
xacro src/voron24_description/urdf/voron24_mock.urdf.xacro > /tmp/v.urdf
check_urdf /tmp/v.urdf
urdf_to_graphiz /tmp/v.urdf    # 트리 시각화 PDF 생성

# RViz + 슬라이더
ros2 launch voron24_description display.launch.py
```

`display.launch.py`:

```python
from launch import LaunchDescription
from launch.actions import DeclareLaunchArgument
from launch.substitutions import Command, LaunchConfiguration, PathJoinSubstitution
from launch_ros.actions import Node
from launch_ros.substitutions import FindPackageShare

def generate_launch_description():
    model = LaunchConfiguration('model')
    pkg = FindPackageShare('voron24_description')
    urdf = PathJoinSubstitution([pkg, 'urdf', model])
    return LaunchDescription([
        DeclareLaunchArgument('model', default_value='voron24_mock.urdf.xacro'),
        Node(package='robot_state_publisher', executable='robot_state_publisher',
             parameters=[{'robot_description': Command(['xacro ', urdf])}]),
        Node(package='joint_state_publisher_gui', executable='joint_state_publisher_gui'),
        Node(package='rviz2', executable='rviz2', arguments=[
             '-d', PathJoinSubstitution([pkg, 'rviz', 'voron24.rviz'])]),
    ])
```

**슬라이더를 움직여 확인**:
- `joint_z` ↑ → 갠트리만 올라가고 **베드는 그대로**
- `joint_x`, `joint_y` → 툴헤드가 정확히 X/Y 방향
- 세 조인트 0일 때 노즐이 베드 좌전방 코너 근처

### 커밋 & 팀 알림

```bash
git add ros2_ws/src/voron24_description ros2_ws/src/voron24_msgs
git commit -m "feat(ros2): mock URDF + 링크 트리 확정 (계약 §3)"
git push
```

**Slack/Discord에 "mock URDF 올렸습니다. B는 Unity 임포트 시작 가능"** 이라고 알리세요.

---

## Step 3 — Mock 퍼블리셔 (W1 Day 2)

B가 Unity에서 뭔가 움직이는 걸 즉시 볼 수 있게 합니다.

`voron24_gcode/voron24_gcode/mock_publisher_node.py`:

```python
#!/usr/bin/env python3
"""계약 §5 검증용 더미 퍼블리셔. 리사주 곡선으로 X/Y/Z를 흔든다."""
import math
import rclpy
from rclpy.node import Node
from sensor_msgs.msg import JointState

class MockPublisher(Node):
    def __init__(self):
        super().__init__('mock_publisher')
        self.declare_parameter('rate', 50.0)
        self.declare_parameter('stroke', 0.250)
        rate = self.get_parameter('rate').value
        self.stroke = self.get_parameter('stroke').value

        self.pub = self.create_publisher(JointState, '/joint_states', 10)
        self.t = 0.0
        self.dt = 1.0 / rate
        self.create_timer(self.dt, self.tick)
        self.get_logger().info(f'mock publisher @ {rate} Hz')

    def tick(self):
        self.t += self.dt
        s, half = self.stroke, self.stroke / 2
        m = JointState()
        m.header.stamp = self.get_clock().now().to_msg()
        m.name = ['joint_x', 'joint_y', 'joint_z']
        m.position = [
            half + half * 0.9 * math.sin(self.t * 1.1),
            half + half * 0.9 * math.sin(self.t * 0.7),
            0.02 + 0.02 * (1 + math.sin(self.t * 0.15)),
        ]
        m.velocity = [0.0] * 3
        self.pub.publish(m)

def main():
    rclpy.init()
    n = MockPublisher()
    try:
        rclpy.spin(n)
    except KeyboardInterrupt:
        pass
    finally:
        n.destroy_node(); rclpy.shutdown()
```

`setup.py`의 `entry_points`:
```python
'console_scripts': [
    'mock_publisher = voron24_gcode.mock_publisher_node:main',
    'gcode_player   = voron24_gcode.gcode_player_node:main',
],
```

---

## Step 4 — Unity 브릿지 (W1 Day 2)

```bash
cd ~/voron24-digital-twin/ros2_ws/src
git clone -b main-ros2 https://github.com/Unity-Technologies/ROS-TCP-Endpoint.git
cd .. && colcon build --packages-select ros_tcp_endpoint && source install/setup.bash

ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=0.0.0.0
```

`voron24_bringup/launch/mock.launch.py`:

```python
from launch import LaunchDescription
from launch.substitutions import Command, PathJoinSubstitution
from launch_ros.actions import Node
from launch_ros.substitutions import FindPackageShare

def generate_launch_description():
    pkg = FindPackageShare('voron24_description')
    urdf = PathJoinSubstitution([pkg, 'urdf', 'voron24_mock.urdf.xacro'])
    return LaunchDescription([
        Node(package='robot_state_publisher', executable='robot_state_publisher',
             parameters=[{'robot_description': Command(['xacro ', urdf]),
                          'publish_frequency': 50.0}]),
        Node(package='ros_tcp_endpoint', executable='default_server_endpoint',
             parameters=[{'ROS_IP': '0.0.0.0', 'ROS_TCP_PORT': 10000}]),
        Node(package='voron24_gcode', executable='mock_publisher',
             parameters=[{'rate': 50.0}]),
        Node(package='rviz2', executable='rviz2',
             arguments=['-d', PathJoinSubstitution([pkg, 'rviz', 'voron24.rviz'])]),
    ])
```

**W1 통합 게이트**: `ros2 launch voron24_bringup mock.launch.py` → RViz와 Unity에서 **동시에** 같은 움직임이 보이면 성공.

---

## Step 5 — 커스텀 메시지 (W2)

`voron24_msgs/msg/PrinterStatus.msg` 등 계약 §5의 정의를 그대로 작성.

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

> **msg를 변경하면 B가 Unity에서 C# 재생성을 해야 합니다.** 변경 시 반드시 알리고, 가급적 W2 안에 확정하세요.

---

## Step 6 — G-code 파서 (W2~W3)

`voron24_gcode/voron24_gcode/gcode_parser.py`:

```python
"""G-code → 이동 명령 리스트. Marlin/Klipper 공통 서브셋."""
import re
from dataclasses import dataclass, field

TOKEN = re.compile(r'([A-Z])(-?\d*\.?\d+)')

@dataclass
class Move:
    x: float; y: float; z: float; e: float
    feed: float                # mm/min
    extruding: bool
    layer: int
    dist: float = 0.0          # mm, XYZ 이동거리
    dur:  float = 0.0          # s

@dataclass
class GcodeProgram:
    moves: list = field(default_factory=list)
    total_time: float = 0.0
    layer_count: int = 0
    filename: str = ""

def parse(path, default_feed=3000.0):
    pos = {'X': 0.0, 'Y': 0.0, 'Z': 0.0, 'E': 0.0}
    feed = default_feed
    absolute_xyz, absolute_e = True, True
    layer = 0
    prog = GcodeProgram(filename=path.split('/')[-1])

    for raw in open(path, errors='ignore'):
        line = raw.split(';')[0].strip()
        # 슬라이서 레이어 주석 (Cura/PrusaSlicer/Orca)
        c = raw.strip()
        if c.startswith((';LAYER:', ';LAYER_CHANGE', ';AFTER_LAYER_CHANGE')):
            layer += 1
        if not line:
            continue
        cmd = line.split()[0].upper()

        if cmd == 'G90': absolute_xyz = absolute_e = True; continue
        if cmd == 'G91': absolute_xyz = absolute_e = False; continue
        if cmd == 'M82': absolute_e = True;  continue
        if cmd == 'M83': absolute_e = False; continue
        if cmd == 'G92':
            for k, v in TOKEN.findall(line[3:]):
                if k in pos: pos[k] = float(v)
            continue
        if cmd == 'G28':
            for k in ('X', 'Y', 'Z'):
                pos[k] = 0.0
            prog.moves.append(Move(0, 0, 0, pos['E'], feed, False, layer))
            continue
        if cmd not in ('G0', 'G1'):
            continue

        tok = dict(TOKEN.findall(line[len(cmd):]))
        feed = float(tok.get('F', feed))
        start = dict(pos)
        for k in ('X', 'Y', 'Z'):
            if k in tok:
                v = float(tok[k])
                pos[k] = v if absolute_xyz else pos[k] + v
        de = 0.0
        if 'E' in tok:
            v = float(tok['E'])
            de = (v - pos['E']) if absolute_e else v
            pos['E'] = v if absolute_e else pos['E'] + v

        dist = sum((pos[k] - start[k]) ** 2 for k in 'XYZ') ** 0.5
        dur = dist / (feed / 60.0) if feed > 0 and dist > 0 else 0.0
        prog.moves.append(Move(pos['X'], pos['Y'], pos['Z'], pos['E'],
                               feed, de > 1e-6 and dist > 1e-6, layer, dist, dur))
        prog.total_time += dur

    prog.layer_count = layer
    return prog
```

> **주의**: 이 파서는 가감속을 무시하므로 예상 시간이 실제보다 짧게 나옵니다. 시각화 목적에는 충분하지만, 정확한 시간이 필요하면 슬라이서가 넣어주는 `;TIME:` 주석을 파싱하세요. `G2/G3`(원호), `G29`(베드 레벨링) 등은 미지원 — 필요하면 확장.

### CoreXY 변환 (선택)

`voron24_gcode/voron24_gcode/corexy.py`:

```python
import math
BELT_PITCH = 2.0      # GT2
PULLEY_TEETH = 20
MM_PER_REV = BELT_PITCH * PULLEY_TEETH     # 40 mm

def xy_to_ab(x_mm, y_mm):
    return x_mm + y_mm, x_mm - y_mm

def ab_to_angles(a_mm, b_mm):
    k = 2 * math.pi / MM_PER_REV
    return a_mm * k, b_mm * k     # rad
```

---

## Step 7 — G-code 플레이어 노드 (W3)

`voron24_gcode/voron24_gcode/gcode_player_node.py`:

```python
#!/usr/bin/env python3
import rclpy
from rclpy.node import Node
from sensor_msgs.msg import JointState
from std_msgs.msg import Header
from voron24_msgs.msg import PrinterStatus, ExtrusionPoint, PrinterCommand
from geometry_msgs.msg import Point
from .gcode_parser import parse

MM = 1e-3

class GcodePlayer(Node):
    def __init__(self):
        super().__init__('gcode_player')
        self.declare_parameter('gcode_path', '')
        self.declare_parameter('rate', 50.0)
        self.declare_parameter('speed_scale', 1.0)
        self.declare_parameter('extrusion_width', 0.42)
        self.declare_parameter('layer_height', 0.20)

        self.rate  = self.get_parameter('rate').value
        self.dt    = 1.0 / self.rate
        self.scale = self.get_parameter('speed_scale').value
        self.width = self.get_parameter('extrusion_width').value
        self.lh    = self.get_parameter('layer_height').value

        self.js_pub  = self.create_publisher(JointState, '/joint_states', 10)
        self.st_pub  = self.create_publisher(PrinterStatus, '/printer/status', 10)
        self.ex_pub  = self.create_publisher(ExtrusionPoint, '/printer/extrusion', 200)
        self.create_subscription(PrinterCommand, '/printer/cmd', self.on_cmd, 10)

        self.prog = None
        self.idx = 0
        self.t_in_move = 0.0
        self.pos = {'X': 0.0, 'Y': 0.0, 'Z': 0.0, 'E': 0.0}
        self.prev = dict(self.pos)
        self.state = 'idle'

        path = self.get_parameter('gcode_path').value
        if path:
            self.load(path)

        self.create_timer(self.dt, self.tick)
        self.create_timer(0.2, self.publish_status)

    # ---------------------------------------------------------------
    def load(self, path):
        self.get_logger().info(f'parsing {path} ...')
        self.prog = parse(path)
        self.idx, self.t_in_move = 0, 0.0
        self.state = 'printing'
        self.get_logger().info(
            f'{len(self.prog.moves)} moves / {self.prog.layer_count} layers / '
            f'{self.prog.total_time/60:.1f} min (가감속 무시)')

    def on_cmd(self, msg):
        c = msg.command
        if   c == 'pause':  self.state = 'paused'
        elif c == 'resume': self.state = 'printing'
        elif c == 'stop':   self.state = 'idle'; self.idx = 0
        elif c == 'load_gcode': self.load(msg.payload)
        elif c == 'jog' and len(msg.args) >= 3:
            self.pos['X'] += msg.args[0]; self.pos['Y'] += msg.args[1]
            self.pos['Z'] += msg.args[2]
        elif c == 'home':
            self.pos.update({'X': 0.0, 'Y': 0.0, 'Z': 0.0})
        self.get_logger().info(f'cmd: {c} → state={self.state}')

    # ---------------------------------------------------------------
    def tick(self):
        if self.state == 'printing' and self.prog:
            self.advance()
        self.publish_joints()

    def advance(self):
        budget = self.dt * self.scale
        while budget > 1e-9 and self.idx < len(self.prog.moves):
            mv = self.prog.moves[self.idx]
            if mv.dur <= 1e-9:                       # 순간 이동
                self.commit(mv, mv.x, mv.y, mv.z)
                self.idx += 1; self.t_in_move = 0.0
                continue
            step = min(budget, mv.dur - self.t_in_move)
            self.t_in_move += step; budget -= step
            s = min(self.t_in_move / mv.dur, 1.0)
            p = self.prog.moves[self.idx - 1] if self.idx > 0 else None
            sx = p.x if p else 0.0; sy = p.y if p else 0.0; sz = p.z if p else 0.0
            self.commit(mv, sx + (mv.x-sx)*s, sy + (mv.y-sy)*s, sz + (mv.z-sz)*s)
            if s >= 1.0:
                self.idx += 1; self.t_in_move = 0.0
        if self.idx >= len(self.prog.moves):
            self.state = 'idle'
            self.get_logger().info('print finished')

    def commit(self, mv, x, y, z):
        self.prev = dict(self.pos)
        self.pos.update({'X': x, 'Y': y, 'Z': z, 'E': mv.e})
        # 압출 궤적 이벤트 (베드 원점 기준, m)
        if mv.extruding:
            ep = ExtrusionPoint()
            ep.header.stamp = self.get_clock().now().to_msg()
            ep.header.frame_id = 'bed_origin'
            ep.position = Point(x=x*MM, y=y*MM, z=z*MM)
            ep.width = self.width * MM
            ep.height = self.lh * MM
            ep.extruding = True
            ep.layer = mv.layer
            self.ex_pub.publish(ep)

    def publish_joints(self):
        m = JointState()
        m.header.stamp = self.get_clock().now().to_msg()
        m.name = ['joint_x', 'joint_y', 'joint_z']
        m.position = [self.pos['X']*MM, self.pos['Y']*MM, self.pos['Z']*MM]
        self.js_pub.publish(m)

    def publish_status(self):
        s = PrinterStatus()
        s.header.stamp = self.get_clock().now().to_msg()
        s.state = self.state
        s.nozzle_temp = 220.0 if self.state == 'printing' else 25.0
        s.nozzle_target = 220.0 if self.state == 'printing' else 0.0
        s.bed_temp = 60.0 if self.state == 'printing' else 25.0
        s.bed_target = 60.0 if self.state == 'printing' else 0.0
        s.chamber_temp = 42.0
        if self.prog:
            s.progress = self.idx / max(len(self.prog.moves), 1)
            s.current_layer = self.prog.moves[min(self.idx, len(self.prog.moves)-1)].layer
            s.total_layers = self.prog.layer_count
            s.filename = self.prog.filename
        self.st_pub.publish(s)

def main():
    rclpy.init(); n = GcodePlayer()
    try: rclpy.spin(n)
    except KeyboardInterrupt: pass
    finally: n.destroy_node(); rclpy.shutdown()
```

### 실행

```bash
ros2 run voron24_gcode gcode_player --ros-args \
  -p gcode_path:=/path/benchy.gcode -p speed_scale:=20.0
```

`speed_scale`로 20~100배 빨리 감기가 가능해야 데모가 됩니다. 실시간(1x)이면 벤치 하나에 1시간 걸립니다.

---

## Step 8 — 실제 URDF (W4)

A가 `cad/measurements.md`와 `cad/inertial_snippets.xml`을 올리면 `voron24.urdf.xacro`를 작성합니다.

```xml
<xacro:property name="mesh" value="package://voron24_description/meshes"/>

<link name="z_gantry">
  <visual>
    <geometry><mesh filename="${mesh}/visual/z_gantry.stl" scale="0.001 0.001 0.001"/></geometry>
    <material name="voron_red"><color rgba="0.85 0.25 0.2 1"/></material>
  </visual>
  <collision>
    <geometry><mesh filename="${mesh}/collision/z_gantry.stl" scale="0.001 0.001 0.001"/></geometry>
  </collision>
  <!-- A의 inertial_snippets.xml 에서 복사 -->
  <inertial>
    <origin xyz="0.00012 -0.00340 0.01205"/>
    <mass value="2.2000"/>
    <inertia ixx="0.020" iyy="0.020" izz="0.040" ixy="0" ixz="0" iyz="0"/>
  </inertial>
</link>
```

**조인트 origin은 `measurements.md`의 mm 값을 1000으로 나눠서** 넣습니다.

### 대조 검증

```bash
# mock 과 real 을 나란히 띄워 자세 비교
ros2 launch voron24_description display.launch.py model:=voron24.urdf.xacro
```

- [ ] `joint_z=0`에서 노즐이 베드에 닿는가
- [ ] `joint_x=0.25`에서 툴헤드가 X빔 우측 끝인가
- [ ] `bed_origin` 프레임이 베드 좌전방 코너에 있는가
- [ ] `ros2 run tf2_tools view_frames` 로 트리 확인

---

## Step 9 — Moonraker 연동 (W5, 실기 디지털 트윈)

Voron은 대부분 Klipper를 쓰므로 **Moonraker API**로 실시간 상태를 받을 수 있습니다.

`voron24_moonraker/voron24_moonraker/moonraker_bridge_node.py`:

```python
#!/usr/bin/env python3
"""Moonraker WebSocket → /joint_states + /printer/status"""
import json, threading
import rclpy, websocket          # pip install websocket-client
from rclpy.node import Node
from sensor_msgs.msg import JointState
from voron24_msgs.msg import PrinterStatus

MM = 1e-3

class MoonrakerBridge(Node):
    def __init__(self):
        super().__init__('moonraker_bridge')
        self.declare_parameter('host', '192.168.0.50')
        self.declare_parameter('port', 7125)
        host = self.get_parameter('host').value
        port = self.get_parameter('port').value

        self.js = self.create_publisher(JointState, '/joint_states', 10)
        self.st = self.create_publisher(PrinterStatus, '/printer/status', 10)
        self.pos = [0.0, 0.0, 0.0]

        url = f'ws://{host}:{port}/websocket'
        self.ws = websocket.WebSocketApp(url, on_open=self.on_open,
                                         on_message=self.on_message)
        threading.Thread(target=self.ws.run_forever, daemon=True).start()
        self.create_timer(0.02, self.pub_joints)     # 50 Hz

    def on_open(self, ws):
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
        self.get_logger().info('moonraker connected')

    def on_message(self, ws, raw):
        d = json.loads(raw)
        status = None
        if d.get("method") == "notify_status_update":
            status = d["params"][0]
        elif "result" in d and "status" in d["result"]:
            status = d["result"]["status"]
        if not status:
            return
        if "toolhead" in status and "position" in status["toolhead"]:
            p = status["toolhead"]["position"]
            self.pos = [p[0]*MM, p[1]*MM, p[2]*MM]
        # 온도/상태는 PrinterStatus 로 별도 퍼블리시 (생략)

    def pub_joints(self):
        m = JointState()
        m.header.stamp = self.get_clock().now().to_msg()
        m.name = ['joint_x', 'joint_y', 'joint_z']
        m.position = list(self.pos)
        self.js.publish(m)
```

> **Moonraker의 `toolhead.position`은 이미 카티전 좌표(mm)** 입니다. CoreXY 역변환을 직접 할 필요 없습니다.
>
> **읽기 전용으로 시작하세요.** Unity에서 실기로 명령을 보내는 방향(`/printer/cmd` → Moonraker `printer.gcode.script`)은 **반드시 별도 안전 인터록을 거친 후에** 활성화합니다:
> - 소프트 리밋 검사 (0~250 범위 밖 거부)
> - `/emergency_stop` 토픽 구독 → 즉시 `M112` 전송
> - 온도 미달 시 압출 명령 차단
> - `enable_write` 파라미터 기본값 `false`

---

## Step 10 — 통합 launch (W4~W5)

`voron24_bringup/launch/sim.launch.py`:

```python
from launch import LaunchDescription
from launch.actions import DeclareLaunchArgument
from launch.substitutions import Command, LaunchConfiguration, PathJoinSubstitution
from launch.conditions import IfCondition
from launch_ros.actions import Node
from launch_ros.substitutions import FindPackageShare

def generate_launch_description():
    pkg   = FindPackageShare('voron24_description')
    gcode = LaunchConfiguration('gcode')
    speed = LaunchConfiguration('speed')
    rviz  = LaunchConfiguration('rviz')
    urdf  = PathJoinSubstitution([pkg, 'urdf', 'voron24.urdf.xacro'])

    return LaunchDescription([
        DeclareLaunchArgument('gcode', default_value=''),
        DeclareLaunchArgument('speed', default_value='20.0'),
        DeclareLaunchArgument('rviz',  default_value='true'),

        Node(package='robot_state_publisher', executable='robot_state_publisher',
             parameters=[{'robot_description': Command(['xacro ', urdf]),
                          'publish_frequency': 50.0}]),
        Node(package='ros_tcp_endpoint', executable='default_server_endpoint',
             parameters=[{'ROS_IP': '0.0.0.0', 'ROS_TCP_PORT': 10000}]),
        Node(package='voron24_gcode', executable='gcode_player',
             parameters=[{'gcode_path': gcode, 'speed_scale': speed, 'rate': 50.0}]),
        Node(package='rviz2', executable='rviz2', condition=IfCondition(rviz),
             arguments=['-d', PathJoinSubstitution([pkg, 'rviz', 'voron24.rviz'])]),
    ])
```

```bash
ros2 launch voron24_bringup sim.launch.py gcode:=/path/benchy.gcode speed:=30.0
```

---

## 검증 & 디버깅

```bash
ros2 topic list
ros2 topic hz /joint_states              # 50 Hz 근처여야 함
ros2 topic echo /joint_states --once
ros2 run tf2_tools view_frames           # frames.pdf 생성
ros2 run tf2_ros tf2_echo bed_origin nozzle     # 노즐의 베드 기준 위치
ros2 param list /gcode_player
ros2 topic pub --once /printer/cmd voron24_msgs/PrinterCommand "{command: 'pause'}"
```

### 체크리스트

- [ ] `check_urdf` 통과
- [ ] `/joint_states` 50 Hz 안정
- [ ] `tf2_echo bed_origin nozzle` 값이 G-code XYZ와 일치 (mm→m)
- [ ] `speed_scale=50`에서도 노드가 뒤처지지 않음
- [ ] Unity가 붙었을 때 RViz와 자세 동일
- [ ] `ros2 topic hz /printer/extrusion` 이 폭주하지 않음 (필요 시 다운샘플링)

---

## 산출물 요약

| 경로 | 설명 | 소비자 |
|---|---|---|
| `voron24_description/urdf/*.xacro` | mock + 실제 URDF | B (Unity 임포트) |
| `voron24_description/launch/display.launch.py` | RViz 확인 | A, C |
| `voron24_description/rviz/voron24.rviz` | RViz 설정 | 전원 |
| `voron24_msgs/msg/*.msg` | 커스텀 메시지 | B (C# 생성) |
| `voron24_gcode/` | 파서 + 플레이어 + mock | B |
| `voron24_moonraker/` | 실기 브릿지 | — |
| `voron24_bringup/launch/*.launch.py` | 통합 실행 | 전원 |

---

## 트러블슈팅

| 증상 | 원인 / 해결 |
|---|---|
| Unity가 연결 안 됨 | `ROS_IP:=0.0.0.0`, 방화벽 `sudo ufw allow 10000`, WSL 포트 포워딩 |
| `ROS_DOMAIN_ID` 불일치 | 팀 전원 동일 값 사용. `~/.bashrc`에 `export ROS_DOMAIN_ID=42` |
| xacro 에러 | `xacro file.xacro` 단독 실행해서 메시지 확인 |
| RViz에 메시가 안 뜸 | `package://` 경로. `colcon build` 후 `source install/setup.bash` 재실행. CMakeLists의 `install(DIRECTORY meshes urdf ...)` 확인 |
| `/joint_states` 충돌 | `joint_state_publisher_gui`와 `gcode_player`를 동시에 띄우면 서로 덮어씀. 하나만 실행 |
| TF에 링크가 없음 | robot_state_publisher가 해당 조인트의 position을 못 받음. `msg.name` 오타 확인 |
| 커스텀 msg import 실패 | `colcon build --packages-select voron24_msgs` 먼저, 그다음 source |
| `speed_scale` 높이면 끊김 | `advance()`의 while 루프가 프레임당 처리량을 초과. 프레임당 최대 이동 수 상한을 걸 것 |
| Moonraker 연결 끊김 | `websocket-client` 재연결 로직 추가, `on_close` 핸들러에서 재시도 |

---

## `voron24_description/CMakeLists.txt` 참고

```cmake
cmake_minimum_required(VERSION 3.8)
project(voron24_description)
find_package(ament_cmake REQUIRED)

install(DIRECTORY urdf meshes launch rviz config
        DESTINATION share/${PROJECT_NAME})

ament_package()
```
