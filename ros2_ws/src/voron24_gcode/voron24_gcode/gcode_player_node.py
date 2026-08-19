#!/usr/bin/env python3
"""
gcode_player_node.py
====================
G-code 파일 -> `/printer/target` 50Hz 재생.

    ros2 run voron24_gcode gcode_player
    ros2 topic pub --once /printer/playback voron24_msgs/PrinterCommand \\
      "{command: 'load_gcode', payload: '/abs/path/test.gcode'}"
    ros2 topic echo /printer/target

텍스트 -> 세그먼트는 gcode_parser.py, 세그먼트 -> 시간축 샘플은 motion.py 담당. 이 노드는
그 두 순수 모듈을 ROS 에 붙이는 껍데기로 타이밍·토픽·단위 변환만 맡음.

mm->m 은 publish 직전 한 곳에서만 (계약 §2). 파서와 보간기가 끝까지 mm 를 유지하므로
이 노드의 나눗셈이 유일한 변환 지점 — 중복 변환 주의.

파일 전체를 샘플 배열로 펼치지 않음. 리더 스레드가 한 줄씩 읽어 Move 큐를 채우고 50Hz 타이머가 그 위를 실시간으로 진행.
그래야 재생 도중의 pause/set_speed/stop 이 즉시 먹고, 수십 MB G-code 도 큐 깊이만큼만 메모리에 올라옴.

좌표 클램프는 하지 않음.
스커트·프라임 라인이 베드를 벗어나는 G-code 는 흔하지만 리밋 지식을 두 곳에 두지 않기 위해 무시, 클램프는 상태 노드(A) 담당.

입력은 `/printer/playback` — `/printer/cmd` 를 직접 듣지 않음. 상태 노드가 걸러 넘긴
스트림 계열(load_gcode/pause/resume/stop/set_speed)만 받고 jog·home 은 상태 노드 몫.
A 없이 단독 검증할 때는 `playback_topic` 파라미터로 `/printer/cmd` 를 직접 물릴 것.
"""
import collections
import math
import os
import queue
import threading

import rclpy
from rclpy.node import Node
from sensor_msgs.msg import JointState
from geometry_msgs.msg import Point
from voron24_msgs.msg import PrinterCommand, PrinterStatus, ExtrusionPoint

from voron24_gcode.gcode_parser import GcodeError, GcodeParser
from voron24_gcode.motion import MotionInterpolator

JOINT_NAMES = ['joint_x', 'joint_y', 'joint_z']   # 계약 §3. 순서·이름 변경 금지.
BED_FRAME = 'bed_origin'                          # ExtrusionPoint 의 기준 프레임

MM_TO_M = 0.001                                   # 이 상수를 쓰는 곳이 곧 변환 지점

# ExtrusionPoint.width 역산용. `;WIDTH:` 태그가 없는 G-code 를 위한 경로.
FILAMENT_RADIUS_MM = 1.75 / 2.0
DEFAULT_LAYER_HEIGHT_MM = 0.2

_END = object()                                   # 큐 종료 sentinel


