"""
gcode_parser.py
===============
G-code 텍스트 → Move 스트림.

    python3 -m voron24_gcode.gcode_parser                 # 자체 검증
    python3 -m voron24_gcode.gcode_parser sample.gcode    # 세그먼트 덤프

좌표 mm 유지 (계약 §2). mm→m 변환은 gcode_player_node publish 직전.
이송속도만 여기서 환산 — `F`(mm/min) ÷ 60 → mm/s.
한 줄씩 읽어 Move 제너레이터로 전달. 전체 메모리 로드 없음.

축별 최대속도 클램프는 motion.py, 좌표 리밋 검사는 상태 노드(A) 담당.
"""
import math
import re
from dataclasses import dataclass

# 워드 = 문자 + 숫자. `G28 X Y` 처럼 값 없는 축 지정이 있어 숫자 optional.
# 공백 없이 붙여 쓴 `G1X120Y100` 도 인식.
WORD_RE = re.compile(r'([A-Za-z])\s*([-+]?[0-9]*\.?[0-9]*)')

# 궤적 무관하여 버리는 코드. 목록에 없는 코드는 경고 후 무시 —
# 모르는 코드를 소리 없이 삼키면 방언 변경 감지 불가.
IGNORED_CODES = frozenset((
    'G21',                                  # mm 모드.
    'M104', 'M109', 'M140', 'M190',         # 온도
    'M106', 'M107',                         # 팬
    'M17', 'M18', 'M84',                    # 모터 on/off
    'M73', 'M117',                          # 진행률 / 디스플레이
    'M201', 'M203', 'M204', 'M205', 'M900', # 가속·저크·선형이송. 등속 재생이라 무의미
    'M220', 'M221',                         # 속도·유량 오버라이드
    'M400', 'M600',                         # 큐 flush / 필라멘트 교체
    'T0', 'T1',                             # 툴 선택. 단일 익스트루더
))

# F 미지정 상태에서 이동 발생 시 사용. 0 이면 보간기가 그 자리에
# 영원히 정지하므로 값 삽입 후 경고. 정상 슬라이서 출력에서는 미사용.
FALLBACK_FEED_MM_S = 50.0


class GcodeError(ValueError):
    """그대로 진행 시 조용히 틀린 궤적이 나오는 경우에만 발생.

    형상 유지되는 쪽(G2, 모르는 M 코드)은 경고로 끝내고, 좌표계 자체가
    어긋나는 쪽(G91, G20)만 예외로 중단.
    """

    def __init__(self, line_no, message):
        super().__init__(f'line {line_no}: {message}')
        self.line_no = line_no
        self.message = message


@dataclass(frozen=True)
class Move:
    """직선 세그먼트 하나. 좌표 mm, 속도 mm/s.

    `extrude_mm` — 이 구간 필라멘트 압출 길이. 압출 여부는 `G0`/`G1` 이
    아니라 이 값으로 판정 — E 없는 `G1`(스커트 이동)과 E 붙은 `G0`(방언) 둘 다
    실재.
    """

    start: tuple            # (x, y, z) [mm]
    end: tuple              # (x, y, z) [mm]
    length_mm: float        # 미리 계산. 보간기 틱마다 참조
    feed_mm_s: float        # F(mm/min) ÷ 60
    extrude_mm: float       # 이 구간 ΔE [mm]. 리트랙션 시 음수
    width_mm: float         # 직전 `;WIDTH:` 태그. 없으면 0
    height_mm: float        # 레이어 높이. `;HEIGHT:` 또는 `;Z:` 차이로 산출
    layer: int              # `;LAYER_CHANGE` 카운트. 첫 층 = 1
    line_no: int            # 원본 줄 번호. 경고/디버깅용

    @property
    def extruding(self):
        """ExtrusionPoint.extruding 대응. false 면 Unity 선 끊김."""
        return self.extrude_mm > 0.0

    @property
    def duration_s(self):
        return self.length_mm / self.feed_mm_s if self.feed_mm_s > 0.0 else 0.0

    def point_at(self, s_mm):
        """시작점에서 s_mm 만큼 진행한 좌표. motion.py 보간 진입점.

        X/Y/Z 선형 보간.
        """
        if self.length_mm <= 0.0:
            return self.end
        u = min(max(s_mm / self.length_mm, 0.0), 1.0)
        return tuple(a + (b - a) * u for a, b in zip(self.start, self.end))


