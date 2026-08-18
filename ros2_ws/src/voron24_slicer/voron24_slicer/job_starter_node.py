#!/usr/bin/env python3
"""
job_starter_node.py
===================
STL 한 장을 슬라이싱해 그 결과를 load_gcode 로 발행하고 죽는 oneshot 노드
(docs/04b_gcode_pipeline.md §248, docs/04_single_entrypoint.md §381).

    ros2 run voron24_slicer job_starter --ros-args \\
      -p stl_path:=/abs/path/model.stl -p cmd_topic:=/printer/cmd

sim.launch.py 의 source:=stl 이 file:= 자리에서 이 노드를 띄움. source:=gcode
쪽은 ros2 topic pub --once 한 줄이면 끝이지만 STL 은 "액션 send_goal → result 경로
재발행" 두 단계라 셸 파이프로 엮기 지저분함. 그래서 노드로 뺌.

**file:= 은 파라미터가 아니라 발행** 이라는 원칙 그대로 유지 (04b §235).
publish_command() 노드 대신 이 노드에서 값을 주입하여 gcode_player의 load_gcode로 주입.

    STL ──▶ SliceModel goal ──▶ slicer_node ──▶ result.gcode_path
                                                      │
                                        PrinterCommand{load_gcode} ──▶ gcode_player
"""
import os

import rclpy
from rclpy.action import ActionClient
from rclpy.node import Node

from voron24_msgs.msg import PrinterCommand
from voron24_slicer_msgs.action import SliceModel
from voron24_slicer.profiles import DEFAULT_PROFILE
from voron24_slicer.slicer_node import ACTION_NAME


class JobStarter(Node):

    def __init__(self):
        super().__init__('job_starter')
        self.declare_parameter('stl_path', '')
        self.declare_parameter('profile', DEFAULT_PROFILE)
        # 상태 노드(A) 가 없으면 sim.launch.py 가 /printer/cmd 대신 릴레이
        # 토픽을 넘김. 이 노드는 어느 쪽인지 몰라도 받은 대로 쏨.
        self.declare_parameter('cmd_topic', '/printer/cmd')
        self.declare_parameter('action_name', ACTION_NAME)
        # 슬라이서 액션 서버 기동 대기. launch 동시 기동이라 몇 초는 정상.
        self.declare_parameter('server_timeout_s', 30.0)
        # load_gcode 발행 전 구독 대기 시간. sim.launch.py gcode 분기에서
        # 3 초 쓰는 것과 같은 이유이며, 여기서는 실제 구독자 수를 볼 수 있으므로
        # 고정 대기 대신 폴링.
        self.declare_parameter('subscriber_timeout_s', 10.0)

        self._pub = self.create_publisher(PrinterCommand, self._param('cmd_topic'), 10)
        self._client = ActionClient(self, SliceModel, self._param('action_name'))
        self.failed = False

    def _param(self, name):
        return self.get_parameter(name).value

    # ------------------------------------------------------------------
    def run(self):
        """Overall sync-run. oneshot 이라 콜백 체인으로 흩을 이유 없음."""
        stl = self._param('stl_path')
        if not stl:
            return self._fail('stl_path is not given')
        stl = os.path.abspath(os.path.expanduser(stl))
        if not os.path.isfile(stl):
            return self._fail(f'No STL exists: {stl}')

        timeout = float(self._param('server_timeout_s'))
        self.get_logger().info(f'Slicer action ready: /{self._param("action_name")}')
        if not self._client.wait_for_server(timeout_sec=timeout):
            return self._fail(f'Live slicer action server not found in {timeout}s'
                              'Check voron24_slicer is built/installed.')

        goal = SliceModel.Goal()
        goal.stl_path = stl
        goal.profile = self._param('profile') or DEFAULT_PROFILE
        self.get_logger().info(f'Slicing initiated: {stl} (profile={goal.profile})')

        send = self._client.send_goal_async(goal, feedback_callback=self._on_feedback)
        rclpy.spin_until_future_complete(self, send)
        handle = send.result()
        if handle is None or not handle.accepted:
            return self._fail('Slicer refused the goal (or slicing already in progress)')

        get_result = handle.get_result_async()
        rclpy.spin_until_future_complete(self, get_result)
        wrapped = get_result.result()
        if wrapped is None:
            return self._fail('No slicing outcome exists')
        result = wrapped.result
        if not result.success:
            return self._fail(f'Slicing failed: {result.message}')

        self.get_logger().info(f'Slicing completed: {result.message}')
        self._send_load(result.gcode_path)

    # ------------------------------------------------------------------
    def _on_feedback(self, msg):
        fb = msg.feedback
        self.get_logger().info(f'  {fb.progress * 100:5.1f}%  {fb.stage}')

    def _send_load(self, gcode_path):
        """gcode_player 로 가는 유일한 진입점. sim.launch.py gcode 분기가 쏘는 것과
        완전히 같은 메시지 — 토폴로지 일치 지점."""
        topic = self._param('cmd_topic')
        deadline = float(self._param('subscriber_timeout_s'))
        waited = 0.0
        while self._pub.get_subscription_count() == 0 and waited < deadline:
            rclpy.spin_once(self, timeout_sec=0.2)
            waited += 0.2
        if self._pub.get_subscription_count() == 0:
            # 죽이지는 않음 — 그래도 한 발 쏘고 사용자에게 알림. sim.launch.py 가
            # file 없음을 경고만 하고 넘어가는 것과 같은 태도.
            self.get_logger().warn(f'No subscriber at {topic} — but publish continues')

        msg = PrinterCommand()
        msg.command = 'load_gcode'
        msg.payload = gcode_path
        self._pub.publish(msg)
        self.get_logger().info(f'{topic} <- load_gcode {gcode_path}')
        # 발행 직후 죽으면 DDS 가 아직 안 내보냈을 수 있음. 잠깐 더 돌림.
        for _ in range(10):
            rclpy.spin_once(self, timeout_sec=0.1)

    def _fail(self, message):
        self.failed = True
        self.get_logger().error(message)


def main(args=None):
    rclpy.init(args=args)
    node = JobStarter()
    try:
        node.run()
    except KeyboardInterrupt:
        pass
    finally:
        failed = node.failed
        node.destroy_node()
        if rclpy.ok():
            rclpy.shutdown()
    # oneshot 이라 여기서 끝. exit code 남기되 launch 에 on_exit 안 걸었으므로
    # 실패해도 나머지 그래프는 계속 돔.
    return 1 if failed else 0


if __name__ == '__main__':
    raise SystemExit(main())
