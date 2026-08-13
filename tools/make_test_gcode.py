#!/usr/bin/env python3
"""
make_test_gcode.py
==================
슬라이서 없이 파서·보간기 검증용 G-code 를 뱉는 생성 스크립트 (04b 5a).

stdlib 만 쓰고 ROS 소싱 없이 돈다. patterns.py 와 같은 관례다.

    python3 tools/make_test_gcode.py square   > /tmp/square.gcode
    python3 tools/make_test_gcode.py cylinder > /tmp/cyl.gcode
    python3 tools/make_test_gcode.py torture  > /tmp/torture.gcode

도형 두 개는 motion.py 의 보간을, torture 는 gcode_parser.py 의 방언 처리를 겨눈다.

    square    긴 직선. 한 변 200mm 를 여러 틱에 나눠 먹는 경로.
    cylinder  짧은 세그먼트 수천 개. sample() 의 while 이 없으면 여기서 10배 느려진다.
    torture   방언 총동원 + 무시 목록. 궤적 자체는 무의미하다.

G-code 는 stdout, 통계 요약은 stderr으로 출력. `> out.gcode` 시 g-code와 예상 소요 시간 포함되어 출력.
motion.py 와 비교하여 검증.

G-code 단위는 **mm**.
`F` 는 mm/min 이므로 motion.py에서 단위 속도가 mm/s이므로 F = mm/min * min/60s 으로 변환.
travel/print로부터의 이송 속도가 각각 다른 단위로 출력됨.
"""
import argparse
import math
import sys

BED_LIMIT_MM = 250.0        # voron24_params.xacro 의 stroke_x/y/z. 넘으면 경고만 한다.
FILAMENT_DIAMETER_MM = 1.75


def format_num(value):
    """float -> G-code 숫자 토큰. 소수 3자리(µm)로 자르고 슬라이서 출력처럼 뒤 0 을 턴다.

    225.0 -> '225', 0.2 -> '0.2'. 한 파일에 두 형태가 섞이므로 파서가 소수점 유무를
    모두 받는지 검증된다. '-0' 은 cos/sin 의 부동소수 잡음이 'X-0' 으로 새는 것을 막는다.
    """
    text = f'{value:.3f}'.rstrip('0').rstrip('.')
    return '0' if text in ('', '-0') else text


class GcodeWriter:
    """한 좌표 값을 반환하면서 modal status와 정답지를 함께 추적한다.

    G-code 는 한 번 지정한 값은 갱신 전까지 유지되는 modal.
    그래서 생성기도 프린터와 같은 상태를 들고 있어야 생략할 축을 판단.

    아래 오른쪽 열은 규격이 정한 워드로, 출력 파일에 그대로 찍힌다.

        필드                  출력 워드
        pos_x / pos_y / pos_z   X Y Z   현재 위치 [mm]
        extruded_mm             E       누적 압출 길이 [mm]. 층마다 G92 E0 로 0 이 된다
        feed_mm_min             F       현재 이송속도 [mm/min]. motion.py 의 mm/s 와 60 배 차이
    """

    def __init__(self, out, width_mm, layer_height_mm, filament_diameter_mm):
        self.out = out
        self.width_mm = width_mm
        self.layer_height_mm = layer_height_mm
        self.filament_area_mm2 = math.pi * (filament_diameter_mm / 2.0) ** 2

        # 모달 상태 — 위 표 참조.
        self.pos_x = self.pos_y = self.pos_z = 0.0
        self.extruded_mm = 0.0
        self.feed_mm_min = None   # 첫 이동 전엔 미정. 값이 바뀔 때만 F 를 찍힘.

        # 정답지 — motion.py 를 맞춰볼 대상.
        self.path_mm = self.extrude_path_mm = 0.0
        self.duration_s = self.extrude_duration_s = 0.0
        self.move_count = self.extrude_count = self.layer_count = 0
        self.out_of_bed = False

    @property
    def filament_per_mm(self):
        """경로 1mm 당 밀어넣을 필라멘트 [mm].

        (선폭 x 층높이) / 필라멘트 단면적. 0.42 x 0.2 / 2.405 ~= 0.0349 로,
        실제 슬라이서 출력과 같은 자릿수다.
        """
        return self.width_mm * self.layer_height_mm / self.filament_area_mm2

    def line(self, text=''):
        print(text, file=self.out)

    def move(self, to_x=None, to_y=None, to_z=None, feed=None, extrude=False, comment=None):
        """한 줄 이동. 생략한 축은 현재 위치를 유지한다."""
        new_x = self.pos_x if to_x is None else to_x
        new_y = self.pos_y if to_y is None else to_y
        new_z = self.pos_z if to_z is None else to_z
        dist_mm = math.dist((self.pos_x, self.pos_y, self.pos_z), (new_x, new_y, new_z))

        feed_changed = feed is not None and feed != self.feed_mm_min
        if feed is not None:
            self.feed_mm_min = feed
        if dist_mm == 0.0 and not feed_changed:
            return

        tokens = ['G1' if extrude else 'G0']
        if feed_changed:
            tokens.append(f'F{format_num(self.feed_mm_min)}')
        # 새로 받은 좌표 값만 g-code로 변환.
        if new_x != self.pos_x:
            tokens.append(f'X{format_num(new_x)}')
        if new_y != self.pos_y:
            tokens.append(f'Y{format_num(new_y)}')
        if new_z != self.pos_z:
            tokens.append(f'Z{format_num(new_z)}')
        if extrude and dist_mm > 0.0:
            self.extruded_mm += dist_mm * self.filament_per_mm
            tokens.append(f'E{format_num(self.extruded_mm)}')
        self.line(' '.join(tokens) + (f' ; {comment}' if comment else ''))

        if not (0.0 <= min(new_x, new_y, new_z) and max(new_x, new_y, new_z) <= BED_LIMIT_MM):
            self.out_of_bed = True
        elapsed_s = dist_mm / (self.feed_mm_min / 60.0) if self.feed_mm_min else 0.0
        self.path_mm += dist_mm
        self.duration_s += elapsed_s
        self.move_count += 1
        if extrude:
            self.extrude_path_mm += dist_mm
            self.extrude_duration_s += elapsed_s
            self.extrude_count += 1
        self.pos_x, self.pos_y, self.pos_z = new_x, new_y, new_z

    def layer_change(self, z):
        self.layer_count += 1
        self.line(';LAYER_CHANGE')
        self.line(f';Z:{format_num(z)}')
        self.line(f';WIDTH:{format_num(self.width_mm)}')
        self.line('G92 E0')      # 슬라이서 관례. 층마다 E 를 0 으로 되돌린다.
        self.extruded_mm = 0.0

    def header(self, header_args):
        self.line(f'; make_test_gcode.py {" ".join(header_args)}')
        self.line('M104 S210        ; 무시 — 온도. 이 레포는 시뮬 전용이다')
        self.line('M140 S60         ; 무시')
        self.line('G21              ; mm')
        self.line('G90              ; 절대좌표')
        self.line('M82              ; 절대 E')
        self.line('G28              ; 홈')
        self.line('M109 S210        ; 무시 — 대기')
        self.line('M106 S255        ; 무시 — 팬')

    def footer(self):
        self.line('M107             ; 무시')
        # F9000 = 150mm/s 인데 vel_z 는 50mm/s 다. Z 클램프 케이스가 여기서 생긴다.
        self.move(to_z=min(self.pos_z + 10.0, BED_LIMIT_MM), feed=9000, comment='리프트')
        self.line('; done')


