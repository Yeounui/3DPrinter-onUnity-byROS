# 04 — 단일 진입점 (초안)

> **상태: 초안.** 구현 전. 이 문서에 적힌 파일들은 존재하지 않으므로
> 있는 것처럼 참조하는 코드를 쓰지 말 것.
> **선행**: [00_interface_contract.md](00_interface_contract.md) 숙지
> **관련**: [03_ros2_workflow.md](03_ros2_workflow.md)

---

## 문제

프로그램 두 개를 순서대로 켜야 하는 작업이다.

```
터미널 A:  ros2 launch voron24_bringup mock.launch.py pattern:=sweep
Unity:     Windows 의 Editor 에서 Play 버튼 클릭
```

Unity Editor 의 Play 버튼은 launch file 이 누를 수 없다. 그래서 "따로 실행"이
구조적으로 강제되고 있다. G-code 재생을 붙이면 여기에 슬라이싱까지 얹혀서 단계가
더 늘어난다.

## 목표

```bash
ros2 launch voron24_bringup sim.launch.py                          # mock
ros2 launch voron24_bringup sim.launch.py source:=gcode file:=~/a.gcode
ros2 launch voron24_bringup sim.launch.py source:=stl   file:=~/a.stl
```

명령 하나로 RViz + ROS-TCP-Endpoint + 값 소스 노드 + **Unity 창**까지 전부 뜬다.
Ctrl+C 하나로 전부 내려간다. Unity 창을 닫아도 launch 전체가 내려간다.

---

## 핵심 전환 — Editor Play 대신 **Linux standalone 빌드**

Editor 의 Play 는 자동화할 수 없으므로 launch file 이 `ExecuteProcess` 로 자식 프로세스로
실행할 계획이다.

**`StandaloneLinux64`** 로 빌드. WSL 내에서 실행.

### 대가 — 렌더링은 검증 대상

현재 환경 조사 결과:

| 항목 | 상태 |
|---|---|
| WSLg | 동작 중 (`DISPLAY=:0`, `WAYLAND_DISPLAY=wayland-0`) |
| GPU 패스스루 | 있음 (`/dev/dxg`, `/usr/lib/wsl/lib` 에 NVIDIA 라이브러리) |
| Mesa | 25.2.8 — d3d12 gallium 으로 하드웨어 가속 OpenGL |
| GUI 스택 | RViz2 가 이미 여기서 돌고 있음 = 검증됨 |

Unity Linux 플레이어는 기본적으로 Vulkan 을 먼저 시도하기에

- Player Settings → Graphics APIs 에서 **Vulkan 을 제거하고 OpenGLCore 만** 남겨야 함.
- 또는 실행 시 `-force-glcore`

둘 다 구현하는 게 안전. URP 가 GL 경로에서 의도대로 나오는지는 실제 검증이 필요하다.

---

## Step 0 — 선행 검증 (코드 쓰기 전)

`PlaybackEngines/` 확인 결과 현재 설치된 모듈은 `MetroSupport`, `WebGLSupport`, `windowsstandalonesupport`
**현재 Linux Build Support가 없어 빌드 불가.**

1. Unity Hub → 2022.3.62f3 → Add modules → **Linux Build Support (Mono)**
   - IL2CPP 는 설치하지 않는다. 빌드 시간이 느려진다.
2. Editor 에서 Linux 빌드 → WSL 에서 실행
3. **판정**: URP 렌더링이 멀쩡하고 프레임이 나오면 이 문서대로 진행.
   안 되면 Windows 빌드 + 경로 번역으로 선회 (이 문서 전면 수정).

---

## 구성 요소

### 1. `unity/Voron24Twin/Assets/Editor/BuildScript.cs`

CLI 에서 `-executeMethod` 로 부를 수 있는 빌드 진입점.

- 타겟: `BuildTarget.StandaloneLinux64`
- 출력: `unity/Voron24Twin/Build/Linux/Voron24Twin.x86_64`
- 씬을 `EditorBuildSettings.scenes` 으로 연결. 하드코딩 금지 — 씬이 늘면 자동 반영
- `Development Build` 는 인자로 노출. 골격 검증 단계에서는 켜고, 데모용에서만 끈다
- 실패 시 **exit code 1**. batch build의 silent failure 주의

### 2. `tools/build_unity.sh`

Editor 는 Windows 에 있고 프로젝트는 WSL 파일시스템에 있다. Windows Unity.exe 를
호출하되 경로를 변환해서 넘긴다.

```bash
"$UNITY_EXE" \
  -quit -batchmode -nographics \
  -projectPath "$(wslpath -w unity/Voron24Twin)" \
  -buildTarget Linux64 \
  -executeMethod Voron24.BuildScript.BuildLinux \
  -logFile -
```

주의점:

- `ProjectSettings/ProjectVersion.txt` 에 에디터 연결을 위한 환경 변수가 포함되어야 한다. 하드코딩 금지.
- **에디터가 이미 열려있을 시 배치 빌드가 라이선스/락 충돌로 실패.** 메시지가 불친절하므로 스크립트에서 먼저 감지해 안내할 것.
- **`chmod +x` 를 반드시 붙일 것.** Windows 쪽 Unity 가 `\\wsl.localhost\...` 를 통해
  파일을 쓰므로 실행 비트가 보존되지 않음. 이걸 빼면 launch 가 `Permission denied`로 죽는데 원인이 전혀 드러나지 않음.
- `-nographics` 는 URP 에서 셰이더 변형 빌드에 문제를 일으키는 경우가 있음. 실패하면 옵션을 제외할 것.

### 3. `ros2_ws/src/voron24_bringup/launch/sim.launch.py`

`mock.launch.py`의 확장. 기존 파일은 지우지 않는다. W1 검증 경로다.

추가 인자:

| 인자 | 기본값 | 값 |
|---|---|---|
| `unity` | `build` | `build` \| `editor` \| `none` |
| `source` | `manual` | `manual` \| `gcode` \| `stl` |
| `pattern` | `none` | `none` \| `home` \| `sweep` \| `square` \| `lissajous` |
| `file` | `''` | `source` 가 gcode/stl 일 때 절대경로 |
| `speed` | **`10.0`** | 재생 배속의 초기값. `set_speed` 로 실행 중 변경 — 아래 "배속" 참조 |
| `rviz` | **`false`** | `mock.launch.py` 와 기본값이 다르다 — 아래 참조 |

#### `rviz` 기본값을 뒤집는다

`mock.launch.py` 는 `default_value='true'` 다(`:55`). **`sim.launch.py` 는 `false` 로 선언한다.**
인자 이름이 같으니 값을 명시하지 않으면 조용히 `true` 를 물려받는 것처럼 보인다. 하지만
`DeclareLaunchArgument` 는 launch 파일마다 독립이므로 여기서 다시 선언하면 된다.

