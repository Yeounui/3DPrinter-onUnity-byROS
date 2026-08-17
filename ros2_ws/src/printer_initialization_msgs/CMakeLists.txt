cmake_minimum_required(VERSION 3.8)
project(printer_initialization_msgs)

find_package(ament_cmake REQUIRED)
find_package(rosidl_default_generators REQUIRED)

rosidl_generate_interfaces(${PROJECT_NAME}
  "msg/CollisionCandidate.msg"
  "msg/CollisionDecision.msg"
  "msg/InitializationResult.msg"
  "srv/EvaluateCollision.srv"
  "srv/EvaluateCollisionPolicy.srv"
  "srv/SetGantryBaseException.srv"
)

ament_export_dependencies(rosidl_default_runtime)
ament_package()
