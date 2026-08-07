# 압출 궤적 1점. ROS2 -> Unity, 이벤트 기반.
# position 은 bed_origin 프레임 기준, 단위 m.
# extruding=false 이면 travel move (선을 끊으라는 신호).

std_msgs/Header header

geometry_msgs/Point position

float32 width      # 압출 폭 [m]
float32 height     # 레이어 높이 [m]
bool    extruding
uint32  layer