두 launch 의 목적이 다르기 때문이다.

| launch | `rviz` | 이유 |
|---|---|---|
| `mock.launch.py` | `true` | W1 게이트가 "RViz 와 Unity 에서 **동시에** 같은 움직임" 이다. 끄면 게이트가 성립하지 않는다 |
| `sim.launch.py` | `false` | Unity 플레이어가 뷰어다. 창이 둘 뜨는 건 "명령 하나로" 라는 목표에 반한다 |

특히 **0 단계 판정을 오염시킨다.** RViz2 와 Unity 플레이어가 같은 Mesa d3d12 GL 경로를
동시에 쓰면 프레임 저하의 원인이 RViz 경합인지 URP 인지 갈라낼 수 없다. 0~4 단계에서는
꺼둔 상태가 기본이어야 하고 대조가 필요할 때만 `rviz:=true` 를 명시적으로 준다.

단 `unity:=none` 일 때는 뷰어가 하나도 없게 된다. 기본값을 `unity` 에 연동한다.

```python
DeclareLaunchArgument(
    'rviz',
    default_value=PythonExpression(["'true' if '", unity, "' == 'none' else 'false'"]),
    description='기본은 꺼짐. unity:=none 일 때만 자동으로 켜짐'),
```

연동이 과하다 싶으면 `default_value='false'` 로 두고 `unity:=none` 사용법에
`rviz:=true` 를 병기하는 것으로 대신한다 — 다만 잊기 쉬운 조합이다.

Unity 기동부의 뼈대:

```python
player = ExecuteProcess(
    cmd=[player_exe,
         '-force-glcore',            # Vulkan 소프트웨어 폴백 방지
         '-screen-fullscreen', '0',  # 창 모드
         '-logFile', '-'],           # stdout 으로 → ROS 로그와 같은 터미널
    output='screen',
    condition=IfCondition(PythonExpression(["'", unity, "' == 'build'"])),
    on_exit=[EmitEvent(event=Shutdown(reason='Unity player closed'))],
)
TimerAction(period=2.0, actions=[player])
```

- `TimerAction` 2 초는 엔드포인트가 먼저 `bind` 하도록 하기 위함. ROS-TCP-Connector가 재시도하긴 하지만 첫 로그가 연결 실패로 지저분해짐.
- 여러 레이어의 프로그램을 한 번에 열고 닫을 수 있도록 `on_exit` 의 `Shutdown` 기능 구현.
- `-logFile -` 로그 통합.

  ```
  [JointState] 자기충돌 해제: 콜라이더 4 개, 6 쌍
  [JointState] bound 3/3 joints under 'voron24'
  [JointState] subscribed to /joint_states
  ```

- `DISPLAY` / `WAYLAND_DISPLAY` 는 launch 프로세스의 환경이 별도
  설정 없이 자식에게 상속됨. 단 `sudo` 나 다른 세션에서 띄우지 말 것.

### 4. Unity 쪽 부트스트랩 — `RosBootstrap.cs`

Linux 빌드에서는 항상 `127.0.0.1`. 인자로 둘 시 나중에 ROS를 다른 머신에 둘 수 있게 함. 우선순위는 낮다.

- `Awake()` 가 아니라 `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` 에서 처리.
  구독을 걸기 전 `JointStateSubscriber.Start()`가 실행되어야 함.
- 인자가 없으면 기존 인스펙터 값을 사용 — Editor 경로가 깨지면 안 됨.

---

## Player Settings 조정

현재 `ProjectSettings.asset` 값과 필요한 변경:

| 항목 | 현재 | 변경 | 이유 |
|---|---|---|---|
| `runInBackground` | `1` | **그대로** | 프로세스 종료 시 ROS 메시지 처리가 멈춤. 터미널을 클릭하면 트윈이 멈추기에 원인 추적이 어려움 |
| `fullscreenMode` | `1` (전체화면 창) | `3` (Windowed) | 터미널 옆에 둠. |
| `resizableWindow` | `0` | `1` | |
| Graphics APIs | Vulkan 우선 | **OpenGLCore** | 위 "대가 — 렌더링" 참조 |

---

## 빌드 시 유지/제외 기능

현재 `Assets/Scripts/` 를 확인한 결과 이식성 문제는 없음.

- `ConnectionMonitor.cs` 의 오버레이는 `OnGUI` 라서 **빌드에서도 출력.** Editor 없이 "Unity 혹은 ROS" 에서 문제가 발생하는지 판별하는 수단이라 빌드 전환 시 이점이다.
- `KeyboardJointController.cs` 도 동작. 단 배선을 고쳐야 한다 — 아래 참조.
- `#if UNITY_EDITOR` 로 제외된 건 `JointStateSubscriber` 의 "Rebind Joints" 컨텍스트 메뉴. 디버깅 기능이고 자동 바인딩은 런타임에 정상 동작한다. `-logFile -`로 대체
- 파일: `~/.config/unity3d/<Company>/Voron24/Player.log`

### Unity 쪽 배선 수정 — 입력도 그래프를 거쳐야 한다

현재 `KeyboardJointController.cs:115` 는 `JointStateSubscriber.SetTarget()` 을 직접 호출한다.
키 입력이 ROS 를 한 바이트도 거치지 않는다. 바로 옆에 `PrinterCommandPublisher.Jog(dx,dy,dz)`
가 이미 구현되어 있으므로 그쪽으로 돌린다.

```
(현재)  키 입력 ──▶ SetTarget()                              ROS 우회
(수정)  키 입력 ──▶ Jog() ──▶ /printer/cmd ──▶ printer_state_node ──▶ /joint_states ──▶ 화면
```

- 대가는 왕복 지연이다. 50 Hz 두 홉이면 체감되지 않는다.
- ROS 미연결 시 키보드가 죽으므로 `JointStateSubscriber.IsConnected == false` 일 때만
  기존 직접 쓰기로 폴백한다 (`LocalMockDriver` 와 같은 규칙이다).
- `Jog` 의 `args` 단위는 **mm** 다 (계약 §6 의 SI 예외). Unity 쪽 조인트 값은 m 이므로
  여기서 ×1000 이 필요하다. **중복 변환 주의** — 되돌아오는 `/joint_states` 는 이미 m 다.

**의도된 예외 둘.** 아래는 ROS 를 거치지 않지만 그대로 둔다.

| 스크립트 | 이유 |
|---|---|
| `LocalMockDriver.cs` | 엔드포인트 없이 Unity 단독 구동용. "Unity 문제인가 ROS 문제인가" 판별 수단이다 (CLAUDE.md — 유지 지시) |
| `NozzleTracker.cs` | 노즐–베드 상대좌표를 Unity 안에서 계산해 화면에만 쓴다. 계약에 대응 토픽이 없다. 시각화 전용 |

