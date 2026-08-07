# 00 — 인터페이스 계약 (전원 필독)
 
> 세 사람의 병렬 작업을 가능하게 하는 근거 문서.
> 여기 적힌 이름·단위·좌표계는 **Day 1에 확정**, 변경 시 PR + 3인 승인 필수.
> 계약 이행 하 상대방 산출물 없이도 mock으로 끝까지 작업 가능.
 
---
 
## 1. 대상 기종
 
**Voron 2.4 R2 / 250mm** (250 × 250 × 250 build volume)
 
기구 특성 — 착각 시 전체가 틀어짐:
 
| 항목 | Voron 2.4 |
|---|---|
| XY 구동 | CoreXY (A/B 모터, 후면 상단 좌우) |
| Z 구동 | **플라잉 갠트리** — 갠트리 전체가 4개 Z 모터 + 벨트로 승강 |
| 베드 | **프레임에 고정. 움직이지 않음** |
| 프레임 | 2020 알루미늄 익스트루전 |
| 레일 | X: MGN12H / Y: MGN9H ×2 / Z: MGN9H ×4 |
| 툴헤드 | Stealthburner + Clockwork 2 |
 
> **빈발 오류**: 베드슬링어(i3)처럼 `y_bed` 링크 생성. Voron 2.4에서 베드는 `base_link`의 일부.
 
---
 
## 2. 좌표계 & 단위
 
### 월드 원점 (`base_link`)
 
```
원점: 프레임 하단면의 정중앙 (바닥 접촉면, X/Y 중심)
X: 우측 (+)   — 정면 기준 오른쪽
Y: 후방 (+)   — 정면 기준 안쪽
Z: 상방 (+)
```
 
REP-103 준수 (right-handed, Z-up).
 
### 단위
 
| 구간 | 단위 |
|---|---|
| FreeCAD / STEP 원본 | **mm** |
| STL 메시 파일 | **mm** (URDF에서 `scale="0.001 0.001 0.001"`) |
| `voron24_params.xacro` | **m**, **rad** |
| URDF `<origin>`, `<limit>` | **m**, **rad** |
| ROS2 토픽 전부 | **m**, **rad**, **초** (SI) |
| `PrinterCommand.args` (jog) | **mm** — 예외. 사용자 조작 단위 |
| G-code | mm → 파서 노드가 m로 변환 후 퍼블리시 |
| Unity ArticulationBody | prismatic **m**, revolute **degree** |
 
**변환 책임 위치 고정** — 중복 변환 금지:
 
| 변환 | 담당 |
|---|---|
| mm → m (G-code, Moonraker) | ROS2 노드 |
| mm → m (메시) | URDF `<mesh scale>` |
| rad → deg (revolute) | Unity `JointStateSubscriber` |
| Unity ↔ ROS 좌표계 | `ROSGeometry` 확장 (`.To<FLU>()`) |
 
---
 
## 3. 링크 트리 (확정, 변경 금지)
 
```
base_link                    프레임, 패널, 베드, 전장, Z모터, 데크
├── z_gantry                 prismatic Z  — 갠트리 어셈블리 전체
│   └── x_beam               prismatic Y  — X축 익스트루전 + MGN12 레일
│       └── toolhead         prismatic X  — Stealthburner + Clockwork2
│           ├── nozzle       fixed        — TCP (형상 없음)
│           └── extruder_gear continuous  — [선택] 압출 기어
├── bed_origin               fixed        — G-code (0,0,0) 기준 (형상 없음)
└── door_front / door_left / ...  revolute — [선택]
```
 
### 조인트 정의
 
| 조인트명 | 타입 | 축 | parent → child | 범위 (m) | 비고 |
|---|---|---|---|---|---|
| `joint_z` | prismatic | `0 0 1` | base_link → z_gantry | 0 ~ 0.250 | +가 위 |
| `joint_y` | prismatic | `0 1 0` | z_gantry → x_beam | 0 ~ 0.250 | +가 후방 |
| `joint_x` | prismatic | `1 0 0` | x_beam → toolhead | 0 ~ 0.250 | +가 우측 |
| `joint_nozzle` | fixed | — | toolhead → nozzle | — | TCP |
| `joint_bed_origin` | fixed | — | **base_link** → bed_origin | — | 베드 고정 |
| `joint_e` | continuous | `0 1 0` | toolhead → extruder_gear | — | 선택 |
 
**`lower`는 반드시 0** — G-code 좌표를 그대로 조인트 값으로 쓰기 위함. 홈 위치 오프셋은 `<origin>`이 흡수.
 
