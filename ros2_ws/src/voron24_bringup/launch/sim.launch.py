#!/usr/bin/env python3
"""통합 진입점 (docs/04_single_entrypoint.md).

    ros2 launch voron24_bringup sim.launch.py                                  # 수동 조작
    ros2 launch voron24_bringup sim.launch.py source:=gcode file:=/abs/a.gcode # G-code 재생
    ros2 launch voron24_bringup sim.launch.py source:=gcode file:=... speed:=25.0
    ros2 launch voron24_bringup sim.launch.py unity:=none                      # ROS 단독 (RViz 자동 ON)

| 인자 | 기본값 | 값 |
|---|---|---|
| `source` | `manual` | `manual` \\| `gcode` \\| `stl` |
| `file` | `''` | source 가 gcode/stl 일 때 절대경로 |
| `speed` | `10.0` | 재생 배속 초기값. 실행 중에는 `set_speed` 로 |
| `pattern` | `none` | source 가 manual 일 때만 의미 있음 |
| `unity` | `build` | `build`(리눅스 플레이어 기동) \\| `editor`(엔드포인트만) \\| `none` |
| `rviz` | (자동) | 기본은 꺼짐. `unity:=none` 이면 자동으로 켜짐 |
| `use_meshes` | `false` | A 의 STL |

mock.launch.py 는 건드리지 않음. 그쪽은 W1 회귀 기준선이고 값 소스가 `/joint_states` 에
직접 쏘는 경로라 이 파일과 토폴로지가 다름.

빠진 부품에 대한 태도 — 아직 없는 노드를 있는 것처럼 참조하면 launch 가 통째로 죽으므로
설치 트리를 확인해 있는 것만 조립하고, 무엇이 빠져 어떻게 우회했는지 기동 로그에 남김.
셋 다 들어오면 조건이 저절로 참이 되어 정상 토폴로지로 붙음.

    printer_state_node (A, 5b)   없으면 -> 값 소스가 /joint_states 로 직접 발행.
                                 jog 적분·리밋 클램프·명령 릴레이가 통째로 빠짐
    manual_publisher (A, 4.5)    없으면 -> mock_publisher 로 대체 (patterns.py 공유)
    Unity 리눅스 빌드 (0~2 단계)  없으면 -> unity:=editor 로 강등. 엔드포인트만 뜸

`file:=` 은 노드 파라미터가 아니라 `/printer/cmd` 에 한 번 발행하는 `load_gcode`.
파일 진입점을 둘로 늘리지 않기 위함 (04b §"file:= 은 파라미터가 아니라 발행이다").
"""
import os

from launch import LaunchDescription
from launch.actions import (DeclareLaunchArgument, ExecuteProcess, LogInfo,
                            OpaqueFunction, Shutdown, TimerAction)
from launch.substitutions import Command, LaunchConfiguration, PathJoinSubstitution
from launch_ros.actions import Node
from launch_ros.parameter_descriptions import ParameterValue
from launch_ros.substitutions import FindPackageShare

SOURCES = ('manual', 'gcode', 'stl')
UNITY_MODES = ('build', 'editor', 'none')
PATTERNS = ('none', 'home', 'sweep', 'square', 'lissajous')   # patterns.py 와 동일

# A 가 상태 노드를 어느 패키지에 둘지는 04a 에 안 박혀 있어 후보를 둘 다 훑음.
STATE_NODE_CANDIDATES = (('voron24_bringup', 'printer_state_node'),
                         ('voron24_gcode', 'printer_state_node'))

# 레포 루트 기준. Editor 의 Play 는 launch 가 누를 수 없어 리눅스 standalone 을 띄움.
UNITY_PLAYER = os.path.join('unity', 'Voron24Twin', 'Build', 'Linux', 'Voron24Twin.x86_64')


