#!/usr/bin/env python3
"""RViz + joint_state_publisher_gui 로 URDF 를 눈으로 검증한다.

    # mock (박스)
    ros2 launch voron24_description display.launch.py
    # real (A 의 메시)
    ros2 launch voron24_description display.launch.py use_meshes:=true
"""
from launch import LaunchDescription
from launch.actions import DeclareLaunchArgument
from launch.conditions import IfCondition
from launch.substitutions import Command, LaunchConfiguration, PathJoinSubstitution
from launch_ros.actions import Node
from launch_ros.substitutions import FindPackageShare


def generate_launch_description():
    pkg = FindPackageShare('voron24_description')
    use_meshes = LaunchConfiguration('use_meshes')
    gui = LaunchConfiguration('gui')
    urdf = PathJoinSubstitution([pkg, 'urdf', 'voron24.urdf.xacro'])

    robot_description = Command([
        'xacro ', urdf, ' use_meshes:=', use_meshes,
    ])

    return LaunchDescription([
        DeclareLaunchArgument('use_meshes', default_value='false',
                              description='true 면 meshes/ 의 STL 사용, false 면 mock 박스'),
        DeclareLaunchArgument('gui', default_value='true',
                              description='joint_state_publisher_gui 슬라이더 사용'),

        Node(package='robot_state_publisher', executable='robot_state_publisher',
             output='screen',
             parameters=[{
                 'robot_description': robot_description,
                 'publish_frequency': 50.0,
             }]),

        Node(package='joint_state_publisher_gui', executable='joint_state_publisher_gui',
             condition=IfCondition(gui), output='screen'),

        Node(package='rviz2', executable='rviz2', output='screen',
             arguments=['-d', PathJoinSubstitution([pkg, 'rviz', 'voron24.rviz'])]),
    ])
