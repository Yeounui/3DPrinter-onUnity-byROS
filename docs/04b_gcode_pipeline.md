# 04b — 값 소스와 재생 파이프라인 (담당 B)

> **상태: 초안.** 구현 전. 이 문서에 적힌 파일들은 아직 존재하지 않는다.
> **선행**: [00_interface_contract.md](00_interface_contract.md) 숙지
> **관련**: [04_single_entrypoint.md](04_single_entrypoint.md) (설계 근거) · [04a_runtime_and_control.md](04a_runtime_and_control.md) (담당 A)

---

## 담당 범위

G-code / STL 을 읽어 **50 Hz 등간격 궤적**으로 바꾸는 값 소스.

```bash
ros2 launch voron24_bringup sim.launch.py source:=gcode file:=~/a.gcode
ros2 launch voron24_bringup sim.launch.py source:=stl   file:=~/a.stl
```

| 단계 | 내용 | 완료 기준 |
|---|---|---|
| **선행** | `patterns.py` 에 `'none'` + `setup.py` 한 줄 | `mock.launch.py pattern:=sweep` 이 추가 전과 똑같이 보인다 |
| 5a | `gcode_parser.py` + `motion.py` + `tools/make_test_gcode.py` | `python3 -m voron24_gcode.motion` 이 궤적을 뱉는다 |
| 5c | `gcode_player_node.py` + `sim.launch.py` 에 `source:=gcode` | 사각형 G-code 가 Unity 에서 `10.0` 배속으로 재생된다 |
| 6 | `voron24_slicer` + `voron24_250.ini` + `job_starter` | `source:=stl` 이 한 명령으로 돈다 |

**5a 는 A 의 진행과 무관하게 시작할 수 있다.** rclpy 없이 도는 순수 파이썬이고 슬라이서도
필요 없다. 이 머신에 슬라이서가 아직 없으므로 사각형·원통을 직접 뱉는 테스트 G-code
생성 스크립트를 5a 와 함께 만들어 파서 검증 입력으로 쓴다.

---

## 이 파이프라인이 놓이는 자리

값의 출처만 바뀌고 나머지는 동일하다. `sim.launch.py` 의 `source` 인자가 소스 노드를 고른다.

```
source:=manual   →  manual_publisher      (A 담당)
source:=gcode    →  gcode_player_node
source:=stl      →  gcode_player_node + voron24_slicer
```

```
                                printer_state_node (A) ──▶ /joint_states ──▶ RViz, Unity
                                     │           ▲
                       /printer/playback    /printer/target
                                     ▼           │
                               ┌─────┴───────────┴─────┐
                               │  값 소스 (배타 선택)     │──▶ /printer/status,
                               │  manual_publisher (A)  │    /printer/extrusion ──▶ Unity
                               │  gcode_player_node ◀───┼─── SliceModel action ◀── voron24_slicer
                               └────────────────────────┘
```

**`/printer/target` 발행자도 하나여야 한다.** `sim.launch.py` 의 `source` 인자가 배타
선택임을 보장할 것.

---

## 선행 커밋

기존 경로를 건드리는 **유일한 변경**이므로 새 코드와 분리해 먼저 커밋한다. 이 커밋만으로
기존 동작이 그대로여야 한다.

| 파일 | 변경 |
|---|---|
| `voron24_gcode/patterns.py` | `PATTERNS` 튜플에 `'none'` 추가 + `PatternGenerator._none()` 신설. **기존 네 패턴의 수식은 무변경** |
| `voron24_gcode/setup.py:22` | `'manual_publisher = voron24_gcode.manual_publisher_node:main'` 한 줄 추가. 기존 `mock_publisher` 줄은 **남긴다** |

기존 수식을 건드리면 `patterns.py` ↔ `LocalMockDriver.cs` 동기화 계약(CLAUDE.md)이
깨진다. 추가만 한다.

`mock_publisher_node.py:69` 의 `if self.pattern not in PATTERNS` 검사는 튜플이 늘어나도
그대로 통과한다. 기존 노드의 기본값이 `lissajous` 이므로 `'none'` 이 추가돼도 동작은 같다.

```bash
colcon build --symlink-install && source install/setup.bash
ros2 launch voron24_bringup mock.launch.py pattern:=sweep   # 추가 전과 동일하게 보여야 함
bash tools/smoke_test.sh
python3 tools/contract_check.py --xacro ros2_ws/src/voron24_description/urdf/voron24.urdf.xacro
```

이 커밋이 머지되어야 A 가 `manual_publisher_node.py` 를 시작할 수 있다.