def executable_path(package, executable):
    """설치 트리의 실행 파일 경로. 없으면 None.

    `ros2 run` 이 찾는 자리를 그대로 봄 — 엔트리포인트만 등록되고 아직 파일이 없는
    상태와, 패키지 자체가 없는 상태를 같은 방식으로 걸러냄.
    """
    try:
        from ament_index_python.packages import get_package_prefix
        path = os.path.join(get_package_prefix(package), 'lib', package, executable)
    except Exception:
        return None
    return path if os.path.isfile(path) else None


def find_unity_player():
    """레포의 Unity 리눅스 빌드 경로. 못 찾으면 None.

    realpath 로 심볼릭 링크를 풀어야 --symlink-install 로 깔린 share 에서 출발해도
    소스 트리에 닿음. 기준 경로는 .../src/voron24_bringup/launch/sim.launch.py.
    """
    here = os.path.realpath(__file__)
    for _ in range(6):
        here = os.path.dirname(here)
        path = os.path.join(here, UNITY_PLAYER)
        if os.path.isfile(path):
            return path
    return None


def publish_command(topic, yaml):
    """`/printer/cmd` 에 PrinterCommand 한 발. launch 가 그래프 밖에서 값을 밀어넣는
    유일한 자리이며, 그것도 토픽을 거침."""
    return ExecuteProcess(
        cmd=['ros2', 'topic', 'pub', '--once', topic,
             'voron24_msgs/PrinterCommand', yaml],
        output='screen')


