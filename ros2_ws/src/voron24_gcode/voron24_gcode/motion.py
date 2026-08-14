"""
motion.py
=========
Move 스트림 -> 시간축 좌표 샘플.

    python3 -m voron24_gcode.motion                    # 자체 검증
    python3 -m voron24_gcode.motion sample.gcode       # 시간축 궤적 확인
    python3 -m voron24_gcode.motion sample.gcode 10    # 10 배속

G-code 가 주는 것은 목표 좌표와 이송속도뿐. `/joint_states` 가 요구하는 50Hz
좌표 열은 이 모듈이 만듦.

좌표는 mm 로 유지 (계약 §2). mm->m 은 gcode_player_node 가 publish 직전에 변환.

한 틱에 세그먼트가 여러 개 소진되기도 하고, 세그먼트 하나가 여러 틱에 걸치기도 함.
슬라이서가 뱉은 곡선은 틱당 이동량보다 짧은 세그먼트 수천 개인 반면, 직선 한 줄은
그 이동량의 수십 배이기 때문. 틱당 하나씩만 소비하면 곡선 구간이 실제보다 수 배 느려짐.

가속(사다리꼴 프로파일)은 넣지 않음. 시각화에는 등속으로 충분, 추가 시 룩어헤드 큐 구현 필요.
`F` 는 축별 최대속도로 클램프하며, 그 최대속도는 voron24_params.xacro 에서 읽음.
"""
import math
import os
import re
from dataclasses import dataclass

from .gcode_parser import GcodeError, Move, parse_file

DEFAULT_RATE_HZ = 50.0

# 0 나눗셈 방지용 하한. 파서가 F 를 보장하므로 실제로 걸릴 일은 없음.
MIN_SPEED_MM_S = 1e-6

# 축별 최대속도의 단일 출처는 voron24_params.xacro. 치수와 리밋은 전부 그 파일의
# property 에서 나옴 (CLAUDE.md 파일 소유권 §4). 여기에 숫자를 복제해 두면 그쪽이
# 바뀔 때 조용히 어긋남.
PARAMS_PACKAGE = 'voron24_description'
PARAMS_XACRO = os.path.join('urdf', 'voron24_params.xacro')

# xacro 를 끝내 못 찾았을 때만 쓰는 값. 임포트를 실패시키는 것보다 낫다는 판단이고,
# 이 값이 쓰였는지는 MAX_SPEED_SOURCE 가 None 인지로 드러남.
FALLBACK_MAX_SPEED_MM_S = (500.0, 500.0, 50.0)


def find_params_xacro():
    """voron24_params.xacro 경로. 설치 트리 -> 소스 트리 순으로 탐색.

    ament 임포트를 try 로 감싼 이유는 mock_publisher_node 의 `HAS_MSGS` 방어와
    같음 — ROS 를 소싱하지 않은 셸에서도 이 모듈 단독으로 돌아가야 함.
    """
    try:
        from ament_index_python.packages import get_package_share_directory
        path = os.path.join(get_package_share_directory(PARAMS_PACKAGE), PARAMS_XACRO)
        if os.path.exists(path):
            return path
    except Exception:
        pass
    # realpath 로 심볼릭 링크를 풀어야 --symlink-install 로 깔린 경우에도 소스
    # 트리에서 출발함. 기준 경로는 .../src/voron24_gcode/voron24_gcode/motion.py.
    here = os.path.realpath(__file__)
    for _ in range(5):
        here = os.path.dirname(here)
        path = os.path.join(here, PARAMS_PACKAGE, PARAMS_XACRO)
        if os.path.exists(path):
            return path
    return None


def read_xacro_property(text, name):
    """`<xacro:property name=".." value=".."/>` 한 개. 숫자가 아니면 None.

    xacro 흉내는 내지 않음. `${...}` 같은 수식이면 포기하고 호출자가 fallback 을
    쓰게 둠 — 여기서 수식을 평가하기 시작하면 xacro 재구현이 되어버림.
    """
    match = re.search(rf'<xacro:property\s+name="{name}"\s+value="([^"]*)"', text)
    if match is None:
        return None
    try:
        return float(match.group(1))
    except ValueError:
        return None