---

## 5a — 순수 파이썬 모듈

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

### 단위 — 변환은 노드가 한 번만

계약 §2 그대로. **파서와 보간기는 G-code 도메인인 mm 를 유지하고 mm→m 은 노드가
publish 직전에 한 번만** 한다. 순수 모듈 안에서 미리 나누면 변환 지점이 둘로 늘어난다.
중복 변환이 이 레포에서 가장 위험한 버그다.

### 파싱 대상

| 지원 | 무시 |
|---|---|
| `G0`/`G1` `X Y Z E F` | `M104`/`M109`/`M140`/`M190` (온도) |
| `G28` 홈 | `M106` 팬 |
| `G90`/`G91` 절대/상대 | `M600` 등 |
| `G92` 좌표 리셋 | `T0` |
| `M82`/`M83` 익스트루더 모드 | |
| `;LAYER_CHANGE`, `;WIDTH:` 주석 태그 | |

- `G2`/`G3` 원호는 미지원. 프로파일에서 arc fitting 을 끄므로 나오지 않는다. 나오면
  경고 후 무시.
- `G91`(상대 좌표)은 **거부**한다. 조용히 틀린 궤적이 나오는 것보다 낫다.
- 파서는 직접 쓴다(150 줄 내외). PyPI 파서를 쓰지 않는 이유 — ① 전체를 리스트로 반환하는
  API 가 많아 스트리밍과 맞지 않음 ② `;WIDTH:` 같은 주석 태그를 버리는 구현이 있음
  ③ 위 표만큼으로 방언이 고정되어 있어 의존이 이득보다 큼.

### 보간 — 문제의 본질

G-code 가 주는 것은 목표 좌표와 이송속도(`F`) 뿐이고 `/joint_states` 가 요구하는 것은
**50 Hz 등간격 샘플**이다. 그 사이를 메우는 것이 `motion.py` 의 일이다.

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

**`while` 은 필수다.** 슬라이스된 곡선은 0.2 mm 짜리 세그먼트가 수천 개인데 50 Hz ·
100 mm/s 면 한 틱에 2 mm 다. 틱당 한 세그먼트만 소비하면 실제보다 10 배 느리게 기고,
반대로 긴 직선 하나는 여러 틱에 걸쳐 나눠 먹어야 한다.

가속도(트라페조이드 프로파일)는 넣지 않는다. 시각화에는 등속으로 충분하고 넣는 순간
룩어헤드 큐가 필요해져 난이도가 급등한다. `F` 는 축별 최대속도로 클램프만 한다.

`Z` 는 레이어 단위로만 변하므로 `joint_z` 궤적은 계단식이 된다. 세그먼트 보간이 X/Y/Z 를
같은 파라미터 `s` 로 함께 훑으므로 레이어 전환의 Z 상승도 그 세그먼트의 이송시간만큼
자연히 이어진다. 별도 처리는 필요 없다.

### `tools/make_test_gcode.py`

슬라이서 없이 파서·보간기를 검증하기 위한 생성 스크립트. 사각형과 원통을 직접 뱉는다.
슬라이서 설치는 6 단계까지 미룰 수 있다.

---

## 5c — `gcode_player_node.py`

**G-code 전체를 미리 샘플 배열로 펼치지 않는다.** 파일을 한 줄씩 읽어 세그먼트로 바꾸고
그 세그먼트 위를 50 Hz 타이머가 실시간으로 진행하며 현재 좌표를 뽑는다. 재생 도중의
`pause` / `set_speed` / `stop` 이 즉시 먹어야 하고 수십 MB 짜리 G-code 를 통째로
메모리에 올리지 않기 위해서다.

파일 읽기 자체는 노드 내부 백그라운드 스레드에서 한다.

### 구독 — `/printer/cmd` 가 아니라 `/printer/playback`

플레이어는 `/printer/cmd` 를 직접 듣지 않는다. `printer_state_node`(A) 가 걸러 넘긴
스트림 계열만 받는다.

| command | 동작 |
|---|---|
| `load_gcode` (payload=절대경로) | 파일 열기. 백그라운드 스레드 |
| `pause` / `resume` | 보간기 정지/재개 |
| `stop` | 스트림 폐기, `state='idle'` |
| `set_speed` (args[0]) | 위 루프의 `speed_scale` |

`jog` / `home` 은 여기 없다 — 상태 노드의 몫이다.

### 출력

