#!/usr/bin/env python3
"""W1 통합 게이트용 런치.

RViz 와 Unity 에서 동시에 같은 움직임이 보이면 성공.

    # 전부 (RViz + Unity 엔드포인트 + mock 퍼블리셔)
    ros2 launch voron24_bringup mock.launch.py

    # 축 방향 검증용
    ros2 launch voron24_bringup mock.launch.py pattern:=sweep

    # 홈 자세 고정 (노즐이 베드 좌전방 코너에 있는지 확인)
    ros2 launch voron24_bringup mock.launch.py pattern:=home

    # A 의 메시가 들어온 뒤
    ros2 launch voron24_bringup mock.launch.py use_meshes:=true

    # ROS-TCP-Endpoint 를 아직 clone 안 했을 때
    ros2 launch voron24_bringup mock.launch.py unity:=false
"""
from launch import LaunchDescription
from launch.actions import DeclareLaunchArgument
from launch.conditions import IfCondition
from launch.substitutions import Command, LaunchConfiguration, PathJoinSubstitution
from launch_ros.actions import Node
from launch_ros.substitutions import FindPackageShare
from launch_ros.parameter_descriptions import ParameterValue


def generate_launch_description():
    desc_pkg = FindPackageShare('voron24_description') # package 가져오기.

    use_meshes = LaunchConfiguration('use_meshes')
    pattern = LaunchConfiguration('pattern')
    rate = LaunchConfiguration('rate')
    period = LaunchConfiguration('period')
    rviz = LaunchConfiguration('rviz')
    unity = LaunchConfiguration('unity')
    ros_ip = LaunchConfiguration('ros_ip')
    ros_port = LaunchConfiguration('ros_port')

    urdf = PathJoinSubstitution([desc_pkg, 'urdf', 'voron24.urdf.xacro'])
    # voron24_description share 경로 아래 urdf/voron24.urdf.xacro 변수 연결.
    robot_description = Command(['xacro ', urdf, ' use_meshes:=', use_meshes])
    # xacro ... use_meshes:=false 실행. :=는 ROS2 launch/xacro 인자 값 지정 표기

    return LaunchDescription([
        DeclareLaunchArgument('use_meshes', default_value='false'),
        DeclareLaunchArgument('pattern', default_value='lissajous',
                              description='home | sweep | square | lissajous'),
        DeclareLaunchArgument('rate', default_value='50.0'),
        DeclareLaunchArgument('period', default_value='12.0'),
        DeclareLaunchArgument('rviz', default_value='true'),
        DeclareLaunchArgument('unity', default_value='true'),
        DeclareLaunchArgument('ros_ip', default_value='0.0.0.0'),
        DeclareLaunchArgument('ros_port', default_value='10000'),

        # mock.launch.py 실행 시 각 노드 생성.
        # IfCondition 인자 참일 때만 해당 노드 생성.

        Node(package='robot_state_publisher', executable='robot_state_publisher',
             name='robot_state_publisher', output='screen',
             parameters=[{
                 'robot_description': ParameterValue(robot_description, value_type=str),
                 'publish_frequency': 50.0,
             }]),
        # package: 실행 파일이 든 ROS2 패키지명
        # executable: 패키지 내 실행 프로그램명 (ros2 run 대상)
        # name: ROS 그래프 노드 이름. ros2 node list 에 /robot_state_publisher 로 표시
        # output='screen': 로그를 launch 터미널에 출력
        # parameters: 노드 기동 시 전달 파라미터
        # robot_description: xacro 결과를 str 로 전달
        # publish_frequency: 50Hz 발행

        Node(package='voron24_gcode', executable='mock_publisher',
             name='mock_publisher', output='screen',
             parameters=[{
                 'pattern': pattern,
                 # value_type=float 필수. 안 붙이면 launch 인자 문자열이 리터럴로
                 # 파싱돼 period:=30 은 INTEGER, 노드는 DOUBLE 기대하므로
                 # InvalidParameterTypeException 발생. period:=30.0 처럼 소수점
                 # 찍어야만 뜨는 건 사용자가 외울 일 아님.
                 'rate': ParameterValue(rate, value_type=float),
                 'period': ParameterValue(period, value_type=float),
             }]),

        Node(package='ros_tcp_endpoint', executable='default_server_endpoint',
             name='ros_tcp_endpoint', output='screen',
             condition=IfCondition(unity),
             parameters=[{
                 'ROS_IP': ros_ip,
                 'ROS_TCP_PORT': ros_port,
             }]),

        Node(package='rviz2', executable='rviz2', name='rviz2', output='screen',
             condition=IfCondition(rviz),
             arguments=['-d', PathJoinSubstitution([desc_pkg, 'rviz', 'voron24.rviz'])]),
    ])
