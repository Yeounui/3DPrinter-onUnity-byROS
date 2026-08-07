# 02 — Unity 담당 워크플로
 
> **담당: B**
> **최종 산출물**: `unity/Voron24Twin/` — G-code 재생에 맞춰 실시간 구동되는 Voron 2.4 디지털 트윈
> **선행**: [00_interface_contract.md](00_interface_contract.md) 숙지 — 특히 §2 단위, §3 조인트명, §6 토픽
> **병렬화 유의**: A의 메시를 대기하지 않을 것. C의 mock URDF로 로직 전부 완성 후 W4에 `use_meshes:=true`로 전환. C의 엔드포인트조차 없으면 `LocalMockDriver`로 선행 작업 가능.

## 이 문서의 목적과 내 역할

이 프로젝트는 실제 Voron 2.4 R2 250 mm 프린터를 Unity와 ROS2에서 재현하는 **디지털 트윈** 프로젝트다. Unity 담당자 B의 역할은 STEP 파일을 단순히 화면에 표시하는 것이 아니라, A의 CAD 형상과 C의 ROS2 데이터를 연결하여 다음 파이프라인을 완성하는 것이다.

```text
A의 링크별 STL + C의 URDF/Xacro
                ↓
          Unity URDF Import
                ↓
C의 /joint_states → JointStateSubscriber → ArticulationBody 구동
                ↓
     상태 UI + 노즐 추적 + 압출 궤적 표시
                ↓
       Unity 명령 → /printer/cmd → ROS2
```

| 담당 | 담당 작업 | Unity와의 접점 |
|---|---|---|
| A — CAD | STEP 원본 분리, visual/collision STL 생성, 조인트 위치 실측 | B는 링크별 STL과 실측 파라미터를 실제 모델 전환 단계에서 반영 |
| **B — Unity** | 모델 표시, 조인트 구동, 카메라·UI·압출 궤적, ROS 연결 | A의 형상과 C의 데이터를 Unity에서 통합하고 검증 |
| C — ROS2 | URDF/Xacro, 토픽, G-code 재생, ROS-TCP-Endpoint | B는 메시를 구독해 모델을 움직이고 사용자 명령을 송신 |

### 현재 확보된 자료와 진행 단계

현재 작업 폴더를 기준으로 확인된 상태다.

| 자료 | 상태 | 판단 및 다음 조치 |
|---|---|---|
| `02_unity_workflow.md` | 확보 | 본 문서를 작업 기준으로 사용 |
| `Voron_2.4r2_Assembly.step` | 확보, 약 241 MB | 전체 CAD 조립 원본. Unity 최종 입력물이 아니라 A의 링크별 STL 추출 원본 |
| `00_interface_contract.md` | 현재 작업 폴더에서 미확인 | 저장소 `docs/`에서 정식 파일을 확보하고 대화 로그가 아닌 정식 계약을 기준으로 사용 |
| Unity 프로젝트 | 현재 작업 폴더에서 미확인 | `unity/Voron24Twin/` 인수 또는 생성 필요 |
| mock URDF/Xacro | 현재 작업 폴더에서 미확인 | C에게 `voron24_description` 패키지 요청 |
| Unity 구동 스크립트 | 현재 작업 폴더에서 미확인 | mock 패키지 또는 저장소에서 존재 여부 확인 후 구현 |
| 링크별 visual/collision STL | 현재 작업 폴더에서 미확인 | W4 전 A에게 계약 형식으로 인수 |

따라서 현재 단계는 **구현 전 자료 인수 및 환경 준비 단계**다. 먼저 계약 문서와 Unity 프로젝트, mock URDF를 확보한 뒤 Step 1부터 진행한다. 전체 STEP 어셈블리를 Unity에 직접 임포트하여 임의로 링크를 구성하지 않는다.

### STEP 원본 사용 원칙

확인된 `Voron_2.4r2_Assembly.step`은 AP214 계열의 전체 조립 파일이며 `Gantry`, `A/B Drives`, 모터, 풀리, 체결 부품 등 많은 파트를 포함한다. 길이 단위에는 millimetre가 정의되어 있다.

A가 이 원본을 가공하여 다음 계약 파일을 제공한다.