| 토픽 | 타입 | 내용 |
|---|---|---|
| `/printer/target` | `sensor_msgs/JointState` | **mm→m 변환 후.** `name` 순서는 계약 §3 |
| `/printer/extrusion` | `voron24_msgs/ExtrusionPoint` | 세그먼트 꼭짓점 |
| `/printer/status` | `voron24_msgs/PrinterStatus` | `progress` 는 바이트 오프셋 / 파일 크기 |

**좌표 클램프는 하지 않는다.** 음수 좌표나 250 mm 초과(스커트/프라임 라인이 베드 밖으로
나가는 G-code 는 흔하다)는 상태 노드가 클램프한다. 리밋 지식을 두 곳에 두지 않기 위해
플레이어는 그대로 흘린다.

### 배속

**시간축은 벽시계에 맞추지 않는다.** 실물 프린트는 시간 단위라 등속(`1.0`) 재생이면 데모가
성립하지 않는다. 위 루프의 `speed_scale` 을 그대로 배속으로 쓴다.

- 노드 파라미터 `speed_default`(기본 **`10.0`**). `sim.launch.py` 의 `speed` 인자가 넘긴다
- `set_speed` 가 들어오면 `speed_scale` 만 바뀐다. 세그먼트 커서(`self.seg`, `self.s`)는
  건드리지 않으므로 재생 도중 배속을 바꿔도 좌표가 튀지 않는다
- 배속은 시간축만 늘리고 줄인다. 궤적 형상과 `ExtrusionPoint` 열은 배속과 무관하게 동일하다
- `1.0` 이 정확히 실시간이라는 보장은 없다. `F` 가 축별 최대속도로 클램프되므로 실제 재생은
  G-code 가 상정한 시간보다 느려질 수 있다
- **임의의 양수를 받는다.** 키보드 단계 리스트(`SPEED_STEPS`)는 A 소유이고 이 노드는
  모른다. 0 이하는 거부하고 경고한다. `0.0` 은 `pause` 가 처리한다

`10.0` 이 데모에 적절한 값인지는 실제 G-code 로 눈으로 보고 조정한다.

### 틱당 이동 상한 (`max_step_mm`) — 배속의 실제 천장

배속은 시간축만 늘이므로, 올린 만큼 **한 틱의 이동량**이 커진다. 좌표는 50Hz 로만 나가기
때문에 이 둘은 같은 손잡이다.

    틱당 이동 = 경로속도 x 0.02s x speed_scale

실측(머그 1.12km 경로, print F1200 / travel F18000):

| 배속 | 틱당 최대 이동 | 40mm 넘는 틱 |
|---|---|---|
| 1x | 6.0mm | 0 |
| 10x | 60mm | 39,230 / 89,026 |
| 100x | 120.5mm | 7,154 / 8,903 (80%) |

100x 에서는 틱의 80% 가 40mm 이상을 건너뛴다. 그건 궤적이 아니라 순간이동이고, 받는 쪽
Unity ArticulationBody 는 force/damping 이 걸린 PD 드라이브라 그 목표를 못 따라간다.
드라이브가 포화하면 헤드는 목표를 쫓지 못한 채 뒤에 처져 헤매고, 시작 지점이 0 이므로
**"영점 근처에 있다가 튄다"** 로 보인다. 궤적 데이터 자체는 멀쩡한데 화면만 깨지는
형태라 원인을 데이터 쪽에서 찾으면 안 나온다.

그래서 `MotionInterpolator` 는 시간 예산과 함께 **거리 예산**(`max_step_mm`)을 쓴다.
거리 상한이 먼저 바닥나면 남은 시간 예산을 **버린다** — 좌표는 반드시 경로 위에 남고
재생만 느려진다. 버린 만큼은 `throttled_ticks` 와 `effective_scale` 로 드러나며 노드가
`요청 100x 대신 약 8.0x` 처럼 한 번 경고한다. 조용히 느려지면 사용자가 알 방법이 없다.

- 순수 모듈의 기본값은 **무제한**이다. "배속은 기계 한계에 걸리지 않는다" 는 위 절의
  약속을 모듈 수준에서 깨지 않기 위해서다. 상한은 화면에 뿌리는 쪽(노드)이 건다
- 노드 파라미터 `max_step_mm` 기본값 **10.0** = `vel_xy`(500mm/s) x `dt`(0.02s). 화면 위의
  헤드가 기계 자신의 최대속도보다 빨리 움직이지 않는다는 뜻이다. `sim.launch.py` 의
  `max_step` 인자가 넘긴다. `0` 이면 무제한(궤적 형상만 볼 때)