---

## 두 모드를 남기는 이유

C# 스크립트를 고칠 때마다 다시 빌드해야 하므로 `unity` 인자를 남겨 에디터로도 실행할 수 있게 둔다.

| 모드 | 용도 | 동작 |
|---|---|---|
| `unity:=editor` | **개발** | 엔드포인트까지만. Play 는 Windows Editor 에서 직접 — 즉 지금 상태 |
| `unity:=build` | **데모 / 사용** | Linux 플레이어까지 자동 기동 |
| `unity:=none` | ROS 단독 디버깅 | 엔드포인트도 안 띄움. **`rviz` 가 자동으로 켜짐** (위 참조) |

에디터는 개발용 경로로 격리한다.

`unity:=editor` 는 Windows Editor 를 쓰므로 경로 번역 문제를 다시 만난다. 개발 중 `load_gcode` 를 테스트할 때는 `unity:=build` 를 쓸 것.

### Editor 는 Windows 에 남는다 — WSL 에 설치하지 않는다

**결정.** 주 실행 경로는 Linux 빌드이고 Editor 는 C# 디버깅용 보조 경로다. 보조 경로 하나를
위해 Hub 를 Linux 에서 굴리고 기존 Windows 워크플로를 폐기할 이유가 없다.

따라올 비용은 둘이고 둘 다 임시가 아닌 상시 감수 대상이다.

| 비용 | 대응 |
|---|---|
| `wslpath -w` 경로 번역 | `build_unity.sh` 안에 가둔다. 다른 스크립트로 새지 않게 할 것 |
| 실행 비트 유실 | 빌드 직후 `chmod +x` (위 "2. `tools/build_unity.sh`") |

둘 다 빌드 스크립트 한 곳에서만 나타나므로 관리 범위가 좁다. 스크립트 밖에서 같은 문제를
또 다루게 되면 그때는 격리가 깨졌다는 신호로 본다.

---

## 노드 구성 — 모든 입출력을 그래프 안으로

**원칙: 런타임의 모든 데이터 입출력은 노드 사이의 토픽/액션이다.** 파일을 launch 인자로
직접 밀어넣거나, Unity 안에서 조인트를 직접 쓰거나, 슬라이서를 launch 가 프로세스로
돌리는 경로는 두지 않는다.

```
   Unity 키보드/UI ──┐
                     ├──▶ /printer/cmd ──▶ printer_state_node ──▶ /joint_states ──▶ RViz, Unity
   터미널 키보드 ────┘                        │           ▲
                                 /printer/playback    /printer/target
                                              ▼           │
                                        ┌─────┴───────────┴─────┐
                                        │  값 소스 (배타 선택 )    │──▶ /printer/status,
                                        │  manual_publisher     │    /printer/extrusion ──▶ Unity
                                        │  gcode_player_node ◀──┼─── SliceModel action ◀── voron24_slicer
                                        └───────────────────────┘
```

### 책임 분리

| 노드 | 소유 |
|---|---|
| `printer_state_node` | **`/joint_states` 의 유일한 발행자.** `/printer/cmd` 의 유일한 구독자. `jog` 적분, `home`, URDF 리밋 클램프 |
| 값 소스 (`manual_publisher` / `gcode_player_node`) | 궤적 생성. 스트림 제어(`pause`/`resume`/`stop`/`set_speed`/`load_gcode`) |
| `voron24_slicer` | STL → G-code. action 서버 |

**`jog` 의 좌표 적분은 `printer_state_node` 만 한다.** 입력 장치(Unity 키보드, 터미널
키보드, 향후 UI)는 전부 `/printer/cmd` 에 델타만 던지는 동등한 peer 다.

`set_speed` 와 `pause` 는 시간축을 다루는 명령이라 보간기가 있는 소스 쪽이 처리해야 한다.
상태 노드가 값을 붙잡고 있는 방식으로 구현하면 재개 시 좌표가 튄다. 그래서 상태 노드는
`/printer/cmd` 를 받아 스트림 계열 명령만 `/printer/playback` 으로 되넘긴다.

### 토픽 — 새 `.msg` 없음

새 토픽 둘 다 기존 메시지 타입을 재사용한다. `.msg` 정의가 안 바뀌므로 계약 §3 은
그대로고 `contract_check.py` 도 볼 것이 없다.

| 토픽 | 타입 | 발행 | 구독 |
|---|---|---|---|
| `/printer/cmd` | `voron24_msgs/PrinterCommand` | Unity | `printer_state_node` |
| **`/printer/playback`** | `voron24_msgs/PrinterCommand` | `printer_state_node` | 활성 소스 |
| **`/printer/target`** | `sensor_msgs/JointState` | 활성 소스 | `printer_state_node` |
| `/joint_states` | `sensor_msgs/JointState` | `printer_state_node` | RViz, Unity |
| `/printer/status` | `voron24_msgs/PrinterStatus` | 활성 소스 | Unity |
| `/printer/extrusion` | `voron24_msgs/ExtrusionPoint` | 활성 소스 | Unity |

> 다만 계약 §5 의 토픽 표에는 두 줄을 **추가해야 한다.** 타입 변경이 아니라 추가이므로
> 계약 변경 절차(PR + 3 인)가 필요한지는 팀 판단 사항이다. 미결로 남긴다.

### 소스 전환은 remapping 으로 — 노드 코드를 안 고친다

W1 검증 경로를 보존하기 위해 소스 전환은 **launch remapping** 으로 한다.

```python
remappings=[('/joint_states', '/printer/target')]
```

`mock.launch.py` 는 remapping 없이 그대로 `/joint_states` 에 직접 쏘므로 지금과 동일하게
동작한다. `sim.launch.py` 에서만 상태 노드를 끼운다.

`manual_publisher` 가 `/printer/playback` 을 구독해 `pause`/`set_speed` 를 흉내내는 것은
선택 사항이다. 넣더라도 `voron24_msgs` 미빌드 시 죽지 않도록 **반드시 `HAS_MSGS` 분기
안쪽에** 둔다 (CLAUDE.md — 이 방어를 제거하지 말 것).

### 상태 노드의 두 모드

`/printer/target` 과 `jog` 이 서로 싸우지 않도록 모드를 둔다. **`manual` 이 초기 상태다.**

| 모드 | 진입 | `/printer/target` | `jog` |
|---|---|---|---|
| `manual` | 초기 상태, `stop`, **`jog` 수신** | 무시 | 누적 적분 |
| `playing` | `load_gcode`, `resume` | 통과 | → `manual` 로 전환 |

