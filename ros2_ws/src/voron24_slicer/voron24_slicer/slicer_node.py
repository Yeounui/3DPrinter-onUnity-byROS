#!/usr/bin/env python3
"""
slicer_node.py
==============
STL -> G-code 슬라이싱 액션 서버 (docs/04b_gcode_pipeline.md §6).

    ros2 run voron24_slicer slicer_node
    ros2 action send_goal /slice_model voron24_slicer_msgs/action/SliceModel \\
      "{stl_path: '/abs/path/model.stl', profile: 'voron24_250'}" --feedback

    Unity/CLI ──▶ SliceModel action ──▶ slicer_node ──▶ (result: gcode 경로)
                                            │                    │
                                       subprocess            load_gcode
                                       PrusaSlicer               ▼
                                                        gcode_player_node

**액션이지 서비스가 아니다.** 슬라이싱은 수십 초 걸리고 도중 취소가 가능해야 한다
(04b §274).

**libslic3r 을 링크하지 않고 subprocess 로 CLI 를 부른다.** AGPL-3.0 이라 링크하면
라이선스가 전파된다. 별도 프로세스 exec 은 해당하지 않는다 (04b §277). 덕분에 런타임
의존성이 순수 파이썬 + 외부 실행파일 하나로 유지되고 Boost/TBB/CGAL/OpenVDB 가 colcon
에 얹히지 않는다.

**슬라이서가 없어도 노드는 뜬다.** 기동 시 경고만 남기고, goal 이 들어오면
success=false + 설치 안내 문구를 result 로 돌려준다. launch 전체를 죽이지 않는 것이
이 레포의 태도다 (sim.launch.py 모듈 docstring).

단위 — 여기서는 아무것도 변환하지 않는다. 슬라이서 출력은 mm 절대좌표이고 mm->m 은
gcode_player_node 가 publish 직전에 한 번만 한다 (계약 §2). 중복 변환 주의.
"""
import os
import queue
import subprocess
import tempfile
import threading

import rclpy
from rclpy.action import ActionServer, CancelResponse, GoalResponse
from rclpy.callback_groups import ReentrantCallbackGroup
from rclpy.executors import MultiThreadedExecutor
from rclpy.node import Node

from voron24_slicer_msgs.action import SliceModel
from voron24_slicer.profiles import (DEFAULT_PROFILE, find_slicer,
                                     missing_slicer_message, profile_dirs,
                                     resolve_profile)

ACTION_NAME = 'slice_model'

# PrusaSlicer 콘솔이 뱉는 `=> <단계>` 표시. 진행률 자체는 stdout 에 안 나오므로
# 단계 도달을 진행률로 환산한다. 문구가 버전마다 조금씩 다르므로 부분일치로 본다.
# 못 알아본 줄은 stage 만 갱신하고 progress 는 유지한다 — 뒤로 가는 것보다 낫다.
STAGES = (
    ('processing triangulated mesh', 0.10),
    ('generating perimeters',        0.25),
    ('preparing infill',             0.40),
    ('generating infill',            0.50),
    ('infilling layers',             0.55),
    ('generating skirt',             0.70),
    ('generating support',           0.75),
    ('estimating',                   0.85),
    ('exporting g-code',             0.92),
    ('slicing finished',             0.99),
)

FEEDBACK_MIN_DELTA = 0.01       # 같은 값을 계속 쏘지 않기 위한 문턱
POLL_S = 0.2                    # 취소 플래그 확인 주기. MoveStream.POLL_S 와 같은 값


class _LineReader:
    """subprocess 의 stdout 을 별도 스레드로 읽어 큐에 넣는다.

    `readline()` 이 블로킹이라 그대로 두면 취소 요청을 볼 틈이 없다. 파일 I/O 를
    타이머/콜백에서 떼어내는 gcode_player_node.MoveStream 과 같은 이유·같은 모양.
    """

    def __init__(self, stream):
        self._queue = queue.Queue()
        self._stream = stream
        self._thread = threading.Thread(target=self._read, name='slicer_stdout',
                                        daemon=True)
        self._thread.start()

    def _read(self):
        try:
            for line in self._stream:
                self._queue.put(line.rstrip('\n'))
        except (OSError, ValueError):
            pass
        finally:
            self._queue.put(None)       # 종료 sentinel

    def poll(self, timeout=POLL_S):
        """다음 줄. 타임아웃이면 '' , 스트림 끝이면 None."""
        try:
            return self._queue.get(timeout=timeout)
        except queue.Empty:
            return ''