- **연속성과 압축은 맞바꾸는 관계다.** 하한은 경로 총 길이가 정한다 — 1.12km 를
  500mm/s 로 그리면 37 분이 바닥이다. 더 줄이려면 상한을 올릴 게 아니라 인필·레이어
  높이를 조정해 경로를 짧게 슬라이싱한다

### ExtrusionPoint 는 틱이 아니라 세그먼트 꼭짓점에서

50 Hz 로 샘플한 점만 흘리면 코너가 뭉개진다. 위 루프에서 세그먼트를 소진할 때마다 그
끝점을 발행하면 Unity 의 선이 G-code 원본 형상과 정확히 일치한다.

- `E` 가 증가하지 않는 구간은 `extruding=false` — Unity 가 선을 끊는다
  (`ExtrusionPoint.msg` 주석의 정의).
- `width` 는 압출량에서 역산: 필라멘트 1.75 mm 기준
  `width = (π·0.875²·ΔE) / (거리 × layer_height)`.
  PrusaSlicer 가 심는 `;WIDTH:` 주석을 그대로 읽어도 된다.

### `sim.launch.py` 에 얹을 인자

A 가 뼈대를 머지한 뒤 그 위에 추가한다.

| 인자 | 기본값 | 값 |
|---|---|---|
| `source` | (A 가 선언) | `gcode` \| `stl` 추가 |
| `file` | `''` | `source` 가 gcode/stl 일 때 절대경로 |
| `speed` | `10.0` | `speed_default` 파라미터로 전달 |

### `file:=` 은 파라미터가 아니라 발행이다

노드가 launch 파라미터로 경로를 받아 직접 열면 **입력 경로가 둘**(파라미터 / `load_gcode`)이
된다. 진입점을 `load_gcode` 하나로 통일하고 `file:=` 은 launch 가 한 번 발행하는 것으로
한다.

```python
TimerAction(period=3.0, actions=[ExecuteProcess(cmd=[
    'ros2', 'topic', 'pub', '--once', '/printer/cmd',
    'voron24_msgs/PrinterCommand',
    "{command: 'load_gcode', payload: '" + file + "'}"])])
```

`source:=stl` 이면 같은 자리에서 `ros2 action send_goal` 로 슬라이서를 호출하고 액션
result 의 G-code 경로를 다시 `load_gcode` 로 발행해야 한다. 이 두 단계는 셸 파이프로
엮기 지저분하므로 `voron24_bringup` 에 **`job_starter` 노드**(oneshot)를 둔다. 액션
클라이언트 + 퍼블리셔 20 줄짜리다.

---

## 6 — `voron24_slicer`

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
- `SliceModel.action` 은 **`voron24_slicer_msgs` 패키지에 정의**한다. `voron24_msgs` 에
  넣으면 계약 변경(PR + 3 인 승인)이 된다.

  > 원래 이 문서는 `voron24_slicer` 자체에 두라고 적었으나 그렇게는 빌드가 안 된다.
  > `rosidl_generate_interfaces(voron24_slicer ...)` 가 이미 같은 이름의 파이썬 패키지와
  > 빌드 타깃을 만들기 때문에, 거기에 `ament_python_install_package(voron24_slicer)` 를
  > 더하면 `ament_cmake_python_build_voron24_slicer_egg` 타깃이 두 번 생성돼 CMake 가
  > 거부한다. 그래서 인터페이스만 `voron24_slicer_msgs`(ament_cmake)로 떼고 노드는
  > `voron24_slicer`(ament_python)에 남겼다 — ROS2 에서 가장 표준적인 구성이다.
  > **이 문서가 막으려던 것은 "계약 패키지(`voron24_msgs`) 오염" 이고 그 의도는 그대로
  > 지켜진다.** 클라이언트가 쓸 타입 이름은 `voron24_slicer_msgs/action/SliceModel` 이다.
- 노드 내부에서는 `subprocess` 로 CLI 를 호출한다. libslic3r 을 링크하지 않는 이유:
  **AGPL-3.0** 이라 링크 시 라이선스가 전파된다. 별도 프로세스 exec 은 해당하지 않는다.
  빌드 의존(Boost/TBB/CGAL/OpenVDB)을 colcon 에 얹지 않아도 되는 것은 덤이다.
- 런타임 의존성은 **순수 파이썬 + 외부 실행파일 하나**를 유지한다. 슬라이서 실행파일
  경로는 ROS 파라미터로 노출해 하드코딩하지 않는다.

### 슬라이서 — PrusaSlicer (apt)

```bash
sudo apt install prusa-slicer     # noble/universe, 2.7.2
prusa-slicer --export-gcode --load voron24_250.ini -o out.gcode model.stl
```