class MoveStream:
    """리더 스레드가 채우는 Move 큐. 보간기에 그대로 물리는 이터레이터.

    파일 I/O 를 타이머 콜백에서 떼어내는 것이 목적. 큐가 비면 `__next__` 가 잠깐
    막히지만 리더가 보간기보다 수백 배 빠르므로 실제로 걸리는 것은 재생 시작 직후뿐.

    소진·정지·에러가 전부 StopIteration 으로 수렴 — 보간기는 셋을 구분할 필요가 없고
    구분이 필요한 노드는 `error` 를 봄.
    """

    QUEUE_DEPTH = 2048          # Move 개수. 0.2mm 세그먼트 기준 400mm 어치 선행
    POLL_S = 0.2                # 큐가 막힌 동안 stop 플래그를 확인하는 주기

    def __init__(self, path, on_warning=None):
        self.path = path
        self.size_bytes = max(os.path.getsize(path), 1)   # progress 분모. 0 나눗셈 방지
        self.offset = 0         # 지금 꺼내 쓴 Move 의 바이트 위치. progress 분자
        self.error = None       # GcodeError / OSError. 정상 소진과 구분하는 유일한 근거
        self.parser = GcodeParser(on_warning=on_warning)
        self._queue = queue.Queue(self.QUEUE_DEPTH)
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._read, name='gcode_reader', daemon=True)

    def start(self):
        self._thread.start()
        return self

    def close(self):
        """스트림 폐기. 리더는 길어야 POLL_S 안에 스스로 빠져나감."""
        self._stop.set()

    # ------------------------------------------------------------------
    def __iter__(self):
        return self

    def __next__(self):
        while True:
            if self._stop.is_set():
                raise StopIteration
            try:
                item = self._queue.get(timeout=self.POLL_S)
            except queue.Empty:
                # 리더가 sentinel 도 못 넣고 죽은 경우까지 여기서 끊음. 아니면 노드가
                # 영원히 빈 큐를 기다림.
                if not self._thread.is_alive():
                    raise StopIteration
                continue
            if item is _END:
                raise StopIteration
            self.offset, move = item
            return move

    # ------------------------------------------------------------------
    def _read(self):
        """리더 스레드 본체. (바이트 오프셋, Move) 쌍을 큐에 넣음."""
        try:
            for item in self._pairs():
                if not self._put(item):
                    return                      # stop — sentinel 도 필요 없음
        except (OSError, GcodeError) as exc:
            self.error = exc
        finally:
            self._put(_END)

    def _pairs(self):
        """파서를 돌리며 각 Move 에 그 줄까지의 바이트 오프셋을 붙임.

        바이너리로 열어 줄 길이를 그대로 더함 — 텍스트 이터레이션 중에는 `tell()` 이
        막히고, 문자 수는 UTF-8 주석이 섞이면 바이트 수와 어긋남.
        """
        offset = 0

        def lines(handle):
            nonlocal offset
            for raw in handle:
                offset += len(raw)
                yield raw.decode('utf-8', 'replace')

        with open(self.path, 'rb') as handle:
            for move in self.parser.parse(lines(handle)):
                yield offset, move

    def _put(self, item):
        """큐에 자리가 날 때까지 대기. stop 이면 False.

        타임아웃을 걸어 되도는 이유 — 소비자가 사라진 뒤 큐가 가득 차면 무한정
        block 되고, 그 스레드는 daemon 이라 프로세스 종료까지 남음.
        """
        while not self._stop.is_set():
            try:
                self._queue.put(item, timeout=self.POLL_S)
                return True
            except queue.Full:
                continue
        return False