class GcodeParser:
    """모달 상태 유지하며 한 줄씩 Move 변환.

    G-code 는 한 번 지정한 값이 갱신 전까지 유지되는 모달 방식. 생략 축은 직전
    위치, 생략 `F` 는 직전 이송속도이므로 파서도 프린터와 같은 상태를 들고
    있어야 함.

    경고는 `warnings` 에 쌓고, `on_warning` 지정 시 즉시 호출 — 노드에서
    `get_logger().warn` 연결 지점.
    """

    def __init__(self, on_warning=None):
        self.on_warning = on_warning
        self.warnings = []
        self.reset()

    def reset(self):
        self.pos = [0.0, 0.0, 0.0]          # 기계 좌표 [mm]
        self.origin = [0.0, 0.0, 0.0]       # G92 오프셋. 기계 = 지령 + origin
        self.feed_mm_s = 0.0
        self.absolute_e = True              # M82 기본. M83 이면 E 증분
        self.e_command = 0.0                # 마지막 E 지령값. 절대 모드 기준점
        self.width_mm = 0.0
        self.height_mm = 0.0
        self.layer = 0
        self.layer_z = 0.0                  # 첫 층 높이 = 첫 `;Z:` 값
        self.line_no = 0
        self.move_count = 0
        self._warned_codes = set()

    # ------------------------------------------------------------------
    def parse(self, lines):
        """줄 이터러블 → Move 제너레이터. 파일 객체 직접 전달 가능."""
        for raw in lines:
            self.line_no += 1
            move = self.feed_line(raw)
            if move is not None:
                yield move

    def feed_line(self, raw):
        """한 줄 처리. 이동 발생 시 Move, 아니면 None."""
        code_text, _, comment = raw.partition(';')
        if comment:
            self.read_tag(comment)
        code_text = code_text.strip()
        if not code_text:
            return None

        words = WORD_RE.findall(code_text)
        if not words or not words[0][1]:
            self.warn(f'해석 불가 줄: {code_text!r}')
            return None

        letter, number = words[0]
        code = f'{letter.upper()}{int(float(number))}'
        args = words[1:]

        if code in ('G0', 'G1'):
            return self.do_move(args)
        if code == 'G28':
            return self.do_home(args)
        if code == 'G92':
            return self.do_set_origin(args)
        if code == 'G90':
            return None                     # 절대좌표. 파서 전제 조건
        if code == 'M82':
            self.absolute_e = True
            return None
        if code == 'M83':
            self.absolute_e = False
            return None
        if code == 'G91':
            raise GcodeError(self.line_no,
                             'G91(상대좌표) 미지원. 슬라이서 프로파일 G90 설정 필요')
        if code == 'G20':
            raise GcodeError(self.line_no,
                             'G20(inch) 미지원. 좌표 25.4배 오차 발생')
        if code in ('G2', 'G3'):
            self.warn(f'{code}(원호) 미지원 — 무시. 프로파일 arc fitting 비활성 필요')
            return None
        if code not in IGNORED_CODES:
            self.warn_once(code, f'모르는 코드 {code} — 무시')
        return None

    # ------------------------------------------------------------------
    def do_move(self, words):
        target = list(self.pos)
        delta_e = 0.0
        for letter, number in words:
            if not number: continue
            value = float(number)
            axis = 'XYZ'.find(letter.upper())
            if axis >= 0:
                target[axis] = value + self.origin[axis]
            elif letter.upper() == 'E':
                if self.absolute_e:
                    delta_e = value - self.e_command
                    self.e_command = value
                else:
                    delta_e = value
                    self.e_command += value
            elif letter.upper() == 'F':
                self.feed_mm_s = value / 60.0       # F 는 mm/min

        length = math.dist(self.pos, target)
        if length == 0.0:
            # 리트랙션(E 만 변함) 또는 F 만 지정한 줄. E 는 위에서 반영 완료.
            self.pos = target
            return None
        if self.feed_mm_s <= 0.0:
            self.warn_once('F', f'F 미지정 상태에서 이동 발생 — {FALLBACK_FEED_MM_S}mm/s 로 대체')
            self.feed_mm_s = FALLBACK_FEED_MM_S

        move = Move(
            start=tuple(self.pos),
            end=tuple(target),
            length_mm=length,
            feed_mm_s=self.feed_mm_s,
            extrude_mm=delta_e,
            width_mm=self.width_mm,
            height_mm=self.height_mm,
            layer=self.layer,
            line_no=self.line_no,
        )
        self.pos = target
        self.move_count += 1
        return move

    def do_home(self, words):
        """G28. 세그먼트 생성 없이 좌표 0 스냅.

        홈잉 속도가 G-code 에 없어 세그먼트화 근거 없음. 파일 첫머리 G28 은
        이미 0 이라 무해하나, 이동 후 등장 시 궤적 점프 발생하므로 경고.
        """
        axes = [i for i, name in enumerate('XYZ')
                if any(letter.upper() == name for letter, _ in words)]
        if not axes:
            axes = [0, 1, 2]
        if any(self.pos[i] != 0.0 for i in axes):
            self.warn('G28 이 0 이 아닌 위치를 폐기 — 궤적 점프 발생')
        for i in axes:
            self.pos[i] = 0.0
            self.origin[i] = 0.0
        return None

    def do_set_origin(self, words):
        """G92. 현재 위치에 다른 좌표를 부여하는 지령 (기계 이동 없음).

        대부분 슬라이서가 층마다 삽입하는 `G92 E0`. 다만 `G92 X0 Y0` 로 좌표계를 통째로
        옮기는 파일도 있어 XYZ 까지 처리.
        """
        if not words:
            words = [('X', '0'), ('Y', '0'), ('Z', '0'), ('E', '0')]
        for letter, number in words:
            value = float(number) if number else 0.0
            axis = 'XYZ'.find(letter.upper())
            if axis >= 0:
                self.origin[axis] = self.pos[axis] - value
            elif letter.upper() == 'E':
                self.e_command = value
        return None

    # ------------------------------------------------------------------
    def read_tag(self, comment):
        """슬라이서 주석 태그. 궤적에 없는 정보라 누락 시 복구 불가."""
        text = comment.strip()
        upper = text.upper()
        if upper.startswith('LAYER_CHANGE'):
            self.layer += 1
        elif upper.startswith('Z:'):
            z = self.read_tag_value(text, 2)
            if z is not None:
                if z > self.layer_z:
                    self.height_mm = z - self.layer_z
                self.layer_z = z
        elif upper.startswith('WIDTH:'):
            value = self.read_tag_value(text, 6)
            if value is not None:
                self.width_mm = value
        elif upper.startswith('HEIGHT:'):
            value = self.read_tag_value(text, 7)
            if value is not None:
                self.height_mm = value

    def read_tag_value(self, text, prefix_len):
        try:
            return float(text[prefix_len:].split()[0])
        except (ValueError, IndexError):
            self.warn(f'태그 값 파싱 실패: {text!r}')
            return None

    # ------------------------------------------------------------------
    def warn(self, message):
        text = f'line {self.line_no}: {message}'
        self.warnings.append(text)
        if self.on_warning:
            self.on_warning(text)

    def warn_once(self, key, message):
        """같은 코드 수천 줄 반복 빈번하여 첫 1회만 출력."""
        if key in self._warned_codes:
            return
        self._warned_codes.add(key)
        self.warn(message)