```text
meshes/visual/base_link.stl
meshes/visual/z_gantry.stl
meshes/visual/x_beam.stl
meshes/visual/toolhead.stl
meshes/collision/base_link.stl
meshes/collision/z_gantry.stl
meshes/collision/x_beam.stl
meshes/collision/toolhead.stl
```

B는 STEP 전체 조립 구조의 부품명을 Unity 조인트명으로 직접 사용하지 않는다. Unity 링크와 조인트는 오직 계약의 `base_link`, `z_gantry`, `x_beam`, `toolhead`, `nozzle`, `bed_origin` 및 `joint_x`, `joint_y`, `joint_z`를 사용한다.

### 전체 진행 순서

| 단계 | Unity 담당 작업 | 입력물 | 완료 판단 |
|---|---|---|---|
| 0. 계약·자료 인수 | 정식 계약, 저장소, Unity 버전, ROS 실행 환경 확인 | 00 문서, 저장소 정보 | 파일 위치와 팀 연결 조건을 모두 기록 |
| 1. 프로젝트 기반 | 패키지·Git·Physics·씬 설정 | Unity 프로젝트 | Console 빨간 오류 없음 |
| 2. mock 임포트 | `use_meshes:=false` URDF 임포트 | C의 Xacro | 모델 자세·질량·링크 트리 정상 |
| 3. Unity 단독 검증 | LocalMockDriver로 X/Y/Z 구동 | mock URDF | ROS 없이 방향·범위 통과 |
| 4. ROS 조인트 연결 | `/joint_states` 구독 | C의 mock publisher | RViz와 Unity 자세 일치 |
| 5. 시각화 | 상태 UI·노즐 추적·압출 궤적 | custom msgs | 출력 과정 실시간 표시 |
| 6. 명령 송신 | `/printer/cmd` 퍼블리시 | UI 입력 | ROS2에서 명령 수신 확인 |
| 7. 실제 메시 전환 | `use_meshes:=true` 재임포트 | A의 STL·실측값 | mock과 동일한 축 동작 |
| 8. 통합 인수 | 성능·연결·좌표·Git 검증 | 전체 시스템 | W1~W5 게이트 통과 |
 
---
 
## 준비
 
### 버전
 
| 항목 | 버전 | 비고 |
|---|---|---|
| **Unity** | **2022.3 LTS** 또는 **6000.x LTS** | 팀 전원 동일 버전 |
| Render Pipeline | **URP** | 조명·투명 패널 표현에 유리 |
| .NET | Standard 2.1 | Player Settings |
 
### 패키지 설치
 
`Window → Package Manager → + → Add package from git URL`
 
```
https://github.com/Unity-Technologies/URDF-Importer.git?path=/com.unity.robotics.urdf-importer#v0.5.2
https://github.com/Unity-Technologies/ROS-TCP-Connector.git?path=/com.unity.robotics.ros-tcp-connector
```
 
> URDF-Importer가 ROS-TCP-Connector를 의존성으로 포함하므로 위 순서 준수. 설치 후 상단 메뉴에 **Robotics** 생성.
 
### 프로젝트 설정 (Git 협업 필수)
 
`Edit → Project Settings → Editor`
- **Asset Serialization → Mode: Force Text**
- **Version Control → Mode: Visible Meta Files**
미설정 시 씬/프리팹이 바이너리로 저장되어 머지 불가.
 
`Edit → Project Settings → Physics`
- **Solver Type: Temporal Gauss Seidel** — ArticulationBody 안정성 향상
- **Default Solver Iterations: 12** / **Velocity Iterations: 4**
- **Time → Fixed Timestep: 0.01** (100Hz)
### .gitignore
 
```
unity/Voron24Twin/[Ll]ibrary/
unity/Voron24Twin/[Tt]emp/
unity/Voron24Twin/[Oo]bj/
unity/Voron24Twin/[Bb]uild/
unity/Voron24Twin/[Ll]ogs/
unity/Voron24Twin/[Uu]serSettings/
*.csproj
*.sln
```
 
---
 
## Step 1 — 씬 골격 (W1)
 
`Assets/Scenes/Voron24Twin.unity`
 
