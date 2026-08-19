#!/usr/bin/env python3
"""
printer_state_node.py
=====================
`/joint_states` 의 유일한 발행자이자 `/printer/cmd` 의 유일한 구독자 (04a §5b).

    ros2 run voron24_bringup printer_state_node
    ros2 topic pub --once /printer/cmd voron24_msgs/PrinterCommand \\
      "{command: 'jog', args: [1.0, 0.0, 0.0]}"          # mm ★
    ros2 topic pub --once /printer/cmd voron24_msgs/PrinterCommand "{command: 'home'}"
    ros2 topic echo /joint_states

값 소스(manual_publisher / gcode_player)는 /printer/target 으로 쏘고 이 노드가 받아
/joint_states 로 냄. 소스 전환은 launch remapping 이 하며 값 소스 코드는 건드리지
않음 (04a §"소스 전환은 remapping 으로").

     터미널 키보드 ──▶ manual_publisher ──▶ /printer/cmd ──▶ 이 노드
                                                               │       ▲
                                                   /printer/playback   /printer/target
                                                               ▼       │
                                              값 소스 (manual_publisher / gcode_player)
                                                               │
                                             /joint_states, /printer/extrusion
                                                               ▼
                                                          RViz, Unity

이 노드가 직접 하는 것은 jog 적분, home, 수동 압출 상태, 리밋 클램프, 모드 관리다.
set_speed 와 pause 처럼 **시간축**을 다루는 명령은 보간기를 쥔 값 소스가 처리해야
하므로 /printer/playback 으로 되넘김. 여기서 값을 붙잡는 방식으로 구현하면 재개 시
좌표가 튐.

단위 — /printer/cmd 의 jog 만 **mm** (계약 §2 유일한 SI 예외). 나머지 입출력은
전부 m 이고, mm→m 변환은 MM_TO_M을 쓰는 jog() 한 곳에서만 일어남. 중복 변환 주의.
"""
import os
import re

import rclpy
from rclpy.node import Node
from sensor_msgs.msg import JointState

# voron24_msgs 가 아직 안 깔렸어도 /printer/target -> /joint_states 통과만은 되게 함.
# mock_publisher_node 의 HAS_MSGS 방어와 같은 이유 (CLAUDE.md — 이 방어를 제거하지 말 것).
try:
    from voron24_msgs.msg import ExtrusionPoint, PrinterCommand, PrinterStatus
    HAS_MSGS = True
except ImportError:  # pragma: no cover
    HAS_MSGS = False

JOINT_NAMES = ['joint_x', 'joint_y', 'joint_z']   # 계약 §3. 순서·이름 변경 금지.

CMD_TOPIC = '/printer/cmd'
TARGET_TOPIC = '/printer/target'
PLAYBACK_TOPIC = '/printer/playback'
STATE_TOPIC = '/joint_states'
STATUS_TOPIC = '/printer/status'
EXTRUSION_TOPIC = '/printer/extrusion'

MM_TO_M = 0.001                                   # 이 상수를 쓰는 곳이 곧 변환 지점

# 상태 노드가 직접 처리하는 명령 / 값 소스로 되넘기는 명령.
DIRECT_COMMANDS = ('jog', 'home', 'set_extrusion')
STREAM_COMMANDS = ('load_gcode', 'pause', 'resume', 'stop', 'set_speed')

# ----------------------------------------------------------------------
# 스트로크 리밋의 단일 출처는 voron24_params.xacro (CLAUDE.md 파일 소유권 §4).
# 여기에 0.250 을 복제해 두면 A 가 값 바꿀 때 조용히 어긋남.
#
# 같은 일을 voron24_gcode/motion.py 가 최대속도(vel_*)에 대해 이미 하고 있어 그 방식을
# 그대로 따르되 **import 는 하지 않음.** bringup 이 gcode 패키지 내부 모듈에
# 의존하면 값 소스를 바꿔 끼우는 이 노드가 특정 소스 패키지에 묶이고, gcode 쪽 리팩터가
# launch 골격을 깨뜨림. 20 줄짜리 파서를 공유하려고 패키지 간 결합 만들 값어치 없음.
PARAMS_PACKAGE = 'voron24_description'
PARAMS_XACRO = os.path.join('urdf', 'voron24_params.xacro')