# ----------------------------------------------------------------------
def parse_file(path, on_warning=None):
    """경로 → (parser, Move 제너레이터). 진행률 계산은 호출 측 몫.

    노드가 자기 스레드에서 파일을 읽으므로 바이트 오프셋도 그 루프에서 세는 게
    정확. 파서는 오프셋 미추적, 줄 이터러블만 수신.
    """
    parser = GcodeParser(on_warning=on_warning)

    def stream():
        with open(path, 'r', errors='replace') as handle:
            yield from parser.parse(handle)

    return parser, stream()


# ----------------------------------------------------------------------
def dump(path, limit=20):
    """세그먼트 덤프 + 합계. make_test_gcode.py 정답지와 대조용."""
    import sys

    parser = GcodeParser(on_warning=lambda text: print(f'WARN {text}', file=sys.stderr))
    total_mm = extrude_mm = 0.0
    total_s = 0.0
    extrude_count = 0
    shown = 0

    try:
        with open(path, 'r', errors='replace') as handle:
            for move in parser.parse(handle):
                if limit == 0 or shown < limit:
                    mark = 'E' if move.extruding else '-'
                    print(f'{move.line_no:6d} L{move.layer:<3d} '
                          f'({move.start[0]:8.3f},{move.start[1]:8.3f},{move.start[2]:7.3f}) -> '
                          f'({move.end[0]:8.3f},{move.end[1]:8.3f},{move.end[2]:7.3f}) '
                          f'{move.length_mm:8.3f}mm {move.feed_mm_s:7.2f}mm/s '
                          f'{mark}{move.extrude_mm:+8.4f} w={move.width_mm:.2f}')
                    shown += 1
                total_mm += move.length_mm
                total_s += move.duration_s
                if move.extruding:
                    extrude_mm += move.length_mm
                    extrude_count += 1
    except GcodeError as exc:
        # 거부 경로도 정상 출력 처리. traceback 미노출.
        print(f'REJECT {exc}', file=sys.stderr)
        return 1

    if limit and parser.move_count > limit:
        print(f'... {parser.move_count - limit} more')
    print(f'\nlayers={parser.layer} moves={parser.move_count} extrude_seg={extrude_count}')
    print(f'  path={total_mm:.1f}mm (extrude {extrude_mm:.1f}mm)')
    print(f'  duration={total_s:.2f}s -> 50Hz {round(total_s * 50)} ticks')
    print(f'  warnings={len(parser.warnings)}')
    return 0