```
Scene
├── --- ENV ---
│   ├── Directional Light        (Rotation 50, -30, 0 / Intensity 0.8)
│   ├── Floor                    (Plane, 10x10, 어두운 회색)
│   └── Reflection Probe
├── --- ROS ---
│   └── ROSConnection
├── --- ROBOT ---
│   └── (URDF 임포트 결과)
├── --- VIZ ---
│   ├── ExtrusionRenderer
│   └── NozzleTrail
├── --- CAMERA ---
│   ├── Main Camera (Orbit)
│   ├── Cam_Front / Cam_Top / Cam_Nozzle
│   └── CameraRig
└── --- UI ---
    └── Canvas (Screen Space - Overlay)
```
 
### ROS 연결 설정
 
`Robotics → ROS Settings`
 
| 항목 | 값 |
|---|---|
| Protocol | **ROS2** |
| ROS IP Address | C의 머신 IP (동일 PC면 `127.0.0.1`) |
| ROS Port | `10000` |
| Show HUD | ☑ (개발 중) |
 
> **WSL2에서 ROS2 구동 시**: WSL IP가 재부팅마다 변동. `wsl hostname -I`로 확인하거나 포트 포워딩 설정. C와 사전 합의 필요.
 
---
 
## Step 2 — mock URDF 임포트 (W1~W2)
 
C가 `voron24.urdf.xacro`를 푸시한 후:
 
1. `voron24_description` 폴더 전체를 `unity/Voron24Twin/Assets/URDF/` 아래로 복사
   > URDF-Importer는 `package://` 경로를 URDF 파일 기준 상대 경로로 해석. 폴더 구조 유지 필수.
2. xacro를 URDF로 확장 (Unity는 xacro를 직접 못 읽음)
```bash
   # mock (박스)
   xacro voron24.urdf.xacro use_meshes:=false -o voron24_mock.urdf
   # real (A의 메시가 들어온 뒤)
   xacro voron24.urdf.xacro use_meshes:=true  -o voron24.urdf
```
 
   > 계약 §4의 단일 URDF 원칙. 파일이 두 개로 보이지만 **생성물**이며, 원본은 xacro 한 벌. 조인트 좌표는 항상 `voron24_params.xacro` 하나에서 유래.
 
3. `.urdf` 우클릭 → **Import Robot from Selected URDF file**
| 항목 | 값 | 이유 |
|---|---|---|
| **Select Axis Type** | **Y Axis** | ROS Z-up → Unity Y-up 변환 |
| **Mesh Decomposer** | **VHACD** | collision STL을 convex로 자동 분해 |
| Use Gravity | ☐ 끄기 (초기) | 튜닝 전 안전 |
| Immovable | ☑ (base_link) | 로봇 침하 방지 |

4. **`LocalMockDriver.cs`**(B 담당)가 동일 수식을 C#으로 중복 구현한 상태. **`patterns.py`** 수정 시 B에게 통지할 것.
 
### 임포트 직후 확인
 
- [ ] `base_link`의 ArticulationBody가 **Immovable** 체크
- [ ] 각 조인트가 **Prismatic**, Axis가 계약 §3과 일치
- [ ] 각 링크의 **Mass가 0이 아님** — 0이면 `<inertial>` 누락. A/C에게 보고
- [ ] Play 시 모델이 무너지거나 진동하지 않음
---
 
## Step 3 — 조인트 드라이버 (W2, 핵심)
 
`Assets/Scripts/Robot/JointStateSubscriber.cs`
 
### 설계 요점
 
**조인트를 이름으로 자동 탐색.** URDF-Importer가 링크명으로 GameObject를 생성하므로, W4에 메시 교체 후 재임포트해도 Inspector 재연결 불요.
 
```csharp
static Transform FindDeep(Transform root, string name)
{
    if (root.name == name) return root;
    for (int i = 0; i < root.childCount; i++)
    {
        var r = FindDeep(root.GetChild(i), name);
        if (r != null) return r;
    }
    return null;
}
```
 
Inspector 설정은 `robotRoot`(임포트된 로봇)와 `jointNames`(계약 §3의 이름) 두 개뿐.
 
### 단위 변환
 
계약 §2 준수. **prismatic은 변환 없음, revolute만 rad → deg.**
 
```csharp
j.target = j.isRevolute ? v * Mathf.Rad2Deg : v;
```
 
### 자동 진단
 
다음 상황을 콘솔 에러로 즉시 통지:
 