# xacro 를 끝내 못 찾았을 때만 쓰는 값. 계약 §3 의 250mm 스트로크.
# 이 값이 쓰였는지는 STROKE_SOURCE 가 None 인지로 드러나고 기동 로그에도 찍힌다.
FALLBACK_STROKE_M = (0.250, 0.250, 0.250)


def find_params_xacro():
    """voron24_params.xacro 경로. 설치 트리 -> 소스 트리 순으로 탐색. 없으면 None.

    ament 임포트를 try 로 감싼 이유는 HAS_MSGS 방어와 같음 — ROS 를 소싱하지 않은
    셸에서도 이 모듈의 순수 부분(리밋 로딩·클램프)이 단독으로 돌아가야 함.
    """
    try:
        from ament_index_python.packages import get_package_share_directory
        path = os.path.join(get_package_share_directory(PARAMS_PACKAGE), PARAMS_XACRO)
        if os.path.exists(path):
            return path
    except Exception:
        pass
    # realpath 로 심볼릭 링크를 풀어야 --symlink-install 로 깔린 경우에도 소스 트리에서
    # 출발함. 기준 경로는 .../src/voron24_bringup/voron24_bringup/printer_state_node.py.
    here = os.path.realpath(__file__)
    for _ in range(5):
        here = os.path.dirname(here)
        path = os.path.join(here, PARAMS_PACKAGE, PARAMS_XACRO)
        if os.path.exists(path):
            return path
    return None


def read_xacro_property(text, name):
    """`<xacro:property name=".." value=".."/>` 한 개. 숫자가 아니면 None.

    xacro 흉내는 내지 않음. `${...}` 같은 수식이면 포기하고 호출자가 fallback 을 쓰게 둠.
    """
    match = re.search(rf'<xacro:property\s+name="{name}"\s+value="([^"]*)"', text)
    if match is None:
        return None
    try:
        return float(match.group(1))
    except ValueError:
        return None


def load_stroke_limits(path=None):
    """→ ((sx, sy, sz) [m], 읽어온 경로). 못 읽으면 fallback 과 None.

    xacro stroke_* 도 m 이라 환산 없음. 모든 prismatic lower 는 0 이므로
    (계약 §3) 하한은 읽을 것 없고 상한만 가져옴.
    """
    path = path or find_params_xacro()
    if path is None:
        return FALLBACK_STROKE_M, None
    try:
        with open(path, 'r') as handle:
            text = handle.read()
    except OSError:
        return FALLBACK_STROKE_M, None

    values = [read_xacro_property(text, f'stroke_{axis}') for axis in ('x', 'y', 'z')]
    if any(value is None or value <= 0.0 for value in values):
        return FALLBACK_STROKE_M, None
    return tuple(values), path


# 임포트 시 한 번만 읽음. 노드에서는 생성자가 다시 읽어 로그에 경로를 남김.
STROKE_M, STROKE_SOURCE = load_stroke_limits()


def clamp_axes(values, stroke):
    """→ (클램프된 값 3개, 실제로 잘린 축 이름 리스트).

    lower 전부 0 (계약 §3). 스커트·프라임 라인이 베드 벗어나는 G-code 는 흔하고
    값 소스는 일부러 클램프하지 않으므로(gcode_player_node 주석) 여기가 유일한 방어선.
    """
    out = []
    hit = []
    for axis, (value, upper) in enumerate(zip(values, stroke)):
        limited = min(max(float(value), 0.0), float(upper))
        if abs(limited - float(value)) > 1e-9:
            hit.append(JOINT_NAMES[axis])
        out.append(limited)
    return out, hit


