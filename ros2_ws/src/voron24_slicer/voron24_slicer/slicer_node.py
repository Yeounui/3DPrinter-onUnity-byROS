#!/usr/bin/env python3
"""
slicer_node.py
==============
STL -> G-code 슬라이싱 action server (docs/04b_gcode_pipeline.md §6).

    ros2 run voron24_slicer slicer_node
    ros2 action send_goal /slice_model voron24_slicer_msgs/action/SliceModel \\
      "{stl_path: '/abs/path/model.stl', profile: 'voron24_250'}" --feedback

    Unity/CLI ──▶ SliceModel action ──▶ slicer_node ──▶ (result: gcode 경로)
                                            │                    │
                                       subprocess            load_gcode
                                       PrusaSlicer               ▼
                                                        gcode_player_node

action 이지 service 가 아님. 슬라이싱은 수십 초 걸리고 도중 취소 가능해야 함 (04b §274).

libslic3r 링크 없이 subprocess 로 CLI 호출. AGPL-3.0 이라 링크 시 라이선스 전파됨.
별도 프로세스 exec 은 해당 없음 (04b §277). 런타임 의존성이 순수 파이썬 + 외부 실행파일
하나로 유지되고 Boost/TBB/CGAL/OpenVDB 가 colcon 에 안 얹힘.

슬라이서 없어도 노드는 뜸. 기동 시 경고만 남기고, goal 들어오면 success=false + 설치
안내 문구를 result 로 돌려줌. launch 전체를 죽이지 않는 게 이 레포의 태도
(sim.launch.py 모듈 docstring).

단위 — 여기서는 변환 없음. 슬라이서 출력은 mm 절대좌표이고 mm->m 은
gcode_player_node 가 publish 직전에 한 번만 함 (계약 §2). 중복 변환 주의.
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

# PrusaSlicer 콘솔의 `=> <단계>` 표시. 진행률 자체는 stdout 에 안 나오므로
# 단계 도달을 진행률로 환산. 버전마다 조금씩 다르기에 문구에 따라 부분일치로 봄.
# case 외 출력 라인은 stage 만 갱신하고 progress 유지.
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

FEEDBACK_MIN_DELTA = 0.01       # 같은 값 반복 방지 문턱
POLL_S = 0.2                    # 취소 플래그 확인 주기. MoveStream.POLL_S 와 동일


class _LineReader:
    """subprocess stdout 을 별도 스레드로 읽어 큐에 넣음.

    readline() 블로킹. 취소 신호가 스트림 내에 없기에 파일 I/O 를 타이머/콜백에서 분리해 작성.
    gcode_player_node.MoveStream 과 같은 이유·같은 모양.
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
            self._queue.put(None)       # 종료 sentinel 삽입

    def poll(self, timeout=POLL_S):
        """다음 줄 반환. 타임아웃이면 '', 스트림 끝이면 None."""
        try:
            return self._queue.get(timeout=timeout)
        except queue.Empty:
            return ''