def launch_setup(context, *args, **kwargs):
    def arg(name):
        return LaunchConfiguration(name).perform(context)

    source = arg('source')
    unity = arg('unity')
    pattern = arg('pattern')
    gcode_file = arg('file')

    # 인자 검증을 launch 단계에서 끝냄. 오타를 노드가 받아 죽으면 어느 노드가 왜
    # 죽었는지 로그에서 걸러내야 함.
    if source not in SOURCES:
        raise RuntimeError(f'source 는 {SOURCES} 중 하나여야 함: {source!r}')
    if unity not in UNITY_MODES:
        raise RuntimeError(f'unity 는 {UNITY_MODES} 중 하나여야 함: {unity!r}')
    if pattern not in PATTERNS:
        raise RuntimeError(f'pattern 은 {PATTERNS} 중 하나여야 함: {pattern!r}')
    # 문자열을 여기서 float 로 못 박음. 노드에 문자열로 넘기면 period:=30 이 INTEGER 로
    # 파싱돼 InvalidParameterTypeException 이 나고, 그건 사용자가 외울 일이 아님.
    speed = float(arg('speed'))
    rate = float(arg('rate'))
    period = float(arg('period'))

    notes = []
    actions = []

    # ------------------------------------------------------------------
    # 형상. mock.launch.py 와 동일하게 xacro 한 벌만 씀 (단일 URDF 원칙).
    desc_pkg = FindPackageShare('voron24_description')
    urdf = PathJoinSubstitution([desc_pkg, 'urdf', 'voron24.urdf.xacro'])
    robot_description = Command(['xacro ', urdf, ' use_meshes:=', LaunchConfiguration('use_meshes')])
    actions.append(Node(
        package='robot_state_publisher', executable='robot_state_publisher',
        name='robot_state_publisher', output='screen',
        parameters=[{
            'robot_description': ParameterValue(robot_description, value_type=str),
            'publish_frequency': 50.0,
        }]))

    # ------------------------------------------------------------------
    # 상태 노드. 있으면 값 소스는 /printer/target 으로 쏘고 이 노드가 /joint_states 를
    # 단독 소유. 없으면 값 소스를 /joint_states 에 직접 붙여 그림은 나오게 함.
    state_node = next(((pkg, exe) for pkg, exe in STATE_NODE_CANDIDATES
                       if executable_path(pkg, exe)), None)
    if state_node is not None:
        actions.append(Node(package=state_node[0], executable=state_node[1],
                            name='printer_state_node', output='screen'))
        target_topic = '/printer/target'
        playback_topic = '/printer/playback'
    else:
        notes.append(LogInfo(msg='[sim] printer_state_node(A) 없음 — 값 소스를 '
                                 '/joint_states 에 직접 물림. jog 적분·리밋 클램프·'
                                 '명령 릴레이 없음'))
        target_topic = '/joint_states'
        # 릴레이가 없으니 플레이어가 /printer/cmd 를 직접 들음. 그래야 아래 load_gcode
        # 발행이 두 경우 모두 같은 토픽으로 나감.
        playback_topic = '/printer/cmd'

    # ------------------------------------------------------------------
    # 값 소스 — 배타 선택. /printer/target 발행자도 하나여야 함.
    if source == 'manual':
        if executable_path('voron24_gcode', 'manual_publisher'):
            actions.append(Node(
                package='voron24_gcode', executable='manual_publisher',
                name='manual_publisher', output='screen',
                parameters=[{'pattern': pattern, 'rate': rate, 'period': period}],
                remappings=[('/printer/target', target_topic)]))
        else:
            # 대타. mock_publisher 는 /joint_states 에 직접 쏘므로 remap 방향이 반대.
            # 키보드 조작은 안 되고 pattern 재생만 됨.
            notes.append(LogInfo(msg='[sim] manual_publisher(A) 없음 — mock_publisher 로 '
                                     '대체. 키보드 조작 불가, pattern 재생만 됨'))
            actions.append(Node(
                package='voron24_gcode', executable='mock_publisher',
                name='mock_publisher', output='screen',
                parameters=[{'pattern': pattern, 'rate': rate, 'period': period}],
                remappings=[('/joint_states', target_topic)]))
        if pattern == 'none':
            notes.append(LogInfo(msg='[sim] pattern:=none — 자동 궤적 없음. 키보드 조작은 '
                                     '별도 터미널에서 `ros2 run voron24_gcode '
                                     'manual_publisher` (launch 로 띄운 노드에는 stdin 이 '
                                     '연결되지 않음)'))

    elif source == 'gcode':
        actions.append(Node(
            package='voron24_gcode', executable='gcode_player',
            name='gcode_player', output='screen',
            parameters=[{'speed_default': speed, 'playback_topic': playback_topic}],
            remappings=[('/printer/target', target_topic)]))
        if pattern != 'none':
            notes.append(LogInfo(msg=f'[sim] source:=gcode 라 pattern:={pattern} 은 무시함'))

    else:   # stl
        # 6 단계. 슬라이서 액션 서버와 job_starter 가 들어와야 성립.
        raise RuntimeError('source:=stl 은 아직 없음 — voron24_slicer + job_starter 가 '
                           '6 단계 (docs/04b_gcode_pipeline.md §6). '
                           '지금은 슬라이싱된 G-code 를 source:=gcode 로 넘길 것')

    # ------------------------------------------------------------------
    # file:= — 노드가 뜬 뒤 load_gcode 한 발. 3 초는 구독이 붙을 시간.
    if source == 'gcode':
        if gcode_file:
            path = os.path.abspath(os.path.expanduser(gcode_file))
            if not os.path.isfile(path):
                # 죽이지는 않음. 나중에 load_gcode 를 직접 쏘면 그만이고, 오타를
                # 여기서 알려주는 편이 노드 로그에서 찾는 것보다 빠름.
                notes.append(LogInfo(msg=f'[sim] file 이 없음: {path}'))
            actions.append(TimerAction(period=3.0, actions=[publish_command(
                '/printer/cmd', "{command: 'load_gcode', payload: '" + path + "'}")]))
        else:
            notes.append(LogInfo(msg='[sim] file:= 없음 — 재생하려면 load_gcode 를 직접 '
                                     '발행할 것'))
    elif source == 'manual' and pattern != 'none' and state_node is not None:
        # 상태 노드의 초기 모드가 manual 이라 /printer/target 을 무시함. 패턴을 보려면
        # playing 으로 한 번 밀어줘야 함 (04a §"소스 전환은 remapping 으로").
        actions.append(TimerAction(period=3.0, actions=[publish_command(
            '/printer/cmd', "{command: 'resume'}")]))

    # ------------------------------------------------------------------
    # Unity. editor 는 엔드포인트만 띄우고 Play 는 Windows Editor 에서 사람이 누름.
    if unity == 'build':
        player = find_unity_player()
        if player is None:
            notes.append(LogInfo(msg=f'[sim] Unity 리눅스 빌드 없음 ({UNITY_PLAYER}) — '
                                     'editor 모드로 진행. tools/build_unity.sh 참조'))
            unity = 'editor'
        else:
            actions.append(TimerAction(period=2.0, actions=[ExecuteProcess(
                cmd=[player,
                     '-force-glcore',             # Vulkan 소프트웨어 폴백 방지
                     '-screen-fullscreen', '0',   # 창 모드. 터미널 옆에 둠
                     '-logFile', '-'],            # ROS 로그와 같은 터미널로
                output='screen',
                # 창을 닫으면 launch 전체가 내려감. 목표가 "Ctrl+C 하나".
                on_exit=[Shutdown(reason='Unity 플레이어 종료')])]))

    if unity != 'none':
        # 2 초 뒤 플레이어가 붙으므로 엔드포인트가 먼저 bind 해야 첫 로그가 깨끗함.
        actions.append(Node(
            package='ros_tcp_endpoint', executable='default_server_endpoint',
            name='ros_tcp_endpoint', output='screen',
            parameters=[{'ROS_IP': arg('ros_ip'), 'ROS_TCP_PORT': int(arg('ros_port'))}]))

    # ------------------------------------------------------------------
    # RViz 기본값이 mock.launch.py 와 반대(false). Unity 플레이어가 뷰어이고 창이 둘
    # 뜨는 것은 "명령 하나" 라는 목표에 반함. 게다가 RViz2 와 플레이어가 같은 Mesa
    # d3d12 GL 경로를 다투면 프레임 저하의 원인을 갈라낼 수 없음.
    rviz = arg('rviz').lower()
    if rviz not in ('', 'true', 'false'):
        raise RuntimeError(f"rviz 는 true | false | '' 여야 함: {rviz!r}")
    if not rviz:
        rviz = 'true' if unity == 'none' else 'false'   # 뷰어가 하나도 없는 경우 방지
    if rviz == 'true':
        actions.append(Node(
            package='rviz2', executable='rviz2', name='rviz2', output='screen',
            arguments=['-d', PathJoinSubstitution([desc_pkg, 'rviz', 'voron24.rviz'])]))

    notes.append(LogInfo(msg=f'[sim] source={source} unity={unity} rviz={rviz} '
                             f'speed={speed} target={target_topic}'))
    return notes + actions