- 설정이 단일 `.ini` 라 레포에 커밋하고 diff 로 추적할 수 있다
- `--export-gcode` 는 콘솔 모드로 X 없이 동작한다. 막히면 `xvfb-run -a` 한 겹을 씌운다
- `--dont-arrange`, `--center 125,125` 로 베드 배치까지 스크립트에서 통제한다

### 프로파일 — `tools/slicer/voron24_250.ini`

PrusaSlicer 공식 번들에 Voron 프로파일은 없다. 조달 경로는 둘.

1. OrcaSlicer(또는 커뮤니티 `.ini`)에서 Voron 2.4 250 설정을 가져와 PrusaSlicer 형식으로 옮긴다
2. 베드 250×250 만 맞춰 직접 만든다. 시뮬레이션 전용이므로 압출·온도·재료는 무의미하고
   궤적만 맞으면 된다

어느 쪽이든 아래는 못박는다. 파서를 단순하게 유지하기 위한 제약이다.

| 설정 | 값 | 이유 |
|---|---|---|
| arc fitting | **끔** | `G2`/`G3` 가 안 나와 파서가 `G0`/`G1` 만 다루면 됨 |
| 좌표 모드 | `G90` (절대) | 상대(`G91`)는 파서에서 거부한다 |
| start/end G-code | **비움** | 홈잉 매크로가 궤적을 오염시킨다 |
| 베드 | 250×250, origin 코너 | 계약의 `bed_origin` 과 부호가 맞는지 `pattern:=sweep` 로 먼저 검증 |

`F`(피드레이트)는 재생 속도로 쓴다. `E`(압출량)는 버리지 않는다 — `extruding` 판정과
`width` 역산에 필요하다.

> 좌표 → 조인트 변환은 **÷1000 한 번뿐**이다(계약 §2). 슬라이서 출력은 mm 절대좌표로
> 고정하고 변환은 `gcode_player_node` 의 publish 직전에서만 한다.

---

## A 와의 인터페이스

→ [04a_runtime_and_control.md](04a_runtime_and_control.md)

| 항목 | 규약 |
|---|---|
| `/printer/playback` | A 가 발행, B 가 구독. `PrinterCommand`. 스트림 계열만 흐른다 |
| `/printer/target` | B 가 발행, A 가 구독. `JointState`, **단위 m**. `name` 순서는 계약 §3 |
| 클램프 | **A 담당.** B 는 리밋을 모르고 그대로 흘린다 |
| `SPEED_STEPS` | **A 소유.** B 의 노드는 임의의 양수를 받는다 |
| `speed` launch 인자 | **B 소유.** `speed_default` 파라미터로 전달, 기본 `10.0` |
| `sim.launch.py` | A 가 뼈대를 먼저 머지 → B 가 `source:=gcode\|stl` + `file` + `speed` 를 얹는다 |
| 선행 커밋 | `patterns.py` `'none'` + `setup.py` 한 줄은 **B 가 먼저.** A 의 4.5 착수 조건이다 |

A 의 상태 노드 없이도 단독 검증이 가능하다.

```bash
ros2 run voron24_gcode gcode_player_node
ros2 topic pub --once /printer/playback voron24_msgs/PrinterCommand \
  "{command: 'load_gcode', payload: '/abs/path/test.gcode'}"
ros2 topic echo /printer/target
```

---

## 새 `.msg` 없음

`/printer/playback` 과 `/printer/target` 둘 다 기존 메시지 타입을 재사용한다. `.msg`
정의가 안 바뀌므로 계약 §3 은 그대로고 `contract_check.py` 도 볼 것이 없다. 다만 계약
§5 토픽 표에는 두 줄을 추가해야 한다.

`SliceModel.action` 은 `voron24_slicer_msgs` 패키지 안에 두므로 계약과 무관하다
(§6 의 각주 참조 — `voron24_slicer` 에 두면 CMake 타깃이 충돌해 빌드가 안 된다).

## 검증

```bash
# ROS 없이
cd ros2_ws/src/voron24_gcode
python3 -m voron24_gcode.gcode_parser sample.gcode
python3 -m voron24_gcode.motion sample.gcode
python3 -m voron24_gcode.patterns              # 선행 커밋 회귀

# 통합
ros2 launch voron24_bringup sim.launch.py source:=gcode file:=/abs/path/test.gcode
ros2 launch voron24_bringup sim.launch.py source:=stl   file:=/abs/path/test.stl
bash tools/smoke_test.sh
```
