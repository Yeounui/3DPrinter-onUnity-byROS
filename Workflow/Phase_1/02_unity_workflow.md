# 02 — Unity 담당 워크플로
 
> **담당: B**
> **최종 산출물**: `unity/Voron24Twin/` — `Voron_2.4r2_Assembly.step` 형상을 사용해 G-code 재생에 맞춰 실시간 구동되는 Voron 2.4 디지털 트윈
> **선행**: `Voron_2.4r2_Assembly.step` 원본 보존 — STEP(AP214)의 mm 단위와 어셈블리 구조를 변환 단계에서 유지
> **병렬화 유의**: STEP 파일은 Unity가 직접 읽을 수 없다. 정적 프레임과 X/Y/Z 가동부를 먼저 분리한 중간 FBX를 만들고, ROS가 준비되지 않았으면 `LocalMockDriver`로 구동 검증한다.
 
---
 
## 준비
 
### 버전
 
| 항목 | 버전 | 비고 |
|---|---|---|
| **Unity** | **2022.3 LTS** 또는 **6000.x LTS** | 팀 전원 동일 버전 |
| Render Pipeline | **URP** | 금속 프레임·투명 패널 표현 |
| .NET | Standard 2.1 | Player Settings |
 
### 패키지 설치
 
`Window → Package Manager → + → Add package from git URL`
 
```
https://github.com/Unity-Technologies/ROS-TCP-Connector.git?path=/com.unity.robotics.ros-tcp-connector
```
 
> STEP 변환은 Unity 밖에서 수행한다. FreeCAD 0.21 이상 또는 STEP 어셈블리 계층을 읽을 수 있는 CAD 도구로 STEP을 열고, Blender 또는 CAD 내보내기로 FBX를 생성한다. Unity Asset Store의 STEP 런타임 임포터를 쓰지 않는 한 원본 `.step`을 `Assets/`에 넣어도 모델로 임포트되지 않는다.
 
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
│   └── Voron24Root
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
| ROS IP Address | ROS 머신 IP (동일 PC면 `127.0.0.1`) |
| ROS Port | `10000` |
| Show HUD | ☑ (개발 중) |
 
> ROS를 사용하지 않는 1차 검증에서는 `ROSConnection`을 비활성화하고 Step 4의 로컬 드라이버만 사용한다. WSL2에서 ROS2를 구동하면 `wsl hostname -I`로 현재 IP를 확인한다.
 
---
 
## Step 2 — mock URDF 임포트 (W1~W2)
 
이 프로젝트에서는 mock URDF 대신 `Voron_2.4r2_Assembly.step`에서 만든 경량 FBX를 1차 모델로 사용한다.
 
1. 원본 STEP을 복제한 뒤 CAD 도구에서 연다. 원본은 **AP214**, 형상 길이는 **mm**이므로 문서 단위를 변경하지 않는다.
2. 어셈블리 트리에서 다음 가동 단위를 별도 최상위 그룹으로 정리한다.
```
   Voron24Root
   ├── base_link       (Frame, Bed Components, Panels, Z 모터/아이들러)
   └── z_gantry        (Gantry, A/B Drives, Gantry Extrusions)
       └── y_carriage  (X 빔 및 Y 이동 결합부)
           └── x_carriage (X_Carriage, Toolhead Revo Voron)
               └── nozzle
```
3. 나사, 와셔, 베어링 볼, 벨트 톱니처럼 화면에서 구분되지 않는 부품은 삭제하거나 정적 그룹에 병합한다. 원본 STEP은 수정하지 않는다.
4. 각 그룹을 원점 변환 없이 FBX로 내보낸다. 모든 그룹이 같은 월드 원점을 공유해야 조립 위치가 유지된다.
5. FBX를 `Assets/Models/Voron24/`에 복사하고 아래 설정으로 임포트한다.
 
| 항목 | 값 | 이유 |
|---|---|---|
| **Scale Factor** | **0.001** | STEP의 mm → Unity의 m |
| Convert Units | ☑ | FBX 단위 메타데이터 반영 |
| Bake Axis Conversion | ☑ | CAD Z-up → Unity Y-up 고정 |
| Read/Write Enabled | ☐ | 런타임 메시 수정이 없으면 메모리 절약 |
 
4. **`LocalMockDriver.cs`**가 X/Y/Z 프리즘 이동을 담당한다. STEP에는 운동학 조인트가 없으므로 조인트 축과 제한값은 Unity에서 명시적으로 정의한다.
 
### 임포트 직후 확인
 
