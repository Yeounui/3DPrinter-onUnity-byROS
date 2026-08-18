#!/usr/bin/env python3
"""
manual_publisher_node.py
========================
키보드 조작을 맡는 값 소스 (04a §4.5, §5b′).

`mock_publisher_node.py` 를 뼈대로 삼되 출력이 다르다. mock 은 `/joint_states` 에 직접
쏘는 W1 회귀 기준선이고, 이 노드는 상태 노드를 앞에 둔 정식 토폴로지에 붙는다.

출력은 둘 (04a §4.5 "출력은 둘"):

    /printer/cmd     voron24_msgs/PrinterCommand   키보드 입력. jog / home / set_speed
    /printer/target  sensor_msgs/JointState        pattern != none 일 때만, rate Hz

키 입력이 `/printer/target` 이 아니라 `/printer/cmd` 로 나가는 것이 이 노드의 요점이다.
좌표 적분과 리밋 클램프는 `printer_state_node` 단독 책임이고 여기서 누산하면 같은 로직이
두 곳에 생긴다. 이 노드는 델타만 mm 로 던지는 입력 장치이며 Unity 의
`KeyboardJointController` 와 동등한 peer 다.

    터미널 키보드 ──▶ manual_publisher ──┐
                                         ├──▶ /printer/cmd ──▶ printer_state_node
    Unity 키보드  ──▶ KeyboardJointController ┘

패턴:
    none       기본값. 자동 궤적 없음 = 키보드 조작 전용.
    home | sweep | square | lissajous
               `patterns.py` 의 수식 그대로. 이때 키보드는 무시한다.

궤적 수식은 `patterns.py` 를 import 해서 공유한다. 복사하면 `patterns.py` ↔
`LocalMockDriver.cs` 이중 구현 계약에 세 번째 사본이 생긴다 (CLAUDE.md).

★ stdin — `ros2 launch` 로 띄운 프로세스에는 stdin 이 연결되지 않아 키가 안 먹는다
   (`output='screen'` 이나 `emulate_tty=True` 로도 해결되지 않음). 키보드 모드는 별도
   터미널에서 `ros2 run` 할 것.

사용:
    ros2 run voron24_gcode manual_publisher                          # 키보드 조작
    ros2 run voron24_gcode manual_publisher --ros-args -p step_mm:=5.0
    ros2 run voron24_gcode manual_publisher --ros-args -p pattern:=sweep

키맵 (04a §5b′ 표가 Unity 와의 단일 출처):
    a / d   X − / +        h   원점 복귀
    s / w   Y − / +        [ / ]  배속 − / +
    q / e   Z − / +        \\   배속 1.0 복귀
    Ctrl-C  종료
"""
import atexit
import os
import select
import signal
import sys

import rclpy
from rclpy.node import Node
from sensor_msgs.msg import JointState

from voron24_gcode.patterns import PatternGenerator, PATTERNS

# voron24_msgs 가 아직 빌드되지 않아도 pattern 재생만으로 동작 가능케 함
# (mock_publisher_node.py 와 같은 방어. 키보드는 이 메시지가 있어야 나간다).
try:
    from voron24_msgs.msg import PrinterCommand
    HAS_MSGS = True
except ImportError:  # pragma: no cover
    HAS_MSGS = False

# termios/tty 는 POSIX 전용. 없는 플랫폼에서도 pattern 재생은 살아 있어야 한다.
try:
    import termios
    import tty
    HAS_TERMIOS = True
except ImportError:  # pragma: no cover
    HAS_TERMIOS = False

JOINT_NAMES = ['joint_x', 'joint_y', 'joint_z']   # 계약 §3. 변경 금지.

# 배속 단계. **이 상수는 A 소유** (04a §5b′) — 키보드 편의 상수일 뿐이고 값 소스 노드는
# 이 리스트를 모른 채 임의의 양수를 받는다.
SPEED_STEPS = (0.5, 1.0, 2.0, 5.0, 10.0, 25.0, 50.0, 100.0)
SPEED_DEFAULT = 1.0

# 키맵 — 04a §5b′ 표가 단일 출처. Unity 의 KeyboardJointController.cs 와 의미를 맞춘다.
# 방향키를 1차 키맵으로 쓰지 않는 이유는 3 바이트 ESC 시퀀스라 상태 기계가 필요해서다.
AXIS_KEYS = {
    'a': (-1.0, 0.0, 0.0), 'd': (+1.0, 0.0, 0.0),     # X − / +
    's': (0.0, -1.0, 0.0), 'w': (0.0, +1.0, 0.0),     # Y − / +
    'q': (0.0, 0.0, -1.0), 'e': (0.0, 0.0, +1.0),     # Z − / +
}
HOME_KEY = 'h'
SPEED_KEYS = {'[': -1, ']': +1, '\\': 0}              # 0 = 1.0 복귀
QUIT_KEY = '\x03'                                     # Ctrl-C. cbreak 는 ISIG 를 살려 두므로
                                                      # 보통은 여기까지 안 오지만 대비해 둔다