# ----------------------------------------------------------------------
# 자체 검증 픽스처. tools/make_test_gcode.py torture 와 같은 방언 대상.
# make_test_gcode.py 는 생성기, 이 모듈은 파서라 상호 참조 없이 각자 보유.
_FIXTURE = """\
G21
G90
M82
G28
;LAYER_CHANGE
;Z:0.2
;WIDTH:0.42
G92 E0
G0 F9000 X100 Y100 Z0.2   ; travel. E 없음
G1 F1800 X120 Y100 E0.698
G1 X120 Y120 E1.396       ; F 생략 — 1800 유지
g1 x100 y120 e2.094 ; 소문자 + 인라인 주석
G92 X0 Y0                 ; 좌표계 리셋. 이후 X0 Y0 = (100,120)
G1 X20 E2.792             ; 실제 X120
M83
G1 X0 Y20 E0.698          ; 이후 E 증분
;WIDTH:0.6
G1 X-20 E0.698
G2 X0 Y0 I10 J0           ; 경고 후 무시
M600
G0 F9000 Z5
"""

# 리트랙션(길이 0) 및 비압출 travel 픽스처.
_FIXTURE_RETRACT = """\
G90
M83
G1 F1800 X10 E0.1
G1 E-0.8
G0 X20
G1 E0.8
G1 X30 E0.35
"""

_FIXTURE_G91 = """\
G21
G28
G91
G1 F1800 X10 Y10 E0.349
"""


def _self_test():
    checks = []

    def check(name, ok, detail=''):
        checks.append((name, ok, detail))

    parser = GcodeParser()
    moves = list(parser.parse(_FIXTURE.splitlines()))
    near = lambda a, b: abs(a - b) < 1e-6
    same = lambda p, q: all(near(a, b) for a, b in zip(p, q))

    check('세그먼트 수', len(moves) == 8, f'{len(moves)} != 8')
    if len(moves) == 8:
        check('F 단위 (9000mm/min -> 150mm/s)', near(moves[0].feed_mm_s, 150.0),
              f'{moves[0].feed_mm_s}')
        check('E 없는 G0 는 travel', not moves[0].extruding)
        check('E 붙은 G1 는 압출', moves[1].extruding and near(moves[1].extrude_mm, 0.698),
              f'{moves[1].extrude_mm}')
        check('F 모달 유지', near(moves[2].feed_mm_s, 30.0), f'{moves[2].feed_mm_s}')
        check('생략 축 유지', same(moves[2].end, (120.0, 120.0, 0.2)), f'{moves[2].end}')
        check('소문자 워드', same(moves[3].end, (100.0, 120.0, 0.2)), f'{moves[3].end}')
        check('G92 좌표계 리셋', same(moves[4].end, (120.0, 120.0, 0.2)), f'{moves[4].end}')
        check('M83 상대 E', near(moves[5].extrude_mm, 0.698)
              and same(moves[5].end, (100.0, 140.0, 0.2)), f'{moves[5].end}')
        check(';WIDTH: 갱신', near(moves[6].width_mm, 0.6), f'{moves[6].width_mm}')
        check(';Z: -> 레이어 높이', near(moves[1].height_mm, 0.2), f'{moves[1].height_mm}')
        check('레이어 카운트', moves[1].layer == 1, f'{moves[1].layer}')
        check('Z 만 움직이는 이동', same(moves[7].end, (80.0, 140.0, 5.0)), f'{moves[7].end}')
        check('압출 세그먼트 수', sum(m.extruding for m in moves) == 6,
              f'{sum(m.extruding for m in moves)}')
        check('point_at 중점', same(moves[1].point_at(moves[1].length_mm / 2.0),
                                    (110.0, 100.0, 0.2)))
    check('G2 는 경고 1건', len(parser.warnings) == 1 and 'G2' in parser.warnings[0],
          str(parser.warnings))

    retract = list(GcodeParser().parse(_FIXTURE_RETRACT.splitlines()))
    check('리트랙션은 세그먼트가 아님', len(retract) == 3, f'{len(retract)} != 3')
    if len(retract) == 3:
        check('E 없는 이동은 travel', [m.extruding for m in retract] == [True, False, True],
              str([m.extruding for m in retract]))

    try:
        list(GcodeParser().parse(_FIXTURE_G91.splitlines()))
        check('G91 거부', False, '에러 미발생')
    except GcodeError as exc:
        check('G91 거부', exc.line_no == 3, str(exc))

    ok = True
    for name, passed, detail in checks:
        if not passed:
            ok = False
        print(f'[{"OK " if passed else "FAIL"}] {name}' + (f'  <- {detail}' if not passed and detail else ''))
    print('\nALL PASS' if ok else '\nFAILED')
    return ok


if __name__ == '__main__':
    import sys

    if len(sys.argv) > 1:
        limit = int(sys.argv[2]) if len(sys.argv) > 2 else 20
        sys.exit(dump(sys.argv[1], limit))
    sys.exit(0 if _self_test() else 1)
