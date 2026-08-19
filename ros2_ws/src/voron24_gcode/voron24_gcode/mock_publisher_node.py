#!/usr/bin/env python3
"""
mock_publisher_node.py
======================
계약(00_interface_contract.md section 5) 검증용 더미 퍼블리셔.

A 메시도, 실제 G-code 도 없이 B 가 Unity 작업 시작 가능하게 함.
W1 게이트: 이 노드 띄웠을 때 RViz 와 Unity 에서 동시에 같은 움직임 보여야 함.

퍼블리시:
    /joint_states       sensor_msgs/JointState   50 Hz
    /printer/status     voron24_msgs/PrinterStatus  5 Hz  (msgs 빌드된 경우만)
    /printer/extrusion  voron24_msgs/ExtrusionPoint 이벤트

패턴:
    lissajous  기본. X/Y 리사주 + Z 완만한 상승. 축 방향 확인용.
    sweep      한 축씩 0 -> stroke -> 0. 축 매핑과 부호 검증용. ★ 처음엔 이걸로.
    square     베드 외곽 사각형. 스트로크 리밋과 좌표 원점 확인용.
    home       전부 0 고정. 홈 자세에서 노즐이 베드 좌전방 코너에 있는지 확인.
    none       원점 고정, 압출 없음. 값 소스가 이 노드가 아닐 때 (sim.launch.py).

사용:
    ros2 run voron24_gcode mock_publisher
    ros2 run voron24_gcode mock_publisher --ros-args -p pattern:=sweep
    ros2 run voron24_gcode mock_publisher --ros-args -p pattern:=home
    ros2 run voron24_gcode mock_publisher --ros-args -p rate:=100.0 -p period:=8.0
"""
import math

import rclpy
from rclpy.node import Node
from sensor_msgs.msg import JointState

# voron24_msgs 빌드 미포함이어도 joint_states 만으로 동작 가능.
from voron24_gcode.patterns import PatternGenerator, PATTERNS

try:
    from voron24_msgs.msg import PrinterStatus, ExtrusionPoint
    from geometry_msgs.msg import Point
    HAS_MSGS = True
except ImportError:  # pragma: no cover
    HAS_MSGS = False

JOINT_NAMES = ['joint_x', 'joint_y', 'joint_z']   # 계약 section 3. 변경 금지.


class MockPublisher(Node):

    def __init__(self):
        super().__init__('mock_publisher') # Node 이름 mock_publisher 정의.

        self.declare_parameter('pattern', 'lissajous')
        self.declare_parameter('rate', 50.0)
        self.declare_parameter('period', 12.0)      # 사이클 주기 [s]
        self.declare_parameter('stroke_x', 0.250)
        self.declare_parameter('stroke_y', 0.250)
        self.declare_parameter('stroke_z', 0.250)
        self.declare_parameter('margin', 0.010)     # 리밋 여유 [m]
        self.declare_parameter('publish_status', True)
        self.declare_parameter('publish_extrusion', True)

        self.pattern = self.get_parameter('pattern').value
        self.rate = float(self.get_parameter('rate').value)
        self.period = float(self.get_parameter('period').value)
        self.sx = float(self.get_parameter('stroke_x').value)
        self.sy = float(self.get_parameter('stroke_y').value)
        self.sz = float(self.get_parameter('stroke_z').value)
        self.margin = float(self.get_parameter('margin').value)

        if self.pattern not in PATTERNS:
            raise ValueError(f'pattern must be one of {PATTERNS}, got {self.pattern!r}')

        self.gen = PatternGenerator(stroke=(self.sx, self.sy, self.sz),
                                    margin=self.margin, period=self.period)
        self.dt = 1.0 / self.rate
        self.t = 0.0
        self.layer = 0
        self._prev_xyz = (0.0, 0.0, 0.0)

        self.js_pub = self.create_publisher(JointState, '/joint_states', 10)
        # JointState: 데이터 형식. /joint_states: 절대 토픽명.
        # 10: QoS queue depth. 수신 지연 시 최근 10개 보관, 초과분 폐기.
        self.create_timer(self.dt, self.tick)

        self.st_pub = None
        self.ex_pub = None
        if HAS_MSGS:
            if self.get_parameter('publish_status').value:
                self.st_pub = self.create_publisher(PrinterStatus, '/printer/status', 10)
                self.create_timer(0.2, self.publish_status)
                # 0.2초마다 publish_status() 실행.
            if self.get_parameter('publish_extrusion').value:
                self.ex_pub = self.create_publisher(ExtrusionPoint, '/printer/extrusion', 200)
        else:
            self.get_logger().warn(
                'Cannot find voron24_msgs. Publish /joint_status only. '
                '(source ros2_ws/install/setup.bash after colcon build --packages-select voron24_msgs)')

        self.get_logger().info(
            f'mock publisher | pattern={self.pattern} rate={self.rate}Hz '
            f'period={self.period}s stroke=({self.sx},{self.sy},{self.sz})')
        self.get_logger().info(f'joint names = {JOINT_NAMES}')

    # ------------------------------------------------------------------
    def compute(self, t):
        """Delegate trajectory generation to patterns.py (Enable standalone test without ROS)."""
        x, y, z, ext = self.gen(self.pattern, t)
        self.layer = self.gen.layer
        return x, y, z, ext

    # ------------------------------------------------------------------
    def tick(self):
        self.t += self.dt
        x, y, z, extruding = self.compute(self.t)

        # msg outputs.
        msg = JointState()
        msg.header.stamp = self.get_clock().now().to_msg()
        msg.name = list(JOINT_NAMES)
        msg.position = [x, y, z]
        px, py, pz = self._prev_xyz
        msg.velocity = [(x - px) / self.dt, (y - py) / self.dt, (z - pz) / self.dt] # delta value of x,y,z coord.
        self.js_pub.publish(msg)

        if self.ex_pub is not None and extruding:
            ep = ExtrusionPoint()
            ep.header.stamp = msg.header.stamp
            ep.header.frame_id = 'bed_origin'
            ep.position = Point(x=x, y=y, z=z)
            ep.width = 0.00042
            ep.height = 0.00020
            ep.extruding = True
            ep.layer = int(self.layer)
            self.ex_pub.publish(ep)

        self._prev_xyz = (x, y, z)

    def publish_status(self):
        s = PrinterStatus()
        s.header.stamp = self.get_clock().now().to_msg()
        s.state = 'printing' if self.pattern != 'home' else 'idle'
        s.progress = float((self.t / 60.0) % 1.0)
        s.current_layer = int(self.layer)
        s.total_layers = 200
        s.filename = f'mock_{self.pattern}.gcode'
        self.st_pub.publish(s)


def main(args=None):
    rclpy.init(args=args)
    node = MockPublisher()
    try:
        rclpy.spin(node)
    except KeyboardInterrupt:
        pass
    finally:
        node.destroy_node()
        if rclpy.ok():
            rclpy.shutdown()


if __name__ == '__main__':
    main()
