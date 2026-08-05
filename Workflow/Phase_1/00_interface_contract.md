# 00 — 인터페이스 계약 (전원 필독)

> **병렬 작업을 가능케하는 최소 규약**
> 기록된 이름·단위·좌표계는 **초기 확정** 이후 변경 시 반드시 PR + 3인 승인.
> 계약 이행 시 상대방 산출물이 없이 mock 기반으로 작업 가능.

---

## 1. 대상 기종

**Voron 2.4 R2 / 250mm** (250 × 250 × 250 build volume)

Voron 2.4의 기구 특성:

| 항목 | Voron 2.4 |
|---|---|
| XY 구동 | CoreXY (A/B 모터, 후면 상단 좌우) |
| Z 구동 | **플라잉 갠트리** — 갠트리 전체가 4개 Z 모터 + 벨트로 승강 |
| 베드 | **프레임에 고정. 움직이지 않음** |
| 프레임 | 2020 알루미늄 익스트루전 |
| 레일 | X: MGN12H / Y: MGN9H ×2 / Z: MGN9H ×4 |
| 툴헤드 | Stealthburner + Clockwork 2 |

> **유의**: `y_bed` 링크 금지. Voron 2.4에서 베드는 `base_link`의 일부임.

---

## 2. 좌표계 & 단위

### 월드 원점 (`base_link`)

```
원점: 프레임 하단면의 정중앙 (바닥에 닿는 면, X/Y 중심)
X: 우측 (+)       — 정면에서 봤을 때 오른쪽
Y: 후방 (+)       — 정면에서 봤을 때 안쪽
Z: 상방 (+)
```

REP-103 (ROS 표준, right-handed, Z-up) 을 따릅니다.

### 단위

| 구간 | 단위 |
|---|---|
| FreeCAD / STEP 원본 | **mm** |
| STL 메시 파일 | **mm** (URDF에서 `scale="0.001 0.001 0.001"`로 축소) |
| URDF `<origin>`, `<limit>` | **m**, **rad** |
| ROS2 토픽 전부 | **m**, **rad**, **초** (SI) |
| G-code | mm → **파서 노드가 m로 변환해서 퍼블리시** |
| Unity ArticulationBody | prismatic=**m**, revolute=**degree** (Unity 측에서 변환 책임) |

> **단위 변환 지점**: mm→m 변환은 **ROS2 노드**가, rad→deg 변환은 **Unity 스크립트**가 담당. 다른 곳에서 중복 변환 금지.

---

## 3. 링크 트리

```
base_link                    프레임, 패널, 베드, 전장, Z모터, 데크
├── z_gantry                 prismatic Z  — 갠트리 어셈블리 전체
│   └── x_beam               prismatic Y  — X축 익스트루전 + MGN12 레일
│       └── toolhead         prismatic X  — Stealthburner + Clockwork2
│           ├── nozzle       fixed        — TCP (형상 없음)
│           └── extruder_gear continuous  — [선택] 압출 기어 회전
├── bed_origin               fixed        — G-code (0,0,0) 기준 프레임 (형상 없음)
├── door_front               revolute     — [선택]
└── door_left / door_right   revolute     — [선택]
```

### 조인트 정의

| 이름 | 타입 | 축 | parent → child | 범위 (m) | 비고 |
|---|---|---|---|---|---|
| `joint_z` | prismatic | `0 0 1` | base_link → z_gantry | 0 ~ 0.250 | +가 위 |
| `joint_y` | prismatic | `0 1 0` | z_gantry → x_beam | 0 ~ 0.250 | +가 후방 |
| `joint_x` | prismatic | `1 0 0` | x_beam → toolhead | 0 ~ 0.250 | +가 우측 |
| `joint_e` | continuous | `0 1 0` | toolhead → extruder_gear | — | 선택 |
| `joint_door_*` | revolute | `0 0 1` | base_link → door_* | 0 ~ 2.0 rad | 선택 |

**조인트 위치값 = G-code 좌표를 m로 변환한 값**. 즉 `G1 X125 Y125 Z10` → `joint_x=0.125, joint_y=0.125, joint_z=0.010`. 홈 위치 오프셋은 URDF의 `<origin>`이 반영.

### CoreXY 벨트 처리 — 중요

CoreXY는 폐루프라 URDF로 표현 불가. **X/Y를 독립 prismatic으로 모델링.** 모터 각도가 필요할 경우 ROS2에서 변환.

```
A_mm = X + Y          # A 모터 이동량
B_mm = X - Y          # B 모터 이동량
θ = (mm / (pulley_teeth × belt_pitch)) × 2π    # GT2 20T → 40mm/rev
```