class SlicerNode(Node):

    def __init__(self):
        super().__init__('voron24_slicer')

        # 실행파일 경로를 하드코딩하지 않는다 (04b §281).
        self.declare_parameter('slicer_executable', '')
        self.declare_parameter('profile_dir', '')
        self.declare_parameter('default_profile', DEFAULT_PROFILE)
        # 산출물 위치. 기본은 시스템 임시 디렉터리 밑 — G-code 는 빌드 산출물이고
        # `*.gcode` 가 LFS 대상이라 레포 트리에 떨구면 실수로 커밋될 때 무겁다.
        self.declare_parameter('output_dir', '')
        # 베드 배치를 스크립트에서 통제 (04b §292). --center 와 --dont-arrange 는
        # 같이 주면 --dont-arrange 가 이겨 STL 원본 XY 가 그대로 쓰이므로 배타로 둔다.
        # 기본은 중앙 정렬 — 임의의 STL 이 베드 밖에 놓이는 쪽이 더 흔한 사고다.
        self.declare_parameter('center', '125,125')     # 250x250 베드의 한가운데
        self.declare_parameter('dont_arrange', False)
        self.declare_parameter('extra_args', [''])      # 탈출구. 빈 문자열은 무시
        self.declare_parameter('timeout_s', 900.0)

        self._proc = None                # 실행 중인 슬라이서. 취소가 여기로 신호를 보냄
        self._busy = threading.Lock()    # 동시 goal 방지

        self._server = ActionServer(
            self, SliceModel, ACTION_NAME,
            execute_callback=self.execute,
            goal_callback=self.on_goal,
            cancel_callback=self.on_cancel,
            # execute 가 몇십 초 블로킹하는 동안에도 취소 콜백이 돌아야 한다.
            callback_group=ReentrantCallbackGroup())

        slicer = find_slicer(self.get_parameter('slicer_executable').value)
        if slicer is None:
            self.get_logger().warn(missing_slicer_message())
        else:
            self.get_logger().info(f'슬라이서: {slicer}')
        self.get_logger().info(f'프로파일 탐색 경로: '
                               f'{profile_dirs(self.get_parameter("profile_dir").value)}')
        self.get_logger().info(f'액션 대기: /{ACTION_NAME}')

    # ------------------------------------------------------------------
    def on_goal(self, goal_request):
        """동시 슬라이싱은 거부. CPU 를 나눠 쓰면 둘 다 느려지고 진행률이 뒤섞인다."""
        if self._busy.locked():
            self.get_logger().warn('이미 슬라이싱 중 — goal 거부')
            return GoalResponse.REJECT
        return GoalResponse.ACCEPT

    def on_cancel(self, goal_handle):
        proc = self._proc
        if proc is not None and proc.poll() is None:
            self.get_logger().info('취소 요청 — 슬라이서 프로세스 종료')
            proc.terminate()
        return CancelResponse.ACCEPT

    # ------------------------------------------------------------------
    def execute(self, goal_handle):
        with self._busy:
            return self._execute(goal_handle)

    def _execute(self, goal_handle):
        result = SliceModel.Result()
        result.success = False
        result.gcode_path = ''

        cmd, out_path, err = self._build_command(goal_handle.request)
        if err is not None:
            goal_handle.abort()
            result.message = err
            self.get_logger().error(err)
            return result

        self.get_logger().info('실행: ' + ' '.join(cmd))
        self._publish(goal_handle, 0.02, '슬라이서 기동')

        try:
            # stderr 를 stdout 으로 합친다. PrusaSlicer 는 단계 표시를 양쪽에 섞어
            # 뱉는 버전이 있어 한 스트림으로 봐야 진행률이 끊기지 않는다.
            self._proc = subprocess.Popen(
                cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, bufsize=1)
        except OSError as exc:
            goal_handle.abort()
            result.message = f'슬라이서 실행 실패: {exc}'
            self.get_logger().error(result.message)
            return result

        tail, canceled = self._pump(goal_handle, self._proc)
        code = self._proc.wait()
        self._proc = None

        if canceled:
            goal_handle.canceled()
            result.message = '취소됨'
            return result

        if code != 0:
            goal_handle.abort()
            result.message = (f'슬라이서가 exit {code} 로 종료. '
                              f'마지막 출력: {tail or "(없음)"}')
            self.get_logger().error(result.message)
            return result

        if not os.path.isfile(out_path):
            goal_handle.abort()
            result.message = (f'슬라이서는 성공했는데 산출물이 없음: {out_path}. '
                              f'마지막 출력: {tail or "(없음)"}')
            self.get_logger().error(result.message)
            return result

        self._publish(goal_handle, 1.0, '완료')
        goal_handle.succeed()
        result.success = True
        result.gcode_path = out_path
        result.message = f'{os.path.getsize(out_path)} bytes -> {out_path}'
        self.get_logger().info('슬라이싱 완료: ' + result.message)
        return result

    # ------------------------------------------------------------------
    def _build_command(self, request):
        """(cmd, 산출물경로, 에러문구). 에러문구가 None 이 아니면 나머지는 무의미."""
        slicer = find_slicer(self.get_parameter('slicer_executable').value)
        if slicer is None:
            return None, None, missing_slicer_message()

        stl = os.path.abspath(os.path.expanduser(request.stl_path or ''))
        if not request.stl_path:
            return None, None, 'goal 에 stl_path 가 없음'
        if not os.path.isfile(stl):
            return None, None, f'STL 이 없음: {stl}'

        name = request.profile or self.get_parameter('default_profile').value
        ini = resolve_profile(name, self.get_parameter('profile_dir').value)
        if ini is None:
            return None, None, (f'프로파일 {name!r} 을 못 찾음. 탐색 경로: '
                                f'{profile_dirs(self.get_parameter("profile_dir").value)} '
                                f'(04b §294 — tools/slicer/voron24_250.ini)')

        out_dir = self.get_parameter('output_dir').value or os.path.join(
            tempfile.gettempdir(), 'voron24_slicer')
        try:
            os.makedirs(out_dir, exist_ok=True)
        except OSError as exc:
            return None, None, f'출력 디렉터리를 못 만듦: {out_dir} ({exc})'
        stem = os.path.splitext(os.path.basename(stl))[0]
        # 같은 STL 을 다시 슬라이싱하면 덮어쓴다. 임시 파일이 쌓이는 편이 나쁘고,
        # 동시 goal 은 on_goal 에서 이미 막았으므로 충돌하지 않는다.
        out_path = os.path.join(out_dir, f'{stem}.gcode')

        cmd = [slicer, '--export-gcode', '--load', ini, '-o', out_path]
        if self.get_parameter('dont_arrange').value:
            cmd.append('--dont-arrange')
        else:
            center = (self.get_parameter('center').value or '').strip()
            if center:
                cmd += ['--center', center]
        cmd += [a for a in (self.get_parameter('extra_args').value or []) if a]
        cmd.append(stl)
        return cmd, out_path, None

    def _pump(self, goal_handle, proc):
        """stdout 을 흘려 보내며 feedback 을 쏘고 취소를 감시. (마지막 줄, 취소됨)."""
        reader = _LineReader(proc.stdout)
        progress = 0.02
        last_sent = 0.0
        stage = '슬라이싱'
        tail = ''
        deadline = self.get_clock().now().nanoseconds * 1e-9 + \
            float(self.get_parameter('timeout_s').value)

        while True:
            if goal_handle.is_cancel_requested:
                if proc.poll() is None:
                    proc.terminate()
                    try:
                        proc.wait(timeout=5.0)
                    except subprocess.TimeoutExpired:
                        proc.kill()
                return tail, True

            if self.get_clock().now().nanoseconds * 1e-9 > deadline:
                self.get_logger().error('timeout_s 초과 — 슬라이서 강제 종료')
                proc.kill()
                return tail + ' (timeout)', False

            line = reader.poll()
            if line is None:
                break                       # 스트림 끝. 종료 코드는 호출자가 본다
            if not line:
                continue                    # 타임아웃 틱 — 위의 취소 검사가 목적
            tail = line
            self.get_logger().debug(line)
            lower = line.lower()
            for needle, value in STAGES:
                if needle in lower:
                    progress = max(progress, value)
                    stage = line.lstrip('=> ').strip() or stage
                    break
            if progress - last_sent >= FEEDBACK_MIN_DELTA:
                self._publish(goal_handle, progress, stage)
                last_sent = progress
        return tail, False

    def _publish(self, goal_handle, progress, stage):
        feedback = SliceModel.Feedback()
        feedback.progress = float(progress)
        feedback.stage = stage
        goal_handle.publish_feedback(feedback)


def main(args=None):
    rclpy.init(args=args)
    node = SlicerNode()
    # execute 가 블로킹하는 동안 취소 콜백이 돌아야 하므로 단일 스레드로는 안 된다.
    executor = MultiThreadedExecutor()
    try:
        rclpy.spin(node, executor=executor)
    except KeyboardInterrupt:
        pass
    finally:
        node.destroy_node()
        if rclpy.ok():
            rclpy.shutdown()


if __name__ == '__main__':
    main()