조인트 위치값 = G-code 좌표 ÷ 1000. `G1 X125 Y125 Z10` → `joint_x=0.125, joint_y=0.125, joint_z=0.010`.
 
### CoreXY 벨트 처리
 
폐루프이므로 URDF 표현 불가. **X/Y를 독립 prismatic으로 모델링**, 모터 각도는 ROS2에서 변환.
 
```
A_mm = X + Y
B_mm = X - Y
θ = (mm / (pulley_teeth × belt_pitch)) × 2π      # GT2 20T → 40mm/rev
```
 
구현: `voron24_gcode/corexy.py`. 모터 풀리 회전 시각화가 필요하면 `/printer/motor_angles`로 별도 퍼블리시 — 기구학과 무관한 순수 시각 요소.
 
---
 
## 4. URDF 파일 구조
 
### 단일 URDF 원칙
 
mock용과 real용을 별도 파일로 두면 **조인트 좌표가 중복되어 반드시 드리프트 발생**. 한 벌로 통합하고 인자로 형상만 전환.
 
```bash
xacro voron24.urdf.xacro use_meshes:=false   # 박스 (mock)
xacro voron24.urdf.xacro use_meshes:=true    # A의 STL (real)
```
 
### 파일 구성과 소유권
 
| 파일 | 내용 | 수정 권한 |
|---|---|---|
| `urdf/voron24_params.xacro` | **모든 치수의 단일 출처** | **A 전용** |
| `urdf/voron24.urdf.xacro` | 링크 트리 정의 | C 전용 |
| `urdf/voron24_macros.xacro` | 관성·형상 매크로 | C 전용 |
| `meshes/visual/*.stl` | 시각 메시 | **A 전용** |
| `meshes/collision/*.stl` | 콜리전 메시 | **A 전용** |
| `launch/`, `rviz/` | 실행 설정 | C 전용 |
 
**A는 URDF 본문을 편집하지 않음.** `voron24_params.xacro`의 값만 실측으로 교체하면 URDF·RViz·Unity가 전부 자동 반영. 같은 패키지를 공유하되 파일이 겹치지 않으므로 머지 충돌 없음.
 
---
 
## 5. 메시 파일 계약
 
### 위치
 
```
ros2_ws/src/voron24_description/meshes/
├── visual/     base_link.stl  z_gantry.stl  x_beam.stl  toolhead.stl
└── collision/  base_link.stl  z_gantry.stl  x_beam.stl  toolhead.stl
```
 
### 규칙
 
1. 파일명은 링크명과 정확히 일치. 소문자 + 언더스코어
2. 단위 **mm**, 바이너리 STL
3. **원점 = 해당 링크의 조인트 축 위치.** 단독 임포트 시 원점 근처에 위치할 것
4. 삼각형 예산:
| 링크 | visual 상한 | collision 상한 |
|---|---|---|
| base_link | 120,000 | 2,000 |
| z_gantry | 60,000 | 1,000 |
| x_beam | 30,000 | 500 |
| toolhead | 40,000 | 1,000 |
| **합계** | **250,000** | — |
 
5. collision은 **convex 형상만** (박스/실린더 근사 또는 convex hull). Unity ArticulationBody는 non-convex MeshCollider 사용 불가
예산 초과 여부는 `contract_check.py --check-meshes`가 자동 검사.
 
---
 
## 6. ROS2 토픽 계약
 
### ROS2 → Unity
 
| 토픽 | 타입 | Rate | 설명 |
|---|---|---|---|
| `/joint_states` | `sensor_msgs/JointState` | 50 Hz | 필수. name/position 필수 |
| `/printer/status` | `voron24_msgs/PrinterStatus` | 5 Hz | 온도, 진행률, 상태 |
| `/printer/extrusion` | `voron24_msgs/ExtrusionPoint` | 이벤트 | 압출 궤적 |
| `/tf`, `/tf_static` | `tf2_msgs/TFMessage` | 50 Hz | robot_state_publisher 발행 |
 
`/joint_states`의 `name` 필드는 §3의 조인트명 사용:
 
```python
msg.name = ['joint_x', 'joint_y', 'joint_z']
msg.position = [x_m, y_m, z_m]
```
 
### Unity → ROS2
 
| 토픽 | 타입 | 설명 |
|---|---|---|
| `/printer/cmd` | `voron24_msgs/PrinterCommand` | jog, home, pause 등 |
| `/printer/door_state` | `std_msgs/Float32MultiArray` | 도어 개폐 각도 [선택] |
 
### 메시지 정의 (`voron24_msgs`)
 
