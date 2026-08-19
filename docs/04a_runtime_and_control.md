# 04a — 실행 골격과 조작 계층 (담당 A)

> **상태: 초안.** 구현 전. 이 문서에 적힌 파일들은 아직 존재하지 않는다.
> **선행**: [00_interface_contract.md](00_interface_contract.md) 숙지
> **관련**: [04_single_entrypoint.md](04_single_entrypoint.md) (설계 근거) · [04b_gcode_pipeline.md](04b_gcode_pipeline.md) (담당 B)

---

## 담당 범위

명령 하나로 전부 뜨고 Ctrl+C 하나로 전부 내려가는 실행 골격, 그리고 사람이 축을
움직이고 배속을 바꾸는 조작 계층.

```bash
ros2 launch voron24_bringup sim.launch.py                    # Unity 창까지 자동 기동
ros2 launch voron24_bringup sim.launch.py source:=manual pattern:=sweep
ros2 run voron24_gcode manual_publisher                      # 별도 터미널 — 키보드 조작
```

| 단계 | 내용 | 완료 기준 |
|---|---|---|
| **0** | Linux Build Support 설치 + 수동 테스트 빌드 | **WSLg 창에서 URP 가 멀쩡히 렌더된다** |
| 1 | Player Settings 조정 | |
| 2 | `BuildScript.cs` + `tools/build_unity.sh` | 배치 빌드가 실행 가능한 `.x86_64` 를 뱉는다 |
| 3 | `sim.launch.py` 뼈대 (`unity` / `rviz` / `source:=manual`) | 명령 하나로 창까지 뜨고 sweep 이 보인다 |
| 4 | `on_exit` shutdown 배선 | Unity 창을 닫으면 전부 내려간다 |
| 4.5 | `manual_publisher_node.py` 신규 | `mock.launch.py pattern:=sweep` 이 추가 전과 똑같이 보인다 |
| 5b | `printer_state_node` | `source:=manual pattern:=sweep` 이 상태 노드를 거쳐도 동일하다 |
| 5b′ | 키보드 입력 (`pattern:=none` + `jog` 적분) | Unity 창과 터미널 양쪽에서 XYZ 가 움직인다 |
| 5d | Unity `KeyboardJointController` 재배선 + 배속 키 | Unity 에서 jog·배속이 ROS 를 거쳐 먹는다 |

**0 단계가 게이트다.** 여기서 실패하면 2 단계 이후가 전부 무의미해지고, Windows 빌드 +
경로 번역으로 선회하면서 이 문서를 전면 수정해야 한다.

