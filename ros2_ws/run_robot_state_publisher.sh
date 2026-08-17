#!/usr/bin/env bash

set -eo pipefail

ROS_SETUP="/opt/ros/jazzy/setup.bash"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
XACRO="$SCRIPT_DIR/src/printer_description/urdf/printer_assembly.urdf.xacro"

source "$ROS_SETUP"

if [[ ! -f "$XACRO" ]]; then
  echo "오류: 조립 Xacro 파일을 찾을 수 없습니다: $XACRO"
  exit 1
fi

if ! command -v xacro >/dev/null 2>&1; then
  echo "오류: xacro 명령을 찾을 수 없습니다. ros-jazzy-xacro를 설치하세요."
  exit 1
fi

PARAM_FILE="$(mktemp /tmp/robot_description.XXXXXX.yaml)"
URDF_FILE="$(mktemp /tmp/printer_assembly.XXXXXX.urdf)"
cleanup() {
  rm -f -- "$PARAM_FILE" "$URDF_FILE"
}
trap cleanup EXIT

xacro "$XACRO" > "$URDF_FILE"

{
  printf '%s\n' '/**:' '  ros__parameters:' '    robot_description: |'
  sed 's/^/      /' "$URDF_FILE"
} > "$PARAM_FILE"

echo "robot_state_publisher가 다음 조립 Xacro를 읽습니다: $XACRO"
ros2 run robot_state_publisher robot_state_publisher --ros-args --params-file "$PARAM_FILE"