class SlicerNode(Node):

    def __init__(self):
        super().__init__('voron24_slicer')

        # 실행파일 경로 하드코딩 금지 (04b §281).
        self.declare_parameter('slicer_executable', '')
        self.declare_parameter('profile_dir', '')
        self.declare_parameter('default_profile', DEFAULT_PROFILE)
        # 산출물 위치. 기본은 시스템 임시 디렉터리 — G-code 는 빌드 산출물
        # *.gcode은 LFS 대상이므로 레포 트리에 떨구면 커밋 실수 시 레포 무거워짐.
        self.declare_parameter('output_dir', '')
        # 베드 배치 스크립트 통제 (04b §292). 중앙 정렬이 기본값이지만 --center 와 --dont-arrange를 함께 인자로 줘서 실행시
        # --dont-arrange 가 우선되어 STL의 원본 XY를 기준으로 함.
        self.declare_parameter('center', '125,125')     # 250x250 베드 한가운데
        self.declare_parameter('dont_arrange', False)
        self.declare_parameter('extra_args', [''])      # 탈출구. 빈 문자열 무시
        self.declare_parameter('timeout_s', 900.0)

        self._proc = None                # 실행 중인 슬라이서. 취소 신호 전달 대상
        self._busy = threading.Lock()    # 동시 goal 방지용

        self._server = ActionServer(
            self, SliceModel, ACTION_NAME,
            execute_callback=self.execute,
            goal_callback=self.on_goal,
            cancel_callback=self.on_cancel,
            # execute 수십 초 블로킹 중에도 취소 콜백이 돌아야 함.
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
        """동시 슬라이싱 거부. CPU 독립 점유."""
        if self._busy.locked():
            self.get_logger().warn('Slicing in progress — goal denied')
            return GoalResponse.REJECT
        return GoalResponse.ACCEPT

    def on_cancel(self, goal_handle):
        proc = self._proc
        if proc is not None and proc.poll() is None:
            self.get_logger().info('Cancel requested — Slicer process terminated')
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

        self.get_logger().info('Executed: ' + ' '.join(cmd))
        self._publish(goal_handle, 0.02, 'Slicer executed')

        try:
            # stderr와 stdout을 합쳐 출력.
            # PrusaSlicer 는 양쪽에서 출력하는 버전이 있어 한 스트림으로 봐야 진행률이 안 끊김.
            self._proc = subprocess.Popen(
                cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, bufsize=1)
        except OSError as exc:
            goal_handle.abort()
            result.message = f'Slicer execution failed: {exc}'
            self.get_logger().error(result.message)
            return result

        tail, canceled = self._pump(goal_handle, self._proc)
        code = self._proc.wait()
        self._proc = None

        if canceled:
            goal_handle.canceled()
            result.message = 'Cancelled'
            return result

        if code != 0:
            goal_handle.abort()
            result.message = (f'Slicer exit {code}. '
                              f'Last output: {tail or "(NA))"}')
            self.get_logger().error(result.message)
            return result

        if not os.path.isfile(out_path):
            goal_handle.abort()
            result.message = (f'Slicer succeed but no output: {out_path}. '
                              f'Last output: {tail or "(NA)"}')
            self.get_logger().error(result.message)
            return result

        self._publish(goal_handle, 1.0, 'Complete')
        goal_handle.succeed()
        result.success = True
        result.gcode_path = out_path
        result.message = f'{os.path.getsize(out_path)} bytes -> {out_path}'
        self.get_logger().info('Slicing Complete: ' + result.message)
        return result

    # ------------------------------------------------------------------
    def _build_command(self, request):
        """(cmd, 산출물경로, 에러문구). 에러문구 None 아니면 나머지 무의미."""
        slicer = find_slicer(self.get_parameter('slicer_executable').value)
        if slicer is None:
            return None, None, missing_slicer_message()

        stl = os.path.abspath(os.path.expanduser(request.stl_path or ''))
        if not request.stl_path:
            return None, None, 'No stl_path exists'
        if not os.path.isfile(stl):
            return None, None, f'No STL exists: {stl}'

        name = request.profile or self.get_parameter('default_profile').value
        ini = resolve_profile(name, self.get_parameter('profile_dir').value)
        if ini is None:
            return None, None, (f'Profile {name!r} not found. Explored path: '
                                f'{profile_dirs(self.get_parameter("profile_dir").value)} '
                                f'(04b §294 — tools/slicer/voron24_250.ini)')

        out_dir = self.get_parameter('output_dir').value or os.path.join(
            tempfile.gettempdir(), 'voron24_slicer')
        try:
            os.makedirs(out_dir, exist_ok=True)
        except OSError as exc:
            return None, None, f'Cannot create output directory: {out_dir} ({exc})'
        stem = os.path.splitext(os.path.basename(stl))[0]
        # 동일 STL 재슬라이싱 시 덮어씀. 임시 파일 쌓이는 것 방지.
        # 동시 goal 은 on_goal 에서 이미 막았으므로 충돌 없음.
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
        """stdout 흘려 보내며 feedback 발행 + 취소 감시. (마지막 줄, 취소여부) 반환."""
        reader = _LineReader(proc.stdout)
        progress = 0.02
        last_sent = 0.0
        stage = 'Slicing'
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
                self.get_logger().error('timeout_s exceeded — forced shutdown')
                proc.kill()
                return tail + ' (timeout)', False

            line = reader.poll()
            if line is None:
                break                       # 스트림 끝. 종료 코드는 호출자가 봄
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
    # execute 블로킹 중 취소 콜백이 돌아야 하므로 단일 스레드 불가.
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