`playing` 중에 `jog` 이 들어오면 거부하지 않고 `/printer/playback` 으로 `pause` 를 보낸 뒤
`manual` 로 전환한다. 사용자가 키를 눌렀는데 아무 반응이 없는 것보다, 재생이 멈추고
손이 먹는 편이 의도에 가깝다. `resume` 으로 되돌아간다.

`home` 은 어느 모드에서든 받되 `playing` 이면 먼저 `stop` 을 `/printer/playback` 으로 보낸다.

`source:=manual` + `pattern != none` 인 경우에는 상태 노드가 `/printer/target` 을 통과시켜야
하므로 launch 가 기동 직후 `resume` 을 한 번 발행한다 (`file:=` 과 같은 방식).

---

## G-code / STL 이 붙는 자리

값의 출처만 바뀌고 나머지 파이프라인은 동일. `source` 인자가 소스 노드를 고른다.

```
source:=manual   →  manual_publisher      (신규. mock_publisher 는 그대로 남는다 — 아래 참조)
source:=gcode    →  gcode_player_node
source:=stl      →  gcode_player_node + voron24_slicer
```

### `file:=` 은 파라미터가 아니라 발행이다

노드가 launch 파라미터로 경로를 받아 직접 열면 **입력 경로가 둘**(파라미터 / `load_gcode`)이
된다. 진입점을 `load_gcode` 하나로 통일하고 `file:=` 은 launch 가 한 번 발행하는 것으로
바꾼다.

```python
TimerAction(period=3.0, actions=[ExecuteProcess(cmd=[
    'ros2', 'topic', 'pub', '--once', '/printer/cmd',
    'voron24_msgs/PrinterCommand',
    "{command: 'load_gcode', payload: '" + file + "'}"])])
```

`source:=stl` 이면 같은 자리에서 `ros2 action send_goal` 로 슬라이서를 호출하고, 액션
result 의 G-code 경로를 다시 `load_gcode` 로 발행한다. 이 두 단계는 셸 파이프로 엮기
지저분하므로 `voron24_bringup` 에 **`job_starter` 노드**(oneshot)를 두는 편이 낫다.
액션 클라이언트 + 퍼블리셔 20 줄짜리다.

파일 읽기 자체는 `gcode_player_node` 내부 백그라운드 스레드에서 한다. 이건 노드 내부
구현이므로 원칙 위반이 아니다.

---

## `gcode_player_node` — 실시간 해석 재생

**G-code 전체를 미리 샘플 배열로 펼치지 않는다.** 파일을 한 줄씩 읽어 세그먼트로 바꾸고
그 세그먼트 위를 50 Hz 타이머가 실시간으로 진행하며 현재 좌표를 뽑는다. 재생 도중의
`pause` / `set_speed` / `stop` 이 즉시 먹어야 하고, 수십 MB짜리 G-code 를 통째로 메모리에
올리지 않기 위해서다.

### 문제의 본질

G-code 가 주는 것은 목표 좌표와 이송속도(`F`) 뿐이고 `/joint_states` 가 요구하는 것은
**50 Hz 등간격 샘플**이다. 그 사이를 메우는 보간기를 만드는 것이 이 노드의 일이다.

### 파일 분리

`patterns.py` 가 rclpy 없이 단독 테스트되는 관례를 그대로 따른다.

```
voron24_gcode/
  gcode_parser.py      # 순수 파이썬. 텍스트 -> Move 스트림 (mm 유지)
  motion.py            # 순수 파이썬. Move 스트림 -> 시간축 샘플 (mm)
  gcode_player_node.py # rclpy. publish 직전에 ÷1000
```

```bash
python3 -m voron24_gcode.gcode_parser sample.gcode   # 세그먼트 덤프
python3 -m voron24_gcode.motion sample.gcode         # 시간축 궤적 검증
```

단위는 계약 §2 그대로 — **파서와 보간기는 G-code 도메인인 mm 를 유지하고 mm→m 은 노드가
publish 직전에 한 번만** 한다. 순수 모듈 안에서 미리 나누면 변환 지점이 둘로 늘어난다.

### 보간 루프

```python
def sample(self, dt):
    budget = self.feed_mm_s * dt * self.speed_scale   # 이번 틱에 갈 거리
    while budget > 0 and self.seg:
        remain = self.seg.length - self.s
        if remain > budget:
            self.s += budget
            break
        budget -= remain                # 세그먼트 소진 -> 다음 것으로
        self.emit_vertex(self.seg.end)  # ExtrusionPoint 는 여기서
        self.seg = next(self.moves, None)
        self.s = 0.0
    return self.seg.point_at(self.s)
```

이 루프에서 `while` 은 필수다. 슬라이스된 곡선은 0.2 mm 짜리 세그먼트가 수천 개인데
50 Hz · 100 mm/s 면 한 틱에 2 mm 다. 틱당 한 세그먼트만 소비하면 실제보다 10 배 느리게 기고
반대로 긴 직선 하나는 여러 틱에 걸쳐 나눠 먹어야 한다.

가속도(트라페조이드 프로파일)는 넣지 않는다. 시각화에는 등속으로 충분하고 넣는 순간
룩어헤드 큐가 필요해져 난이도가 급등한다. `F` 는 축별 최대속도로 클램프만 한다.

### 배속 — 기본값은 `10.0`

**시간축은 벽시계에 맞추지 않는다.** 실물 프린트는 시간 단위라 등속(`1.0`) 재생이면 데모가
성립하지 않는다. 위 루프의 `speed_scale` 을 그대로 배속으로 쓰고 기본값을 `10.0` 으로 둔다.

- 노드 파라미터 `speed_default`(기본 `10.0`). `sim.launch.py` 의 `speed` 인자가 이걸 넘긴다
- `set_speed` 가 들어오면 `speed_scale` 만 바뀐다. 세그먼트 커서(`self.seg`, `self.s`)는
  건드리지 않으므로 재생 도중 배속을 바꿔도 좌표가 튀지 않는다
- 배속은 시간축만 늘리고 줄인다. 궤적 형상과 `ExtrusionPoint` 열은 배속과 무관하게 동일하다
- `1.0` 이 정확히 실시간이라는 보장은 없다. `F` 가 축별 최대속도로 클램프되므로 실제 재생은
  G-code 가 상정한 시간보다 느려질 수 있다. 배속은 재생 속도 조절기일 뿐이다

단계는 고정 리스트로 오간다. 키를 누른 횟수와 배속이 1:1 로 대응해야 눈으로
따라갈 수 있기 때문이다.

```python
SPEED_STEPS = (0.5, 1.0, 2.0, 5.0, 10.0, 25.0, 50.0, 100.0)
```

`set_speed` 의 `args[0]` 은 리스트 안의 값일 필요가 없다. 단계는 키보드 쪽 편의일 뿐이고
노드는 임의의 양수를 받는다. 0 이하는 거부하고 경고한다. `0.0` 은 `pause` 가 처리한다.

