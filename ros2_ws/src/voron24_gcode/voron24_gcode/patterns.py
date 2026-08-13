"""
patterns.py
===========
mock 퍼블리셔의 궤적 생성 로직. rclpy 의존성이 없음.

    python3 -m voron24_gcode.patterns        # 자체 검증 실행

모든 좌표는 metre, bed_origin 기준 (= G-code 좌표 / 1000).
"""
import math

PATTERNS = ('home', 'sweep', 'square', 'lissajous', 'none')


class PatternGenerator:

    def __init__(self, stroke=(0.250, 0.250, 0.250), margin=0.010, period=12.0):
        self.sx, self.sy, self.sz = stroke
        self.margin = margin
        self.period = period
        self.layer = 0

    # ------------------------------------------------------------------
    def __call__(self, pattern, t):
        """-> (x, y, z, extruding(bool)). 리밋 클램프까지 마친 값."""
        fn = getattr(self, '_' + pattern, None)
        if fn is None:
            raise ValueError(f'unknown pattern: {pattern} (choose from {PATTERNS})')
        x, y, z, ext = fn(t)
        return (
            min(max(x, 0.0), self.sx),
            min(max(y, 0.0), self.sy),
            min(max(z, 0.0), self.sz),
            ext,
        )

    # ------------------------------------------------------------------
    def _home(self, t):
        """전부 0. 홈 자세에서 노즐이 베드 좌전방 코너에 있는지 확인용."""
        return 0.0, 0.0, 0.0, False

    def _none(self, t):
        """원점 고정, 압출 없음. 동작 없이 초기화만.

        `_home` 과 반환값은 같지만 다른 의도에 의해 작성되었으므로 통합하지 말 것.
        `_home` 은 "홈 위치 표시"를 위해서 `_none` 은 "이동 없이 초기화".
        sim.launch.py 에서 값 소스가 manual_publisher / gcode_player_node 로
        넘어갈 때 mock 퍼블리셔를 무해하게 만드는 용도다 (04b 선행 커밋).
        """
        return 0.0, 0.0, 0.0, False

    def _sweep(self, t):
        """축을 하나씩 왕복. 축 매핑과 부호 검증용. 처음엔 이걸로 확인할 것."""
        m = self.margin
        seg = self.period / 3.0
        phase = (t % self.period) / seg
        k = int(phase)
        u = phase - k
        tri = 1.0 - abs(2.0 * u - 1.0)          # 0 -> 1 -> 0
        if k == 0:
            return m + (self.sx - 2 * m) * tri, m, 0.0, False
        if k == 1:
            return m, m + (self.sy - 2 * m) * tri, 0.0, False
        return m, m, self.sz * tri, False

    def _square(self, t):
        """베드 외곽 사각형. 스트로크 리밋과 원점 확인용."""
        m = self.margin
        u = (t % self.period) / self.period * 4.0
        k = int(u)
        v = u - k
        x0, y0 = m, m
        x1, y1 = self.sx - m, self.sy - m
        if k == 0:
            x, y = x0 + (x1 - x0) * v, y0
        elif k == 1:
            x, y = x1, y0 + (y1 - y0) * v
        elif k == 2:
            x, y = x1 - (x1 - x0) * v, y1
        else:
            x, y = x0, y1 - (y1 - y0) * v
        self.layer = int(t / self.period)
        return x, y, 0.0002 + self.layer * 0.0002, True

    def _lissajous(self, t):
        """기본. X/Y 리사주 + Z 완만한 상승. 궤적이 겹치지 않아 시각적으로 보기 좋다."""
        m = self.margin
        ax, ay = self.sx - 2 * m, self.sy - 2 * m
        w = 2.0 * math.pi / self.period
        x = self.sx / 2.0 + (ax / 2.0) * math.sin(w * t)
        y = self.sy / 2.0 + (ay / 2.0) * math.sin(w * t * 0.618)
        z = 0.010 + 0.010 * (1.0 + math.sin(w * t * 0.13))
        self.layer = int(t / self.period)
        return x, y, z, True


# ----------------------------------------------------------------------
def _self_test():
    ok = True
    for pat in PATTERNS:
        g = PatternGenerator()
        lo = [1e9] * 3
        hi = [-1e9] * 3
        prev = None
        max_jump = 0.0
        for i in range(0, 4000):
            t = i * 0.02
            x, y, z, _ = g(pat, t)
            for j, v in enumerate((x, y, z)):
                lo[j] = min(lo[j], v)
                hi[j] = max(hi[j], v)
            if prev:
                jump = max(abs(x - prev[0]), abs(y - prev[1]), abs(z - prev[2]))
                max_jump = max(max_jump, jump)
            prev = (x, y, z)

        in_range = all(0.0 <= lo[j] and hi[j] <= 0.250 + 1e-9 for j in range(3))
        smooth = max_jump < 0.010          # 20ms 안에 10mm 넘게 튀면 안 됨
        status = 'OK ' if (in_range and smooth) else 'FAIL'
        if not (in_range and smooth):
            ok = False
        print(f'[{status}] {pat:10s} '
              f'x[{lo[0]:.3f},{hi[0]:.3f}] y[{lo[1]:.3f},{hi[1]:.3f}] z[{lo[2]:.3f},{hi[2]:.3f}] '
              f'max_step={max_jump*1000:.2f}mm')
    print('\nALL PASS' if ok else '\nFAILED')
    return ok


if __name__ == '__main__':
    import sys
    sys.exit(0 if _self_test() else 1)