- [ ] 정적 상태에서 원본 CAD와 프레임·갠트리·베드·툴헤드의 상대 위치가 일치
- [ ] `Voron24Root`의 Transform이 Position `(0,0,0)`, Rotation `(0,0,0)`, Scale `(1,1,1)`
- [ ] 300 mm 부품이 Unity에서 약 `0.3` unit로 측정됨
- [ ] `x_carriage`, `y_carriage`, `z_gantry`가 각각 한 개의 독립 GameObject
- [ ] 투명 패널과 벨트가 가동부 자식으로 잘못 묶이지 않음
---
 
## Step 3 — 조인트 드라이버 (W2, 핵심)
 
`Assets/Scripts/Robot/JointStateSubscriber.cs`
 
### 설계 요점
 
**가동 그룹을 이름으로 자동 탐색.** FBX를 다시 내보내도 아래 GameObject 이름을 보존하면 Inspector의 개별 링크를 다시 연결할 필요가 없다.
 
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
 
Inspector 설정은 `robotRoot`와 `jointNames = joint_x, joint_y, joint_z`로 제한한다. STEP 어셈블리의 `A/B Drives`와 네 개 Z 구동계는 실제 벨트 운동을 개별 해석하지 않고 최종 축 변위로 표현한다.
 
### 단위 변환
 
Unity와 ROS의 프리즘 변위는 m를 사용한다. UI와 G-code의 mm 값만 `0.001f`를 곱해 변환한다.
 
```csharp
float targetMetres = targetMillimetres * 0.001f;
joint.target = targetMetres;
```
 
STEP 좌표의 mm는 FBX 임포트 Scale Factor `0.001`에서 한 번만 변환한다. 스크립트에서 모델 Transform에 다시 `0.001`을 적용하지 않는다.
 
### 자동 진단
 
다음 상황을 콘솔 에러로 즉시 통지:
 
| 조건 | 메시지 |
|---|---|
| 이름으로 GameObject 미발견 | CAD/FBX 그룹명이 `x_carriage`, `y_carriage`, `z_gantry`인지 확인 |
| ArticulationBody 부재 | Step 3 구성요소 추가 여부 확인 |
| 월드 Scale이 1이 아님 | FBX 임포트 배율과 루트 Transform 중복 적용 확인 |
| 수신 메시지에 조인트명 부재 | 퍼블리셔의 `msg.name` 확인 |
 
### 드라이브 게인 튜닝
 
각 가동 그룹에 `ArticulationBody`를 추가하고 부모부터 Z → Y → X 순으로 연결한다.
 
| 조인트 | Stiffness | Damping | Force Limit |
|---|---|---|---|
| `joint_x` | 100,000 | 3,000 | 200 |
| `joint_y` | 150,000 | 5,000 | 300 |
| `joint_z` | 300,000 | 20,000 | 1,000 |
 
증상별 대응:
- 목표 추종 지연 → Stiffness ↑, Force Limit ↑
- 오버슈트/진동 → Damping ↑
- 메시가 분리됨 → 잘못된 가동 그룹의 자식 관계 수정
### Kinematic 모드
 
정확한 시각 재현이 목적이면 기본값으로 권장한다. CAD 어셈블리에는 질량·관성·조인트 제한이 Unity 물리용으로 정리되어 있지 않으므로 물리 모드는 검증 후 사용한다.
 
```csharp
void ApplyKinematic(float x, float y, float z)
{
    xCarriage.localPosition = xRest + Vector3.right * x;
    yCarriage.localPosition = yRest + Vector3.forward * y;
    zGantry.localPosition   = zRest + Vector3.up * z;
}
```
 
---
 
## Step 4 — ROS 없이 선행 작업 (W1)
 
`Assets/Scripts/Robot/LocalMockDriver.cs`
 
ROS 연결 전에도 Unity 단독으로 가동 범위와 계층을 검증한다.
 
- Home: 세 축 최소 위치
- Sweep: X/Y/Z를 한 축씩 왕복
- Square: XY 평면 사각 경로
- Lissajous: 동시 축 이동과 부모-자식 관계 검증
```csharp
[SerializeField] bool yieldToRos = true;
 
void Update()
{
    if (yieldToRos && subscriber != null && subscriber.IsConnected) return;
    _t += Time.deltaTime;
    Apply(Compute(_t));
}
```
 
ROS 연결 감지 시 자동으로 물러난다. 최초 Sweep은 저속으로 실행하고 X 캐리지 외의 프레임 부품이 함께 움직이지 않는지 확인한다.
 
