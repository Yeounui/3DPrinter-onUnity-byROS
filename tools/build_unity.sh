#!/usr/bin/env bash
# Unity Linux 플레이어 배치 빌드.
#
# Editor 는 Windows 에 있고 프로젝트는 WSL 경로에 있다(04 §"Editor 는 Windows 에 남는다").
# 그래서 이 스크립트가 지는 짐은 둘이다.
#
#   1. 경로 번역        — wslpath -w. 여기 밖으로 새면 격리가 깨진 신호다
#   2. 실행 비트 복원   — Windows 쪽 Unity 가 쓴 파일은 +x 가 없다
#
# 산출물은 sim.launch.py 의 UNITY_PLAYER 와 같은 자리에 떨어진다:
#   unity/Voron24Twin/Build/Linux/Voron24Twin.x86_64
#
# 실제 빌드 규칙(씬 목록·타깃·종료 코드)은 Assets/Editor/BuildScript.cs 가 갖는다.
# 이 스크립트는 그걸 부르는 껍데기일 뿐이다.
set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
PROJECT="$REPO_ROOT/unity/Voron24Twin"
OUTPUT="$PROJECT/Build/Linux/Voron24Twin.x86_64"   # ← sim.launch.py:53 UNITY_PLAYER
METHOD="Voron24.BuildScript.BuildLinux"

DEV_BUILD=true
NOGRAPHICS=true
FORCE=false

usage() {
  cat <<'USAGE'
사용법: tools/build_unity.sh [옵션]

  Unity Editor 를 배치 모드로 돌려 StandaloneLinux64 플레이어를 만든다.
  결과: unity/Voron24Twin/Build/Linux/Voron24Twin.x86_64  (sim.launch.py 가 찾는 자리)

옵션
  --release      Development Build 를 끈다. 데모용 (기본은 켜짐 — 04a §2)
  --dev          Development Build 를 켠다 (기본값)
  --graphics     -nographics 를 빼고 돌린다. URP 셰이더 변형 빌드가 -nographics 에서
                 깨지는 경우가 있다 (04a §2). 빌드가 셰이더에서 죽으면 이걸 붙일 것
  --force        Editor 가 열려 있어도(Temp/UnityLockfile) 그냥 진행한다
  -h, --help     이 도움말

Unity 실행 파일 경로
  UNITY_PATH 환경 변수가 1순위다. 하드코딩된 경로는 없다.

    export UNITY_PATH='/mnt/c/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Unity.exe'
    tools/build_unity.sh

  안 주면 ProjectSettings/ProjectVersion.txt 의 에디터 버전으로 Unity Hub 의 기본
  설치 위치들을 훑는다. Windows 쪽 Unity.exe 와 리눅스 네이티브 Unity 둘 다 받는다.
USAGE
}

while [ $# -gt 0 ]; do
  case "$1" in
    --release)  DEV_BUILD=false ;;
    --dev)      DEV_BUILD=true ;;
    --graphics) NOGRAPHICS=false ;;
    --force)    FORCE=true ;;
    -h|--help)  usage; exit 0 ;;
    *) echo "알 수 없는 옵션: $1" >&2; echo >&2; usage >&2; exit 2 ;;
  esac
  shift
done

die() { echo "[build_unity] 오류: $*" >&2; exit 1; }

# ---------------------------------------------------------------- 프로젝트 확인
[ -d "$PROJECT/Assets" ] || die "Unity 프로젝트가 없다: $PROJECT"
[ -f "$PROJECT/Assets/Editor/BuildScript.cs" ] || \
  die "BuildScript.cs 가 없다. $METHOD 를 부를 수 없다."

VERSION_FILE="$PROJECT/ProjectSettings/ProjectVersion.txt"
[ -f "$VERSION_FILE" ] || die "ProjectVersion.txt 가 없다: $VERSION_FILE"

# 에디터 버전은 하드코딩하지 않는다. 프로젝트가 올라가면 여기가 같이 따라간다.
UNITY_VERSION="$(sed -n 's/^m_EditorVersion: *//p' "$VERSION_FILE" | tr -d '\r')"
[ -n "$UNITY_VERSION" ] || die "ProjectVersion.txt 에서 m_EditorVersion 을 못 읽었다."

# ---------------------------------------------------------------- Unity 실행 파일
UNITY_BIN="${UNITY_PATH:-${UNITY_EXE:-}}"

if [ -z "$UNITY_BIN" ]; then
  for cand in \
    "/mnt/c/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe" \
    "/mnt/d/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe" \
    "/mnt/c/Program Files/Unity/Editor/Unity.exe" \
    "$HOME/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity" \
    "/opt/unity/editors/$UNITY_VERSION/Editor/Unity" \
    "/opt/Unity/Editor/Unity"
  do
    if [ -f "$cand" ]; then UNITY_BIN="$cand"; break; fi
  done