`source:=manual` 일 때는 배속이 의미가 없다. `manual_publisher` 는 `set_speed` 를 무시한다.

### ExtrusionPoint 는 틱이 아니라 세그먼트 꼭짓점에서

50 Hz 로 샘플한 점만 흘리면 코너가 뭉개진다. 위 루프에서 세그먼트를 소진할 때마다 그
끝점을 발행하면 Unity 의 선이 G-code 원본 형상과 정확히 일치한다.

- `E` 가 증가하지 않는 구간은 `extruding=false` — Unity 가 선을 끊는다 (`ExtrusionPoint.msg` 주석의 정의).
- `width` 는 압출량에서 역산: 필라멘트 1.75 mm 기준 `width = (π·0.875²·ΔE) / (거리 × layer_height)`.
  PrusaSlicer 가 심는 `;WIDTH:` 주석을 그대로 읽어도 된다.

### 파싱 대상

| 지원 | 무시 |
|---|---|
| `G0`/`G1` `X Y Z E F` | `M104`/`M109`/`M140`/`M190` (온도) |
| `G28` 홈 | `M106` 팬 |
| `G90`/`G91` 절대/상대 | `M600` 등 |
| `G92` 좌표 리셋 | `T0` |
| `M82`/`M83` 익스트루더 모드 | |
| `;LAYER_CHANGE`, `;WIDTH:` 주석 태그 | |

`G2`/`G3` 원호는 미지원이다. 프로파일에서 arc fitting 을 끄므로 나오지 않는다. 나오면 경고 후 무시.

파서는 직접 쓴다(150 줄 내외). PyPI 파서를 쓰지 않는 이유는 셋이다. ① 전체를 리스트로
반환하는 API 가 많아 스트리밍과 맞지 않음 ② `;WIDTH:` 같은 주석 태그를 버리는 구현이 있음
③ 위 표만큼으로 방언이 고정되어 있어 라이브러리 의존이 이득보다 크다.

### 구독 — `/printer/cmd` 가 아니라 `/printer/playback`

플레이어는 `/printer/cmd` 를 직접 듣지 않는다. `printer_state_node` 가 걸러 넘긴
스트림 계열만 받는다 (위 "노드 구성").

| command | 동작 |
|---|---|
| `load_gcode` (payload=절대경로) | 파일 열기. 백그라운드 스레드 |
| `pause` / `resume` | 보간기 정지/재개 |
| `stop` | 스트림 폐기, `state='idle'` |
| `set_speed` (args[0]) | 위 루프의 `speed_scale` |

`jog` / `home` 은 여기 없다 — 상태 노드의 몫이다.

출력은 `/printer/target`(JointState, mm→m 변환 후)과 `/printer/extrusion`,
그리고 `/printer/status`. `progress` 는 바이트 오프셋 / 파일 크기.

### 주의점 둘

1. **`/printer/target` 발행자도 하나여야 한다.** `sim.launch.py` 의 `source` 인자가
   **배타 선택**임을 보장할 것. `/joint_states` 는 상태 노드가 단독 소유하므로 이제
   여기서는 안전하다.
2. **좌표 클램프는 상태 노드가 한다.** 모든 prismatic 의 `lower` 가 0 이라 음수 좌표나
   250 mm 초과는 URDF 리밋을 넘는다(스커트/프라임 라인이 베드 밖으로 나가는 G-code 는
   흔하다). 리밋 지식을 두 곳에 두지 않기 위해 플레이어는 클램프하지 않고 그대로 흘린다.
   상태 노드가 클램프하고 첫 위반에서 한 번만 경고한다.

---

## `voron24_slicer` — STL 입력

STL 도 그래프 안으로 들어와야 하므로 슬라이서는 **액션 서버 노드**다. launch 가
`ExecuteProcess` 로 슬라이서를 직접 돌리지 않는다.

```
Unity/CLI ──▶ SliceModel action ──▶ voron24_slicer ──▶ (result: gcode 경로)
                                          │                      │
                                     subprocess              load_gcode
                                     PrusaSlicer                 ▼
                                                        gcode_player_node
```

| 필드 | 내용 |
|---|---|
| goal | `string stl_path`, `string profile` (기본 `voron24_250`) |
| feedback | `float32 progress`, `string stage` — PrusaSlicer stdout 파싱 |
| result | `string gcode_path`, `bool success`, `string message` |

- **액션이지 서비스가 아니다.** 슬라이싱은 수십 초 걸리고 취소가 가능해야 한다.
- `SliceModel.action` 은 **`voron24_slicer` 패키지 자체에 정의**한다. `voron24_msgs` 에 넣으면
  계약 변경(PR + 3 인 승인)이 되므로 피한다.
- 노드 내부에서는 `subprocess` 로 CLI 를 호출한다. libslic3r 을 링크하지 않는 이유:
  **AGPL-3.0** 이라 링크 시 라이선스가 전파된다. 별도 프로세스 exec 은 해당하지 않는다.
  빌드 의존(Boost/TBB/CGAL/OpenVDB)을 colcon 에 얹지 않아도 되는 것은 덤이다.
- 따라서 런타임 의존성은 여전히 **순수 파이썬 + 외부 실행파일 하나**다. 슬라이서 실행파일
  경로는 ROS 파라미터로 노출해 하드코딩하지 않는다.

### 결정: PrusaSlicer (apt)

```bash
sudo apt install prusa-slicer     # noble/universe, 2.7.2
prusa-slicer --export-gcode --load voron24_250.ini -o out.gcode model.stl
```

- 설정이 단일 `.ini` 라 레포에 커밋하고 diff 로 추적할 수 있다. `voron24_params.xacro` 와 같은 방식이다
- `--export-gcode` 는 콘솔 모드로 X 없이 동작.
  막히면 `xvfb-run -a` 한 겹을 씌운다.
- `--dont-arrange`, `--center 125,125` 로 베드 배치까지 스크립트에서 통제.

### 프로파일 — `tools/slicer/voron24_250.ini`

PrusaSlicer 공식 번들에 Voron 프로파일은 없다. 조달 경로는 둘.

1. OrcaSlicer(또는 커뮤니티 `.ini`)에서 Voron 2.4 250 설정을 가져와 PrusaSlicer 형식으로 옮긴다
2. 베드 250×250 만 맞춰 직접 만든다. 시뮬레이션 전용이므로 압출·온도·재료는 무의미하고 궤적만 맞으면 된다

어느 쪽이든 아래는 못박는다. 파서를 단순하게 유지하기 위한 제약이다.