### 연결 상태 모니터
 
`Assets/Scripts/Robot/ConnectionMonitor.cs` — 화면 좌상단에 LOCAL/CONNECTED 상태, 조인트 값, 노즐 좌표, 현재 스케일을 표시한다.
 
---
 
## Step 5 — 노즐 위치 추적 (W3)
 
`Assets/Scripts/Robot/NozzleTracker.cs`
 
STEP의 `Toolhead Revo Voron` 하단 노즐 팁에 빈 GameObject `nozzle`을 배치하고, 베드 출력면의 좌전방 기준점에 `bed_origin`을 둔다.
 
```csharp
/// 베드 원점 기준 노즐 위치 [m]
public Vector3 NozzleInBed()
    => bedOrigin.InverseTransformPoint(nozzle.position);
 
/// G-code 좌표계 [mm]. UI 표시용
public Vector3 NozzleGcodeMm() => NozzleInBed() * 1000f;
```
 
> `nozzle`은 `x_carriage`의 자식, `bed_origin`은 `base_link`의 자식이어야 한다. 노즐 팁 위치는 CAD 단면 또는 측정 도구로 잡고 메시 바운드 중심을 사용하지 않는다.
 
---
 
## Step 6 — 커스텀 메시지 활성화 (W2)
 
ROS2 커스텀 메시지를 사용하는 스크립트는 C# 클래스가 생성된 뒤 활성화한다. ROS를 사용하지 않으면 이 단계는 건너뛰어도 STEP 기반 로컬 구동에는 영향이 없다.
 
`Assets/Scripts/Robot/_pending_msgs/` 아래 `.cs.txt`로 확장자를 막아둔다.
 
### 활성화 절차
 
1. ROS2 워크스페이스에서 메시지 패키지를 빌드한다.
```bash
   colcon build --packages-select voron24_msgs && source install/setup.bash
   ros2 interface show voron24_msgs/msg/PrinterStatus
```
 
2. `Robotics → Generate ROS Messages...`
   - **ROS message path**: `<repo>/ros2_ws/src/voron24_msgs`
   - `msg/` 아래 각 메시지에 **Build msg** 실행
   - `Assets/RosMessages/Voron24/msg/*.cs` 생성 확인
3. `.cs.txt` → `.cs`로 변경 후 상위 폴더로 이동
```
   _pending_msgs/PrinterStatusSubscriber.cs.txt  ->  ../PrinterStatusSubscriber.cs
   _pending_msgs/PrinterCommandPublisher.cs.txt  ->  ../PrinterCommandPublisher.cs
```
 
4. 생성된 `Assets/RosMessages/`도 커밋한다.
> 메시지 스키마가 바뀌면 C# 메시지를 재생성하고 Play Mode에서 직렬화 오류가 없는지 확인한다.
 
---
 
## Step 7 — 압출 궤적 렌더링 (W3~W5)
 
`/printer/extrusion`을 수신하거나 로컬 드라이버의 노즐 위치를 사용해 출력물을 렌더링한다.
 
### 방식 비교
 
| 방식 | 장점 | 단점 | 용도 |
|---|---|---|---|
| LineRenderer 다중 | 구현 간단 | 세그먼트 증가 시 드로우콜 증가 | 프로토타입 |
| **동적 Mesh 생성** | 성능 양호, 두께 표현 | 구현 복잡 | **본 구현** |
| GPU Instancing | 매우 빠름 | 연결부가 부자연스러움 | 대량 레이어 |
| VFX Graph | 시각 효과 우수 | 실제 형상 아님 | 데모용 |
 
### 동적 Mesh 방식 요점
 
- 렌더러는 `bed_origin`의 자식
- 레이어별 별도 Mesh 사용
- `extruding == false`면 선을 끊어 travel move 표현
- 변경된 레이어만 `LateUpdate`에서 갱신
- CAD로부터 가져온 베드 표면과 압출 시작 높이의 간섭 확인
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
 
좌표는 미터로 전달하고, 폭·높이가 G-code의 mm 값이면 수신 시 한 번만 `0.001`을 곱한다.
 
---
 
## Step 8 — 카메라 & UI (W3)
 
### 카메라 리그
 
- 우클릭 드래그 → 오빗, 휠 → 줌
- 프리셋 뷰: 정면 / 상단 / 노즐 클로즈업 / 도어 오픈
- 투명 패널 표시/숨김 토글
- 노즐 추종 모드 토글
### UI 패널 (`/printer/status` 구독)
 