| 조건 | 메시지 |
|---|---|
| 이름으로 GameObject 미발견 | 계약 §3과 URDF 조인트명 대조 요청 |
| ArticulationBody 부재 | URDF-Importer 임포트 여부 확인 |
| `mass == 0` | `<inertial>` 누락 → A/C에게 통지 |
| 수신 메시지에 조인트명 부재 | 퍼블리셔의 `msg.name` 확인 (계약 §6) |
 
### 드라이브 게인 튜닝
 
Inspector의 각 ArticulationBody **xDrive**:
 
| 조인트 | Stiffness | Damping | Force Limit |
|---|---|---|---|
| `joint_x` | 100,000 | 3,000 | 200 |
| `joint_y` | 150,000 | 5,000 | 300 |
| `joint_z` | 300,000 | 20,000 | 1,000 |
 
증상별 대응:
- 목표 추종 지연 → Stiffness ↑, Force Limit ↑
- 오버슈트/진동 → Damping ↑
- 지속 불안정 → Solver Iterations ↑, Fixed Timestep ↓
### Kinematic 모드
 
물리가 계속 불안정하면 전환. 3D 프린터 트윈의 목적은 **정확한 위치 재현**이지 동역학 시뮬이 아님. 데모 안전장치로 스위치 상시 유지 권장.
 
```csharp
if (kinematicMode)
{
    float d = j.isRevolute ? 0f : j.current;
    j.tf.localPosition = j.restPos + j.localAxis * d;
    if (j.isRevolute)
        j.tf.localRotation = Quaternion.AngleAxis(j.current, j.localAxis);
}
else
{
    var drive = j.body.xDrive;
    drive.target = j.current;
    j.body.xDrive = drive;
}
```
 
---
 
## Step 4 — ROS 없이 선행 작업 (W1)
 
`Assets/Scripts/Robot/LocalMockDriver.cs`
 
C의 엔드포인트가 아직 없어도 Unity 단독으로 조인트 구동. 용도:
 
- W1에 B가 C를 기다리지 않고 시작
- ROS 문제인지 Unity 문제인지 판별
- 데모 중 네트워크 단절 시 폴백
```csharp
[SerializeField] bool yieldToRos = true;
 
void Update()
{
    if (yieldToRos && subscriber != null && subscriber.IsConnected) return;
    _t += Time.deltaTime;
    Apply(Compute(_t));
}
```
 
ROS 연결 감지 시 자동으로 물러남. 궤적 수식은 `voron24_gcode/patterns.py`와 동일 (Home / Sweep / Square / Lissajous).
 
> 두 언어로 같은 수식을 중복 구현한 상태. 한쪽 수정 시 다른 쪽도 갱신 필요. 폴백 용도이므로 완전 동기화가 필수는 아님.
 
### 연결 상태 모니터
 
`Assets/Scripts/Robot/ConnectionMonitor.cs` — 화면 좌상단에 연결 상태 + 조인트 값 + 노즐 좌표 오버레이. 통합 디버깅 시 원인 판별용.
 
---
 
## Step 5 — 노즐 위치 추적 (W3)
 
`Assets/Scripts/Robot/NozzleTracker.cs`
 
압출 궤적 렌더링과 UI 좌표 표시가 이 값을 사용. 계약 §3의 `nozzle`과 `bed_origin` 프레임 활용.
 
```csharp
/// 베드 원점 기준 노즐 위치 [m]
public Vector3 NozzleInBed()
    => bedOrigin.InverseTransformPoint(nozzle.position);
 
/// G-code 좌표계 [mm]. UI 표시용
public Vector3 NozzleGcodeMm() => NozzleInBed() * 1000f;
```
 
> **좌표 변환 직접 계산 금지.** ROS-TCP-Connector의 `ROSGeometry` 확장 사용 (계약 §2).
> ```csharp
> using Unity.Robotics.ROSTCPConnector.ROSGeometry;
> var rosPos   = transform.position.To<FLU>();     // Unity -> ROS
> var unityPos = msg.position.From<FLU>();         // ROS -> Unity
> ```
 
---
 
## Step 6 — 커스텀 메시지 활성화 (W2)
 
`voron24_msgs` 의존 스크립트는 **C# 클래스 생성 전까지 컴파일되면 안 됨.** `RosMessageTypes.Voron24` 네임스페이스 부재 시 컴파일 에러 발생.
 