| 설정 | 값 | 이유 |
|---|---|---|
| arc fitting | **끔** | `G2`/`G3` 가 안 나와 파서가 `G0`/`G1` 만 다루면 됨 |
| 좌표 모드 | `G90` (절대) | 상대(`G91`)는 파서에서 **거부**한다. 조용히 틀린 궤적이 나오는 것보다 낫다 |
| start/end G-code | **비움** | 홈잉 매크로가 궤적을 오염시킨다 |
| 베드 | 250×250, origin 코너 | 계약의 `bed_origin` 과 부호가 맞는지 `pattern:=sweep` 로 먼저 검증할 것 |

`F`(피드레이트)는 재생 속도로 쓴다. `E`(압출량)는 버리지 않는다. `extruding` 판정과
`width` 역산에 필요하다(위 "ExtrusionPoint 는 세그먼트 꼭짓점에서").

`Z` 는 레이어 단위로만 변하므로 `joint_z` 궤적은 계단식이 된다. 세그먼트 보간이 X/Y/Z 를
같은 파라미터 `s` 로 함께 훑으므로 레이어 전환의 Z 상승도 그 세그먼트의 이송시간만큼
자연히 이어진다. 별도 처리는 필요 없다.

> 좌표 → 조인트 변환은 **÷1000 한 번뿐**이다(계약 §2). 슬라이서 출력은 mm 절대좌표로
> 고정하고, 변환은 ROS2 노드의 publish 직전에서만 한다. 중복 변환이 이 레포에서 가장 위험한 버그다.

---

## `manual_publisher` 신규 추가 + 키보드 조작

G-code 소스가 생겨도 **사람이 축을 직접 움직여 보는 일은 계속 필요하다.** 그 역할을 맡는
정식 값 소스로 `manual_publisher` 를 새로 만든다. 주된 동작은 키보드 조작이다.

> 시뮬레이션 전용이라는 범위는 그대로다. 이름이 `real_*` 이 아니라 `manual_*` 인 이유다.

### 기존 mock 경로는 손대지 않는다

**`mock_publisher_node.py` 와 `mock.launch.py` 는 그대로 둔다.** 파일명·노드명·클래스명·기본
`pattern` 값 전부 현재 상태를 유지하고 새 노드는 그 옆에 추가한다.

W1 검증 경로가 살아 있어야 새 노드가 깨졌을 때 비교 대상이 남는다.

| | 기존 | 신규 |
|---|---|---|
| 노드 | `mock_publisher` | `manual_publisher` |
| 파일 | `mock_publisher_node.py` | `manual_publisher_node.py` |
| launch | `mock.launch.py` | `sim.launch.py` (`source:=manual`) |
| 출력 | `/joint_states` 직접 | `/printer/cmd` + `/printer/target` |
| `pattern` 기본값 | `lissajous` (변경 없음) | `none` |
| 주 용도 | W1 검증·회귀 기준선 | 키보드 조작, G-code 와 나란한 값 소스 |

두 노드를 **동시에 띄우지 않는다.** 둘 다 결국 조인트 값을 만들므로 같이 돌리면 발행자가
둘이 된다. `sim.launch.py` 의 `source` 가 배타 선택인 것과 같은 이유다.

### 자동 패턴 — `patterns.py` 를 공유한다

`manual_publisher` 도 `pattern` 인자를 받는다. 다만 **`patterns.py` 를 그대로 import 한다.**
궤적 수식을 새 파일에 복사하면 `patterns.py` ↔ `LocalMockDriver.cs` 동기화 계약(CLAUDE.md)에
세 번째 사본이 생긴다. 이 레포에서 가장 비싼 종류의 실수다.

`patterns.py` 에 가하는 변경은 하나뿐이고 그것도 추가다.

- `PATTERNS` 튜플에 `'none'` 추가 + `PatternGenerator._none()` 신설
- 기존 네 패턴의 수식은 **무변경** — `mock_publisher` 와 `LocalMockDriver.cs` 가 같이 쓴다

`mock_publisher_node.py:69` 의 `if self.pattern not in PATTERNS` 검사는 튜플이 늘어나도
그대로 통과한다. 기존 노드의 기본값이 `lissajous` 이므로 `'none'` 이 추가돼도 동작은 같다.

| `pattern` 값 | `manual_publisher` 에서의 동작 |
|---|---|
| **`none`** (신규, **기본값**) | 자동 궤적 없음. 키보드 입력만 반영 |
| `home` \| `sweep` \| `square` \| `lissajous` | 기존 수식 그대로. 이때 키보드는 무시 |

### 키보드 입력은 `/printer/cmd` 로 나간다 — `/printer/target` 이 아니다

**적분과 클램프는 `printer_state_node` 가 단독으로 한다.** `manual_publisher` 가 좌표를
직접 누산해 `/printer/target` 으로 쏘면 같은 로직이 두 곳에 생긴다.
`patterns.py` ↔ `LocalMockDriver.cs` 중복과 같은 종류의 함정이다.

```
터미널 키보드 ──▶ manual_publisher ──┐
                                     ├──▶ /printer/cmd ──▶ printer_state_node ──▶ /joint_states
Unity 키보드  ──▶ KeyboardJointController ┘
```

두 입력 장치가 완전히 동등해진다. 즉 `manual_publisher` 의 출력은 둘이다.

| 출력 | 조건 |
|---|---|
| `/printer/cmd` (`PrinterCommand`) | 키보드 입력. `jog` / `home` |
| `/printer/target` (`JointState`) | `pattern != none` 일 때만 |

### 키맵

Unity 의 `KeyboardJointController.cs` 와 의미를 맞춘다.

| 축 | Unity | 터미널 |
|---|---|---|
| X − / + | `←` / `→` | `a` / `d` |
| Y − / + | `↓` / `↑` | `s` / `w` |
| Z − / + | `PgDn` / `PgUp` | `q` / `e` |
| 원점 복귀 | `Home` | `h` |
| 배속 − / + | `[` / `]` | `[` / `]` |
| 배속 `1.0` 복귀 | `\` | `\` |
| 종료 | — | `Ctrl-C` |

배속 키는 `SPEED_STEPS` 를 한 칸씩 오간다(위 "배속"). 양 끝에서는 더 가지 않고 현재 값을
그대로 다시 보낸다. 눌러도 아무 일이 없는 것보다 현재 배속이 로그에 다시 찍히는 편이 낫다.
축 키와 달리 **키 홀드 반복(`repeat_hz`)은 걸지 않는다.** 한 번 눌러 한 단계다.

배속 키가 만드는 것도 `/printer/cmd` 의 `PrinterCommand` 다. `jog` 과 같은 경로로 나가고,
`printer_state_node` 가 스트림 계열이라고 판단해 `/printer/playback` 으로 되넘긴다
(위 "노드 구성"). 키보드가 플레이어에 직접 말을 거는 경로는 여기서도 없다.

```
키 `]` ──▶ /printer/cmd {set_speed, args:[25.0]} ──▶ printer_state_node
                                                          │
                                          /printer/playback ──▶ gcode_player_node