```
┌─ Printer Status ─────────────┐
│ State    : PRINTING          │
│ Source   : STEP / FBX        │
│ Layer    : 42 / 187          │
│ Progress : ███████░░░  38%   │
│ Nozzle   : 218.3 / 220.0 °C  │
│ Bed      :  59.8 /  60.0 °C  │
│ Scale    : 1 Unity unit = 1m │
│ Position : X125.4 Y87.2 Z8.4 │
└──────────────────────────────┘
[◀◀] [▶/❚❚] [▶▶]  Speed: [1x ▼]
```
 
TextMeshPro를 사용한다. 개발 빌드에서는 가동 그룹의 Bounds와 피벗을 켜고, 배포 빌드에서는 숨긴다.
 
---
 
## Step 9 — Unity → ROS2 퍼블리시 (W4)
 
`PrinterCommandPublisher.cs`. UI 버튼 OnClick에 메서드를 연결한다.
 
```csharp
public void JogXPlus()  => Send("jog", new[] {  jogStepMm, 0f, 0f });
public void Home()      => Send("home");
public void Pause()     => Send("pause");
public void LoadGcode(string path) => Send("load_gcode", null, path);
```
 
> UI의 `jog` 입력은 mm, Unity 내부 축 위치는 m로 유지한다. 명령 전송 값과 시각 모델 변환 값을 혼용하지 않는다.
 
---
 
## Step 10 — 실제 메시 전환 (W4)
 
STEP 기반 고해상도 메시로 전환할 때:
 
1. `Voron_2.4r2_Assembly.step`을 CAD에서 다시 열고 가동 그룹 이름과 공통 원점을 확인
2. 나사산·베어링·벨트 등 불필요한 형상을 억제하고 삼각형 수를 줄임
3. `base_link`, `z_gantry`, `y_carriage`, `x_carriage`를 동일 설정으로 FBX 재출력
4. 기존 FBX를 덮어쓰고 Unity가 `.meta` GUID를 유지한 채 재임포트하는지 확인
5. Prefab Override와 가동부 피벗을 검증한 뒤 적용
> STEP에는 Unity용 조인트와 피벗이 저장되어 있지 않다. 재변환할 때 그룹명과 공통 원점을 바꾸면 스크립트 바인딩 또는 이동 기준이 깨진다.
 
### 메시 임포트 설정
 
| 항목 | 값 |
|---|---|
| Scale Factor | **0.001** — STEP/FBX mm를 Unity m로 변환 |
| Bake Axis Conversion | ☑ |
| Read/Write Enabled | ☐ |
| Generate Colliders | ☐ — 단순 Box Collider 별도 구성 |
| Optimize Mesh | ☑ |
| Normals | Import 우선, 깨지면 Calculate / 60° |
 
### 머티리얼
 
| 부위 | 셰이더 | Metallic | Smoothness | 비고 |
|---|---|---|---|---|
| 알루미늄 익스트루전 | URP/Lit | 0.9 | 0.55 | STEP 색상 참조 |
| 프린트 파츠(ABS) | URP/Lit | 0.0 | 0.35 | Voron 컬러 배색 |
| 패널(투명) | URP/Lit, Transparent | 0.0 | 0.95 | Alpha 0.25 |
| PEI 베드 | URP/Lit | 0.3 | 0.7 | |
| 벨트·케이블 | URP/Lit | 0.0 | 0.25 | 정적 표현 |
| 필라멘트 | URP/Lit | 0.0 | 0.4 | Vertex Color |
 
---
 
## Step 11 — 검증
 
### 축 방향 (`pattern:=sweep`으로 확인)
 
- [ ] `joint_x` 증가 → 툴헤드만 우측으로 이동
- [ ] `joint_y` 증가 → X빔과 툴헤드가 후방으로 함께 이동
- [ ] `joint_z` 증가 → 전체 갠트리가 상승하고 베드는 정지
- [ ] Home → 노즐이 설정한 `bed_origin`의 최소 X/Y 및 안전 Z에 위치
### 기타
 
- [ ] CAD 기준 치수와 Unity `Bounds`가 0.1% 이내로 일치
- [ ] 가동 범위 끝에서 패널·프레임·베드 관통 없음
- [ ] 모든 Renderer가 올바른 가동 그룹 아래에 있어 분리 이동 없음
- [ ] 50Hz `/joint_states`에서 움직임 끊김 없음
- [ ] `nozzle`의 베드 기준 좌표와 G-code 좌표가 일치
- [ ] Profiler에서 목표 프레임 유지, 정적 메시 드로우콜과 삼각형 수 기록
- [ ] Windows/Mac 빌드에서 FBX와 머티리얼 누락 없음
---
 