`Assets/Scripts/Robot/_pending_msgs/` 아래 `.cs.txt`로 확장자를 막아둔 상태.
 
### 활성화 절차
 
1. C가 `voron24_msgs`를 빌드했는지 확인
```bash
   colcon build --packages-select voron24_msgs && source install/setup.bash
   ros2 interface show voron24_msgs/msg/PrinterStatus
```
 
2. `Robotics → Generate ROS Messages...`
   - **ROS message path**: `<repo>/ros2_ws/src/voron24_msgs`
   - `msg/` 아래 3개 각각 **Build msg**
   - `Assets/RosMessages/Voron24/msg/*.cs` 생성 확인
3. `.cs.txt` → `.cs`로 변경 후 상위 폴더로 이동
```
   _pending_msgs/PrinterStatusSubscriber.cs.txt  ->  ../PrinterStatusSubscriber.cs
   _pending_msgs/PrinterCommandPublisher.cs.txt  ->  ../PrinterCommandPublisher.cs
```
 
4. 생성된 `Assets/RosMessages/`도 **커밋** — 팀원이 재생성하지 않도록
> C가 `.msg`를 변경하면 2번 재실행 필요. 계약 §6 변경은 PR + 3인 승인 사항이므로 반드시 통지받을 것.
 
---
 
## Step 7 — 압출 궤적 렌더링 (W3~W5)
 
`/printer/extrusion` (`ExtrusionPoint`)를 수신해 출력물 렌더링.
 
### 방식 비교
 
| 방식 | 장점 | 단점 | 용도 |
|---|---|---|---|
| LineRenderer 다중 | 구현 간단 | 세그먼트 증가 시 드로우콜 폭증 | 프로토타입 |
| **동적 Mesh 생성** | 성능 양호, 두께/단면 표현 | 구현 복잡 | **본 구현** |
| GPU Instancing | 매우 빠름 | 세그먼트 연결이 부자연스러움 | 대량 레이어 |
| VFX Graph | 시각 효과 우수 | 실제 형상 아님 | 데모용 |
 
### 동적 Mesh 방식 요점
 
- **레이어별 별도 Mesh** — 65k 정점 제한 회피 + 컬링 효율
- `extruding == false`면 선 끊기 (travel move)
- `LateUpdate`에서 **변경된 레이어만** 갱신. 매 프레임 전체 재빌드 시 성능 붕괴
- 완성된 레이어는 `mesh.UploadMeshData(true)`로 고정
- 세그먼트 수십만 개 도달 시 오래된 레이어 컬링 필수
```csharp
public void AddSegment(Vector3 posLocal, float width, float height,
                       bool extruding, uint layer)
{
    if (!extruding) { _last = posLocal; _hasLast = true; return; }
    if (!_hasLast)  { _last = posLocal; _hasLast = true; return; }
    var lm = GetLayer(layer);
    AppendTube(lm, _last, posLocal, width, height, layer);
    _last = posLocal;
    lm.dirty = true;
}
```
 
좌표는 `bed_origin` 기준이므로 렌더러 GameObject를 `bed_origin`의 자식으로 두면 변환 불요.
 
---
 
## Step 8 — 카메라 & UI (W3)
 
### 카메라 리그
 
- 우클릭 드래그 → 오빗, 휠 → 줌
- 프리셋 뷰: 정면 / 상단 / 노즐 클로즈업 / 도어 오픈
- 노즐 추종 모드 토글
### UI 패널 (`/printer/status` 구독)
 
```
┌─ Printer Status ─────────────┐
│ State    : PRINTING          │
│ File     : benchy.gcode      │
│ Layer    : 42 / 187          │
│ Progress : ███████░░░  38%   │
│ Nozzle   : 218.3 / 220.0 °C  │
│ Bed      :  59.8 /  60.0 °C  │
│ Chamber  :  41.2 °C          │
│ Position : X125.4 Y87.2 Z8.4 │
└──────────────────────────────┘
[◀◀] [▶/❚❚] [▶▶]  Speed: [1x ▼]
```
 
TextMeshPro 사용. 온도는 목표 대비 색상 변화(회색→주황→빨강) 적용 시 직관성 향상.
 
---
 
## Step 9 — Unity → ROS2 퍼블리시 (W4)
 
