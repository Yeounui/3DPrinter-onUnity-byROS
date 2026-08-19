#!/usr/bin/env python3
"""
topic_probe.py
==============
"Unity 문제인가 ROS 문제인가" 단번 판별 진단기. 실행 중인 launch 옆 터미널에서.

    /usr/bin/python3 tools/topic_probe.py             # 3초 관찰
    /usr/bin/python3 tools/topic_probe.py 10          # 10초
    ROS_DOMAIN_ID=42 /usr/bin/python3 tools/topic_probe.py    # 타 도메인 확인

`ros2 topic echo` 대신 사용하는 이유 둘:

1. conda/venv python3 선행 시 `ros2` CLI 멈춤 (README '확인' 절).
   `/usr/bin/python3` 직접 호출로 우회 가능.
2. echo 는 **발행자 수** 미표시. 이 레포 최빈 고장이
   "이전 실행 고아 노드가 같은 토픽에 동시 퍼블리시" 이고 값이 아닌 발행자
   수 확인으로 검출.

판독:

    발행자 0개   → 값 소스 없음. launch 미실행 또는 ROS_DOMAIN_ID 불일치
    발행자 2개+  → 고아 노드. ps -ef | grep voron24_ 후 pkill -f voron24_
    안 움직임     → 상태 노드 manual 모드. load_gcode 미도달
    움직임        → ROS 정상. 이후 Unity/엔드포인트 문제
"""
import sys
import time

import rclpy
from rclpy.node import Node
from sensor_msgs.msg import JointState

TOPICS = ('/joint_states', '/printer/target')


class Probe(Node):

    def __init__(self):
        super().__init__('voron24_topic_probe')
        self.seen = {t: [] for t in TOPICS}
        for topic in TOPICS:
            self.create_subscription(
                JointState, topic,
                lambda msg, t=topic: self.seen[t].append(tuple(msg.position)), 200)


def report(probe, topic, duration):
    publishers = [i.node_name for i in probe.get_publishers_info_by_topic(topic)]
    samples = probe.seen[topic]
    print(f'\n{topic}')
    print(f'  발행자 {len(publishers)} 개: {", ".join(publishers) or "(없음)"}')
    if len(publishers) > 1:
        print('  ** 발행자 2개 이상 — 두 흐름 번갈아 도착, 포즈 왕복. '
              '대개 이전 실행 고아 노드 (ps -ef | grep voron24_)')
    print(f'  {duration:g}초 {len(samples)}개 '
          f'({len(samples) / duration:.0f}Hz)')
    if not samples:
        return
    span = [max(p[i] for p in samples) - min(p[i] for p in samples) for i in range(3)]
    moved = max(span) > 1e-6
    first = tuple(round(v, 4) for v in samples[0])
    last = tuple(round(v, 4) for v in samples[-1])
    print(f'  {first} → {last}  이동폭 '
          f'({span[0] * 1000:.1f}, {span[1] * 1000:.1f}, {span[2] * 1000:.1f})mm')
    if not moved:
        print('  ** 값 고정 — 좌표 변동 없음. /joint_states 0 고정 시 '
              '상태 노드 manual 모드 (load_gcode 미도달)')


def main():
    duration = float(sys.argv[1]) if len(sys.argv) > 1 else 3.0
    rclpy.init()
    probe = Probe()
    import os
    print(f'ROS_DOMAIN_ID={os.environ.get("ROS_DOMAIN_ID", "0 (미설정)")} '
          f'— {duration:g}초 관찰')
    end = time.time() + duration
    while time.time() < end:
        rclpy.spin_once(probe, timeout_sec=0.05)
    for topic in TOPICS:
        report(probe, topic, duration)
    probe.destroy_node()
    if rclpy.ok():
        rclpy.shutdown()
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