def load_axis_limits(path=None):
    """-> ((vx, vy, vz) [mm/s], 읽어온 경로). 못 읽으면 fallback 과 None.

    xacro 의 `vel_*` 는 m/s, 이 모듈은 mm/s.
    """
    path = path or find_params_xacro()
    if path is None:
        return FALLBACK_MAX_SPEED_MM_S, None
    try:
        with open(path, 'r') as handle:
            text = handle.read()
    except OSError:
        return FALLBACK_MAX_SPEED_MM_S, None

    vel_xy = read_xacro_property(text, 'vel_xy')
    vel_z = read_xacro_property(text, 'vel_z')
    if vel_xy is None or vel_z is None:
        return FALLBACK_MAX_SPEED_MM_S, None
    return (vel_xy * 1000.0, vel_xy * 1000.0, vel_z * 1000.0), path


# 임포트 시 한 번만 읽음. 노드에서는 생성자 인자로 덮어쓸 수 있음.
MAX_SPEED_MM_S, MAX_SPEED_SOURCE = load_axis_limits()


@dataclass(frozen=True)
class Sample:
    """한 틱의 결과.

    `vertices` 는 이번 틱에 **소진된** 세그먼트. ExtrusionPoint 는 틱 단위가 아니라
    여기서 발행해야 함 — 50Hz 로 샘플한 점만 흘리면 코너가 뭉개지지만, 세그먼트
    끝점을 흘리면 Unity 의 선이 G-code 원본 형상과 정확히 일치.
    한 틱에 여러 개가 소진될 수 있어 리스트.
    """

    position: tuple         # (x, y, z) [mm]
    move: Move              # 지금 올라타 있는 세그먼트. 스트림이 끝났으면 None
    vertices: tuple         # 이번 틱에 소진된 Move. 꼭짓점은 각 move.end
    done: bool              # 스트림 소진