def generate_launch_description():
    return LaunchDescription([
        DeclareLaunchArgument('source', default_value='manual',
                              description='manual | gcode | stl'),
        DeclareLaunchArgument('file', default_value='',
                              description='source 가 gcode/stl 일 때 절대경로'),
        DeclareLaunchArgument('speed', default_value='10.0',
                              description='재생 배속 초기값'),
        DeclareLaunchArgument('pattern', default_value='none',
                              description='none | home | sweep | square | lissajous'),
        DeclareLaunchArgument('unity', default_value='build',
                              description='build | editor | none'),
        DeclareLaunchArgument('rviz', default_value='',
                              description='빈 값이면 unity:=none 일 때만 자동으로 켜짐'),
        DeclareLaunchArgument('use_meshes', default_value='false'),
        DeclareLaunchArgument('rate', default_value='50.0'),
        DeclareLaunchArgument('period', default_value='12.0'),
        DeclareLaunchArgument('ros_ip', default_value='0.0.0.0'),
        DeclareLaunchArgument('ros_port', default_value='10000'),

        # 인자 값에 따라 노드 구성 자체가 갈리므로 조건부 substitution 대신 OpaqueFunction
        # 으로 파이썬에서 분기. IfCondition 을 3 지선다에 쓰면 조건식이 노드마다 붙음.
        OpaqueFunction(function=launch_setup),
    ])