def generate_square(writer, args):
    """N 층 사각형 외곽선. 한 변이 길어 여러 틱에 나눠 먹는 경로를 만든다."""
    center_x, center_y = args.center
    half = args.size / 2.0
    min_x, min_y = center_x - half, center_y - half
    max_x, max_y = center_x + half, center_y + half
    for layer_i in range(args.layers):
        z = (layer_i + 1) * args.layer_height
        writer.layer_change(z)
        writer.move(min_x, min_y, z, feed=args.feed_travel, comment='travel')
        for corner_x, corner_y in ((max_x, min_y), (max_x, max_y),
                                   (min_x, max_y), (min_x, min_y)):
            writer.move(corner_x, corner_y, feed=args.feed_print, extrude=True)


def generate_cylinder(writer, args):
    """N 층 원주. 현 하나가 seg_len 이라 짧은 세그먼트가 수천 개 나온다."""
    center_x, center_y = args.center
    circumference = 2.0 * math.pi * args.radius
    seg_count = max(3, round(circumference / args.seg_len))
    for layer_i in range(args.layers):
        z = (layer_i + 1) * args.layer_height
        writer.layer_change(z)
        writer.move(center_x + args.radius, center_y, z,
                    feed=args.feed_travel, comment='travel')
        for seg_i in range(1, seg_count + 1):
            angle = 2.0 * math.pi * seg_i / seg_count
            writer.move(center_x + args.radius * math.cos(angle),
                        center_y + args.radius * math.sin(angle),
                        feed=args.feed_print, extrude=True)


# torture 는 궤적이 아니라 방언을 겨눈다. GcodeWriter 를 거치지 않고 직접 적는다 —
# G92 가 좌표계를 옮기므로 writer 의 추적값과 실제가 갈라지고, 통계도 무의미하다.
TORTURE = """\
; make_test_gcode.py torture
; 파서 방언 검증용 픽스처. 궤적 자체는 의미 없다.
M104 S210          ; 무시 — 온도
M140 S60           ; 무시
G21                ; mm
G90                ; 절대좌표
M82                ; 절대 E
G28                ; 홈
T0                 ; 무시 — 툴 선택
M106 S255          ; 무시 — 팬
;LAYER_CHANGE
;Z:0.2
;WIDTH:0.42
G92 E0             ; E 리셋
G0 F9000 X100 Y100 Z0.2   ; travel. E 가 없다
G1 F1800 X120 Y100 E0.698 ; 압출 시작. F 설정
G1 X120 Y120 E1.396       ; F 생략 — 직전 1800 이 유지되어야 한다
g1 x100 y120 e2.094 ; 소문자 + 코드 뒤 인라인 주석
G92 X0 Y0          ; 좌표계 리셋 — 이후 X0 Y0 는 (100,120) 이다
G1 X20 E2.792      ; 실제로는 X120. G92 를 무시하면 X20 으로 튄다
M83                ; 상대 E 로 전환
G1 X0 Y20 E0.698   ; 이후 E 는 증분값
;WIDTH:0.6
G1 X-20 E0.698     ; 실제 X100 Y140
G2 X0 Y0 I10 J0    ; 미지원 — 경고 후 무시되어야 한다
M600               ; 무시
G0 F9000 Z5
; done
"""

