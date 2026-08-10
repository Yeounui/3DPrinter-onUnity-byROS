# 프린터 상태. ROS2 -> Unity, 5 Hz.
# 계약 00_interface_contract.md section 5

std_msgs/Header header

# "idle" | "printing" | "paused" | "error"
string state

# 0.0 ~ 1.0
float32 progress

uint32 current_layer
uint32 total_layers

string filename
