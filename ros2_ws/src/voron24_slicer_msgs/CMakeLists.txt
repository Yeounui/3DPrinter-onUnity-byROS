cmake_minimum_required(VERSION 3.8)
project(voron24_slicer_msgs)

find_package(ament_cmake REQUIRED)
find_package(rosidl_default_generators REQUIRED)
find_package(action_msgs REQUIRED)

# 인터페이스만 있는 패키지다. 노드는 voron24_slicer(ament_python) 쪽에 있다.
# voron24_msgs 와 같은 모양이며, 이유는 package.xml 주석 참조.
rosidl_generate_interfaces(${PROJECT_NAME}
  "action/SliceModel.action"
  DEPENDENCIES action_msgs
)

ament_export_dependencies(rosidl_default_runtime)
ament_package()