TORTURE_G91 = """\
; make_test_gcode.py torture --dialect g91
; G91 거부 경로 검증용. 파서는 이 파일을 읽다가 에러로 멈춰야 한다.
G21
G28
G91                ; 상대좌표 — 04b 는 이를 거부하기로 정했다
G1 F1800 X10 Y10 E0.349
G1 X10 E0.698
"""


def strip_out_option(argv):
    """헤더에 남길 인자에서 -o 를 뺀다.

    출력 위치만 다르다고 내용이 달라지면 회귀 픽스처로 쓸 수 없다
    (같은 인자 -> 같은 바이트).
    """
    kept = []
    skip_next = False
    for token in argv:
        if skip_next:
            skip_next = False
            continue
        if token in ('-o', '--out'):
            skip_next = True
            continue
        if token.startswith('--out='):
            continue
        kept.append(token)
    return kept


def print_answer_key(writer, shape):
    """motion.py 의 50Hz 샘플을 맞춰볼 대상. stdout 을 더럽히지 않게 stderr 로."""
    def say(text):
        print(text, file=sys.stderr)

    say(f'{shape}: layers={writer.layer_count} moves={writer.move_count} '
        f'extrude_seg={writer.extrude_count}')
    say(f'  path={writer.path_mm:.1f}mm (extrude {writer.extrude_path_mm:.1f}mm) '
        f'filament={writer.filament_per_mm * writer.extrude_path_mm:.1f}mm')
    say(f'  duration={writer.duration_s:.2f}s (extrude {writer.extrude_duration_s:.2f}s) '
        f'-> 50Hz {round(writer.duration_s * 50)} ticks')
    if writer.extrude_count:
        say(f'  mean_seg={writer.extrude_path_mm / writer.extrude_count:.3f}mm')
    if writer.out_of_bed:
        say(f'  WARN  0~{format_num(BED_LIMIT_MM)}mm 범위를 벗어난 좌표가 있다 (stroke 초과)')


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    parser = argparse.ArgumentParser(
        description=__doc__.split('\n')[3],
        formatter_class=argparse.ArgumentDefaultsHelpFormatter)
    parser.add_argument('shape', choices=('square', 'cylinder', 'torture'))
    parser.add_argument('-o', '--out', help='출력 파일 (기본 stdout)')
    parser.add_argument('--layers', type=int, default=20)
    parser.add_argument('--layer-height', type=float, default=0.2)
    parser.add_argument('--width', type=float, default=0.42, help='선폭 [mm]')
    parser.add_argument('--size', type=float, default=200.0, help='square 한 변 [mm]')
    parser.add_argument('--radius', type=float, default=40.0, help='cylinder 반지름 [mm]')
    parser.add_argument('--seg-len', type=float, default=0.2,
                        help='cylinder 현 길이 [mm]. 짧을수록 세그먼트가 많아진다')
    parser.add_argument('--center', type=float, nargs=2, default=[125.0, 125.0],
                        metavar=('X', 'Y'), help='베드 중앙 [mm]')
    parser.add_argument('--feed-print', type=float, default=3600.0, help='압출 이송 [mm/min]')
    parser.add_argument('--feed-travel', type=float, default=9000.0, help='이동 이송 [mm/min]')
    parser.add_argument('--filament', type=float, default=FILAMENT_DIAMETER_MM,
                        help='필라멘트 지름 [mm]')
    parser.add_argument('--dialect', choices=('g90', 'g91'), default='g90',
                        help='torture 전용. g91 은 파서가 거부해야 하는 입력이다')
    args = parser.parse_args(argv)

    out = open(args.out, 'w') if args.out else sys.stdout
    try:
        if args.shape == 'torture':
            out.write(TORTURE_G91 if args.dialect == 'g91' else TORTURE)
            print(f'torture ({args.dialect}) — 방언 픽스처. 통계 없음.', file=sys.stderr)
            return 0

        writer = GcodeWriter(out, args.width, args.layer_height, args.filament)
        writer.header(strip_out_option(argv))
        generate = generate_square if args.shape == 'square' else generate_cylinder
        generate(writer, args)
        writer.footer()
    finally:
        if args.out:
            out.close()

    print_answer_key(writer, args.shape)
    return 0


if __name__ == '__main__':
    sys.exit(main())