```

`set_speed` 는 스트림 명령이라 `jog` 과 달리 **모드를 `manual` 로 되돌리지 않는다.**
재생 중 배속만 올리는 것이 원래 의도인데 그때마다 재생이 멈추면 쓸모가 없다.

터미널에서 방향키를 1차 키맵으로 쓰지 않는 이유는 화살표가 단일 문자가 아니라 3 바이트
ESC 시퀀스(`\x1b[A` 등)라서 상태 기계가 필요하기 때문이다. 문자 키를 먼저 넣고 화살표
지원은 선택으로 둔다.

`jog` 의 단위는 **mm** 다 (계약 §6 SI 예외). 노드 파라미터 `step_mm`(기본 `1.0`)과
`repeat_hz`(키 홀드 시 반복률, 기본 `20.0`)로 이동량을 조절한다.

### ★ 터미널 stdin — launch 로 띄우면 키가 안 먹는다

**`Node(...)` 로 launch 에서 띄운 프로세스에는 stdin 이 연결되지 않는다.**
`teleop_twist_keyboard` 가 늘 별도 터미널에서 `ros2 run` 으로 실행되는 이유다.
`output='screen'` 이나 `emulate_tty=True` 로는 해결되지 않는다.

- **원칙: `manual_publisher` 의 키보드 모드는 별도 터미널에서 `ros2 run` 한다.**

  ```bash
  ros2 run voron24_gcode manual_publisher            # 이 터미널에서 키 입력
  ```

- launch 에 넣어야 한다면 `prefix='xterm -e'` 로 창을 따로 띄우는 수밖에 없다.
  WSLg 에서 `xterm` 별도 설치가 필요하므로 기본 경로로 삼지 않는다.
- `sim.launch.py` 는 `pattern != none` 일 때만 이 노드를 자동 기동한다.
  키보드 조작은 **Unity 창에서** 하는 것이 기본 경로다. 그쪽은 stdin 문제가 없다.

구현은 `termios` + `tty.setcbreak(sys.stdin)` 에 논블로킹 읽기다. **종료 시 반드시
`termios.tcsetattr` 로 원상복구**할 것. 빼먹으면 노드가 죽은 뒤 터미널 에코가 꺼진 채
남아 `reset` 을 쳐야 한다. `try/finally` 로 감싼다.

### 레포에 "mock" 이 세 가지 뜻으로 쓰이고 있다

셋 다 **이름을 바꾸지 않는다.** 다만 읽는 사람이 셋을 같은 것으로 오해하므로 구분은 필요하다.

| # | 뜻 | 위치 | 처분 |
|---|---|---|---|
| ① | **값 소스로서의 임시 퍼블리셔** | `mock_publisher_node.py`, `mock.launch.py` | **유지.** 옆에 `manual_publisher` 를 추가 |
| ② | **형상으로서의 박스** (`use_meshes:=false`) | URDF xacro 주석, `contract_check.py` 출력 | 유지. ①과 헷갈리므로 **용어만** "박스 프리뷰"로 정리 |
| ③ | **ROS 우회 구동기** | `LocalMockDriver.cs` | **유지.** 진짜 mock 이다 |

②는 메시가 들어오면 사라질 임시 형상이다. ③은 CLAUDE.md 가 유지를 명시한다.

### 신규 — 코드

기존 파일에 손대는 것은 추가 3 줄뿐이다. 나머지는 전부 새 파일이다.

| 파일 | 성격 | 내용 |
|---|---|---|
| `voron24_gcode/manual_publisher_node.py` | **신규** | `class ManualPublisher`, 노드명 `manual_publisher`, `pattern` 기본값 `'none'`, 키보드 입력 → `/printer/cmd` |
| `voron24_gcode/patterns.py` | 추가 | `PATTERNS` 에 `'none'`, `PatternGenerator._none()`. **기존 수식 무변경** |
| `voron24_gcode/setup.py:22` | 추가 | `'manual_publisher = voron24_gcode.manual_publisher_node:main'` 한 줄 추가. 기존 `mock_publisher` 줄은 **남긴다** |
| `voron24_gcode/mock_publisher_node.py` | **무변경** | 파일명·클래스명·노드명·`pattern` 기본값 `'lissajous'` 그대로 |
| `voron24_bringup/launch/mock.launch.py` | **무변경** | `executable='mock_publisher'` 그대로 |
| `LocalMockDriver.cs` | **무변경** | 주석의 `mock_publisher` 참조도 여전히 맞는 말이다 |

새 노드에서 `mock_publisher_node.py` 를 복사해 시작하되 다음 둘은 **베끼지 말 것.**

- `/joint_states` 직접 발행. 새 노드의 출력은 `/printer/cmd` 와 `/printer/target` 이다
- `s.filename = f'mock_{p}.gcode'` — 존재하지 않는 파일명을 흘리면 Unity 가 실제 파일로
  오인한다. 새 노드에서는 `'<manual>'` / `f'<pattern:{p}>'` 로 쓴다

### 신규 — launch 와 인자

- **`mock.launch.py` 는 그대로 둔다.** W1 게이트 문서가 "실행하면 움직인다" 를 전제하므로
  `pattern` 기본값 `lissajous` 도 유지된다.
- `sim.launch.py` 의 `source:=manual` 이 `manual_publisher` 를 띄운다. `source:=mock` 은
  두지 않는다. 기존 mock 경로는 `mock.launch.py` 로 간다.
- `pattern:=sweep` 등 패턴 인자 이름은 두 launch 에서 같다. **기본값만 다르다**
  (`mock.launch.py` 는 `lissajous`, `sim.launch.py` 는 `none`).

### 신규 — 문서

기존 문서의 `mock_publisher` 표기는 그대로 두고 새 노드를 아는 문장만 더한다.

| 파일 | 추가할 내용 |
|---|---|
| `README.md` | `sim.launch.py` 사용법에 `source:=manual` 항목 |
| `CLAUDE.md` | "명령어" 절에 새 launch, "Mock 우선 전략" 절에 두 노드의 역할 구분 한 줄 |
| `docs/03_ros2_workflow.md` | `:67` launch 목록에 `sim.launch.py` 추가 |
| `docs/00_interface_contract.md` | `§8 Mock 우선 전략` — ①/③ 구분 문장 추가 |

`docs/00`·`docs/02` 의 "Mock 우선 전략" 은 **전략 이름이므로 그대로 둔다.** 병렬 작업 방식을
가리키는 말이지 노드 이름이 아니다.

### 곁다리로 드러난 문제 — `voron24_mock.urdf`

```
ros2_ws/src/voron24_description/urdf/voron24_mock.urdf   ← 생성물인데 커밋됨
unity/Voron24Twin/Assets/urdf/voron24_mock.urdf          ← 내용 동일
```

둘 다 `xacro voron24.urdf.xacro use_meshes:=false` 의 **자동 생성 결과**이며 헤더에
`autogenerated ... EDITING THIS FILE BY HAND IS NOT RECOMMENDED` 가 박혀 있다. xacro 를
고쳐도 이 둘은 안 따라가므로 **단일 URDF 원칙이 조용히 깨진다.**

- **ros2_ws 쪽은 삭제 + `.gitignore`.** 그쪽엔 xacro 원본이 있으므로 존재 이유가 없다.
- **Unity 쪽은 남긴다.** URDF-Importer 가 xacro 를 못 돌리므로 평문 `.urdf` 가 필요하다.
  대신 ①과 헷갈리는 이름이므로 `voron24_boxes.urdf` 가 맞다 — **다만 개명하면 URDF 재임포트가
  강제되고 씬의 프리팹 참조가 끊긴다.** 이름 변경은 미루고 재생성 절차를
  `tools/export_unity_urdf.sh` 로 스크립트화해 드리프트만 먼저 막는다.

### 커밋 분리

`patterns.py` 의 `'none'` 추가와 `setup.py` 한 줄은 **기존 경로를 건드리는 유일한 변경**이므로
새 노드 본체와 분리해 먼저 커밋한다. 이 커밋만으로 기존 동작이 그대로여야 한다.

```bash
colcon build --symlink-install && source install/setup.bash
ros2 launch voron24_bringup mock.launch.py pattern:=sweep   # 추가 전과 동일하게 보여야 함
bash tools/smoke_test.sh
python3 tools/contract_check.py --xacro ros2_ws/src/voron24_description/urdf/voron24.urdf.xacro
```

`contract_check.py` 는 URDF 만 보므로 여기 걸리지 않는다. 회귀 판정은 `smoke_test.sh` 와
눈으로 보는 sweep 으로 한다.

새 노드가 이상하면 `mock.launch.py` 와 두 경로를 나란히 돌려 비교한다.

---

## 진행 순서

골격을 먼저 세우고 G-code 를 끼워 넣는다. 순서를 뒤집으면 G-code 작업 중 무엇이 깨졌는지 판별이 안 된다.

| 단계 | 내용 | 완료 기준 |
|---|---|---|
| **0** | **Linux Build Support 설치 + 수동 테스트 빌드** | **WSLg 창에서 URP 가 멀쩡히 렌더된다** |
| 1 | Player Settings 조정 (GL, 창 모드, companyName) | |
| 2 | `BuildScript.cs` + `build_unity.sh` | 배치 빌드가 실행 가능한 `.x86_64` 를 뱉는다 |
| 3 | `sim.launch.py` (`unity` 인자, mock only) | 명령 하나로 창까지 뜨고 sweep 이 보인다 |
| 4 | `on_exit` shutdown 배선 | Unity 창을 닫으면 전부 내려간다 |
| 4.5 | **`manual_publisher` 신규 추가** (`patterns.py` 에 `'none'`, `setup.py` 한 줄) | `mock.launch.py pattern:=sweep` 이 추가 전과 똑같이 보인다 |
| 5a | `gcode_parser.py` + `motion.py` (ROS 없음) | `python3 -m voron24_gcode.motion` 이 궤적을 뱉는다 |
| 5b | `printer_state_node` + `manual_publisher` remap | `source:=manual pattern:=sweep` 이 상태 노드를 거쳐도 동일하다 |
| 5b′ | 키보드 입력 (`pattern:=none` + `jog` 적분) | Unity 창과 터미널 양쪽에서 XYZ 가 움직인다 |
| 5c | `gcode_player_node.py` + `source:=gcode` (`speed` 인자 포함) | 사각형 G-code 가 Unity 에서 `10.0` 배속으로 재생된다 |
| 5d | 명령 배선 (`/printer/cmd` → `/printer/playback`) + 키보드 재배선 (배속 키 포함) | Unity 에서 일시정지·배속·jog 가 ROS 를 거쳐 먹는다 |
| 6 | `voron24_slicer` + `voron24_250.ini` + `job_starter` | `source:=stl` 이 한 명령으로 돈다 |

**0 단계가 게이트다.** 여기서 실패하면 2 단계 이후가 전부 무의미해짐. 3 단계까지가 이 문서의 실질적 목표.

5a 는 슬라이서 없이 시작할 수 있다. 이 머신에 슬라이서가 아직 없으므로 사각형·원통을
직접 뱉는 **테스트 G-code 생성 스크립트**(`tools/make_test_gcode.py`)를 5a 와 함께 만들어
파서 검증 입력으로 쓴다. 슬라이서 설치는 6 단계까지 미룰 수 있다.

---

## 미결 사항

- **계약 §5 토픽 표에 `/printer/playback`, `/printer/target` 두 줄 추가.** 기존 타입 재사용이라
  `.msg` 변경은 없지만 토픽 추가가 계약 변경 절차(PR + 3 인)에 해당하는지 팀 합의 필요.
- 프로파일 실물 `tools/slicer/voron24_250.ini` 제작. 6 단계에 속한다.
- ~~재생 시간축을 벽시계에 맞출지, 배속 기본값을 둘지.~~ **결정: 배속을 둔다. 기본값 `10.0`**
  (위 "배속"). `10.0` 이 데모에 적절한 값인지는 5c 에서 실제 G-code 로 눈으로 보고
  `SPEED_STEPS` 안에서 조정한다. 숫자 하나의 문제다.
- ~~Unity Editor 자체를 WSL 안에 설치할 것인가.~~ **결정: 설치하지 않는다.** Editor 는
  Windows 에 그대로 두고 디버깅용 보조 경로로만 쓴다(위 "Editor 는 Windows 에 남는다").
  경로 번역과 `chmod +x` 는 `build_unity.sh` 안에서 상시 처리한다.
- Linux 플레이어가 요구하는 런타임 라이브러리가 WSL 에 다 있는지. `ros-jazzy-desktop` 이
  X 라이브러리를 상당수 끌어오므로 아마 문제없지만 0 단계에서 확인된다.
- ~~`mock.launch.py` 를 `sim.launch.py` 의 얇은 래퍼로 바꿀지, 그냥 둘지.~~ **결정: 그냥 둔다.**
  기존 mock 경로 전체를 손대지 않는 것이 방침이다(위 "기존 mock 경로는 손대지 않는다").
  래퍼로 바꾸는 순간 회귀 비교 기준선이 새 코드에 의존하게 되어 기준선 구실을 못 한다.