# 화살표는 선택 지원 (04a §5b′). Unity 키맵과 같은 의미를 문자 키로 접어 넣는다.
ESC_SEQUENCES = {
    '\x1b[D': 'a', '\x1b[C': 'd',       # ← / →
    '\x1b[B': 's', '\x1b[A': 'w',       # ↓ / ↑
    '\x1b[6~': 'q', '\x1b[5~': 'e',     # PgDn / PgUp
    '\x1b[H': 'h', '\x1b[1~': 'h',      # Home (터미널마다 둘 중 하나)
}


# ----------------------------------------------------------------------
def parse_keys(buf):
    """읽어 들인 바이트열 -> (키 목록, 남길 꼬리).

    rclpy 없이 부를 수 있는 순수 함수다. ESC 시퀀스가 읽기 경계에서 잘리면 그 조각을
    꼬리로 돌려주고 다음 읽기와 이어붙인다 — 안 그러면 홀드 중인 화살표가 간헐적으로
    엉뚱한 문자로 풀린다.
    """
    keys = []
    i = 0
    while i < len(buf):
        ch = buf[i]
        if ch != '\x1b':
            keys.append(ch.lower() if ch.isalpha() else ch)   # CapsLock 대비
            i += 1
            continue

        seq = next((s for s in ESC_SEQUENCES if buf.startswith(s, i)), None)
        if seq is not None:
            keys.append(ESC_SEQUENCES[seq])
            i += len(seq)
            continue

        tail = buf[i:]
        if any(s.startswith(tail) for s in ESC_SEQUENCES):
            return keys, tail       # 아직 덜 온 시퀀스. 보류한다
        i += 1                      # 모르는 시퀀스 — ESC 한 바이트만 버리고 뒤는 살린다
    return keys, ''