4.5 에 착수하려면 B 의 선행 커밋(`patterns.py` 에 `'none'` 추가 + `setup.py` 한 줄)이
먼저 머지되어 있어야 한다. → [04b](04b_gcode_pipeline.md#선행-커밋)

---

## 0 — 선행 검증 (코드 쓰기 전)

현재 설치된 모듈은 `MetroSupport`, `WebGLSupport`, `windowsstandalonesupport` 뿐이라
Linux 빌드가 불가능한 상태다.

1. Unity Hub → 2022.3.62f3 → Add modules → **Linux Build Support (Mono)**
   - IL2CPP 는 설치하지 않는다. 빌드 시간이 느려진다.
2. Editor 에서 Linux 빌드 → WSL 에서 실행
3. **판정**: URP 렌더링이 멀쩡하고 프레임이 나오면 진행.

현재 환경:

| 항목 | 상태 |
|---|---|
| WSLg | 동작 중 (`DISPLAY=:0`, `WAYLAND_DISPLAY=wayland-0`) |
| GPU 패스스루 | 있음 (`/dev/dxg`, `/usr/lib/wsl/lib` 에 NVIDIA 라이브러리) |
| Mesa | 25.2.8 — d3d12 gallium 으로 하드웨어 가속 OpenGL |
| GUI 스택 | RViz2 가 이미 여기서 돌고 있음 = 검증됨 |

Unity Linux 플레이어는 기본적으로 Vulkan 을 먼저 시도하므로 **Vulkan 을 제거하고
OpenGLCore 만** 남기고, 실행 시에도 `-force-glcore` 를 준다. 둘 다 건다.

Linux 플레이어가 요구하는 런타임 라이브러리가 WSL 에 다 있는지도 이 단계에서 확인된다
(`ros-jazzy-desktop` 이 X 라이브러리를 상당수 끌어온다).

---

## 1 — Player Settings

| 항목 | 현재 | 변경 | 이유 |
|---|---|---|---|
| `runInBackground` | `1` | **그대로** | 끄면 창이 포커스를 잃을 때 ROS 메시지 처리가 멈춘다. 터미널을 클릭하면 트윈이 멈추는데 원인 추적이 어렵다 |
| `fullscreenMode` | `1` (전체화면 창) | `3` (Windowed) | 터미널 옆에 둔다 |
| `resizableWindow` | `0` | `1` | |
| Graphics APIs | Vulkan 우선 | **OpenGLCore 만** | 0 단계 참조 |
| `companyName` | 확인 필요 | 확정 | 로그 경로가 여기서 결정된다 |

플레이어 로그: `~/.config/unity3d/<Company>/Voron24/Player.log`

---

## 2 — 빌드 파이프라인

### `unity/Voron24Twin/Assets/Editor/BuildScript.cs`

CLI 에서 `-executeMethod` 로 부를 수 있는 빌드 진입점.

- 타겟: `BuildTarget.StandaloneLinux64`
- 출력: `unity/Voron24Twin/Build/Linux/Voron24Twin.x86_64`
- 씬을 `EditorBuildSettings.scenes` 으로 연결. 하드코딩 금지 — 씬이 늘면 자동 반영
- `Development Build` 는 인자로 노출. 골격 검증 단계에서는 켜고 데모용에서만 끈다
- 실패 시 exit code 1. 배치 빌드의 silent failure 주의

### `tools/build_unity.sh`

Editor 는 Windows 에 있고 프로젝트는 WSL 파일시스템에 있다. Windows `Unity.exe` 를
호출하되 경로를 변환해 넘긴다.

```bash
"$UNITY_EXE" \
  -quit -batchmode -nographics \
  -projectPath "$(wslpath -w unity/Voron24Twin)" \
  -buildTarget Linux64 \
  -executeMethod Voron24.BuildScript.BuildLinux \
  -logFile -
```

- 에디터 버전은 `ProjectSettings/ProjectVersion.txt` 에서 읽어 환경 변수로 넘긴다. 하드코딩 금지.
- 에디터가 이미 열려 있으면 배치 빌드가 라이선스/락 충돌로 실패하는데, 메시지가
  불친절하므로 스크립트에서 먼저 감지해 안내한다.
- **빌드 직후 `chmod +x` 를 반드시 붙인다.** Windows 쪽 Unity 가 `\\wsl.localhost\...` 를
  통해 파일을 쓰므로 실행 비트가 보존되지 않는다. 빠뜨리면 launch 가 `Permission denied`
  로 죽는데 원인이 전혀 드러나지 않는다.
- `-nographics` 는 URP 셰이더 변형 빌드에서 문제를 일으키는 경우가 있다. 실패하면 뺀다.

다른 스크립트에서 같은 문제를 또 다루게 되면 격리가 깨졌다는 신호다. 경로
번역(`wslpath -w`)과 실행 비트 처리는 이 스크립트 안에 가둔다.

---

## 3 — `sim.launch.py` 뼈대

`mock.launch.py` 의 확장이되 기존 파일은 지우지 않는다. W1 검증 경로이자 회귀 비교
기준선이다.

### 인자 — A 담당분

| 인자 | 기본값 | 값 |
|---|---|---|
| `unity` | `build` | `build` \| `editor` \| `none` |
| `source` | `manual` | `manual` (`gcode`/`stl` 는 B 가 추가) |
| `pattern` | `none` | `none` \| `home` \| `sweep` \| `square` \| `lissajous` |
| `rviz` | `false` | `unity:=none` 일 때만 자동으로 `true` |

`source:=mock` 은 두지 않는다. 기존 mock 경로는 `mock.launch.py` 로 간다.

### `rviz` 기본값

`mock.launch.py` 는 `default_value='true'` 지만(`:55`) `sim.launch.py` 는 `false` 로
다시 선언한다. `DeclareLaunchArgument` 가 launch 파일마다 독립이기 때문이다.

| launch | `rviz` | 이유 |
|---|---|---|
| `mock.launch.py` | `true` | W1 게이트가 "RViz 와 Unity 에서 동시에 같은 움직임" |
| `sim.launch.py` | `false` | Unity 플레이어가 뷰어다. 창이 둘 뜨면 "명령 하나로" 에 반한다 |

RViz2 와 Unity 플레이어가 같은 Mesa d3d12 GL 경로를 동시에 쓰면 프레임 저하의 원인이
RViz 경합인지 URP 인지 갈라낼 수 없다. 0~4 단계 판정을 오염시키므로 꺼둔 상태가 기본이고
대조가 필요할 때만 `rviz:=true` 를 명시한다.

`unity:=none` 이면 뷰어가 하나도 없으므로 기본값을 `unity` 에 연동한다.

```python
DeclareLaunchArgument(
    'rviz',
    default_value=PythonExpression(["'true' if '", unity, "' == 'none' else 'false'"]),
    description='기본은 꺼짐. unity:=none 일 때만 자동으로 켜짐'),
```

### Unity 기동부

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

- `TimerAction` 2 초는 엔드포인트가 먼저 `bind` 하도록 하기 위함. ROS-TCP-Connector 가
  재시도하긴 하지만 첫 로그가 연결 실패로 지저분해진다.
- `-logFile -` 로 Unity 로그가 ROS 로그와 같은 터미널에 섞인다.

  ```
  [JointState] 자기충돌 해제: 콜라이더 4 개, 6 쌍
  [JointState] bound 3/3 joints under 'voron24'
  [JointState] subscribed to /joint_states
  ```

- `DISPLAY` / `WAYLAND_DISPLAY` 는 별도 설정 없이 자식에게 상속된다. 단 `sudo` 나 다른
  세션에서 띄우지 말 것.

### `unity` 모드 셋

| 모드 | 용도 | 동작 |
|---|---|---|
| `unity:=editor` | 개발 | 엔드포인트까지만. Play 는 Windows Editor 에서 직접 |
| `unity:=build` | 데모 / 사용 | Linux 플레이어까지 자동 기동 |
| `unity:=none` | ROS 단독 디버깅 | 엔드포인트도 안 띄움. `rviz` 가 자동으로 켜짐 |

`unity:=editor` 는 Windows Editor 를 쓰므로 경로 번역 문제를 다시 만난다. `load_gcode` 를
테스트할 때는 `unity:=build` 를 쓴다.

---

## 4 — 종료 배선

`on_exit=[EmitEvent(event=Shutdown(...))]` 으로 Unity 창을 닫으면 launch 전체가 내려가게
한다. 반대로 Ctrl+C 는 자식 프로세스를 전부 정리해야 한다.

---

## Unity 부트스트랩 — `RosBootstrap.cs`

- Linux 빌드에서는 항상 `127.0.0.1`. 인자로 노출해 나중에 ROS 를 다른 머신에 둘 수 있게
  하되 우선순위는 낮다.
- `Awake()` 가 아니라 `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` 에서 처리한다.
  구독을 걸기 전에 `JointStateSubscriber.Start()` 가 실행되어야 한다.
- 인자가 없으면 기존 인스펙터 값을 사용 — Editor 경로가 깨지면 안 된다.

## 빌드에서 기존 스크립트의 거동

`Assets/Scripts/` 확인 결과 이식성 문제는 없다.

- `ConnectionMonitor.cs` 의 오버레이는 `OnGUI` 라서 빌드에서도 출력된다. Editor 없이
  "Unity 문제인가 ROS 문제인가" 를 판별하는 수단이므로 이점이다.
- `KeyboardJointController.cs` 도 동작한다. 단 배선을 고쳐야 한다 (5d).
- `#if UNITY_EDITOR` 로 빠지는 건 `JointStateSubscriber` 의 "Rebind Joints" 컨텍스트 메뉴
  뿐이다. 자동 바인딩은 런타임에 정상 동작하고, 확인은 `-logFile -` 로 대체한다.

---

## 4.5 — `manual_publisher_node.py` 신규

키보드 조작을 맡는 정식 값 소스. `mock_publisher_node.py` 와 `mock.launch.py` 는 그대로
둔다 — 파일명·노드명·클래스명·기본 `pattern` 값 전부 유지하고 새 노드를 옆에 추가한다.

| | 기존 | 신규 |
|---|---|---|
| 노드 | `mock_publisher` | `manual_publisher` |
| 파일 | `mock_publisher_node.py` | `manual_publisher_node.py` |
| launch | `mock.launch.py` | `sim.launch.py` (`source:=manual`) |
| 출력 | `/joint_states` 직접 | `/printer/cmd` + `/printer/target` |
| `pattern` 기본값 | `lissajous` (변경 없음) | `none` |
| 주 용도 | W1 검증·회귀 기준선 | 키보드 조작, G-code 와 나란한 값 소스 |

둘 다 조인트 값을 만들기 때문에 함께 띄우면 발행자가 둘이 된다. 두 노드를 동시에
올리지 않는다.

### 출력은 둘

| 출력 | 조건 |
|---|---|
| `/printer/cmd` (`PrinterCommand`) | 키보드 입력. `jog` / `home` / `set_speed` |
| `/printer/target` (`JointState`) | `pattern != none` 일 때만 |

키보드 입력은 `/printer/cmd` 로 나간다 — `/printer/target` 이 아니다. 적분과 클램프는
`printer_state_node` 가 단독으로 하며, 여기서 좌표를 누산하면 같은 로직이 두 곳에 생긴다.

```
터미널 키보드 ──▶ manual_publisher ──┐
                                     ├──▶ /printer/cmd ──▶ printer_state_node ──▶ /joint_states
Unity 키보드  ──▶ KeyboardJointController ┘
```

### 자동 패턴 — `patterns.py` 를 import 한다

궤적 수식을 새 파일에 복사하면 `patterns.py` ↔ `LocalMockDriver.cs` 동기화 계약
(CLAUDE.md)에 세 번째 사본이 생긴다. **반드시 import 로 공유한다.**

| `pattern` 값 | 동작 |
|---|---|
| **`none`** (기본값) | 자동 궤적 없음. 키보드 입력만 반영 |
| `home` \| `sweep` \| `square` \| `lissajous` | 기존 수식 그대로. 이때 키보드는 무시 |

### 코드 변경 범위

| 파일 | 성격 | 내용 |
|---|---|---|
| `voron24_gcode/manual_publisher_node.py` | **신규** | `class ManualPublisher`, 노드명 `manual_publisher`, `pattern` 기본값 `'none'` |
| `voron24_gcode/patterns.py` | B 의 선행 커밋 | `'none'` 추가분을 그대로 쓴다 |
| `voron24_gcode/setup.py` | B 의 선행 커밋 | 엔트리포인트 등록분을 그대로 쓴다 |
| `voron24_gcode/mock_publisher_node.py` | **무변경** | |
| `voron24_bringup/launch/mock.launch.py` | **무변경** | |
| `LocalMockDriver.cs` | **무변경** | |

`mock_publisher_node.py` 를 복사해 시작하되 둘은 베끼지 말 것.

- `/joint_states` 직접 발행 — 새 노드의 출력은 `/printer/cmd` 와 `/printer/target` 이다
- `s.filename = f'mock_{p}.gcode'` — 존재하지 않는 파일명을 흘리면 Unity 가 실제 파일로
  오인한다. `'<manual>'` / `f'<pattern:{p}>'` 로 쓴다

---

## 5b — `printer_state_node`

**`/joint_states` 의 유일한 발행자이자 `/printer/cmd` 의 유일한 구독자.**

| 책임 | |
|---|---|
| `jog` 적분 | 입력 장치가 던진 델타를 좌표로 누산 |
| `home` | 원점 복귀 |
| 리밋 클램프 | URDF 스트로크 리밋. 첫 위반에서 **한 번만** 경고 |
| 모드 관리 | `manual` / `playing` |
| 스트림 명령 릴레이 | `/printer/cmd` → `/printer/playback` |

`jog` 의 좌표 적분은 이 노드만 한다. 입력 장치(Unity 키보드, 터미널 키보드, 향후 UI)는
전부 `/printer/cmd` 에 델타만 던지는 동등한 peer 다.

클램프도 마찬가지다. 모든 prismatic 의 `lower` 가 0 이라 음수 좌표나 250 mm 초과는
리밋을 넘는데(스커트/프라임 라인이 베드 밖으로 나가는 G-code 는 흔하다), 값 소스는
클램프하지 않고 그대로 흘린다.

### 스트림 명령은 되넘긴다

`set_speed` 와 `pause` 는 시간축을 다루는 명령이라 보간기가 있는 값 소스 쪽이 처리해야
한다. 상태 노드가 값을 붙잡고 있는 방식으로 구현하면 재개 시 좌표가 튄다.

```
Unity 키보드/UI ──┐
                  ├──▶ /printer/cmd ──▶ printer_state_node ──▶ /joint_states ──▶ RViz, Unity
터미널 키보드 ────┘                        │           ▲
                              /printer/playback    /printer/target
                                           ▼           │
                                        값 소스 (manual_publisher / gcode_player_node)
```

| command | 처리 |
|---|---|
| `jog` | 직접 적분. 모드를 `manual` 로 전환 |
| `home` | 직접 처리. `playing` 이면 먼저 `stop` 을 `/printer/playback` 으로 |
| `pause` / `resume` / `stop` / `set_speed` / `load_gcode` | `/printer/playback` 으로 릴레이 |

### 두 모드

`manual` 이 초기 상태다.

| 모드 | 진입 | `/printer/target` | `jog` |
|---|---|---|---|
| `manual` | 초기 상태, `stop`, **`jog` 수신** | 무시 | 누적 적분 |
| `playing` | `load_gcode`, `resume` | 통과 | → `manual` 로 전환 |

`playing` 중에 `jog` 이 들어오면 거부하지 않고 `/printer/playback` 으로 `pause` 를 보낸 뒤
`manual` 로 전환한다. `resume` 으로 되돌아간다.

재생 중 배속만 올리는 것이 의도인데 그때마다 재생이 멈추면 쓸모가 없으므로, `set_speed`
는 모드를 되돌리지 않는다.

### 소스 전환은 remapping 으로

값 소스 노드의 코드를 고치지 않는다.

```python
remappings=[('/joint_states', '/printer/target')]
```

`mock.launch.py` 는 remapping 없이 그대로 `/joint_states` 에 직접 쏘므로 지금과 동일하게
동작한다. `sim.launch.py` 에서만 상태 노드를 끼운다.

`source:=manual` + `pattern != none` 이면 상태 노드가 `/printer/target` 을 통과시켜야
하므로 launch 가 기동 직후 `resume` 을 한 번 발행한다.

### 토픽

| 토픽 | 타입 | 발행 | 구독 |
|---|---|---|---|
| `/printer/cmd` | `voron24_msgs/PrinterCommand` | Unity, `manual_publisher` | `printer_state_node` |
| `/printer/playback` | `voron24_msgs/PrinterCommand` | `printer_state_node` | 활성 소스 |
| `/printer/target` | `sensor_msgs/JointState` | 활성 소스 | `printer_state_node` |
| `/joint_states` | `sensor_msgs/JointState` | `printer_state_node` | RViz, Unity |

새 `.msg` 는 없다. 두 신규 토픽 모두 기존 메시지 타입을 재사용하므로 `contract_check.py`
에는 걸리지 않는다. 다만 계약 §5 토픽 표에 두 줄을 추가해야 한다.

---

## 5b′ — 터미널 키보드

### 키맵

Unity 의 `KeyboardJointController.cs` 와 의미를 맞춘다. **이 표가 양쪽의 단일 출처다.**

| 축 | Unity | 터미널 |
|---|---|---|
| X − / + | `←` / `→` | `a` / `d` |
| Y − / + | `↓` / `↑` | `s` / `w` |
| Z − / + | `PgDn` / `PgUp` | `q` / `e` |
| 원점 복귀 | `Home` | `h` |
| 배속 − / + | `[` / `]` | `[` / `]` |
| 배속 `1.0` 복귀 | `\` | `\` |
| 종료 | — | `Ctrl-C` |

터미널에서 방향키를 1차 키맵으로 쓰지 않는 이유는 화살표가 단일 문자가 아니라 3 바이트
ESC 시퀀스(`\x1b[A` 등)라서 상태 기계가 필요하기 때문이다. 문자 키를 먼저 넣고 화살표
지원은 선택으로 둔다.

### 단위와 파라미터

`jog` 의 단위는 **mm** 다 (계약 §6 SI 예외).

| 파라미터 | 기본값 | |
|---|---|---|
| `step_mm` | `1.0` | 키 한 번당 이동량 |
| `repeat_hz` | `20.0` | 축 키 홀드 시 반복률 |

### 배속 키

```python
SPEED_STEPS = (0.5, 1.0, 2.0, 5.0, 10.0, 25.0, 50.0, 100.0)
```

**이 상수는 A 소유다** (`manual_publisher` / Unity 양쪽의 키보드 편의 상수). 값 소스
노드는 이 리스트를 모르고 임의의 양수를 받는다.

- 한 칸씩 오간다. 양 끝에서는 더 가지 않고 현재 값을 그대로 다시 보낸다 — 눌러도 아무
  일이 없는 것보다 현재 배속이 로그에 다시 찍히는 편이 낫다.
- 축 키와 달리 키 홀드 반복은 걸지 않는다. 한 번 눌러 한 단계다.
- 배속 키가 만드는 것도 `/printer/cmd` 의 `PrinterCommand` 다. `jog` 과 같은 경로로 나간다.

```
키 `]` ──▶ /printer/cmd {set_speed, args:[25.0]} ──▶ printer_state_node
                                                          │
                                          /printer/playback ──▶ gcode_player_node
```

`source:=manual` 일 때는 배속이 의미가 없다. `manual_publisher` 는 자신이 받은
`/printer/playback` 의 `set_speed` 를 무시한다.

### ★ stdin — launch 로 띄우면 키가 안 먹는다

**`Node(...)` 로 launch 에서 띄운 프로세스에는 stdin 이 연결되지 않는다.**
`output='screen'` 이나 `emulate_tty=True` 로는 해결되지 않는다.

- 원칙: `manual_publisher` 의 키보드 모드는 별도 터미널에서 `ros2 run` 한다.

  ```bash
  ros2 run voron24_gcode manual_publisher            # 이 터미널에서 키 입력
  ```

- `sim.launch.py` 는 `pattern != none` 일 때만 이 노드를 자동 기동한다.
- 키보드 조작의 기본 경로는 Unity 창이다. 그쪽은 stdin 문제가 없다.
- launch 에 넣어야 한다면 `prefix='xterm -e'` 뿐인데 WSLg 에서 `xterm` 별도 설치가
  필요하므로 기본 경로로 삼지 않는다.

구현은 `termios` + `tty.setcbreak(sys.stdin)` 에 논블로킹 읽기다. 종료 시
`termios.tcsetattr` 로 원상복구하는 것을 `try/finally` 로 감싸 반드시 보장할 것 —
빼먹으면 노드가 죽은 뒤 터미널 에코가 꺼진 채 남아 `reset` 을 쳐야 한다.

---

## 5d — Unity 입력 재배선

현재 `KeyboardJointController.cs:115` 는 `JointStateSubscriber.SetTarget()` 을 직접
호출해서 키 입력이 ROS 를 한 바이트도 거치지 않는다. 바로 옆에
`PrinterCommandPublisher.Jog(dx,dy,dz)` 가 이미 구현되어 있으므로 그쪽으로 돌린다.

```
(현재)  키 입력 ──▶ SetTarget()                              ROS 우회
(수정)  키 입력 ──▶ Jog() ──▶ /printer/cmd ──▶ printer_state_node ──▶ /joint_states ──▶ 화면
```

- 대가는 왕복 지연이다. 50 Hz 두 홉이면 체감되지 않는다.
- ROS 미연결 시 키보드가 죽으므로 `JointStateSubscriber.IsConnected == false` 일 때만
  기존 직접 쓰기로 폴백한다 (`LocalMockDriver` 와 같은 규칙).
- `Jog` 의 `args` 단위는 **mm** 다. Unity 쪽 조인트 값은 m 이므로 여기서 ×1000 이 필요하다.
  **중복 변환 주의** — 되돌아오는 `/joint_states` 는 이미 m 다.
- 배속 키(`[` `]` `\`)도 같은 퍼블리셔로 `set_speed` 를 보낸다.

ROS 를 거치지 않지만 그대로 두는 예외 둘.

| 스크립트 | 이유 |
|---|---|
| `LocalMockDriver.cs` | 엔드포인트 없이 Unity 단독 구동용. "Unity 문제인가 ROS 문제인가" 판별 수단 (CLAUDE.md — 유지 지시) |
| `NozzleTracker.cs` | 노즐–베드 상대좌표를 Unity 안에서 계산해 화면에만 쓴다. 계약에 대응 토픽이 없다. 시각화 전용 |

---

## 곁다리 — `voron24_mock.urdf` 드리프트

```
ros2_ws/src/voron24_description/urdf/voron24_mock.urdf   ← 생성물인데 커밋됨
unity/Voron24Twin/Assets/urdf/voron24_mock.urdf          ← 내용 동일
```

둘 다 `xacro voron24.urdf.xacro use_meshes:=false` 의 자동 생성 결과다. xacro 를 고쳐도
이 둘은 안 따라가므로 **단일 URDF 원칙이 조용히 깨진다.**

- ros2_ws 쪽은 삭제하고 `.gitignore` 에 넣는다. 그쪽엔 xacro 원본이 있다.
- URDF-Importer 가 xacro 를 못 돌려 평문 `.urdf` 가 필요하므로 Unity 쪽은 남긴다.
  재생성 절차를 `tools/export_unity_urdf.sh` 로 스크립트화해 드리프트를 막는다.
  파일명 변경은 하지 않는다 — 개명하면 URDF 재임포트가 강제되고 씬의 프리팹 참조가 끊긴다.

---

## B 와의 인터페이스

→ [04b_gcode_pipeline.md](04b_gcode_pipeline.md)

| 항목 | 규약 |
|---|---|
| `/printer/playback` | A 가 발행, B 가 구독. `PrinterCommand`. 스트림 계열만 흐른다 |
| `/printer/target` | B 가 발행, A 가 구독. `JointState`, **단위 m**. `name` 순서는 계약 §3 |
| `SPEED_STEPS` | **A 소유.** B 의 노드는 임의의 양수를 받는다 (0 이하는 거부 + 경고, `0.0` 은 `pause` 가 처리) |
| `speed` launch 인자 | **B 소유** (`speed_default` 파라미터로 전달, 기본 `10.0`). 실행 중 변경은 A 의 키가 `set_speed` 로 |
| `sim.launch.py` | A 가 뼈대 + `unity`/`rviz`/`source:=manual` 을 먼저 머지 → B 가 `source:=gcode\|stl` + `file` + `speed` 를 얹는다 |
| `patterns.py` `'none'` | **B 의 선행 커밋.** A 는 그 위에서 4.5 를 시작한다 |

---

## 검증

```bash
colcon build --symlink-install && source install/setup.bash

# 회귀 — 기존 경로가 그대로여야 한다
ros2 launch voron24_bringup mock.launch.py pattern:=sweep
bash tools/smoke_test.sh
python3 tools/contract_check.py --xacro ros2_ws/src/voron24_description/urdf/voron24.urdf.xacro

# 신규 경로
ros2 launch voron24_bringup sim.launch.py                          # Unity 창까지
ros2 launch voron24_bringup sim.launch.py source:=manual pattern:=sweep
ros2 run voron24_gcode manual_publisher                            # 별도 터미널
```

`contract_check.py` 는 URDF 만 보므로 노드 변경에는 걸리지 않는다. 회귀 판정은
`smoke_test.sh` 와 눈으로 보는 sweep 으로 한다. 새 노드가 이상하면 `mock.launch.py` 와
두 경로를 나란히 돌려 비교한다.

## 문서 갱신

| 파일 | 추가할 내용 |
|---|---|
| `README.md` | `sim.launch.py` 사용법에 `source:=manual` 항목 |
| `CLAUDE.md` | "명령어" 절에 새 launch, "Mock 우선 전략" 절에 두 노드의 역할 구분 한 줄 |
| `docs/03_ros2_workflow.md` | `:67` launch 목록에 `sim.launch.py` 추가 |
| `docs/00_interface_contract.md` | §5 토픽 표에 `/printer/playback`, `/printer/target` 두 줄 |

`docs/00`·`docs/02` 의 "Mock 우선 전략" 은 전략 이름이므로 그대로 둔다.