class MotionInterpolator:
    """Move 스트림 위를 시간으로 진행하며 현재 좌표를 뽑음.

    스트림을 미리 펼치지 않음. `next()` 로 한 세그먼트씩 당겨 쓰므로 파서
    제너레이터를 그대로 물려도 수십 MB G-code 가 통째로 메모리에 올라오지 않음.

    `pause` 는 여기 없음. 노드가 `sample()` 을 부르지 않는 것이 곧 일시정지.
    커서(`seg`, `s`)를 건드리지 않으므로 재개해도 좌표가 튀지 않음.
    """

    def __init__(self, moves, speed_scale=1.0, max_speed_mm_s=MAX_SPEED_MM_S,
                 start=(0.0, 0.0, 0.0)):
        self.moves = iter(moves)
        self.max_speed_mm_s = tuple(max_speed_mm_s)
        self.speed_scale = speed_scale          # 검증은 아래 setter 담당
        self.position = tuple(start)
        self.s = 0.0                            # 현재 세그먼트 진행거리 [mm]
        self.time_s = 0.0                       # 소비한 G-code 시간 (배속 반영)
        self.tick_count = 0
        self.vertex_count = 0
        self.clamped_count = 0
        self.seg = None
        self._seg_speed = 0.0
        self._advance()

    # ------------------------------------------------------------------
    @property
    def speed_scale(self):
        """재생 배속. 시간축만 늘이고 줄임 — 궤적 형상과 꼭짓점 열은 그대로.

        축별 클램프 **뒤에** 곱함. 배속은 기계 한계가 아니라 시각화용 시간
        왜곡이므로 10 배속이 500mm/s 한계에 걸려서는 안 됨.
        """
        return self._speed_scale

    @speed_scale.setter
    def speed_scale(self, value):
        value = float(value)
        if value <= 0.0:
            # 0 은 pause 의 몫이고 음수는 의미가 없음. 노드가 받아서 경고 처리.
            raise ValueError(f'speed_scale 은 양수여야 함: {value}')
        self._speed_scale = value

    @property
    def done(self):
        return self.seg is None

    # ------------------------------------------------------------------
    def axis_limit_mm_s(self, move):
        """축별 최대속도가 허용하는 이 방향의 경로속도 [mm/s].

        한 축이라도 자기 한계를 넘으면 안 되므로, 축별 한계를 방향 성분으로 나눈
        값 중 최소를 취함. Z 한계가 XY 의 1/10 이라 Z 가 섞인 이동은 대개 Z 가 결정.
        """
        limit = float('inf')
        for i in range(3):
            delta = abs(move.end[i] - move.start[i])
            if delta > 0.0:
                limit = min(limit, self.max_speed_mm_s[i] * move.length_mm / delta)
        return limit

    def speed_of(self, move):
        limit = self.axis_limit_mm_s(move)
        if move.feed_mm_s > limit:
            self.clamped_count += 1
            return limit
        return max(move.feed_mm_s, MIN_SPEED_MM_S)

    def _advance(self):
        """다음 세그먼트로 이동. 속도는 세그먼트당 한 번만 계산."""
        self.seg = next(self.moves, None)
        self.s = 0.0
        self._seg_speed = 0.0 if self.seg is None else self.speed_of(self.seg)

    # ------------------------------------------------------------------
    def sample(self, dt):
        """dt 초 진행한 뒤의 상태.

        거리가 아니라 **시간**을 예산으로 씀. 세그먼트마다 `F` 가 달라
        (travel 9000 과 print 1800 이 한 틱에 함께 걸리는 일이 흔함) 거리 예산은
        경계에서 어긋남. 시간으로 재면 세그먼트마다 자기 속도로 환산됨.
        """
        vertices = []
        time_left = dt * self._speed_scale
        while time_left > 0.0 and self.seg is not None:
            remain_s = (self.seg.length_mm - self.s) / self._seg_speed
            if remain_s > time_left:
                self.s += self._seg_speed * time_left
                time_left = 0.0
                break
            time_left -= remain_s                # 세그먼트 소진 -> 다음 것으로
            self.position = self.seg.end
            vertices.append(self.seg)
            self._advance()

        if self.seg is not None:
            self.position = self.seg.point_at(self.s)
        # 스트림이 틱 중간에 끝나면 남은 시간은 소비되지 않은 것. 재생 시간에서 뺌.
        self.time_s += dt * self._speed_scale - time_left
        self.tick_count += 1
        self.vertex_count += len(vertices)
        return Sample(self.position, self.seg, tuple(vertices), self.seg is None)

    def run(self, dt):
        """끝까지 샘플하는 제너레이터. 검증용 — 노드는 타이머로 sample() 을 호출."""
        while not self.done:
            yield self.sample(dt)


# ----------------------------------------------------------------------
def trace(path, speed_scale=1.0, rate_hz=DEFAULT_RATE_HZ, show=10):
    """파일 -> 50Hz 궤적 + 합계. 합계는 make_test_gcode.py 로 생성된 좌표 값과 대조."""
    import sys

    dt = 1.0 / rate_hz
    parser, stream = parse_file(path, on_warning=lambda t: print(f'WARN {t}', file=sys.stderr))
    interp = MotionInterpolator(stream, speed_scale=speed_scale)

    prev = interp.position
    max_step_mm = 0.0
    extruding_ticks = 0
    try:
        for sample in interp.run(dt):
            step = math.dist(prev, sample.position)
            max_step_mm = max(max_step_mm, step)
            prev = sample.position
            if sample.move is not None and sample.move.extruding:
                extruding_ticks += 1
            if interp.tick_count <= show:
                x, y, z = sample.position
                print(f'{interp.tick_count * dt:7.3f}s ({x:8.3f},{y:8.3f},{z:7.3f}) '
                      f'step={step:6.3f}mm vertices={len(sample.vertices)}')
    except GcodeError as exc:
        print(f'REJECT {exc}', file=sys.stderr)
        return 1

    vx, vy, vz = interp.max_speed_mm_s
    print(f'\nspeed_scale={speed_scale:g} rate={rate_hz:g}Hz')
    print(f'  max_speed=({vx:g},{vy:g},{vz:g})mm/s '
          f'<- {MAX_SPEED_SOURCE or "fallback (xacro 를 못 찾음)"}')
    print(f'  ticks={interp.tick_count} (wall {interp.tick_count * dt:.2f}s)')
    print(f'  gcode_time={interp.time_s:.2f}s  extruding_ticks={extruding_ticks}')
    print(f'  vertices={interp.vertex_count} / moves={parser.move_count}')
    print(f'  clamped_seg={interp.clamped_count}  max_step={max_step_mm:.3f}mm/tick')
    x, y, z = interp.position
    print(f'  end=({x:.3f},{y:.3f},{z:.3f})')
    return 0