모터 풀리 회전 시각화 필요 시 `/printer/motor_angles` 토픽으로 별도 퍼블리시 (기구학과 무관한 순수 시각 요소).

---

## 4. 메시 파일 계약

### 위치

```
ros2_ws/src/voron24_description/meshes/
├── visual/
│   ├── base_link.stl
│   ├── z_gantry.stl
│   ├── x_beam.stl
│   ├── toolhead.stl
│   ├── extruder_gear.stl      (선택)
│   └── door.stl               (선택, 4면 공용)
└── collision/
    ├── base_link.stl
    ├── z_gantry.stl
    ├── x_beam.stl
    └── toolhead.stl
```

### 규칙

1. **파일명은 링크명과 정확히 일치.** 소문자 + 언더스코어.
2. **단위 mm**, 바이너리 STL.
3. **원점 = 해당 링크의 조인트 축 위치.** mesh import 시 원점 근처.
4. **삼각형 예산** (초과 시 Unity가 느려집니다):

| 링크 | visual 상한 | collision 상한 |
|---|---|---|
| base_link | 120,000 | 2,000 |
| z_gantry | 60,000 | 1,000 |
| x_beam | 30,000 | 500 |
| toolhead | 40,000 | 1,000 |
| **합계** | **250,000** | — |

5. **collision은 convex 형상만** (박스/실린더 근사 또는 convex hull). Unity ArticulationBody는 non-convex MeshCollider 사용 불가.

---

## 5. ROS2 토픽 계약

### CAD → 없음 (파일 산출물만)

### ROS2 → Unity

| 토픽 | 타입 | Rate | 설명 |
|---|---|---|---|
| `/joint_states` | `sensor_msgs/JointState` | 50 Hz | 필수. name/position 필수, velocity 선택 |
| `/printer/status` | `voron24_msgs/PrinterStatus` | 5 Hz | 온도, 진행률, 상태 |
| `/printer/extrusion` | `voron24_msgs/ExtrusionPoint` | 이벤트 | 압출 궤적 시각화용 |
| `/tf`, `/tf_static` | `tf2_msgs/TFMessage` | 50 Hz | robot_state_publisher가 발행 |

`/joint_states`의 `name` 필드는 **반드시** 위 조인트명 사용:
```python
msg.name = ['joint_x', 'joint_y', 'joint_z', 'joint_e']
msg.position = [x_m, y_m, z_m, e_rad]
```

### Unity → ROS2

| 토픽 | 타입 | 설명 |
|---|---|---|
| `/printer/cmd` | `voron24_msgs/PrinterCommand` | GUI 조작 (jog, home, pause) |
| `/printer/door_state` | `std_msgs/Float32MultiArray` | 도어 개폐 각도 (4개) |

### 커스텀 메시지 정의 (`voron24_msgs`)

```
# PrinterStatus.msg
std_msgs/Header header
string state              # "idle" | "printing" | "paused" | "error"
float32 nozzle_temp
float32 nozzle_target
float32 bed_temp
float32 bed_target
float32 chamber_temp
float32 progress          # 0.0 ~ 1.0
uint32  current_layer
uint32  total_layers
string  filename
```

```
# ExtrusionPoint.msg
std_msgs/Header header
geometry_msgs/Point position   # bed_origin 기준, m
float32 width                  # 압출 폭 m
float32 height                 # 레이어 높이 m
bool    extruding              # false면 travel move
uint32  layer
```

```
# PrinterCommand.msg
string  command           # "jog" | "home" | "pause" | "resume" | "stop" | "load_gcode"
float32[] args
string  payload
```

---

## 6. Git 레포 구조

```
voron24-digital-twin/
├── README.md
├── .gitattributes                  # Git LFS 설정
├── Workflow/Phase_1
│   ├── 00_interface_contract.md    ← 이 문서
│   ├── 01_cad_workflow.md          ← A 담당
│   ├── 02_unity_workflow.md        ← B 담당
│   └── 03_ros2_workflow.md         ← C 담당
├── cad/                            ← A 담당 (LFS)
│   ├── source/                     원본 STEP
│   ├── working/                    FreeCAD 작업 파일 (.FCStd)
│   ├── scripts/                    추출 매크로 (.py)
│   └── measurements.md             조인트 좌표 실측표
├── ros2_ws/src/                    ← C 담당 (description은 A와 공유)
│   ├── voron24_description/        URDF + 메시  ★ A와 C의 접점
│   ├── voron24_msgs/
│   ├── voron24_gcode/
│   ├── voron24_bringup/
│   └── voron24_moonraker/
└── unity/Voron24Twin/              ← B 담당
    ├── Assets/
    ├── Packages/
    └── ProjectSettings/
```

### Git LFS

