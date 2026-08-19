#!/usr/bin/env python3
"""
manual_publisher_node.py
========================
키보드 조작 값 소스 (04a §4.5, §5b′).

mock_publisher_node.py 를 뼈대로 삼되 출력이 다름. mock 은 /joint_states 에 직접
쏘는 W1 회귀 기준선이고, 이 노드는 상태 노드를 앞에 둔 정식 토폴로지에 붙음.

출력 둘 (04a §4.5):

    /printer/cmd     voron24_msgs/PrinterCommand   jog / home / set_extrusion / set_speed
    /printer/target  sensor_msgs/JointState        pattern != none 일 때만, rate Hz

키 입력이 /printer/target 이 아니라 /printer/cmd 로 나가는 것이 이 노드의 요점.
좌표 적분과 리밋 클램프는 printer_state_node 단독 책임이고 여기서 누산하면 같은 로직이
두 곳에 생김. 이 노드는 델타만 mm 로 던지는 유일한 키보드 입력 장치다.

    터미널 키보드 ──▶ manual_publisher ──▶ /printer/cmd ──▶ printer_state_node

패턴:
    none       기본값. 자동 궤적 없음 = 키보드 조작 전용.
    home | sweep | square | lissajous
               patterns.py 수식 그대로. 이때 키보드 무시.

궤적 수식은 `patterns.py` import 로 공유. 복사 시 `patterns.py` ↔
`LocalMockDriver.cs` 이중 구현 계약에 세 번째 사본 발생 (CLAUDE.md).

★ stdin — `ros2 launch` 로 띄운 프로세스에는 stdin 미연결로 키 입력 불가
   (`output='screen'` / `emulate_tty=True` 로도 미해결). 키보드 모드는 별도
   터미널에서 `ros2 run` 필요.

사용:
    ros2 run voron24_gcode manual_publisher                          # 키보드 조작
    ros2 run voron24_gcode manual_publisher --ros-args -p step_mm:=5.0
    ros2 run voron24_gcode manual_publisher --ros-args -p pattern:=sweep

키맵:
    a / d   X − / +        h / Home   원점 복귀
    s / w   Y − / +        Space      압출 on/off
    q / e   Z − / +        [ / ]      배속 − / +
                            \\         배속 1.0 복귀
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

# voron24_msgs 미빌드 시에도 pattern 재생만으로 동작 가능
# (mock_publisher_node.py 와 같은 방어. 키보드는 이 메시지 필요).
try:
    from voron24_msgs.msg import PrinterCommand
    HAS_MSGS = True
except ImportError:  # pragma: no cover
    HAS_MSGS = False

# termios/tty POSIX 전용. 미지원 플랫폼에서도 pattern 재생 유지 필요.
try:
    import termios
    import tty
    HAS_TERMIOS = True
except ImportError:  # pragma: no cover
    HAS_TERMIOS = False

JOINT_NAMES = ['joint_x', 'joint_y', 'joint_z']   # 계약 §3. 변경 금지.

# 배속 단계. **A 소유 상수** (04a §5b′) — 키보드 편의 상수. 값 소스 노드는
# 이 리스트 미참조, 임의 양수 수신.
SPEED_STEPS = (0.5, 1.0, 2.0, 5.0, 10.0, 25.0, 50.0, 100.0)
SPEED_DEFAULT = 1.0

# 키맵 — 터미널 입력의 단일 출처. 방향키와 PageUp/PageDown은 명시적으로 무시한다.
AXIS_KEYS = {
    'a': (-1.0, 0.0, 0.0), 'd': (+1.0, 0.0, 0.0),     # X − / +
    's': (0.0, -1.0, 0.0), 'w': (0.0, +1.0, 0.0),     # Y − / +
    'q': (0.0, 0.0, -1.0), 'e': (0.0, 0.0, +1.0),     # Z − / +
}
HOME_KEY = 'h'
EXTRUSION_KEY = ' '
SPEED_KEYS = {'[': -1, ']': +1, '\\': 0}              # 0 = 1.0 복귀
QUIT_KEY = '\x03'                                     # Ctrl-C. ISIG를 끄고 문자로 직접 받음

# ANSI escape 시퀀스를 한 덩어리로 소비한다. Home만 유지하고 화살표와
# PageUp/PageDown은 None으로 버려, 시퀀스의 마지막 A/D 등이 축 입력으로 오인되지 않게 한다.
ESC_SEQUENCES = {
    '\x1b[D': None, '\x1b[C': None,      # ← / →
    '\x1b[B': None, '\x1b[A': None,      # ↓ / ↑
    '\x1bOD': None, '\x1bOC': None,       # application cursor ← / →
    '\x1bOB': None, '\x1bOA': None,       # application cursor ↓ / ↑
    '\x1b[6~': None, '\x1b[5~': None,    # PgDn / PgUp
    '\x1b[H': 'h', '\x1b[1~': 'h',       # Home (터미널별 변형)
    '\x1bOH': 'h', '\x1b[7~': 'h',
}


# ----------------------------------------------------------------------
def parse_keys(buf):
    """읽어 들인 바이트열 → (키 목록, 남은 꼬리).

    rclpy 미의존 순수 함수. ESC 시퀀스가 읽기 경계에서 잘리면 그 조각을
    꼬리로 반환하여 다음 읽기와 결합 — 미처리 시 홀드 중 화살표가 간헐적으로
    엉뚱한 문자로 해석됨.
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
            mapped = ESC_SEQUENCES[seq]
            if mapped is not None:
                keys.append(mapped)
            i += len(seq)
            continue

        tail = buf[i:]
        if any(s.startswith(tail) for s in ESC_SEQUENCES):
            return keys, tail       # 미완료 시퀀스. 보류
        # 미등록 ANSI CSI/SS3 시퀀스도 통째로 버린다. ESC만 버리면 마지막 A/D가
        # WASD 축 입력으로 오인될 수 있다.
        if tail.startswith('\x1b['):
            end = next((j for j in range(2, len(tail)) if '@' <= tail[j] <= '~'), None)
            if end is None:
                return keys, tail
            i += end + 1
            continue
        if tail.startswith('\x1bO'):
            if len(tail) < 3:
                return keys, tail
            i += 3
            continue
        i += 1
    return keys, ''