## 산출물 요약
 
| 경로 | 설명 |
|---|---|
| `Assets/Scenes/Voron24Twin.unity` | 메인 씬 |
| `Assets/Models/Voron24/` | STEP에서 변환한 최적화 FBX |
| `Assets/Scripts/Robot/JointStateSubscriber.cs` | 조인트 드라이버 (자동 바인딩) |
| `Assets/Scripts/Robot/LocalMockDriver.cs` | ROS 없이 구동 |
| `Assets/Scripts/Robot/NozzleTracker.cs` | 노즐 위치 |
| `Assets/Scripts/Robot/ConnectionMonitor.cs` | 연결 상태 오버레이 |
| `Assets/Scripts/Robot/PrinterStatusSubscriber.cs` | 상태 구독 |
| `Assets/Scripts/Robot/PrinterCommandPublisher.cs` | 명령 퍼블리시 |
| `Assets/Scripts/Viz/ExtrusionRenderer.cs` | 압출 궤적 |
| `Assets/Scripts/Viz/OrbitCamera.cs` | 카메라 |
| `Assets/RosMessages/Voron24/` | 자동 생성 C# 메시지 |
| `Assets/Materials/` | 머티리얼 |
| `Assets/Prefabs/Voron24.prefab` | 로봇 프리팹 |
| `ProjectSettings/` | 프로젝트 설정 |
 
---
 
## 커밋
 
```bash
git checkout -b feat/unity-step-voron24
git add unity/Voron24Twin/Assets unity/Voron24Twin/ProjectSettings unity/Voron24Twin/Packages
git commit -m "feat(unity): STEP 기반 Voron 2.4 가동 모델 구성
 
- Voron_2.4r2_Assembly STEP을 Unity용 FBX 가동 그룹으로 변환
- X/Y/Z 이름 기반 자동 바인딩과 mm-to-m 단위 변환
- LocalMockDriver로 ROS 없이 축 방향 및 가동 범위 검증
- 노즐/베드 기준점과 경량 URP 머티리얼 구성"
git push -u origin feat/unity-step-voron24
```
 
> **FBX 재출력 주의.** 파일을 삭제 후 다시 추가하지 말고 같은 경로에 덮어써 `.meta` GUID를 보존한다. 씬은 한 명이 관리하고 공유 요소는 프리팹으로 분리한다.
 
---
 
## 트러블슈팅
 
| 증상 | 원인 / 대응 |
|---|---|
| STEP이 Unity에서 보이지 않음 | Unity 기본 임포터는 STEP 미지원. CAD에서 FBX로 변환 |
| 모델이 1000배 크거나 작음 | STEP mm → Unity m 변환 누락/중복. Scale Factor를 `0.001`로 한 번만 적용 |
| 모델이 90° 누움 | CAD/FBX의 Z-up 변환 누락. Bake Axis Conversion 후 재임포트 |
| 부품이 원점에 흩어짐 | 그룹별 내보내기에서 공통 월드 원점이 보존되지 않음 |
| X 이동 시 툴헤드 외 부품이 움직임 | Renderer가 `x_carriage` 아래 잘못 배치됨. FBX 계층 수정 |
| Y 이동 시 툴헤드가 따라오지 않음 | `x_carriage`가 `y_carriage` 자식이 아님 |
| Z 이동 시 베드가 움직임 | Voron 2.4는 고정 베드 구조. `z_gantry` 그룹을 이동 대상으로 수정 |
| 피벗 기준으로 모델이 튐 | 가동 그룹의 공통 원점 또는 rest position이 재출력 과정에서 변경됨 |
| 투명 패널 정렬이 이상함 | URP Transparent 재질의 Surface Type과 Render Face 확인 |
| 프레임 속도가 낮음 | 나사·베어링·벨트 세부 형상 제거, 정적 메시 결합, LOD 적용 |
| 콜라이더 생성이 매우 느림 | 고해상도 MeshCollider 대신 가동 범위용 Box Collider 사용 |
| ROS 연결 실패 | IP/포트, 방화벽, WSL 포트 포워딩 확인 |
| 조인트 역방향 | Unity 로컬 축과 ROS 축 매핑 확인 후 한 곳에서만 부호 수정 |
| 노즐 좌표 오프셋 | `nozzle` 팁과 `bed_origin` 위치, 부모 계층, mm/m 변환 확인 |