class ManualPublisher(Node):

    def __init__(self):
        super().__init__('manual_publisher')

        self.declare_parameter('pattern', 'none')   # mock 과 달리 기본값이 none
        self.declare_parameter('rate', 50.0)
        self.declare_parameter('period', 12.0)      # 한 사이클 [s]
        self.declare_parameter('stroke_x', 0.250)
        self.declare_parameter('stroke_y', 0.250)
        self.declare_parameter('stroke_z', 0.250)
        self.declare_parameter('margin', 0.010)     # 리밋에서 띄울 여유 [m]
        self.declare_parameter('step_mm', 1.0)      # 키 한 번당 이동량 [mm] (04a §5b′)
        self.declare_parameter('repeat_hz', 20.0)   # 축 키 홀드 시 반복률
        self.declare_parameter('keyboard', True)    # stdin 이 터미널이어도 강제로 끄고 싶을 때
        self.declare_parameter('playback_topic', '/printer/playback')

        self.pattern = self.get_parameter('pattern').value
        self.rate = float(self.get_parameter('rate').value)
        self.period = float(self.get_parameter('period').value)
        self.sx = float(self.get_parameter('stroke_x').value)
        self.sy = float(self.get_parameter('stroke_y').value)
        self.sz = float(self.get_parameter('stroke_z').value)
        self.margin = float(self.get_parameter('margin').value)
        self.step_mm = float(self.get_parameter('step_mm').value)
        self.repeat_hz = float(self.get_parameter('repeat_hz').value)

        if self.pattern not in PATTERNS:
            raise ValueError(f'pattern must be one of {PATTERNS}, got {self.pattern!r}')

        self.gen = PatternGenerator(stroke=(self.sx, self.sy, self.sz),
                                    margin=self.margin, period=self.period)
        self.dt = 1.0 / self.rate
        self.t = 0.0
        self.paused = False
        self._prev_xyz = (0.0, 0.0, 0.0)

        self.speed_index = SPEED_STEPS.index(SPEED_DEFAULT)

        # 터미널 상태. _saved 가 None 이 아니면 raw 모드에 들어가 있다는 뜻이다.
        self._fd = None
        self._saved = None
        self._prev_sigterm = None
        self._buf = ''
        self._prev_keys = set()

        # ------------------------------------------------------------------
        self.target_pub = self.create_publisher(JointState, '/printer/target', 10)
        self.cmd_pub = None
        if HAS_MSGS:
            self.cmd_pub = self.create_publisher(PrinterCommand, '/printer/cmd', 10)
            # 활성 값 소스라 상태 노드가 스트림 명령을 되넘긴다. set_speed 는 여기서
            # 의미가 없어 무시하고(04a §5b′), pause/resume/stop 만 패턴 시간축에 건다.
            self.create_subscription(PrinterCommand,
                                     self.get_parameter('playback_topic').value,
                                     self.on_playback, 10)
        else:
            self.get_logger().warn(
                'Cannot find voron24_msgs. Keyboard commands cannot be published. '
                '(source ros2_ws/install/setup.bash after colcon build --packages-select voron24_msgs)')

        if self.pattern != 'none':
            self.create_timer(self.dt, self.tick)

        self.get_logger().info(
            f'manual publisher | pattern={self.pattern} rate={self.rate}Hz '
            f'period={self.period}s step={self.step_mm}mm repeat={self.repeat_hz}Hz')

        self.start_keyboard()

    # ------------------------------------------------------------------
    # 자동 패턴 — /printer/target
    # ------------------------------------------------------------------
    def tick(self):
        """pattern != none 일 때만 돈다. 좌표는 patterns.py 가 만든다."""
        if not self.paused:
            self.t += self.dt
        x, y, z, _ = self.gen(self.pattern, self.t)

        msg = JointState()
        msg.header.stamp = self.get_clock().now().to_msg()
        msg.name = list(JOINT_NAMES)
        msg.position = [x, y, z]
        px, py, pz = self._prev_xyz
        msg.velocity = [(x - px) / self.dt, (y - py) / self.dt, (z - pz) / self.dt]
        self.target_pub.publish(msg)
        self._prev_xyz = (x, y, z)

    def on_playback(self, msg):
        """상태 노드가 되넘긴 스트림 명령. 이 노드가 아는 것은 시간축뿐이다."""
        command = msg.command.strip().lower()
        if command == 'pause':
            self.paused = True
        elif command == 'resume':
            self.paused = False
        elif command == 'stop':
            self.paused = False
            self.t = 0.0
        elif command == 'set_speed':
            # source:=manual 에서는 배속에 의미가 없다 (04a §5b′). 조용히 버리지 않고 남긴다.
            self.get_logger().debug('set_speed 는 manual 소스에서 의미 없음 — 무시함')
        else:
            self.get_logger().debug(f'스트림 명령이 아님 — 무시함: {msg.command!r}')

    # ------------------------------------------------------------------
    # 키보드 — /printer/cmd
    # ------------------------------------------------------------------
    def start_keyboard(self):
        """키보드 모드 진입. 조건이 안 맞으면 이유를 로그에 남기고 물러난다.

        여기서 조용히 실패하면 사용자가 "키를 눌러도 아무 일이 없다" 만 보게 된다.
        특히 launch 경로는 stdin 이 아예 없으므로 대처 명령까지 찍어 준다.
        """
        if self.pattern != 'none':
            self.get_logger().info(
                f'pattern={self.pattern} — 자동 궤적 재생 중이라 키보드 입력은 무시함. '
                '키보드로 조작하려면 pattern:=none (04a §4.5)')
            return
        if not self.get_parameter('keyboard').value:
            self.get_logger().info('keyboard:=false — 키보드 입력 비활성')
            return
        if not HAS_TERMIOS:
            self.get_logger().warn('termios 없음 (POSIX 전용) — 키보드 입력 비활성')
            return
        if not sys.stdin.isatty():
            self.get_logger().warn(
                'stdin 이 터미널이 아님 — 키보드 조작 불가. `ros2 launch` 로 띄운 '
                '프로세스에는 stdin 이 연결되지 않으며 output=screen / emulate_tty 로도 '
                '해결되지 않음 (04a §5b′). 별도 터미널에서 '
                '`ros2 run voron24_gcode manual_publisher` 로 띄울 것')
            return

        # SIGTERM 을 먼저 잡는다 — **cbreak 진입 전이어야 한다.** 순서를 뒤집으면 그
        # 사이에 들어온 SIGTERM 이 에코 꺼진 터미널을 그대로 남긴다. 파이썬 기본
        # SIGTERM 처리는 프로세스를 즉시 끝내며 atexit 도 finally 도 타지 않는다.
        self._prev_sigterm = signal.signal(signal.SIGTERM, self.on_sigterm)

        self._fd = sys.stdin.fileno()
        self._saved = termios.tcgetattr(self._fd)
        tty.setcbreak(self._fd)     # ISIG 는 살아 있어 Ctrl-C 가 그대로 먹는다
        # main 의 try/finally 가 정상 경로를 덮지만, 그 밖으로 새는 종료 경로에서도
        # 에코가 꺼진 터미널이 남지 않도록 한 겹 더 건다. 복원은 idempotent 다.
        atexit.register(self.restore_terminal)

        self.create_timer(1.0 / self.repeat_hz, self.tick_keyboard)
        self.get_logger().info(
            f'keyboard | X a/d  Y s/w  Z q/e  home h  speed [ ] \\  quit Ctrl-C  '
            f'| step={self.step_mm}mm speed={SPEED_STEPS[self.speed_index]}x')

    def on_sigterm(self, signum, frame):
        """SIGTERM 은 터미널이 만드는 시그널이 아니라 cbreak 의 ISIG(Ctrl-C) 방어가 닿지
        않는다. `kill -TERM` 한 방에 에코 꺼진 터미널이 남는 것을 막는 유일한 경로다.

        여기서는 복원만 하고 나간다. 종료 절차는 원래 핸들러와 main 의 finally 몫이다.
        """
        self.restore_terminal()
        if callable(self._prev_sigterm):    # rclpy 가 걸어 둔 것이 있으면 넘겨준다
            self._prev_sigterm(signum, frame)
        raise KeyboardInterrupt             # main 의 정상 종료 경로로 합류

    def restore_terminal(self):
        """터미널 원상복구. 몇 번 불려도 안전해야 한다 —
        finally / atexit / SIGTERM 핸들러 셋이 같은 곳으로 들어온다."""
        if self._saved is None:
            return
        saved, self._saved = self._saved, None
        try:
            termios.tcsetattr(self._fd, termios.TCSADRAIN, saved)
        except Exception:   # pragma: no cover - 복원 실패를 종료 경로에서 다시 던지지 않는다
            pass

    def read_stdin(self):
        """지금 버퍼에 와 있는 것만 읽는다. 논블로킹 — 타이머 콜백을 막으면 안 된다."""
        chunks = []
        while select.select([self._fd], [], [], 0.0)[0]:
            data = os.read(self._fd, 64)
            if not data:            # EOF. 파이프가 닫힌 경우
                break
            chunks.append(data.decode('utf-8', 'ignore'))
        return ''.join(chunks)

    def tick_keyboard(self):
        keys, self._buf = parse_keys(self._buf + self.read_stdin())
        pressed = set(keys)

        if QUIT_KEY in pressed:
            raise KeyboardInterrupt

        # 축 키 — 한 틱에 한 스텝. 홀드하면 이 타이머 주기(repeat_hz)로 반복된다.
        # 같은 축 키가 한 틱에 여러 번 들어와도 한 스텝인 이유는, 터미널 자동 반복률이
        # 환경마다 달라 그대로 흘리면 이동 속도가 장비마다 달라지기 때문이다.
        dx = dy = dz = 0.0
        for key, (ux, uy, uz) in AXIS_KEYS.items():
            if key in pressed:
                dx += ux
                dy += uy
                dz += uz
        if dx or dy or dz:
            self.send_jog(dx * self.step_mm, dy * self.step_mm, dz * self.step_mm)

        # 단발 키 — 눌린 순간에만. 홀드 반복을 걸지 않는다 (04a §5b′).
        fresh = pressed - self._prev_keys
        if HOME_KEY in fresh:
            self.send_home()
        for key in SPEED_KEYS:
            if key in fresh:
                self.send_speed(SPEED_KEYS[key])
        self._prev_keys = pressed

    # ------------------------------------------------------------------
    def publish_command(self, command, args=(), payload=''):
        if self.cmd_pub is None:
            return
        msg = PrinterCommand()
        msg.command = command
        msg.args = [float(a) for a in args]
        msg.payload = payload
        self.cmd_pub.publish(msg)

    def send_jog(self, dx_mm, dy_mm, dz_mm):
        """jog 의 단위는 **mm** — 계약 §2 의 유일한 SI 예외.

        델타만 보낸다. 누산도 클램프도 printer_state_node 몫이다.
        """
        self.publish_command('jog', (dx_mm, dy_mm, dz_mm))

    def send_home(self):
        self.publish_command('home')            # payload 빈 값 = 전축
        self.get_logger().info('home')

    def send_speed(self, direction):
        """배속 한 칸. 양 끝에서는 더 가지 않고 현재 값을 그대로 다시 보낸다 —
        눌러도 아무 일이 없는 것보다 현재 배속이 로그에 다시 찍히는 편이 낫다 (04a §5b′)."""
        if direction == 0:
            self.speed_index = SPEED_STEPS.index(SPEED_DEFAULT)
        else:
            self.speed_index = min(max(self.speed_index + direction, 0), len(SPEED_STEPS) - 1)
        scale = SPEED_STEPS[self.speed_index]
        self.publish_command('set_speed', (scale,))
        self.get_logger().info(f'set_speed {scale}x')


def main(args=None):
    rclpy.init(args=args)
    node = ManualPublisher()
    try:
        rclpy.spin(node)
    except KeyboardInterrupt:
        pass
    finally:
        # 복원이 먼저다. 이걸 빼먹으면 노드가 죽은 뒤 에코가 꺼진 터미널이 남아
        # 사용자가 `reset` 을 쳐야 한다 (04a §5b′).
        node.restore_terminal()
        node.destroy_node()
        if rclpy.ok():
            rclpy.shutdown()


if __name__ == '__main__':
    main()
