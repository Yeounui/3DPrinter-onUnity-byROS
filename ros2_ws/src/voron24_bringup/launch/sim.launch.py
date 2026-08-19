#!/usr/bin/env python3
"""통합 진입점 (docs/04_single_entrypoint.md).

    ros2 launch voron24_bringup sim.launch.py                                  # 수동 조작
    ros2 launch voron24_bringup sim.launch.py source:=gcode file:=/abs/a.gcode # G-code 재생
    ros2 launch voron24_bringup sim.launch.py source:=gcode file:=... speed:=25.0
    ros2 launch voron24_bringup sim.launch.py source:=stl   file:=/abs/a.stl   # 슬라이싱 후 재생
    ros2 launch voron24_bringup sim.launch.py unity:=none                      # ROS 단독 (RViz 자동 ON)

| 인자 | 기본값 | 값 |
|---|---|---|
| `source` | `manual` | `manual` \\| `gcode` \\| `stl` |
| `file` | `''` | source 가 gcode/stl 일 때 절대경로 |
| `speed` | `10.0` | 재생 배속 초기값. 실행 중에는 `set_speed` 로 |
| `max_step` | `10.0` | 틱당 헤드 이동 상한 [mm]. 배속을 올려도 이 이상은 안 건너뜀. `0` = 무제한 |
| `pattern` | `none` | source 가 manual 일 때만 의미 있음 |
| `unity` | `build` | `build`(리눅스 플레이어 기동) \\| `editor`(엔드포인트만) \\| `none` |
| `rviz` | (자동) | 기본은 꺼짐. `unity:=none` 이면 자동으로 켜짐 |
| `use_meshes` | `false` | A 의 STL |
| `ros_ip` | `0.0.0.0` | 엔드포인트가 **서버로서 bind** 하는 주소 |
| `ros_port` | `10000` | 엔드포인트 포트. 플레이어에도 같이 넘어감 |
| `unity_connect_ip` | `127.0.0.1` | 플레이어가 **클라이언트로서 접속** 할 주소 |

mock.launch.py 는 건드리지 않음. 그쪽은 W1 회귀 기준선이고 값 소스가 `/joint_states` 에
직접 쏘는 경로라 이 파일과 토폴로지가 다름.

빠진 부품에 대한 태도 — 아직 없는 노드를 있는 것처럼 참조하면 launch 가 통째로 죽으므로
설치 트리를 확인해 있는 것만 조립하고, 무엇이 빠져 어떻게 우회했는지 기동 로그에 남김.
셋 다 들어오면 조건이 저절로 참이 되어 정상 토폴로지로 붙음.

    printer_state_node (A, 5b)   없으면 -> 값 소스가 /joint_states 로 직접 발행.
                                 jog 적분·리밋 클램프·명령 릴레이가 통째로 빠짐
    manual_publisher (A, 4.5)    없으면 -> mock_publisher 로 대체 (patterns.py 공유)
    Unity 리눅스 빌드 (0~2 단계)  없으면 -> unity:=editor 로 강등. 엔드포인트만 뜸
    voron24_slicer (B, 6)        없으면 -> source:=stl 이 슬라이싱만 생략. 플레이어는
                                 그대로 떠 있어 load_gcode 를 직접 쏘면 재생됨.
                                 prusa-slicer 실행파일 유무는 슬라이서 노드가 판단하며
                                 없으면 goal 이 success=false 로 떨어질 뿐 launch 는 삶

`file:=` 은 노드 파라미터가 아니라 `/printer/cmd` 에 한 번 발행하는 `load_gcode`.
파일 진입점을 둘로 늘리지 않기 위함 (04b §"file:= 은 파라미터가 아니라 발행이다").
`source:=stl` 은 그 한 발을 job_starter 가 대신 쏜다 — STL 은 "슬라이싱 액션 ->
result 의 경로" 두 단계라 `ros2 topic pub` 한 줄로 안 되기 때문 (04b §248).
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
    # 배속 올리면 틱당 이동량이 그만큼 커짐. 이 상한 없으면 speed:=100 에서
    # 50Hz 좌표가 40mm 씩 건너뛰어 Unity 헤드가 경로를 따라가지 못함 (04b §배속).
    max_step = float(arg('max_step'))
    rate = float(arg('rate'))
    period = float(arg('period'))
    # 엔드포인트 파라미터와 플레이어 인자 양쪽에 같은 값이 들어가므로 여기서 한 번만
    # 못 박음. 두 자리가 갈리면 "엔드포인트는 떴는데 안 붙는" 상태가 됨.
    ros_port = int(arg('ros_port'))

    # bind 주소와 connect 주소는 다름. `ros_ip` 는 엔드포인트가 서버로서 bind 하는
    # 주소라 기본값이 0.0.0.0(모든 인터페이스)이고, 이 값은 접속 대상이 될 수 없음.
    # 그래서 플레이어에게 넘길 주소를 인자로 따로 뺌. 암묵적으로 0.0.0.0 -> 127.0.0.1
    # 치환을 하지 않는 이유는 이 파일이 인자 검증을 launch 단계에서 끝내는 방침이라
    # 숨은 규칙을 두지 않기 위함.
    unity_connect_ip = arg('unity_connect_ip')
    if unity == 'build' and unity_connect_ip in ('', '0.0.0.0'):
        raise RuntimeError('unity_connect_ip 는 접속 가능한 주소여야 함 '
                           f'(bind 주소인 ros_ip 와 다름): {unity_connect_ip!r}')

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
        if pattern == 'none':
            # ros2 launch 자식 프로세스에는 stdin이 연결되지 않는다. 입력 없는
            # manual_publisher를 중복 기동하지 않고, 사용자가 연 별도 터미널의 노드가
            # 키보드 입력을 전담하게 한다.
            notes.append(LogInfo(msg='[sim] 수동 키보드는 별도 터미널에서 '
                                     '`ros2 run voron24_gcode manual_publisher` 실행. '
                                     '조작: A/D=X, S/W=Y, Q/E=Z, Home=원점, Space=압출'))
        elif executable_path('voron24_gcode', 'manual_publisher'):
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

    elif source == 'gcode':
        actions.append(Node(
            package='voron24_gcode', executable='gcode_player',
            name='gcode_player', output='screen',
            parameters=[{'speed_default': speed, 'max_step_mm': max_step,
                         'playback_topic': playback_topic}],
            remappings=[('/printer/target', target_topic)]))
        if pattern != 'none':
            notes.append(LogInfo(msg=f'[sim] source:=gcode 라 pattern:={pattern} 은 무시함'))

    else:   # stl
        # gcode 와 토폴로지가 같음. 플레이어와 load_gcode 는 그대로고 앞에 STL -> G-code
        # 한 단이 더 붙을 뿐 (04b §6). 그래서 값 소스 노드는 gcode 분기와 같은 것을 씀.
        actions.append(Node(
            package='voron24_gcode', executable='gcode_player',
            name='gcode_player', output='screen',
            parameters=[{'speed_default': speed, 'max_step_mm': max_step,
                         'playback_topic': playback_topic}],
            remappings=[('/printer/target', target_topic)]))
        # 슬라이서는 액션 서버라 그래프 안에 노드로 들어옴. launch 가 ExecuteProcess 로
        # prusa-slicer 를 직접 돌리지 않는 이유 (04b §257).
        if executable_path('voron24_slicer', 'slicer_node'):
            actions.append(Node(
                package='voron24_slicer', executable='slicer_node',
                name='voron24_slicer', output='screen'))
        else:
            notes.append(LogInfo(msg='[sim] voron24_slicer 없음 — 슬라이싱 불가. '
                                     '`colcon build --packages-select voron24_slicer_msgs '
                                     'voron24_slicer` '
                                     '후 다시 실행할 것 (docs/04b §6). 플레이어는 떠 '
                                     '있으므로 load_gcode 를 직접 쏘면 재생은 됨'))
        if pattern != 'none':
            notes.append(LogInfo(msg=f'[sim] source:=stl 라 pattern:={pattern} 은 무시함'))

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
    elif source == 'stl':
        # STL 은 "액션 send_goal -> result 의 G-code 경로를 다시 load_gcode 로" 두 단계라
        # topic pub 한 줄로 안 됨. 그 두 단계를 묶은 것이 job_starter (04b §248).
        # 경로가 이 노드의 파라미터로 들어가지만 gcode_player 로 가는 진입점은 여전히
        # load_gcode 하나뿐이므로 "file:= 은 파라미터가 아니라 발행" 원칙은 그대로.
        starter = executable_path('voron24_slicer', 'job_starter')
        if not gcode_file:
            notes.append(LogInfo(msg='[sim] file:= 없음 — 슬라이싱하려면 `ros2 action '
                                     'send_goal /slice_model '
                                     'voron24_slicer_msgs/action/SliceModel '
                                     '"{stl_path: \'/abs/a.stl\'}"` 를 직접 쏠 것'))
        elif starter is None:
            notes.append(LogInfo(msg='[sim] job_starter 없음 — STL 자동 슬라이싱 생략'))
        else:
            path = os.path.abspath(os.path.expanduser(gcode_file))
            if not os.path.isfile(path):
                notes.append(LogInfo(msg=f'[sim] file 이 없음: {path}'))
            # gcode 분기와 같은 3 초. job_starter 는 서버가 뜰 때까지 스스로도 기다리지만
            # 로그가 뒤섞이는 것을 막는 값이기도 함.
            actions.append(TimerAction(period=3.0, actions=[Node(
                package='voron24_slicer', executable='job_starter',
                name='job_starter', output='screen',
                parameters=[{'stl_path': path, 'cmd_topic': '/printer/cmd'}])]))
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
                     '-logFile', '-',             # ROS 로그와 같은 터미널로
                     # RosBootstrap.cs 가 읽어 씬의 ROSConnection 을 덮어씀. 안 넘기면
                     # 씬에 박힌 값으로 조용히 폴백해서 ros_port:= 오버라이드가 먹지
                     # 않음. 인자 이름·형식은 RosBootstrap.ValueOf 와 맞춰야 함
                     # (`--이름 값` / `--이름=값` 둘 다 받지만 앞의 형식을 씀).
                     '--ros-ip', unity_connect_ip,
                     '--ros-port', str(ros_port)],
                output='screen',
                # 창을 닫으면 launch 전체가 내려감. 목표가 "Ctrl+C 하나".
                on_exit=[Shutdown(reason='Unity 플레이어 종료')])]))

    if unity != 'none':
        # 2 초 뒤 플레이어가 붙으므로 엔드포인트가 먼저 bind 해야 첫 로그가 깨끗함.
        actions.append(Node(
            package='ros_tcp_endpoint', executable='default_server_endpoint',
            name='ros_tcp_endpoint', output='screen',
            parameters=[{'ROS_IP': arg('ros_ip'), 'ROS_TCP_PORT': ros_port}]))

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
                             f'speed={speed} max_step={max_step}mm target={target_topic}'))
    return notes + actions


def generate_launch_description():
    return LaunchDescription([
        DeclareLaunchArgument('source', default_value='manual',
                              description='manual | gcode | stl'),
        DeclareLaunchArgument('file', default_value='',
                              description='source 가 gcode/stl 일 때 절대경로'),
        DeclareLaunchArgument('speed', default_value='10.0',
                              description='재생 배속 초기값'),
        DeclareLaunchArgument('max_step', default_value='10.0',
                              description='틱당 헤드 이동 상한 [mm]. 0 이면 무제한'),
        DeclareLaunchArgument('pattern', default_value='none',
                              description='none | home | sweep | square | lissajous'),
        DeclareLaunchArgument('unity', default_value='build',
                              description='build | editor | none'),
        DeclareLaunchArgument('rviz', default_value='',
                              description='빈 값이면 unity:=none 일 때만 자동으로 켜짐'),
        DeclareLaunchArgument('use_meshes', default_value='false'),
        DeclareLaunchArgument('rate', default_value='50.0'),
        DeclareLaunchArgument('period', default_value='12.0'),
        DeclareLaunchArgument('ros_ip', default_value='0.0.0.0',
                              description='엔드포인트가 bind 하는 주소 (서버 쪽)'),
        DeclareLaunchArgument('ros_port', default_value='10000'),
        DeclareLaunchArgument('unity_connect_ip', default_value='127.0.0.1',
                              description='플레이어가 접속할 주소 (클라이언트 쪽). '
                                          'ros_ip 와 같은 값이 아님 — 0.0.0.0 은 '
                                          '접속 대상이 될 수 없음'),

        # 인자 값에 따라 노드 구성이 갈리므로 조건부 substitution 대신 OpaqueFunction
        # 으로 파이썬에서 분기. IfCondition 을 3지선다에 쓰면 조건식이 노드마다 붙음.
        OpaqueFunction(function=launch_setup),
    ])
