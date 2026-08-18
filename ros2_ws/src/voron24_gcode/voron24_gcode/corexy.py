"""CoreXY 변환.

URDF 는 폐루프 표현 불가하므로 X/Y 를 독립 prismatic 으로 모델링.
모터 풀리 회전 시각화 시에만 이 변환으로 별도 토픽 퍼블리시.
기구학 자체와는 무관.
"""
import math

BELT_PITCH_MM = 2.0        # GT2
PULLEY_TEETH = 20
MM_PER_REV = BELT_PITCH_MM * PULLEY_TEETH      # 40 mm


def xy_to_ab(x_mm, y_mm):
    """카티전 → A/B 모터 이동량 [mm]"""
    return x_mm + y_mm, x_mm - y_mm


def ab_to_xy(a_mm, b_mm):
    """A/B 모터 이동량 → 카티전 [mm]"""
    return 0.5 * (a_mm + b_mm), 0.5 * (a_mm - b_mm)


def mm_to_rad(mm):
    return mm * (2.0 * math.pi / MM_PER_REV)


def xy_to_motor_angles(x_mm, y_mm):
    """카티전 [mm] → (theta_a, theta_b) [rad]"""
    a, b = xy_to_ab(x_mm, y_mm)
    return mm_to_rad(a), mm_to_rad(b)


if __name__ == '__main__':
    for x, y in [(0, 0), (125, 125), (250, 0), (0, 250)]:
        a, b = xy_to_ab(x, y)
        ta, tb = xy_to_motor_angles(x, y)
        rx, ry = ab_to_xy(a, b)
        print(f'X{x:6.1f} Y{y:6.1f} -> A{a:7.1f} B{b:7.1f} mm | '
              f'theta {math.degrees(ta):8.1f} {math.degrees(tb):8.1f} deg | '
              f'inv ({rx:.1f},{ry:.1f})')