`PrinterCommandPublisher.cs`. UI 버튼 OnClick에 메서드 직결.
 
```csharp
public void JogXPlus()  => Send("jog", new[] {  jogStepMm, 0f, 0f });
public void Home()      => Send("home");
public void Pause()     => Send("pause");
public void LoadGcode(string path) => Send("load_gcode", null, path);
```
 
> **`jog`의 args 단위는 mm** (계약 §6). SI 예외 항목이므로 주의.
 
---
 
## Step 10 — 실제 메시 전환 (W4)
 
A의 메시가 들어온 후:
 
1. `git pull` → `meshes/` 갱신 확인
2. `Assets/URDF/voron24_description/`로 복사
3. `xacro voron24.urdf.xacro use_meshes:=true -o voron24.urdf`
4. 재임포트 → 새 GameObject 생성
5. 스크립트의 `robotRoot`만 새 로봇으로 교체
> **조인트는 이름으로 자동 연결되므로 개별 재할당 불요** (Step 3). `robotRoot` 한 개만 바꾸면 됨.
 
### 메시 임포트 설정
 
| 항목 | 값 |
|---|---|
| Scale Factor | **1.0** — URDF에 `scale="0.001"`이 있으므로 여기서 중복 적용 금지 |
| Read/Write Enabled | ☐ (메모리 절약) |
| Generate Colliders | ☐ (collision STL 별도 사용) |
| Optimize Mesh | ☑ |
| Normals | Calculate, Smoothing Angle 60 |
 
### 머티리얼
 
| 부위 | 셰이더 | Metallic | Smoothness | 비고 |
|---|---|---|---|---|
| 알루미늄 익스트루전 | URP/Lit | 0.9 | 0.55 | |
| 프린트 파츠(ABS) | URP/Lit | 0.0 | 0.35 | Voron 컬러 배색 |
| 패널(투명) | URP/Lit, Transparent | 0.0 | 0.95 | Alpha 0.25 |
| PEI 베드 | URP/Lit | 0.3 | 0.7 | |
| 필라멘트 | URP/Lit | 0.0 | 0.4 | Vertex Color |
 
---
 
## Step 11 — 검증
 
### 축 방향 (`pattern:=sweep`으로 확인)
 
- [ ] `joint_x = 0.25` → 툴헤드가 **우측 끝**
- [ ] `joint_y = 0.25` → X빔이 **후방 끝**
- [ ] `joint_z = 0.25` → **갠트리가 상승. 베드는 정지**
- [ ] 세 조인트 0 (`pattern:=home`) → 노즐이 베드 좌전방 코너
### 기타
 
- [ ] 스트로크 끝에서 부품 관통 없음
- [ ] 50Hz `/joint_states`에서 움직임 끊김 없음
- [ ] RViz와 Unity 자세 육안 일치
- [ ] Profiler에서 60fps 유지 (압출 궤적 5,000 세그먼트 기준)
- [ ] `ConnectionMonitor` 오버레이가 CONNECTED

---

## Step 12 — 현재 시점의 착수 순서

현재 작업 폴더에는 Unity 프로젝트, mock URDF와 링크별 STL이 아직 확인되지 않았다. 따라서 다음 순서로 착수한다.

### 1순위 — 정식 계약과 저장소 확보

- [ ] `docs/00_interface_contract.md` 정식 파일을 확보
- [ ] `unity/Voron24Twin/` 프로젝트 위치 확인
- [ ] 팀 공용 브랜치와 Unity 작업 브랜치 확인
- [ ] Unity LTS 버전을 팀원과 동일하게 맞춤
- [ ] ROS2가 Ubuntu, WSL2 또는 별도 PC 중 어디에서 실행되는지 확인
- [ ] ROS IP, port `10000`, `ROS_DOMAIN_ID` 기록

### 2순위 — C의 mock 입력물 인수

- [ ] `ros2_ws/src/voron24_description/` 확보
- [ ] `voron24.urdf.xacro`, `voron24_params.xacro`, `voron24_macros.xacro` 확인
- [ ] `LocalMockDriver.cs`, `JointStateSubscriber.cs`, `ConnectionMonitor.cs`, `NozzleTracker.cs` 존재 여부 확인
- [ ] `pattern:=home`과 `pattern:=sweep` 실행 방법 확인
- [ ] `/joint_states`가 50Hz로 `joint_x`, `joint_y`, `joint_z`를 보내는지 확인