```
# PrinterStatus.msg
std_msgs/Header header
string  state          # "idle" | "printing" | "paused" | "error"
float32 nozzle_temp
float32 nozzle_target
float32 bed_temp
float32 bed_target
float32 chamber_temp
float32 progress       # 0.0 ~ 1.0
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
bool    extruding              # false면 travel move (선 끊기 신호)
uint32  layer
```
 
```
# PrinterCommand.msg
string    command
float32[] args
string    payload
 
# command 별 args 규약
#   "jog"        args = [dx, dy, dz]   단위 mm ★
#   "home"       args = []             payload 로 축 지정 가능 ("XYZ")
#   "pause" / "resume" / "stop"        args = []
#   "load_gcode" payload = 파일 절대경로
#   "set_speed"  args = [scale]
```
 
> `.msg` 변경 시 B가 Unity에서 C# 재생성 필요. **변경 = 계약 변경 = PR + 3인 승인.** W2 내 확정 권장.
 
---
 
## 7. Git 레포 구조

### 현재 저장소 구조

현재 Mock 경로를 유지한 채 아래 디렉터리를 단계적으로 추가한다. 실제 파일을 옮길 때는 문서의 명령어와 소유권을 함께 갱신한다.

```
3DPrinter-onUnity-byROS/
├── README.md
├── LICENSE
├── .gitattributes                  Git LFS 설정
├── docs
│   ├── 00_interface_contract.md    이 문서
│   ├── 01_cad_workflow.md          A
│   ├── 02_unity_workflow.md        B
│   └── 03_ros2_workflow.md         C
├── tools/
│   ├── contract_check.py           통합 검증 진입점
│   └── smoke_test.sh               통합 게이트
├── cad/                            A (LFS)
│   ├── source/                     원본 STEP
│   ├── working/                    FreeCAD 작업 파일
│   ├── scripts/                    추출 매크로
│   ├── groups.json                 링크별 파츠 배정
│   └── measurements.md             조인트 실측표
├── ros2_ws/src/
│   ├── voron24_description/        A와 C의 접점 (§4 소유권 참조)
│   ├── voron24_msgs/
│   ├── voron24_gcode/
│   ├── voron24_bringup/
│   └── voron24_moonraker/
└── unity/Voron24Twin/
    ├── README.md 
    ├── Assets/                 모델 산출물 메모
    ├── Packages/
    └── ProjectSettings/
```

`Mock/contract_check.py`는 `tools/`가 추가되기 전 현재 실행 위치. `tools/`로 통합한 뒤에 기존 Mock 검증 경로를 갑자기 삭제하지 말고 README와 CI 명령을 함께 전환.
 
### Git LFS
 
`.gitattributes`:
 
```
*.stl   filter=lfs diff=lfs merge=lfs -text
*.step  filter=lfs diff=lfs merge=lfs -text
*.stp   filter=lfs diff=lfs merge=lfs -text
*.FCStd filter=lfs diff=lfs merge=lfs -text
*.gcode filter=lfs diff=lfs merge=lfs -text
 
*.unity  merge=unityyamlmerge eol=lf
*.prefab merge=unityyamlmerge eol=lf
*.asset  merge=unityyamlmerge eol=lf
*.mat    merge=unityyamlmerge eol=lf
```
 
Unity 프로젝트 설정 필수:
- **Editor → Asset Serialization → Force Text**
- **Editor → Version Control → Visible Meta Files**
미설정 시 씬/프리팹 머지 불가.
 
### 브랜치
 
```
main      보호. PR + 리뷰 1인 이상
dev       통합 브랜치
feat/cad-*      A
feat/unity-*    B
feat/ros2-*     C
```
 
---
 
## 8. Mock 우선 전략 (병렬화의 핵심)
 
**C의 Day 1~2 최우선 과제**: 형상 없는 mock URDF + 더미 퍼블리셔를 `dev`에 푸시.
 
효과:
 
| 담당 | 언블록 내용 |
|---|---|
| B (Unity) | 실제 메시 없이 임포트/구독/드라이브 로직 완성. W4에 `use_meshes:=true`만 |
| A (CAD) | 링크 이름과 조인트 위치만 계약대로 맞추면 됨. Unity 지식 불요 |
| C (ROS2) | 형상과 무관하게 노드/런치/G-code 파서 완성 |
 
**추가 안전장치** — B는 C의 엔드포인트가 없어도 시작 가능:
 
`LocalMockDriver.cs`가 Unity 단독으로 조인트를 구동. ROS 연결 감지 시 자동으로 물러남. `patterns.py`와 동일한 궤적 수식 사용.
 
---
 