class GcodePlayer(Node):

    def __init__(self):
        super().__init__('gcode_player')

        self.declare_parameter('speed_default', 10.0)   # sim.launch.py 의 speed 인자
        self.declare_parameter('rate', 50.0)
        self.declare_parameter('status_rate', 5.0)      # 계약 §5
        self.declare_parameter('playback_topic', '/printer/playback')
        # 틱당 경로 이동량 상한 [mm]. 0 이하면 무제한.
        #
        # 배속은 시간축만 늘이므로 올리는 만큼 한 틱의 이동량이 커짐. 50Hz 로 뽑는
        # 좌표가 40mm 씩 뛰면 그건 궤적이 아니라 순간이동이고, 받는 쪽 ArticulationBody
        # 는 그 목표를 못 따라가 뒤처진 채 헤맴("점 사이를 안 가고 튐"의 정체).
        # 기본값 10mm 는 vel_xy(500mm/s) x dt(0.02s) — 즉 화면 위의 헤드가 기계 자신의
        # 최대속도보다 빨리 움직이지 않게 하는 값.
        self.declare_parameter('max_step_mm', 10.0)

        self.rate = float(self.get_parameter('rate').value)
        self.dt = 1.0 / self.rate
        self.max_step_mm = float(self.get_parameter('max_step_mm').value)
        self.speed_scale = float(self.get_parameter('speed_default').value)
        if self.speed_scale <= 0.0:
            self.get_logger().warn(
                f'speed_default 는 양수여야 함: {self.speed_scale} — 1.0 으로 진행')
            self.speed_scale = 1.0

        self.stream = None
        self.interp = None
        self.state = 'idle'         # idle | printing | paused | error
        self.filename = ''
        self.progress = 0.0
        self.layer = 0
        self._prev_m = (0.0, 0.0, 0.0)
        self._throttle_warned = False       # 거리 상한 경고는 재생당 한 번
        self._dup_warned = False            # /printer/target 발행자 중복 경고

        # 파서 경고는 리더 스레드에서 나옴. 로거를 그 스레드에서 부르지 않으려고
        # 여기 쌓아 두고 상태 타이머(노드 스레드)가 꺼내 감. deque 의 append/popleft
        # 는 원자적이라 락 불필요.
        self.pending_warnings = collections.deque(maxlen=256)

        self.target_pub = self.create_publisher(JointState, '/printer/target', 10)
        self.ext_pub = self.create_publisher(ExtrusionPoint, '/printer/extrusion', 200)
        self.status_pub = self.create_publisher(PrinterStatus, '/printer/status', 10)

        topic = self.get_parameter('playback_topic').value
        self.create_subscription(PrinterCommand, topic, self.on_command, 10)
        self.create_timer(2.0, self.check_sole_source)
        self.create_timer(self.dt, self.tick)
        self.create_timer(1.0 / float(self.get_parameter('status_rate').value),
                          self.publish_status)

        cap = (f'{self.max_step_mm:g}mm/tick ({self.max_step_mm * self.rate:g}mm/s)'
               if self.max_step_mm > 0.0 else '무제한')
        self.get_logger().info(
            f'gcode player | rate={self.rate}Hz speed={self.speed_scale}x '
            f'max_step={cap} cmd={topic}')

    # ------------------------------------------------------------------
    def on_command(self, msg):
        """`/printer/playback` 수신. 스트림 계열만 처리."""
        command = msg.command.strip().lower()
        if command == 'load_gcode':
            self.load(msg.payload.strip())
        elif command == 'pause':
            self.pause()
        elif command == 'resume':
            self.resume()
        elif command == 'stop':
            self.stop()
        elif command == 'set_speed':
            self.set_speed(msg.args)
        else:
            # jog / home 은 상태 노드 몫. 여기까지 흘러온 것 자체가 A 쪽 필터 이슈라
            # 조용히 버리지 않고 남김.
            self.get_logger().debug(f'스트림 명령이 아님 — 무시함: {msg.command!r}')

    # ------------------------------------------------------------------
    def load(self, path):
        """load_gcode. 재생 진입점은 이것 하나뿐 (파일 경로 파라미터를 두지 않는 이유)."""
        if not path:
            self.fail('load_gcode 에 payload(파일 경로)가 없음')
            return
        path = os.path.expanduser(path)
        if not os.path.isabs(path):
            # 노드의 cwd 는 launch 가 정하는 것이라 상대경로는 의미가 흔들림.
            path = os.path.abspath(path)
            self.get_logger().warn(f'상대경로를 받아 절대경로로 해석함: {path}')
        if not os.path.isfile(path):
            self.fail(f'파일이 없음: {path}')
            return

        self.discard()
        try:
            stream = MoveStream(path, on_warning=self.pending_warnings.append).start()
        except OSError as exc:
            self.fail(f'파일을 열 수 없음: {exc}')
            return

        # 생성자가 첫 세그먼트를 당겨 오므로 리더가 한 줄이라도 뱉을 때까지 잠깐 막힘.
        self.stream = stream
        self.interp = MotionInterpolator(stream, speed_scale=self.speed_scale,
                                         max_step_mm=self.max_step_mm)
        self._throttle_warned = False
        self.filename = os.path.basename(path)
        self.progress = 0.0
        self.layer = 0
        self.state = 'printing'
        self.get_logger().info(
            f'재생 시작 | {self.filename} ({stream.size_bytes / 1024.0:.1f}KiB) '
            f'{self.speed_scale}x')

    def pause(self):
        """보간기를 세움. 커서(seg, s)는 그대로라 재개해도 좌표가 튀지 않음."""
        if self.state != 'printing':
            return
        self.state = 'paused'
        self.get_logger().info(f'일시정지 | progress={self.progress:.1%}')

    def resume(self):
        if self.state != 'paused':
            return
        self.state = 'printing'
        self.get_logger().info('재개')

    def stop(self):
        """스트림 폐기 후 idle. 재개 불가 — 다시 틀려면 load_gcode."""
        if self.stream is None and self.state == 'idle':
            return
        self.discard()
        self.state = 'idle'
        self.get_logger().info('정지')
        self.publish_status()

    def set_speed(self, args):
        """배속 변경. 커서를 건드리지 않으므로 재생 도중에 바꿔도 좌표가 튀지 않음.

        임의의 양수를 받음 — 키보드 단계 리스트(`SPEED_STEPS`)는 A 소유이고 이 노드는
        모름. 0 이하는 거부하며, `0.0` 이 뜻하는 것은 pause.
        """
        if not args:
            self.get_logger().warn('set_speed 에 args[0](배속)이 없음')
            return
        value = float(args[0])
        if value <= 0.0:
            self.get_logger().warn(f'배속은 양수여야 함: {value} — 정지는 pause 로')
            return
        self.speed_scale = value
        if self.interp is not None:
            self.interp.speed_scale = value
        self.get_logger().info(f'배속 {value}x')

    # ------------------------------------------------------------------
    def tick(self):
        """50Hz. 일시정지·정지 중에는 아무것도 발행하지 않음 — A 가 마지막 값을 유지."""
        if self.state != 'printing' or self.interp is None:
            return

        sample = self.interp.sample(self.dt)
        self.publish_target(sample.position)
        for move in sample.vertices:
            self.publish_vertex(move)
        if self.stream is not None:
            self.progress = min(self.stream.offset / self.stream.size_bytes, 1.0)
        if sample.done:
            self.finish()

    def finish(self):
        """스트림 소진. 리더가 남긴 에러가 있으면 정상 종료가 아님."""
        error = self.stream.error if self.stream is not None else None
        moves = self.stream.parser.move_count if self.stream is not None else 0
        warnings = len(self.stream.parser.warnings) if self.stream is not None else 0
        effective = ''
        if self.interp is not None and self.interp.throttled_ticks:
            effective = (f' 실제평균={self.interp.effective_scale:.1f}x'
                         f'(요청 {self.speed_scale:g}x)')
        self.discard()

        if error is not None:
            self.state = 'error'
            self.get_logger().error(f'재생 중단 | {error}')
        else:
            self.state = 'idle'
            self.progress = 1.0
            self.get_logger().info(
                f'재생 완료 | {self.filename} moves={moves} '
                f'layers={self.layer} warnings={warnings}{effective}')
        self.publish_status()

    def fail(self, message):
        self.discard()
        self.state = 'error'
        self.get_logger().error(message)
        self.publish_status()

    def discard(self):
        """스트림과 보간기를 놓음. 리더 스레드는 close() 를 보고 스스로 끝남."""
        if self.stream is not None:
            self.stream.close()
        self.stream = None
        self.interp = None

    # ------------------------------------------------------------------
    def publish_target(self, position_mm):
        """보간 좌표 -> JointState [m]. 이 나눗셈이 파이프라인의 유일한 mm->m."""
        x, y, z = (value * MM_TO_M for value in position_mm)
        msg = JointState()
        msg.header.stamp = self.get_clock().now().to_msg()
        msg.name = list(JOINT_NAMES)
        msg.position = [x, y, z]
        px, py, pz = self._prev_m
        msg.velocity = [(x - px) / self.dt, (y - py) / self.dt, (z - pz) / self.dt]
        self.target_pub.publish(msg)
        self._prev_m = (x, y, z)

    def publish_vertex(self, move):
        """세그먼트 끝점 하나. 틱이 아니라 여기서 내보내야 코너가 살아남음.

        travel 구간도 `extruding=false` 로 내보냄 — Unity 가 선을 끊는 신호가 됨.
        """
        point = ExtrusionPoint()
        point.header.stamp = self.get_clock().now().to_msg()
        point.header.frame_id = BED_FRAME
        x, y, z = (value * MM_TO_M for value in move.end)
        point.position = Point(x=x, y=y, z=z)
        point.width = float(self.width_m(move))
        point.height = float((move.height_mm or DEFAULT_LAYER_HEIGHT_MM) * MM_TO_M)
        point.extruding = move.extruding
        point.layer = max(int(move.layer), 0)
        self.ext_pub.publish(point)
        self.layer = point.layer

    def width_m(self, move):
        """압출 폭 [m]. 슬라이서가 심은 `;WIDTH:` 우선, 없으면 압출량에서 역산.

        필라멘트 원기둥이 층높이 x 폭 x 길이 로 눕는다는 가정:
        `width = π r² ΔE / (거리 x 층높이)`.
        """
        if move.width_mm > 0.0:
            return move.width_mm * MM_TO_M
        if move.extrude_mm <= 0.0 or move.length_mm <= 0.0:
            return 0.0                          # travel. 어차피 extruding=false
        height = move.height_mm or DEFAULT_LAYER_HEIGHT_MM
        volume = math.pi * FILAMENT_RADIUS_MM ** 2 * move.extrude_mm
        return volume / (move.length_mm * height) * MM_TO_M

    def publish_status(self):
        """5Hz. progress 는 바이트 오프셋 / 파일 크기.

        분자는 리더의 위치가 아니라 보간기가 꺼내 간 Move 의 위치 — 리더는 큐 깊이만큼
        앞서 있어 그대로 쓰면 진행률이 실제 재생보다 먼저 100% 에 닿음.
        """
        for text in self.take_warnings():
            self.get_logger().warn(text)
        self.report_throttle()

        status = PrinterStatus()
        status.header.stamp = self.get_clock().now().to_msg()
        status.state = self.state
        status.progress = float(self.progress)
        status.current_layer = int(self.layer)
        status.total_layers = 0                 # 총 층수는 끝까지 읽기 전엔 모름
        status.filename = self.filename
        self.status_pub.publish(status)

    def check_sole_source(self):
        """`/printer/target` 의 값 소스가 나 하나인지 2 초마다 확인.

        값 소스는 배타 선택임 (sim.launch.py). 플레이어가 둘이면 각자의 보간기가
        서로 다른 진행률로 같은 토픽에 50Hz 씩 쏘고, 상태 노드는 그때그때 온 값을
        그대로 쓰므로 헤드가 두 지점 사이를 오감. 새로 띄운 쪽은 파일 맨 앞
        (0,0,0) 에서 출발하므로 **"영점으로 돌아갔다 다시 G-code 좌표로 감"** 이 됨.

        원인은 대개 앞 실행의 플레이어가 고아로 살아남은 것. `/printer/playback`
        은 그 고아에게도 그대로 도달하므로 load_gcode 를 쏘면 둘이 같이 재생을 시작함.

            ps -ef | grep -E 'gcode_player|printer_state_node'
            pkill -f voron24_                # 확인 후 정리
        """
        others = self.count_publishers('/printer/target') - 1
        if others > 0:
            if not self._dup_warned:
                self._dup_warned = True
                self.get_logger().error(
                    f'/printer/target 에 다른 값 소스가 {others} 개 더 있음 — 값 소스는 '
                    '배타 선택임. 헤드가 영점과 G-code 좌표 사이를 오가면 그 탓이며 '
                    '대개 앞 실행의 고아 플레이어임: '
                    "ps -ef | grep voron24_ 로 확인 후 pkill -f voron24_")
        elif self._dup_warned:
            self._dup_warned = False
            self.get_logger().info('/printer/target 발행자가 다시 하나가 됨')

    def report_throttle(self):
        """거리 상한에 걸렸음을 재생당 한 번 알림.

        조용히 느려지면 "배속을 100 으로 줬는데 왜 이러지" 로 끝남. 요청 배속과 실제
        배속을 같이 찍어 "이 배속으로 연속 재생이 안 됨" 을 눈에 보이게 함.
        """
        if self._throttle_warned or self.interp is None:
            return
        if not self.interp.throttled_ticks:
            return
        self._throttle_warned = True
        self.get_logger().warn(
            f'틱당 이동 상한({self.max_step_mm:g}mm)에 걸림 — 요청 {self.speed_scale:g}x '
            f'대신 약 {self.interp.effective_scale:.1f}x 로 재생함. '
            '더 빨리 보려면 max_step_mm 을 올리되(0 이면 무제한) 헤드가 경로를 '
            '건너뛰고 Unity 쪽이 목표를 못 따라감')

    def take_warnings(self):
        """리더 스레드가 쌓아 둔 파서 경고를 노드 스레드로 옮김."""
        out = []
        while self.pending_warnings:
            try:
                out.append(self.pending_warnings.popleft())
            except IndexError:                  # 드레인 도중 비는 경우
                break
        return out


def main(args=None):
    rclpy.init(args=args)
    node = GcodePlayer()
    try:
        rclpy.spin(node)
    except KeyboardInterrupt:
        pass
    finally:
        node.discard()                          # 리더 스레드 정리
        node.destroy_node()
        if rclpy.ok():
            rclpy.shutdown()


if __name__ == '__main__':
    main()