### 3순위 — Unity mock 게이트 통과

- [ ] Step 1의 프로젝트 설정 완료
- [ ] Step 2의 mock URDF 임포트 완료
- [ ] Step 4의 Unity 단독 X/Y/Z 축 검증 완료
- [ ] Step 3의 ROS 조인트 구독 완료
- [ ] RViz와 Unity 자세 비교 결과 기록

### 4순위 — 시각화와 제어 기능

- [ ] Step 5의 노즐 위치 추적
- [ ] Step 6의 custom message C# 생성
- [ ] Step 7의 압출 궤적 렌더링
- [ ] Step 8의 카메라와 상태 UI
- [ ] Step 9의 Unity → ROS2 명령 전송

### 5순위 — 실제 CAD 메시 전환

- [ ] A에게 visual/collision STL 8개 인수
- [ ] `voron24_params.xacro` 실측값과 측정 근거 인수
- [ ] C의 `contract_check.py --use-meshes --check-meshes` 통과 확인
- [ ] Step 10의 실제 모델 재임포트
- [ ] Step 11 검증 전체 재실행

> **중요:** STEP 원본을 받았다는 이유로 5순위를 먼저 진행하지 않는다. mock에서 조인트와 ROS 데이터 흐름을 먼저 검증해야 실제 메시 문제와 통신 문제를 분리할 수 있다.

---

## 담당자별 인수·요청 항목

### A — CAD 담당자에게 받을 것

| 항목 | 요구 조건 | Unity에서 확인할 내용 |
|---|---|---|
| 기종 확인 | Voron 2.4 R2 / 250 mm | 빌드 영역과 모델 크기 비교 |
| visual STL 4개 | 계약 링크명, mm, 링크 조인트 원점 기준 | 형상, 방향, 머티리얼 배정 |
| collision STL 4개 | 단순·볼록 형상, triangle budget 준수 | VHACD와 Articulation 안정성 |
| Xacro 실측값 | `voron24_params.xacro`에 반영 | 홈과 스트로크 끝 위치 |
| 측정 근거 | `measurements.md` 또는 주석 | 오프셋 오류 발생 시 추적 |

### C — ROS2 담당자에게 받을 것

| 항목 | 요구 조건 | Unity에서 확인할 내용 |
|---|---|---|
| mock URDF/Xacro | 단일 URDF, `use_meshes` 인자 지원 | mock/real 전환 가능 여부 |
| `/joint_states` | 50Hz, 계약 조인트명, 위치 단위 m | 축 방향과 부드러운 움직임 |
| custom msgs | 계약 §6의 3개 `.msg` | C# 생성 후 컴파일 여부 |
| mock launch | home/sweep 패턴 지원 | Unity 단독 수식과 비교 |
| ROS-TCP-Endpoint | ROS2, port 10000 | 연결 HUD와 재접속 |
| 검사 결과 | contract check와 smoke test | 통합 오류의 책임 구간 분리 |

### B — Unity 담당자가 제공할 것

- [ ] 동일 Unity 버전에서 열리는 `unity/Voron24Twin/`
- [ ] mock과 real 모델에 공통으로 동작하는 이름 기반 조인트 바인딩
- [ ] 축 방향·범위 검증 화면 또는 짧은 영상
- [ ] 연결 상태, 최신 수신 시각, 조인트 값, 노즐 좌표 진단 화면
- [ ] 상태 UI, 압출 궤적, 카메라 및 명령 퍼블리셔
- [ ] Unity Console 빨간 오류가 없는 통합 씬
- [ ] 사용한 commit과 입력 URDF가 기록된 검증 보고

---

## 단계 완료 보고 형식

각 Step 완료 시 다음 형식으로 팀 채널 또는 PR에 기록한다.

```text
[Unity / Step N 완료]
- Unity 버전:
- 작업 브랜치 및 commit:
- 입력 URDF: mock / real
- ROS 연결: 미사용 / 연결됨
- ROS IP 및 port:
- 통과한 검증 항목:
- 남은 오류 또는 위험:
- A에게 필요한 입력물:
- C에게 필요한 입력물:
- 증빙 화면 또는 영상 경로:
```