## 9. 계약 검증 자동화
 
세 사람이 병렬로 진행하면 이름·축이 조용히 어긋남. 통합 시점에 발견하면 원인 추적에 반나절 소요.
 
```bash
# 커밋 전 필수
python3 tools/contract_check.py --xacro ros2_ws/src/voron24_description/urdf/voron24.urdf.xacro
 
# A의 메시가 들어온 뒤
python3 tools/contract_check.py \
  --xacro ros2_ws/src/voron24_description/urdf/voron24.urdf.xacro \
  --use-meshes --check-meshes
```
 
검출 항목:
 
- 링크/조인트 이름 (§3)
- parent-child 관계, 축 방향
- 스트로크 리밋 범위, `lower=0` 여부
- `<inertial>` 누락 — Unity mass=0 폭발의 주원인
- 메시 `scale` (mm 메시면 0.001)
- 삼각형 예산 (§5)
- **`bed_origin`의 parent가 `base_link`인지** — 베드슬링어 혼동 방지
- 루트 링크가 `base_link` 하나인지
### pre-commit 훅
 
`.git/hooks/pre-commit`:
 
```bash
#!/usr/bin/env bash
python3 tools/contract_check.py \
  --xacro ros2_ws/src/voron24_description/urdf/voron24.urdf.xacro || exit 1
```
 
### 통합 스모크 테스트
 
```bash
# 터미널 1
ros2 launch voron24_bringup mock.launch.py
# 터미널 2
bash tools/smoke_test.sh
```
 
검사: 토픽 존재, `/joint_states` 주기(40~60Hz), 조인트 이름, `bed_origin → nozzle` TF, TCP 10000 개방.
 
---
 
## 10. 마일스톤
 
| 주차 | A (CAD) | B (Unity) | C (ROS2) | 통합 게이트 |
|---|---|---|---|---|
| W1 | STEP 임포트, 파츠 인벤토리 | 프로젝트 셋업, 패키지 설치, `LocalMockDriver`로 선행 작업 | **mock URDF + mock 퍼블리셔 + contract_check** | `smoke_test.sh` 통과, RViz+Unity 동시 구동 |
| W2 | 좌표 정렬, 링크 그룹핑, **좌표 실측** | mock URDF 임포트, 조인트 드라이버 | 커스텀 msg 확정, G-code 파서 | Unity가 mock 패턴에 반응 |
| W3 | 메시 추출 + 데시메이션 | 카메라, UI, 압출 궤적 렌더 | G-code 플레이어, 궤적 퍼블리시 | `pattern:=square` 궤적이 Unity에 그려짐 |
| W4 | **`voron24_params.xacro` 값 + 메시 커밋** | `use_meshes:=true` 전환, 머티리얼 | launch 통합, Moonraker 브릿지 | **실제 모델로 G-code 재생** |
| W5 | 도어/디테일, LOD | 압출 렌더 최적화, 폴리시 | 실기 연동, 안전 인터록 | 전체 데모 |
 
W4의 A 산출물이 "URDF 작성"에서 "파라미터 값 채우기"로 축소된 점에 주의 (§4 단일 URDF 원칙).
 
---
 
## 11. 용어 통일
 
| 사용 | 금지 |
|---|---|
| `base_link` | frame, body, root |
| `z_gantry` | gantry, z_axis, zGantry |
| `x_beam` | x_gantry, x_rail, cross_beam |
| `toolhead` | hotend, extruder, carriage, print_head |
| `bed_origin` | origin, print_origin, zero |
| `nozzle` | tcp, tip, tool_tip |
 
---
 
## 12. 라이선스
 
Voron 2.4 CAD는 **VoronDesign/Voron-2** 레포 배포, **GPLv3**.
 
- 레포 루트 `LICENSE`를 `cad/source/LICENSE_VORON`으로 복사 후 커밋
- 파생 저작물(메시, URDF) 공개 배포 시 동일 라이선스 적용 의무 발생 가능
- Voron Design은 상표를 별도 관리. 프로젝트명에 "Voron" 사용 시 상표 정책 확인
- 내부 연구/학습 목적은 무방. 발표·논문·공개 시 **출처 명시 필수**
---
 
## 13. 변경 이력
 
| 날짜 | 변경 | 승인 |
|---|---|---|
| 2026-08-04 | 초안 | A/B/C |
| 2026-08-05 | URDF 단일 파일 구조로 통합, `voron24_params.xacro` 도입, 소유권 규칙 개정 (§4) | A/B/C |
| 2026-08-05 | `contract_check.py` / `smoke_test.sh` 도입 (§9) | A/B/C |
