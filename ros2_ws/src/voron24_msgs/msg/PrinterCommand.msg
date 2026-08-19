# Unity -> ROS2 조작 명령.
# command 별 args 규약:
#   "jog"        args = [dx, dy, dz]  (mm)
#   "home"       args = []            (payload 로 "XYZ" 등 축 지정 가능)
#   "pause"      args = []
#   "resume"     args = []
#   "stop"       args = []
#   "load_gcode" payload = 파일 절대경로
#   "set_speed"  args = [scale]
#   "set_extrusion" args = [0.0 | 1.0]

string    command
float32[] args
string    payload