# ----------------------------------------------------------------------
class PrinterState(Node):

    def __init__(self):
        super().__init__('printer_state_node')

        self.declare_parameter('rate', 50.0)            # /joint_states 발행 주기. 계약 §6
        self.declare_parameter('status_rate', 5.0)      # /printer/status. 계약 §6
        self.declare_parameter('manual_extrusion_width', 0.005)   # 필라멘트 폭 [m]
        self.declare_parameter('manual_layer_height', 0.0002)     # 레이어 높이 [m]

        self.rate = float(self.get_parameter('rate').value)
        self.dt = 1.0 / self.rate

        self.stroke, self.stroke_source = load_stroke_limits()
        self.mode = 'manual'        # manual | playing. 초기 상태는 manual (04a §"두 모드")
        self.paused = False         # playing 중 pause 를 되넘긴 상태
        self.filename = ''
        self.position = [0.0, 0.0, 0.0]     # 현재 조인트 값 [m]. jog 적분의 누산기
        self._prev = (0.0, 0.0, 0.0)        # velocity 계산용 직전 값
        self.clamp_count = 0
        self._clamp_warned = False          # 첫 위반에서 한 번만 경고 (04a §5b)
        self._stray_target_warned = False
        self.extruding = False
        self.manual_extrusion_width = max(
            float(self.get_parameter('manual_extrusion_width').value), 1e-6)
        self.manual_layer_height = max(
            float(self.get_parameter('manual_layer_height').value), 1e-6)

        self.js_pub = self.create_publisher(JointState, STATE_TOPIC, 10)
        self.create_subscription(JointState, TARGET_TOPIC, self.on_target, 10)
        # 값 소스가 없거나 멈춰 있어도 계속 돎 — /joint_states 는 마지막 값을 유지해야
        # RViz/Unity 의 포즈가 유지되고, 재생 일시정지 중 화면이 얼어붙는 것이 정상.
        self.create_timer(self.dt, self.tick)

        self.cmd_sub = None
        self.playback_pub = None
        self.status_pub = None
        self.extrusion_pub = None
        if HAS_MSGS:
            self.playback_pub = self.create_publisher(PrinterCommand, PLAYBACK_TOPIC, 10)
            self.cmd_sub = self.create_subscription(
                PrinterCommand, CMD_TOPIC, self.on_command, 10)
            self.status_pub = self.create_publisher(PrinterStatus, STATUS_TOPIC, 10)
            self.extrusion_pub = self.create_publisher(
                ExtrusionPoint, EXTRUSION_TOPIC, 10)
            self.create_timer(1.0 / float(self.get_parameter('status_rate').value),
                              self.publish_status)
        else:
            self.get_logger().warn(
                'voron24_msgs 없음 — /printer/cmd 구독과 /printer/playback 릴레이가 빠짐. '
                '/printer/target -> /joint_states 통과만 함 '
                '(colcon build --packages-select voron24_msgs 후 setup.bash 재소싱)')

        self._yielded_status = False        # 남이 /printer/status 를 잡고 있음
        self._dup_warned = False            # /joint_states 발행자 중복 경고
        self.create_timer(2.0, self.check_sole_ownership)
        sx, sy, sz = self.stroke
        self.get_logger().info(
            f'printer state | rate={self.rate}Hz mode={self.mode} '
            f'stroke=({sx},{sy},{sz})m <- {self.stroke_source or "fallback (xacro 를 못 찾음)"}')

    # ------------------------------------------------------------------
    def set_mode(self, mode, reason):
        if mode == self.mode:
            return
        self.get_logger().info(f'모드 {self.mode} -> {mode} ({reason})')
        self.mode = mode
        if mode == 'manual':
            self.paused = False

    def relay(self, msg):
        """스트림 명령을 /printer/playback 으로 되넘김. 메시지를 그대로 흘림.

        args/payload 를 해석하지 않는 것이 요점 — set_speed 배속 단계표(SPEED_STEPS)
        도, G-code 경로 유효성도 값 소스가 판단할 몫.
        """
        if self.playback_pub is None:
            return False
        self.playback_pub.publish(msg)
        return True

    # ------------------------------------------------------------------
    def on_command(self, msg):
        """`/printer/cmd` 수신. jog·home 은 직접, 스트림 계열은 릴레이."""
        command = msg.command.strip().lower()
        if command == 'jog':
            self.jog(msg.args)
        elif command == 'home':
            self.home(msg.payload)
        elif command == 'set_extrusion':
            self.set_extrusion(msg.args)
        elif command in STREAM_COMMANDS:
            if not self.relay(msg):
                self.get_logger().warn(f'릴레이 불가 — voron24_msgs 없음: {command}')
                return
            self.after_relay(command, msg)
        else:
            self.get_logger().warn(f'모르는 명령 — 무시함: {msg.command!r}')

    def after_relay(self, command, msg):
        """릴레이 후 모드 전환. 값은 이미 넘겼고 여기서는 상태만 움직임."""
        if command == 'load_gcode':
            self.set_extrusion((0.0,), reason='load_gcode')
            self.filename = os.path.basename(msg.payload.strip())
            self.paused = False
            self.set_mode('playing', f'load_gcode {self.filename or "?"}')
        elif command == 'resume':
            self.set_extrusion((0.0,), reason='resume')
            self.paused = False
            self.set_mode('playing', 'resume')
        elif command == 'pause':
            # 모드는 그대로 playing — 값 소스가 커서를 쥔 채 멈춘 것뿐.
            self.paused = True
        elif command == 'stop':
            self.set_extrusion((0.0,), reason='stop')
            self.filename = ''
            self.set_mode('manual', 'stop')
        # set_speed 는 모드를 되돌리지 않음. 재생 중 배속만 올리는 것이 의도인데 그때마다
        # manual 로 떨어지면 쓸모 없음 (04a §"두 모드").

    # ------------------------------------------------------------------
    def jog(self, args):
        """델타를 좌표로 누산. **args 는 mm** (계약 §2 유일한 SI 예외).

        적분을 이 노드만 하는 이유 — 터미널 키보드와 향후 UI는 델타만 던지는 입력
        장치다. 어느 입력 쪽이 좌표를 들고 있으면 다른 쪽에서 움직인 만큼이 사라짐.
        """
        if not args:
            self.get_logger().warn('jog 에 args(dx, dy, dz)가 없음')
            return
        if self.mode == 'playing':
            # 거부하지 않음. 재생을 세우고 손으로 넘겨받음. resume 으로 되돌아감.
            self.relay(self._command('pause'))
            self.paused = True
            self.set_mode('manual', 'jog')

        delta = [float(args[i]) * MM_TO_M if i < len(args) else 0.0 for i in range(3)]
        self.set_position([self.position[i] + delta[i] for i in range(3)])

    def home(self, payload=''):
        """원점 복귀. payload 로 축 지정 가능 ("XYZ", "Z" 등). 빈 값이면 전축.

        playing 중이면 먼저 stop 을 되넘김 — 값 소스가 계속 target 을 쏘는 채로
        좌표만 0 으로 밀면 다음 틱에 도로 끌려감 (04a §5b 표).
        """
        self.set_extrusion((0.0,), reason='home')

        if self.mode == 'playing':
            self.relay(self._command('stop'))
            self.filename = ''
            self.set_mode('manual', 'home')

        axes = payload.strip().lower()
        target = list(self.position)
        moved = []
        for axis, key in enumerate('xyz'):
            if not axes or key in axes:
                target[axis] = 0.0
                moved.append(JOINT_NAMES[axis])
        if not moved:
            self.get_logger().warn(f'home payload 에 축이 없음: {payload!r} — 무시함')
            return
        self.set_position(target)
        self.get_logger().info(f'home {"+".join(moved)}')

    def set_extrusion(self, args, reason='keyboard'):
        """수동 압출 상태 설정. args[0] >= 0.5 이면 on, 아니면 off."""
        if not args:
            self.get_logger().warn('set_extrusion 에 args[0] (0 또는 1)이 없음')
            return

        enabled = float(args[0]) >= 0.5
        if enabled and self.mode == 'playing':
            self.relay(self._command('pause'))
            self.paused = True
            self.set_mode('manual', 'manual extrusion')

        changed = enabled != self.extruding
        self.extruding = enabled
        self.publish_extrusion(enabled)
        if changed:
            self.get_logger().info(
                f'manual extrusion {"ON" if enabled else "OFF"} ({reason})')

    def publish_extrusion(self, enabled):
        """현재 노즐 목표 위치를 bed_origin 기준 압출 점으로 발행."""
        if self.extrusion_pub is None:
            return

        msg = ExtrusionPoint()
        msg.header.stamp = self.get_clock().now().to_msg()
        msg.header.frame_id = 'bed_origin'
        msg.position.x = float(self.position[0])
        msg.position.y = float(self.position[1])
        msg.position.z = float(self.position[2])
        msg.width = self.manual_extrusion_width
        msg.height = self.manual_layer_height
        msg.extruding = bool(enabled)
        msg.layer = 0
        self.extrusion_pub.publish(msg)

    def set_position(self, values):
        """클램프 후 반영. 좌표가 바뀌는 유일한 통로 — jog·home·target 이 전부 여기로."""
        clamped, hit = clamp_axes(values, self.stroke)
        if hit:
            self.clamp_count += 1
            if not self._clamp_warned:
                self._clamp_warned = True
                sx, sy, sz = self.stroke
                self.get_logger().warn(
                    f'스트로크 리밋 클램프: {"+".join(hit)} '
                    f'(요청 {tuple(round(float(v), 4) for v in values)} -> '
                    f'{tuple(round(v, 4) for v in clamped)}, 상한 ({sx},{sy},{sz})m). '
                    '이후 클램프는 종료 시 횟수로만 남김')
        self.position = clamped

    def _command(self, name):
        """릴레이용 PrinterCommand 한 개. 상태 노드가 스스로 만드는 것은 pause/stop 뿐."""
        msg = PrinterCommand()
        msg.command = name
        return msg

    # ------------------------------------------------------------------
    def on_target(self, msg):
        """/printer/target 수신. playing 일 때만 통과시킴.

        초기 모드가 manual 이라 값 소스를 먼저 띄워도 그림이 안 움직이는 것이 정상이며,
        패턴을 보려면 resume 을 한 번 밀어야 함 (sim.launch.py 가 그렇게 함).
        """
        if self.mode != 'playing':
            if not self._stray_target_warned:
                self._stray_target_warned = True
                self.get_logger().info(
                    f'{TARGET_TOPIC} 수신했으나 모드가 manual 이라 무시함 — '
                    "재생하려면 /printer/cmd 로 resume 또는 load_gcode")
            return

        values = list(self.position)
        if msg.name:
            index = {name: i for i, name in enumerate(msg.name)}
            for axis, name in enumerate(JOINT_NAMES):
                i = index.get(name)
                if i is not None and i < len(msg.position):
                    values[axis] = float(msg.position[i])
        else:
            # 이름이 비어 있으면 계약 §3 의 순서로 간주. 값 소스가 name 을 채우는 것이
            # 정상이지만 `ros2 topic pub` 손검증에서 빠뜨리기 쉬움.
            for axis in range(min(3, len(msg.position))):
                values[axis] = float(msg.position[axis])
        self.set_position(values)

    def tick(self):
        """50Hz. 값이 안 들어와도 마지막 좌표를 계속 냄 — 이 토픽의 단독 소유자이므로
        여기가 비면 RViz TF 가 늙고 Unity 가 연결 끊긴 것으로 오인함."""
        msg = JointState()
        msg.header.stamp = self.get_clock().now().to_msg()
        msg.name = list(JOINT_NAMES)
        msg.position = list(self.position)
        px, py, pz = self._prev
        x, y, z = self.position
        msg.velocity = [(x - px) / self.dt, (y - py) / self.dt, (z - pz) / self.dt]
        self.js_pub.publish(msg)
        self._prev = (x, y, z)
        if self.extruding:
            # 조인트가 물리적으로 목표에 수렴하는 동안에도 Unity가 실제 노즐 중심을
            # 계속 샘플링할 수 있도록 수동 압출 중에는 joint_states와 같은 50Hz로 발행.
            self.publish_extrusion(True)

    def check_sole_ownership(self):
        """`/joint_states` 발행자가 나 하나인지 2 초마다 확인.

        이 토픽의 단독 소유는 이 노드의 전제 (04a §5b). 발행자가 둘이면 두 흐름이
        50Hz 씩 번갈아 도착하고, 받는 쪽은 그때그때 온 값을 그대로 쓰므로 포즈가 두
        좌표 사이를 오감. 한쪽이 정지해 있으면 **"영점으로 돌아갔다 다시 움직임"**
        으로 보임.

        압도적으로 흔한 원인은 앞 실행의 노드가 살아남은 것. 터미널을 닫거나
        Ctrl+C 가 launch 에 안 먹으면 노드만 고아(PPID=1)로 남아 몇 시간이고 계속
        쏨. 궤적 데이터는 멀쩡한데 화면만 깨지므로 데이터 쪽을 봐서는 안 나옴.

            ps -ef | grep -E 'printer_state_node|gcode_player|mock_publisher'
            pkill -f voron24_                # 확인 후 정리

        `count_publishers` 에는 자기 자신이 포함되므로 1 초과가 곧 "남이 있음".
        """
        others = self.count_publishers(STATE_TOPIC) - 1
        if others > 0:
            if not self._dup_warned:
                self._dup_warned = True
                self.get_logger().error(
                    f'{STATE_TOPIC} 에 다른 발행자가 {others} 개 더 있음 — 이 토픽은 '
                    '단독 소유가 전제. 포즈가 두 좌표 사이를 오가면 그 탓이며 '
                    '대개 앞 실행의 고아 노드임: '
                    "ps -ef | grep voron24_ 로 확인 후 pkill -f voron24_")
        elif self._dup_warned:
            self._dup_warned = False
            self.get_logger().info(f'{STATE_TOPIC} 발행자가 다시 하나가 됨')

    # ------------------------------------------------------------------
    def publish_status(self):
        """5Hz. 진행률·파일명을 아는 쪽은 값 소스이므로 남이 잡고 있으면 물러남.

        source:=gcode 에서는 gcode_player 도 같은 토픽에 5Hz 로 쏨. 발행자가 둘이면
        Unity 가 두 흐름 번갈아 받아 progress 가 0 과 실제 값 사이로 튐.
        count_publishers 에는 자기 자신 포함이므로 1 초과가 곧 "남이 있음".
        """
        if self.status_pub is None:
            return
        if self.count_publishers(STATUS_TOPIC) > 1:
            if not self._yielded_status:
                self._yielded_status = True
                self.get_logger().info(
                    f'{STATUS_TOPIC} 에 다른 발행자가 있어 상태 발행을 넘김 (값 소스 담당)')
            return
        self._yielded_status = False

        status = PrinterStatus()
        status.header.stamp = self.get_clock().now().to_msg()
        if self.mode == 'manual':
            status.state = 'printing' if self.extruding else 'idle'
        else:
            status.state = 'paused' if self.paused else 'printing'
        status.progress = 0.0           # 진행률은 보간기를 쥔 값 소스만 알 수 있음
        status.current_layer = 0
        status.total_layers = 0
        status.filename = self.filename
        self.status_pub.publish(status)


def main(args=None):
    rclpy.init(args=args)
    node = PrinterState()
    try:
        rclpy.spin(node)
    except KeyboardInterrupt:
        pass
    finally:
        if node.extruding and rclpy.ok():
            node.set_extrusion((0.0,), reason='shutdown')
        if node.clamp_count:
            node.get_logger().info(f'종료 | 리밋 클램프 {node.clamp_count} 회')
        node.destroy_node()
        if rclpy.ok():
            rclpy.shutdown()


if __name__ == '__main__':
    main()