`.gitattributes`:
```
*.stl   filter=lfs diff=lfs merge=lfs -text
*.step  filter=lfs diff=lfs merge=lfs -text
*.stp   filter=lfs diff=lfs merge=lfs -text
*.FCStd filter=lfs diff=lfs merge=lfs -text
*.fbx   filter=lfs diff=lfs merge=lfs -text
*.psd   filter=lfs diff=lfs merge=lfs -text
```

Unity는 `.gitignore`에 `Library/`, `Temp/`, `Logs/`, `Obj/`, `Build/`, `UserSettings/` 추가.
Unity 프로젝트 설정: **Edit → Project Settings → Editor → Asset Serialization → Force Text**, **Version Control → Visible Meta Files**. 그렇지 않을 시 씬 파일 머지가 불가능함.

### 브랜치

```
main        보호. PR + 리뷰 1인 이상
dev         통합 브랜치
feat/cad-*      A
feat/unity-*    B
feat/ros2-*     C
```

`voron24_description`은 A와 C가 함께 건드리므로 **A가 메시/URDF, C가 launch/config**로 파일 단위 분리. 같은 파일을 동시에 수정하지 않도록 합니다.

---

## 7. Mock 기반 병렬 작업

**C 우선 작업**: 형상 없이 **더미 URDF**와 **사인파 joint_states 퍼블리셔**를 먼저 `dev`에 추가.

```xml
<!-- voron24_description/urdf/voron24_mock.urdf — 박스만 있는 더미 -->
<robot name="voron24">
  <link name="base_link">
    <visual><geometry><box size="0.35 0.35 0.05"/></geometry></visual>
    <inertial><mass value="10"/><inertia ixx="0.1" iyy="0.1" izz="0.1"
              ixy="0" ixz="0" iyz="0"/></inertial>
  </link>
  <link name="z_gantry"> ... 박스 ... </link>
  <joint name="joint_z" type="prismatic"> ... </joint>
  ...
</robot>
```

이렇게 하면:
- **B(Unity)**: 실제 메시 없이도 임포트/구독/드라이브 로직을 완성. 나중에 메시만 교체
- **A(CAD)**: 링크 이름과 조인트 위치만 계약대로 맞추면 됨. Unity를 몰라도 됨
- **C(ROS2)**: 형상과 무관하게 노드/런치/G-code 파서를 완성

메시 교체는 파일 덮어쓰기 + URDF의 `<geometry>` 한 줄 교체로 끝.

---

## 8. 마일스톤

| 주차 | A (CAD) | B (Unity) | C (ROS2) | 통합 게이트 |
|---|---|---|---|---|
| W1 | STEP 임포트, 파츠 인벤토리 | 프로젝트 셋업, 패키지 설치 | **mock URDF + 사인파 퍼블리셔** | mock URDF가 RViz+Unity 양쪽에서 움직임 |
| W2 | 좌표 정렬, 링크 그룹핑, 좌표 실측 | mock URDF 임포트, 조인트 드라이버 | G-code 파서 노드 | Unity가 사인파에 반응 |
| W3 | 메시 추출 + 데시메이션 | 카메라, UI, 씬 구성 | 커스텀 msg, 궤적 퍼블리시 | — |
| W4 | **실제 URDF + 메시 커밋** | 메시 교체, 머티리얼 | launch 통합, Moonraker 브릿지 | **실제 모델로 G-code 재생** |
| W5 | 도어/디테일, LOD | 압출 궤적 렌더, 폴리시 | 실기 연동 / 안전 인터록 | 전체 데모 |

---

## 9. 용어 통일

| 허용 | 금지 |
|---|---|
| `z_gantry` | gantry, z_axis, zGantry |
| `x_beam` | x_gantry, x_rail, cross_beam |
| `toolhead` | hotend, extruder, carriage, print_head |
| `bed_origin` | origin, print_origin, zero |

---

## 10. 라이선스

Voron 2.4 CAD는 **VoronDesign/Voron-2** 레포 배포 **GPLv3**.

- 레포 루트의 `LICENSE` 파일을 반드시 확인하고, `cad/source/LICENSE_VORON`으로 복사
- 파생 저작물(Mesh, URDF)을 공개 배포 시 **동일 라이선스 적용 의무** 발생.
- Voron은 상표(trademark)를 별도로 관리. 프로젝트명에 "Voron"을 넣어 배포 시 Voron Design의 상표 정책 확인 필요
- 내부 연구/학습 목적이면 문제 없으나, 발표·논문·GitHub 공개 시 **출처 명시 필수**

---

## 11. 변경 이력

| 날짜 | 변경 | 승인 |
|---|---|---|
| 2026-08-04 | 초안 | A/B/C |