# ----------------------------------------------------------------------
def _seg(start, end, feed_mm_s, extrude_mm=0.0):
    return Move(start=start, end=end, length_mm=math.dist(start, end),
                feed_mm_s=feed_mm_s, extrude_mm=extrude_mm,
                width_mm=0.42, height_mm=0.2, layer=1, line_no=0)


def _drain(moves, dt=1.0 / DEFAULT_RATE_HZ, speed_scale=1.0, limit=200000):
    """끝까지 돌린 뒤 (틱 수, 꼭짓점 리스트, 틱당 최대 이동량, 보간기) 반환."""
    interp = MotionInterpolator(moves, speed_scale=speed_scale)
    vertices = []
    prev = interp.position
    max_step = 0.0
    while not interp.done and interp.tick_count < limit:
        sample = interp.sample(dt)
        vertices.extend(sample.vertices)
        max_step = max(max_step, math.dist(prev, sample.position))
        prev = sample.position
    return interp.tick_count, vertices, max_step, interp


def _self_test():
    checks = []

    def check(name, ok, detail=''):
        checks.append((name, ok, detail))

    def near_ticks(got, want):
        return abs(got - want) <= 1          # 틱 경계에서 1 틱 오차는 허용

    # 리밋을 xacro 에서 읽어왔는지 확인. fallback 으로 떨어졌으면 여기서 드러남.
    check('축 리밋을 xacro 에서 읽음', MAX_SPEED_SOURCE is not None,
          '경로를 못 찾아 fallback 을 쓰는 중')
    check('  vel_xy/vel_z -> mm/s', MAX_SPEED_MM_S == (500.0, 500.0, 50.0),
          f'{MAX_SPEED_MM_S}')
    check('  숫자가 아닌 property 는 포기',
          read_xacro_property('<xacro:property name="v" value="${a*2}"/>', 'v') is None)
    check('  없는 property 는 None',
          read_xacro_property('<xacro:property name="v" value="1"/>', 'nope') is None)
    check('  파일이 없으면 fallback',
          load_axis_limits('/nonexistent.xacro') == (FALLBACK_MAX_SPEED_MM_S, None))

    # 긴 직선 하나를 여러 틱에 걸쳐 소비. 100mm / 50mm/s = 2s = 100 틱.
    ticks, vertices, max_step, interp = _drain([_seg((0, 0, 0), (100, 0, 0), 50.0)])
    check('긴 직선 분할', near_ticks(ticks, 100), f'{ticks} != ~100')
    check('  꼭짓점 1개', len(vertices) == 1, f'{len(vertices)}')
    check('  종점 도달', interp.position == (100, 0, 0), f'{interp.position}')
    check('  틱당 이동 = 1mm', abs(max_step - 1.0) < 1e-6, f'{max_step}')

    # 짧은 세그먼트 다발. 0.2mm x 3000 = 600mm / 60mm/s = 10s = 500 틱.
    # while 없이 틱당 하나만 소비하면 3000 틱이 걸림. 이 모듈의 핵심.
    tiny = [_seg((i * 0.2, 0, 0), ((i + 1) * 0.2, 0, 0), 60.0, 0.007)
            for i in range(3000)]
    ticks, vertices, max_step, interp = _drain(tiny)
    check('틱당 다중 세그먼트 소진', near_ticks(ticks, 500), f'{ticks} != ~500 (while 누락?)')
    check('  꼭짓점 = 세그먼트 수', len(vertices) == 3000, f'{len(vertices)}')
    check('  꼭짓점 순서 보존', vertices == tiny)

    # 세그먼트마다 F 가 다른 경우. 100mm/100mm/s(1s) + 100mm/25mm/s(4s) = 5s = 250 틱.
    # 거리 예산으로 짜면 경계를 넘는 틱에서 뒷 세그먼트를 앞 속도로 소비해 틀어짐.
    ticks, _, _, _ = _drain([_seg((0, 0, 0), (100, 0, 0), 100.0),
                             _seg((100, 0, 0), (200, 0, 0), 25.0)])
    check('세그먼트별 F 반영', near_ticks(ticks, 250), f'{ticks} != ~250')

    # Z 클램프. F9000(150mm/s) 이어도 vel_z 는 50mm/s -> 10mm 에 0.2s = 10 틱.
    ticks, _, _, interp = _drain([_seg((0, 0, 0), (0, 0, 10), 150.0)])
    check('Z 축 클램프', near_ticks(ticks, 10) and interp.clamped_count == 1,
          f'{ticks} != ~10, clamped={interp.clamped_count}')

    # XY 클램프. F60000(1000mm/s) -> 500mm/s -> 100mm 에 0.2s = 10 틱.
    ticks, _, _, interp = _drain([_seg((0, 0, 0), (100, 0, 0), 1000.0)])
    check('XY 축 클램프', near_ticks(ticks, 10) and interp.clamped_count == 1,
          f'{ticks} != ~10, clamped={interp.clamped_count}')

    # 대각선은 성분으로 나눔. X10 Z10 은 Z 성분이 0.707 이라 70.7mm/s 가 한계.
    diagonal = _seg((0, 0, 0), (10, 0, 10), 500.0)
    limit = MotionInterpolator([]).axis_limit_mm_s(diagonal)
    check('대각선 클램프는 Z 가 결정', abs(limit - 50.0 * math.sqrt(2)) < 1e-6, f'{limit}')

    # 클램프에 안 걸리는 이동은 그대로 통과.
    ticks, _, _, interp = _drain([_seg((0, 0, 0), (100, 0, 0), 50.0)])
    check('한계 이하는 무클램프', interp.clamped_count == 0, f'{interp.clamped_count}')

    # 배속은 시간축만 줄임. 형상과 꼭짓점은 그대로.
    ticks_x1, vertices_x1, _, interp_x1 = _drain(tiny)
    ticks_x2, vertices_x2, _, interp_x2 = _drain(tiny, speed_scale=2.0)
    check('배속 2 -> 틱 절반', near_ticks(ticks_x2, ticks_x1 / 2), f'{ticks_x2} vs {ticks_x1}')
    check('  꼭짓점은 배속 무관', vertices_x2 == vertices_x1)
    check('  종점 동일', interp_x2.position == interp_x1.position)
    check('  G-code 시간 동일', abs(interp_x2.time_s - interp_x1.time_s) < 1e-9,
          f'{interp_x2.time_s} vs {interp_x1.time_s}')

    # 0 이하 배속은 거부. pause 는 노드가 sample() 호출을 멈추는 것으로 처리.
    try:
        MotionInterpolator([], speed_scale=0.0)
        check('0 배속 거부', False, '에러가 발생하지 않음')
    except ValueError:
        check('0 배속 거부', True)

    # 빈 스트림은 즉시 done.
    empty = MotionInterpolator([])
    check('빈 스트림', empty.done and empty.sample(0.02).done)

    ok = True
    for name, passed, detail in checks:
        if not passed:
            ok = False
        print(f'[{"OK " if passed else "FAIL"}] {name}'
              + (f'  <- {detail}' if not passed and detail else ''))
    print('\nALL PASS' if ok else '\nFAILED')
    return ok


if __name__ == '__main__':
    import sys

    if len(sys.argv) > 1:
        scale = float(sys.argv[2]) if len(sys.argv) > 2 else 1.0
        sys.exit(trace(sys.argv[1], scale))
    sys.exit(0 if _self_test() else 1)