fi

if [ -z "$UNITY_BIN" ] || [ ! -f "$UNITY_BIN" ]; then
  cat >&2 <<MSG
[build_unity] 오류: Unity 실행 파일을 못 찾았다 (프로젝트 요구 버전 $UNITY_VERSION).

UNITY_PATH 로 경로를 직접 주면 된다. Windows 쪽 Unity.exe 도 그대로 받는다.

  export UNITY_PATH='/mnt/c/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe'
  tools/build_unity.sh

Hub 에 이 버전이 없으면 먼저 설치할 것. Linux Build Support (Mono) 모듈도 함께
필요하다 (IL2CPP 는 불필요 — 04a 0 단계).
MSG
  exit 1
fi

# ---------------------------------------------------------------- 경로 번역
# Windows 실행 파일이면 -projectPath 도 Windows 경로여야 한다. wslpath 가 9p 바인드
# 마운트를 native C:\ 경로로 풀어주므로 \\wsl.localhost\ 를 타지 않는다.
case "$UNITY_BIN" in
  *.exe|*.EXE|/mnt/*) IS_WINDOWS_EDITOR=true ;;
  *)                  IS_WINDOWS_EDITOR=false ;;
esac

if [ "$IS_WINDOWS_EDITOR" = true ]; then
  command -v wslpath >/dev/null 2>&1 || \
    die "Windows 쪽 Unity 인데 wslpath 가 없다. WSL 에서 실행할 것."
  PROJECT_ARG="$(wslpath -w "$PROJECT")" || die "wslpath 실패: $PROJECT"
else
  PROJECT_ARG="$PROJECT"
fi

# ---------------------------------------------------------------- Editor 락 확인
# 에디터가 열려 있으면 배치 빌드가 라이선스/락 충돌로 죽는데 Unity 가 내는 메시지가
# 불친절해서 원인이 안 드러난다. 먼저 걸러 안내한다.
if [ -f "$PROJECT/Temp/UnityLockfile" ] && [ "$FORCE" != true ]; then
  cat >&2 <<MSG
[build_unity] 오류: Unity Editor 가 이 프로젝트를 열고 있는 것으로 보인다.
  ($PROJECT/Temp/UnityLockfile 존재)

배치 빌드는 같은 프로젝트를 동시에 열 수 없다. Editor 를 닫고 다시 실행할 것.
락 파일만 남은 것이 확실하면 --force 로 무시할 수 있다.
MSG
  exit 1
fi

# ---------------------------------------------------------------- 빌드
CMD=("$UNITY_BIN" -quit -batchmode)
[ "$NOGRAPHICS" = true ] && CMD+=(-nographics)
CMD+=(-projectPath "$PROJECT_ARG"
      -buildTarget Linux64
      -executeMethod "$METHOD"
      -logFile -
      -devBuild "$DEV_BUILD")

echo "[build_unity] Unity     : $UNITY_BIN"
echo "[build_unity] 버전      : $UNITY_VERSION"
echo "[build_unity] 프로젝트  : $PROJECT_ARG"
echo "[build_unity] 산출물    : $OUTPUT"
echo "[build_unity] devBuild  : $DEV_BUILD / nographics: $NOGRAPHICS"
echo

START=$(date +%s)
"${CMD[@]}"
STATUS=$?
ELAPSED=$(( $(date +%s) - START ))

echo
if [ "$STATUS" -ne 0 ]; then
  echo "[build_unity] Unity 가 종료 코드 $STATUS 로 끝났다 (${ELAPSED}s)." >&2
  if [ "$NOGRAPHICS" = true ]; then
    echo "[build_unity] 셰이더 변형 빌드에서 죽은 것이라면 --graphics 로 재시도할 것." >&2
  fi
  exit "$STATUS"
fi

[ -f "$OUTPUT" ] || die "Unity 는 성공했다는데 산출물이 없다: $OUTPUT"

# ★ 실행 비트 복원.
# Windows 쪽 Unity 가 쓴 파일에는 +x 가 없다. 빼먹으면 sim.launch.py 가
# Permission denied 로 죽는데 로그에 원인이 전혀 안 드러난다.
chmod +x "$OUTPUT" || die "chmod +x 실패: $OUTPUT"

echo "[build_unity] 완료 (${ELAPSED}s) — $OUTPUT"
echo "[build_unity] 확인: ros2 launch voron24_bringup sim.launch.py"