class ManualPublisher(Node):

    def __init__(self):
        super().__init__('manual_publisher')

        self.declare_parameter('pattern', 'none')   # mock 과 달리 기본값 none
        self.declare_parameter('rate', 50.0)
        self.declare_parameter('period', 12.0)      # 한 사이클 [s]
        self.declare_parameter('stroke_x', 0.250)
        self.declare_parameter('stroke_y', 0.250)
        self.declare_parameter('stroke_z', 0.250)
        self.declare_parameter('margin', 0.010)     # 리밋 여유 [m]
        self.declare_parameter('step_mm', 1.0)      # 키 1회 이동량 [mm] (04a §5b′)
        self.declare_parameter('repeat_hz', 20.0)   # 축 키 홀드 시 반복률
        self.declare_parameter('keyboard', True)    # stdin 터미널이어도 강제 비활성 시
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
        self.extruding = False

        # 터미널 상태. _saved != None 이면 raw 모드 진입 상태.
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
            # 활성 값 소스라 상태 노드가 스트림 명령 전달. set_speed 는 여기서
            # 무의미하여 무시 (04a §5b′), pause/resume/stop 만 패턴 시간축에 적용.
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
        """pattern != none 일 때만 실행. 좌표는 patterns.py 생성."""
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
        """상태 노드가 전달한 스트림 명령. 이 노드는 시간축만 관여."""
        command = msg.command.strip().lower()
        if command == 'pause':
            self.paused = True
        elif command == 'resume':
            self.paused = False
        elif command == 'stop':
            self.paused = False
            self.t = 0.0
        elif command == 'set_speed':
            # source:=manual 에서 배속 무의미 (04a §5b′). 로그 남기고 무시.
            self.get_logger().debug('set_speed manual 소스에서 무의미 — 무시')
        else:
            self.get_logger().debug(f'스트림 명령 아님 — 무시: {msg.command!r}')

    # ------------------------------------------------------------------
    # 키보드 — /printer/cmd
    # ------------------------------------------------------------------
    def start_keyboard(self):
        """키보드 모드 진입. 조건 불충족 시 사유 로그 후 비활성.

        조용히 실패 시 사용자에게 "키 입력 무반응" 으로만 보임.
        launch 경로는 stdin 미연결이므로 대처 명령까지 표시.
        """
        if self.pattern != 'none':
            self.get_logger().info(
                f'pattern={self.pattern} — 자동 궤적 재생 중, 키보드 입력 무시. '
                '키보드 조작 시 pattern:=none (04a §4.5)')
            return
        if not self.get_parameter('keyboard').value:
            self.get_logger().info('keyboard:=false — 키보드 비활성')
            return
        if not HAS_TERMIOS:
            self.get_logger().warn('termios 없음 (POSIX 전용) — 키보드 비활성')
            return
        if not sys.stdin.isatty():
            self.get_logger().warn(
                'stdin 이 터미널이 아님 — 키보드 조작 불가. `ros2 launch` 로 띄운 '
                '프로세스에는 stdin 이 연결되지 않으며 output=screen / emulate_tty 로도 '
                '해결되지 않음 (04a §5b′). 별도 터미널에서 '
                '`ros2 run voron24_gcode manual_publisher` 로 띄울 것')
            return

        # SIGTERM 먼저 등록 — **cbreak 진입 전 필수.** 순서 반전 시 그
        # 사이 SIGTERM 이 에코 꺼진 터미널을 방치. 파이썬 기본
        # SIGTERM 처리는 프로세스 즉시 종료, atexit/finally 미실행.
        self._prev_sigterm = signal.signal(signal.SIGTERM, self.on_sigterm)

        self._fd = sys.stdin.fileno()
        self._saved = termios.tcgetattr(self._fd)
        tty.setcbreak(self._fd)
        # Ctrl-C를 rclpy의 SIGINT 핸들러가 먼저 받으면 ROS context가 닫혀
        # set_extrusion(false)를 발행할 수 없다. ISIG만 내려 \x03 문자로 직접 받아
        # tick_keyboard -> main finally 순서로 안전하게 압출을 끈다.
        attrs = termios.tcgetattr(self._fd)
        attrs[3] &= ~termios.ISIG
        termios.tcsetattr(self._fd, termios.TCSANOW, attrs)
        # main try/finally 가 정상 경로 커버. 그 외 종료 경로에서도
        # 에코 꺼진 터미널 방지용 이중 등록. 복원은 idempotent.
        atexit.register(self.restore_terminal)

        self.create_timer(1.0 / self.repeat_hz, self.tick_keyboard)
        self.get_logger().info(
            f'keyboard | X a/d  Y s/w  Z q/e  home h/Home  extrusion Space  '
            f'speed [ ] \\  quit Ctrl-C  '
            f'| step={self.step_mm}mm speed={SPEED_STEPS[self.speed_index]}x')

    def on_sigterm(self, signum, frame):
        """SIGTERM 은 터미널 시그널이 아니라 termios ISIG 설정 범위 밖.
        `kill -TERM` 한 방에 에코 꺼진 터미널 방치를 막는 유일한 경로.

        압출 OFF와 터미널 복원 후 원래 핸들러 + main finally로 종료한다.
        """
        if self.extruding and rclpy.ok():
            self.send_extrusion(False)
        self.restore_terminal()
        if callable(self._prev_sigterm):    # rclpy 기존 핸들러 전달
            self._prev_sigterm(signum, frame)
        raise KeyboardInterrupt             # main 정상 종료 경로 합류

    def restore_terminal(self):
        """터미널 원상복구. 다중 호출 안전 필수 —
        finally / atexit / SIGTERM 핸들러 셋 모두 진입점."""
        if self._saved is None:
            return
        saved, self._saved = self._saved, None
        try:
            termios.tcsetattr(self._fd, termios.TCSADRAIN, saved)
        except Exception:   # pragma: no cover - 복원 실패를 종료 경로에서 재throw 금지
            pass

    def read_stdin(self):
        """버퍼에 있는 것만 읽음. 논블로킹 — 타이머 콜백 차단 금지."""
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

        # Space 상태를 같은 틱의 jog보다 먼저 보내 첫 이동점부터
        # 압출 상태가 적용되게 한다.
        fresh = pressed - self._prev_keys
        if EXTRUSION_KEY in fresh:
            self.send_extrusion(not self.extruding)

        # 축 키 — 1틱 1스텝. 홀드 시 타이머 주기(repeat_hz)로 반복.
        # 동일 축 키 1틱 다중 입력도 1스텝 — 터미널 자동 반복률이
        # 환경마다 달라 그대로 전달 시 장비별 이동 속도 불일치.
        dx = dy = dz = 0.0
        for key, (ux, uy, uz) in AXIS_KEYS.items():
            if key in pressed:
                dx += ux
                dy += uy
                dz += uz
        if dx or dy or dz:
            self.send_jog(dx * self.step_mm, dy * self.step_mm, dz * self.step_mm)

        # 단발 키 — 눌린 순간만. 홀드 반복 없음 (04a §5b′).
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
        """jog 단위 **mm** — 계약 §2 유일한 SI 예외.

        델타만 전송. 누산/클램프는 printer_state_node 담당.
        """
        self.publish_command('jog', (dx_mm, dy_mm, dz_mm))

    def send_home(self):
        self.publish_command('home')            # payload 빈 값 = 전축
        self.extruding = False
        self.get_logger().info('home')

    def send_extrusion(self, enabled):
        self.extruding = bool(enabled)
        self.publish_command('set_extrusion', (1.0 if self.extruding else 0.0,))
        self.get_logger().info(f'extrusion {"ON" if self.extruding else "OFF"}')

    def send_speed(self, direction):
        """배속 1칸 변경. 양 끝에서는 현재 값 재전송 —
        무반응보다 현재 배속 로그 재표시가 나음 (04a §5b′)."""
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
        # 복원 우선. 누락 시 노드 종료 후 에코 꺼진 터미널 잔존,
        # 사용자 `reset` 실행 필요 (04a §5b′).
        node.restore_terminal()
        if node.extruding and rclpy.ok():
            node.send_extrusion(False)
        node.destroy_node()
        if rclpy.ok():
            rclpy.shutdown()


if __name__ == '__main__':
    main()