오류를 보고할 때는 “안 됨”으로만 기록하지 않고 다음 네 항목을 포함한다.

1. 사용한 URDF가 mock인지 real인지
2. 수신한 조인트명과 실제 값
3. Unity Hierarchy에서 문제가 발생한 링크명
4. Unity Console의 첫 번째 빨간 오류 전문
---
 
## 산출물 요약
 
| 경로 | 설명 |
|---|---|
| `Assets/Scenes/Voron24Twin.unity` | 메인 씬 |
| `Assets/Scripts/Robot/JointStateSubscriber.cs` | 조인트 드라이버 (자동 바인딩) |
| `Assets/Scripts/Robot/LocalMockDriver.cs` | ROS 없이 구동 |
| `Assets/Scripts/Robot/NozzleTracker.cs` | 노즐 위치 |
| `Assets/Scripts/Robot/ConnectionMonitor.cs` | 연결 상태 오버레이 |
| `Assets/Scripts/Robot/PrinterStatusSubscriber.cs` | 상태 구독 (msg 생성 후) |
| `Assets/Scripts/Robot/PrinterCommandPublisher.cs` | 명령 퍼블리시 (msg 생성 후) |
| `Assets/Scripts/Viz/ExtrusionRenderer.cs` | 압출 궤적 |
| `Assets/Scripts/Viz/OrbitCamera.cs` | 카메라 |
| `Assets/RosMessages/Voron24/` | 자동 생성 C# 메시지 (커밋 필요) |
| `Assets/Materials/` | 머티리얼 |
| `Assets/Prefabs/Voron24.prefab` | 로봇 프리팹 |
| `ProjectSettings/` | 프로젝트 설정 (커밋 필요) |
 
---
 
## 커밋
 
```bash
git checkout -b feat/unity-joint-driver
git add unity/Voron24Twin/Assets unity/Voron24Twin/ProjectSettings unity/Voron24Twin/Packages
git commit -m "feat(unity): joint_states 구독 + ArticulationBody 드라이버
 
- 계약 §6 /joint_states 구독, prismatic m / revolute deg 변환
- 조인트 이름 기반 자동 바인딩 (재임포트 시 재연결 불요)
- LocalMockDriver 로 ROS 없이 선행 검증
- SmoothDamp 보간, kinematic 모드 스위치"
git push -u origin feat/unity-joint-driver
```
 
> **씬 파일 충돌 주의.** Unity 씬은 머지 난이도가 높음. B 단독으로 씬을 관리하고, 공유가 필요하면 프리팹으로 분리. **UnityYAMLMerge**를 git mergetool로 등록 권장.
 
---
 
## 트러블슈팅
 
| 증상 | 원인 / 대응 |
|---|---|
| ROS 연결 실패 | IP/포트, 방화벽, WSL 포트 포워딩. HUD로 상태 확인 |
| 임포트 후 로봇 침하 | `base_link` ArticulationBody의 **Immovable** 미체크 |
| 로봇 격렬한 진동 | mass/inertia 비현실적, Solver Iterations 부족, Stiffness 과다 |
| 모델이 90° 누움 | 임포트 시 **Axis Type: Y Axis** 미선택 → 재임포트 |
| 모델 1000배 크기 이상 | URDF scale과 임포트 Scale Factor 이중 적용. 한 곳에서만 |
| 조인트 역방향 | URDF `<axis>` 부호. **URDF를 수정할 것.** Unity에서 부호 반전 시 RViz와 불일치 |
| 목표 추종 지연 | xDrive Stiffness / Force Limit 상향 |
| 움직임 끊김 | `/joint_states` rate 확인(C). 또는 `smoothTime` 조정 |
| VHACD 콜리전 실패 | collision STL이 non-manifold. A에게 Evaluate & Repair 요청 |
| `RosMessageTypes.Voron24` 미발견 | Step 6 미수행. `_pending_msgs/README.md` 참조 |
| 커스텀 메시지 컴파일 에러 | `Robotics → Generate ROS Messages` 재실행 |
| 씬 머지 충돌 | Force Text 설정 확인. UnityYAMLMerge 등록 |
| `mass 가 0` 에러 로그 | URDF `<inertial>` 누락. C에게 `contract_check.py` 실행 요청 |
