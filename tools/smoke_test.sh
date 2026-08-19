#!/usr/bin/env bash
# W1 통합 게이트 스모크 테스트.
# mock.launch.py 를 띄운 다른 터미널에서 실행할 것.
set -u

FAIL=0
pass() { echo "  OK    $1"; }
fail() { echo "  FAIL  $1"; FAIL=1; }

echo
echo "=== Voron24 digital twin — smoke test ==="
echo

# 0) ros2 데몬
# ros2 CLI 의 토픽 조회는 데몬(127.0.0.1:11511+ROS_DOMAIN_ID)을 경유한다. 데몬은 2시간
# 유휴 후 스스로 종료하는데(ros2cli/daemon/__init__.py 의 serve timeout=2*60*60), 그 뒤
# 자동 재spawn 이 무한 대기에 빠지는 경우가 있다. 명시적 start 는 1초 이내이고 이미 떠
# 있으면 no-op 이므로 먼저 확실히 띄우고 시작한다.
echo "[0] ros2 데몬"
ND=""
if timeout 20 ros2 daemon start >/dev/null 2>&1; then
  pass "데몬 준비됨"
else
  echo "  WARN  데몬 기동 실패 — --no-daemon 으로 우회 (조회가 느려질 수 있음)"
  ND="--no-daemon"
fi

# 1) 토픽 존재
echo
echo "[1] 토픽"
TOPICS=$(timeout 20 ros2 topic list $ND 2>&1); RC=$?
if [ "$RC" -ne 0 ] || [ -z "$TOPICS" ]; then
  # 여기서 조용히 "토픽 없음"으로 넘어가면 퍼블리셔가 죽은 것인지 조회가 실패한 것인지
  # 구분이 안 된다. 원문 에러를 그대로 보여준다.
  fail "ros2 topic list 실패 (rc=$RC)"
  echo "$TOPICS" | sed 's/^/        /'
  echo "        └ 진단: ros2 topic list --no-daemon 이 되면 노드는 정상이고 데몬 문제다."
  echo "                ros2 daemon stop && ros2 daemon start"
else
  for t in /joint_states /tf /robot_description; do
    echo "$TOPICS" | grep -qx "$t" && pass "$t" || fail "$t 없음"
  done
  echo "$TOPICS" | grep -qx "/printer/status" \
    && pass "/printer/status" \
    || echo "  SKIP  /printer/status (voron24_msgs 미빌드일 수 있음)"
fi

# 2) 퍼블리시 주기
echo
echo "[2] /joint_states 주기 (목표 50Hz)"
HZ=$(timeout 6 ros2 topic hz /joint_states 2>&1 | grep -oP 'average rate: \K[0-9.]+' | tail -1)
if [ -n "${HZ:-}" ]; then
  awk -v h="$HZ" 'BEGIN{ if (h>40 && h<60) exit 0; exit 1 }' \
    && pass "${HZ} Hz" || fail "${HZ} Hz (40~60 범위 밖)"
else
  fail "측정 실패 — 퍼블리셔가 돌고 있나요?"
fi

# 3) 조인트 이름 (계약 section 3)
echo
echo "[3] 조인트 이름"
NAMES=$(timeout 10 ros2 topic echo /joint_states --once $ND 2>/dev/null | grep -A5 '^name:' | tr -d ' -')
for j in joint_x joint_y joint_z; do
  echo "$NAMES" | grep -q "$j" && pass "$j" || fail "$j 없음 (계약 section 3 위반)"
done

# 4) TF 체인
echo
echo "[4] TF: bed_origin -> nozzle"
TF=$(timeout 6 ros2 run tf2_ros tf2_echo bed_origin nozzle 2>&1 | grep 'Translation' | tail -1)
if [ -n "${TF:-}" ]; then
  pass "$TF"
else
  fail "TF 조회 실패 — robot_state_publisher 와 URDF 확인"
fi

# 5) Unity 엔드포인트 포트
echo
echo "[5] Unity 엔드포인트 (TCP 10000)"
if command -v ss >/dev/null 2>&1; then
  ss -ltn 2>/dev/null | grep -q ':10000' \
    && pass "10000 LISTEN" \
    || fail "10000 미개방 — ros_tcp_endpoint 가 떠 있나요?"
else
  echo "  SKIP  ss 명령 없음"
fi

echo
[ "$FAIL" -eq 0 ] && echo "=== ALL PASS ===" || echo "=== FAILED ==="
exit $FAIL
