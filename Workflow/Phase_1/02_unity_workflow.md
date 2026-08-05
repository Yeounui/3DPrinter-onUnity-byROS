# 02 — Unity 담당 워크플로

> **담당자: B**
> **최종 산출물**: `unity/Voron24Twin/` — G-code 재생에 맞춰 실시간 구동되는 Voron 2.4 디지털 트윈
> **선행 조건**: [00_interface_contract.md](00_interface_contract.md) 숙지 (특히 §2 단위, §3 조인트명, §5 토픽)
> **핵심**: **A의 메시를 기다리지 말 것.** C가 W1에 올리는 mock URDF(박스만 있는)로 로직을 전부 완성하고, W4에 메시만 교체.

---

## 준비

### 버전

| 항목 | 버전 | 비고 |
|---|---|---|
| **Unity** | **2022.3 LTS** 또는 **6000.x LTS** | URDF-Importer 호환성 확인된 조합 사용. 팀 전원 동일 버전 |
| Render Pipeline | **URP** | Built-in도 되지만 URP가 조명·투명 패널 표현에 유리 |
| .NET | Standard 2.1 | Player Settings |

### 패키지 설치

`Window → Package Manager → + → Add package from git URL`

```
https://github.com/Unity-Technologies/URDF-Importer.git?path=/com.unity.robotics.urdf-importer#v0.5.2
https://github.com/Unity-Technologies/ROS-TCP-Connector.git?path=/com.unity.robotics.ros-tcp-connector
```

> URDF-Importer가 ROS-TCP-Connector를 의존성으로 끌어오므로 순서는 위와 같이. 설치 후 상단 메뉴에 **Robotics** 가 생깁니다.

### 프로젝트 설정 (Git 협업 필수)

`Edit → Project Settings → Editor`
- **Asset Serialization → Mode: Force Text**
- **Version Control → Mode: Visible Meta Files**

안 하면 씬/프리팹 파일이 바이너리라 머지가 불가능.

`Edit → Project Settings → Physics`
- **Solver Type: Temporal Gauss Seidel** (ArticulationBody 안정성 향상)
- **Default Solver Iterations: 12** / **Velocity Iterations: 4**
- **Fixed Timestep: 0.01** (Time 설정, 100Hz)

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
│   └── ROSConnection            (빈 GameObject, 스크립트 부착)
├── --- ROBOT ---
│   └── (URDF 임포트 결과가 여기 들어감)
├── --- VIZ ---
│   ├── ExtrusionRenderer        (압출 궤적)
│   └── NozzleTrail
├── --- CAMERA ---
│   ├── Main Camera              (Orbit)
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
| ROS IP Address | C의 머신 IP (같은 PC면 `127.0.0.1`) |
| ROS Port | `10000` |
| Show HUD | ☑ (개발 중) |

> **WSL2에서 ROS2를 돌리는 경우**: WSL IP는 재부팅마다 바뀜. `wsl hostname -I`로 확인하거나 Windows에서 포트 포워딩을 설정하세요. C와 미리 합의할 것.

---

## Step 2 — mock URDF 임포트 (W2)

C가 `ros2_ws/src/voron24_description/urdf/voron24_mock.urdf`를 올리면:

1. `voron24_description` 폴더 전체를 `unity/Voron24Twin/Assets/URDF/` 아래로 복사
   > URDF-Importer는 `package://` 경로를 URDF 파일 기준 상대 경로로 해석합니다. 폴더 구조를 그대로 유지해야 메시를 찾을 수 있음.
2. `.urdf` 파일 우클릭 → **Import Robot from Selected URDF file**
3. 임포트 다이얼로그:

| 항목 | 값 | 이유 |
|---|---|---|
| **Select Axis Type** | **Y Axis** | ROS Z-up → Unity Y-up 변환 |
| **Mesh Decomposer** | **VHACD** | collision STL을 convex로 자동 분해. 없으면 Unity Collider |
| Use Gravity | ☐ 끄기 (초기) | 튜닝 전엔 꺼두는 게 안전 |
| Immovable | ☑ (base_link) | 로봇이 바닥으로 꺼지는 것 방지 |

임포트되면 하이어라키에 ArticulationBody 계층이 생성.

### 임포트 직후 확인

- [ ] `base_link`의 ArticulationBody가 **Immovable** 체크됨
- [ ] 각 조인트가 **Prismatic**, Axis가 계약과 일치
- [ ] Inspector에서 각 링크의 **Mass가 0이 아님** (0이면 A/C에게 `<inertial>` 누락 보고)
- [ ] Play를 눌렀을 때 모델이 무너지거나 진동하지 않음

---

## Step 3 — 조인트 드라이버 (W2, 핵심)

`Assets/Scripts/Robot/JointStateSubscriber.cs`

```csharp
using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosJointState = RosMessageTypes.Sensor.JointStateMsg;

/// <summary>
/// /joint_states 를 구독해 ArticulationBody 를 구동한다.
/// 계약: prismatic=m(SI), revolute=rad(SI). Unity ArticulationBody 는
///       prismatic=m, revolute=degree 이므로 revolute 만 변환한다.
/// </summary>
public class JointStateSubscriber : MonoBehaviour
{
    [System.Serializable]
    public class JointMap
    {
        public string rosName;               // "joint_x"
        public ArticulationBody body;
        [HideInInspector] public bool isRevolute;
    }

    [Header("ROS")]
    [SerializeField] string topic = "/joint_states";

    [Header("Joints")]
    [SerializeField] JointMap[] joints;

    [Header("Smoothing")]
    [Tooltip("퍼블리시 주기보다 렌더 주기가 빠를 때 보간. 0이면 끔")]
    [SerializeField] float smoothTime = 0.02f;

    [Header("Debug")]
    [SerializeField] bool logFirstMessage = true;

    readonly Dictionary<string, JointMap> _map = new();
    float[] _target, _current, _vel;
    bool _received;

    void Start()
    {
        _target  = new float[joints.Length];
        _current = new float[joints.Length];
        _vel     = new float[joints.Length];

        for (int i = 0; i < joints.Length; i++)
        {
            var j = joints[i];
            if (j.body == null) { Debug.LogError($"[JointState] body 미할당: {j.rosName}"); continue; }
            j.isRevolute = j.body.jointType == ArticulationJointType.RevoluteJoint;
            _map[j.rosName] = j;
        }

        ROSConnection.GetOrCreateInstance().Subscribe<RosJointState>(topic, OnJointState);
        Debug.Log($"[JointState] subscribed: {topic}");
    }

    void OnJointState(RosJointState msg)
    {
        if (logFirstMessage && !_received)
        {
            Debug.Log($"[JointState] first msg: names=[{string.Join(",", msg.name)}]");
            _received = true;
        }

        for (int i = 0; i < msg.name.Length && i < msg.position.Length; i++)
        {
            if (!_map.TryGetValue(msg.name[i], out var j)) continue;
            int idx = System.Array.IndexOf(joints, j);
            float v = (float)msg.position[i];
            _target[idx] = j.isRevolute ? v * Mathf.Rad2Deg : v;   // rad→deg
        }
    }

    void FixedUpdate()
    {
        for (int i = 0; i < joints.Length; i++)
        {
            var body = joints[i].body;
            if (body == null) continue;

            if (smoothTime > 0f)
                _current[i] = Mathf.SmoothDamp(_current[i], _target[i], ref _vel[i],
                                               smoothTime, Mathf.Infinity, Time.fixedDeltaTime);
            else
                _current[i] = _target[i];

            var drive = body.xDrive;      // prismatic/revolute 모두 xDrive 사용
            drive.target = _current[i];
            body.xDrive = drive;
        }
    }

    public float GetTarget(string rosName)
        => _map.TryGetValue(rosName, out var j)
           ? _target[System.Array.IndexOf(joints, j)] : 0f;
}
```

### 드라이브 게인 튜닝

Inspector에서 각 ArticulationBody의 **xDrive**:

| 조인트 | Stiffness | Damping | Force Limit | 비고 |
|---|---|---|---|---|
| `joint_x` | 100,000 | 3,000 | 200 | 가벼움, 빠름 |
| `joint_y` | 150,000 | 5,000 | 300 | 갠트리 X빔 |
| `joint_z` | 300,000 | 20,000 | 1,000 | 무겁고 느림 |

**증상별 처방**
- 목표를 못 따라감(뒤처짐) → Stiffness ↑, Force Limit ↑
- 오버슈트/진동 → Damping ↑
- 그래도 불안정 → Project Settings의 Solver Iterations ↑, Fixed Timestep ↓

> **물리가 계속 말썽이면 Kinematic 모드로 전환하세요.** 3D 프린터 트윈은 정확한 위치 재현이 목적이지 동역학 시뮬이 아니기에, Stiffness를 극단적으로 높이거나(1e7), 아예 ArticulationBody 대신 Transform을 직접 갱신하는 모드를 스위치로 만들어 두면 데모가 안전.

```csharp
[Header("Mode")]
[SerializeField] bool kinematicMode = false;
// FixedUpdate 안에서:
if (kinematicMode) {
    // 조인트 로컬 축 방향으로 Transform 직접 이동
    var t = joints[i].body.transform;
    t.localPosition = _restPos[i] + _axis[i] * _current[i];
} else { /* xDrive */ }
```

---

## Step 4 — TF 기반 노즐 위치 추적 (W3)

압출 궤적을 그리려면 노즐이 **베드 기준 어디에 있는지** 알아야 함. 계약 §3의 `nozzle`과 `bed_origin` 프레임을 사용.

```csharp
using UnityEngine;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;

public class NozzleTracker : MonoBehaviour
{
    [SerializeField] Transform nozzle;       // URDF의 nozzle 링크
    [SerializeField] Transform bedOrigin;    // URDF의 bed_origin 링크

    /// 베드 원점 기준 노즐 위치 (m, ROS FLU 규약)
    public Vector3 NozzleInBed()
    {
        Vector3 local = bedOrigin.InverseTransformPoint(nozzle.position);
        return local;    // Unity 좌표계. ROS로 보낼 땐 .To<FLU>() 사용
    }
}
```

> **좌표 변환은 직접 계산하지 말 것.** ROS-TCP-Connector의 `ROSGeometry` 확장 사용:
> ```csharp
> using Unity.Robotics.ROSTCPConnector.ROSGeometry;
> var rosPos  = transform.position.To<FLU>();       // Unity → ROS
> var unityPos = msg.position.From<FLU>();          // ROS → Unity
> ```

---

## Step 5 — 압출 궤적 렌더링 (W3~W5)

C가 `/printer/extrusion` (`ExtrusionPoint`)를 이벤트로 전송받아 실제 출력물을 그림.

### 방식 선택

| 방식 | 장점 | 단점 | 추천 |
|---|---|---|---|
| **LineRenderer 다중** | 구현 간단 | 세그먼트 많아지면 드로우콜 폭증 | 프로토타입 |
| **동적 Mesh 생성** | 성능 좋음, 두께/단면 표현 | 구현 복잡 | **본 구현** |
| **GPU Instancing (큐브)** | 매우 빠름 | 세그먼트 연결이 부자연스러움 | 대량 레이어 |
| **VFX Graph** | 예쁨 | 실제 형상 아님 | 데모용 |

### 동적 Mesh 방식 (권장)

```csharp
using System.Collections.Generic;
using UnityEngine;

/// 압출 세그먼트를 사각 단면 튜브 메시로 누적 생성.
/// 레이어별로 별도 Mesh 를 만들어 65k 정점 제한과 컬링 효율을 확보한다.
public class ExtrusionRenderer : MonoBehaviour
{
    [SerializeField] Transform bedOrigin;
    [SerializeField] Material  filamentMaterial;
    [SerializeField] int       maxLayers = 500;

    class LayerMesh
    {
        public GameObject go; public Mesh mesh;
        public List<Vector3> v = new(); public List<int> t = new();
        public List<Vector3> n = new(); public List<Color> c = new();
    }
    readonly Dictionary<uint, LayerMesh> _layers = new();
    Vector3 _last; bool _hasLast;

    public void AddSegment(Vector3 posLocal, float width, float height,
                           bool extruding, uint layer)
    {
        if (!extruding) { _last = posLocal; _hasLast = true; return; }
        if (!_hasLast)  { _last = posLocal; _hasLast = true; return; }

        var lm = GetLayer(layer);
        AppendTube(lm, _last, posLocal, width, height, layer);
        _last = posLocal;
    }

    LayerMesh GetLayer(uint layer)
    {
        if (_layers.TryGetValue(layer, out var lm)) return lm;
        var go = new GameObject($"Layer_{layer:D4}");
        go.transform.SetParent(bedOrigin, false);
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mr.material = filamentMaterial;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lm = new LayerMesh { go = go, mesh = new Mesh() };
        lm.mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mf.mesh = lm.mesh;
        _layers[layer] = lm;
        return lm;
    }

    void AppendTube(LayerMesh lm, Vector3 a, Vector3 b, float w, float h, uint layer)
    {
        Vector3 dir = b - a;
        if (dir.sqrMagnitude < 1e-10f) return;
        dir.Normalize();
        Vector3 up    = Vector3.up;                       // Unity Y-up
        Vector3 right = Vector3.Cross(dir, up).normalized * (w * 0.5f);
        Vector3 top   = up * (h * 0.5f);

        int b0 = lm.v.Count;
        // 8정점 직육면체 (단순 사각 단면)
        Vector3[] corners = {
            a - right - top, a + right - top, a + right + top, a - right + top,
            b - right - top, b + right - top, b + right + top, b - right + top,
        };
        lm.v.AddRange(corners);
        Color col = Color.HSVToRGB((layer * 0.013f) % 1f, 0.55f, 0.95f);
        for (int i = 0; i < 8; i++) { lm.n.Add(up); lm.c.Add(col); }

        int[] quads = { 0,1,5,4, 1,2,6,5, 2,3,7,6, 3,0,4,7, 4,5,6,7, 3,2,1,0 };
        for (int q = 0; q < quads.Length; q += 4)
        {
            int i0=b0+quads[q], i1=b0+quads[q+1], i2=b0+quads[q+2], i3=b0+quads[q+3];
            lm.t.Add(i0); lm.t.Add(i1); lm.t.Add(i2);
            lm.t.Add(i0); lm.t.Add(i2); lm.t.Add(i3);
        }
    }

    void LateUpdate()   // 프레임당 1회만 메시 갱신 (매 세그먼트마다 하면 죽음)
    {
        foreach (var lm in _layers.Values)
        {
            if (lm.v.Count == 0) continue;
            lm.mesh.Clear();
            lm.mesh.SetVertices(lm.v);
            lm.mesh.SetTriangles(lm.t, 0);
            lm.mesh.SetColors(lm.c);
            lm.mesh.RecalculateNormals();
            lm.mesh.RecalculateBounds();
        }
    }

    public void Clear()
    {
        foreach (var lm in _layers.Values) Destroy(lm.go);
        _layers.Clear(); _hasLast = false;
    }
}
```

> **성능 주의**: `LateUpdate`에서 매 프레임 전체 레이어를 다시 빌드하면 느림. 실제로는 **변경된 레이어만 dirty 플래그로 갱신**하고, 완성된 레이어는 `mesh.UploadMeshData(true)`로 고정. 세그먼트가 수십만 개가 되면 레이어별 메시 분리 + 오래된 레이어 컬링이 필수.

---

## Step 6 — 카메라 & UI (W3)

### 카메라 리그

```csharp
public class OrbitCamera : MonoBehaviour
{
    [SerializeField] Transform target;      // 프린터 중심 또는 노즐
    [SerializeField] float distance = 1.2f, minDist = 0.3f, maxDist = 3f;
    [SerializeField] float yaw = 45f, pitch = 25f;
    [SerializeField] float orbitSpeed = 200f, zoomSpeed = 2f;
    [SerializeField] bool followNozzle = false;

    void LateUpdate()
    {
        if (Input.GetMouseButton(1)) {
            yaw   += Input.GetAxis("Mouse X") * orbitSpeed * Time.deltaTime;
            pitch -= Input.GetAxis("Mouse Y") * orbitSpeed * Time.deltaTime;
            pitch  = Mathf.Clamp(pitch, -10f, 85f);
        }
        distance = Mathf.Clamp(distance - Input.mouseScrollDelta.y * zoomSpeed * 0.1f,
                               minDist, maxDist);
        var rot = Quaternion.Euler(pitch, yaw, 0);
        transform.position = target.position + rot * (Vector3.back * distance);
        transform.rotation = rot;
    }
}
```

프리셋 뷰 버튼: 정면 / 상단 / 노즐 클로즈업 / 도어 오픈 뷰

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

TextMeshPro 사용. 온도는 목표 대비 색상 변화(회색→주황→빨강)를 주면 직관적.

---

## Step 7 — Unity → ROS2 퍼블리시 (W4)

```csharp
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosCmd = RosMessageTypes.Voron24.PrinterCommandMsg;

public class PrinterCommandPublisher : MonoBehaviour
{
    [SerializeField] string topic = "/printer/cmd";
    ROSConnection _ros;

    void Start()
    {
        _ros = ROSConnection.GetOrCreateInstance();
        _ros.RegisterPublisher<RosCmd>(topic);
    }

    public void Jog(float dx, float dy, float dz)
        => _ros.Publish(topic, new RosCmd { command = "jog", args = new[]{dx,dy,dz} });

    public void Home()   => _ros.Publish(topic, new RosCmd { command = "home" });
    public void Pause()  => _ros.Publish(topic, new RosCmd { command = "pause" });
    public void Resume() => _ros.Publish(topic, new RosCmd { command = "resume" });
}
```

### 커스텀 메시지 C# 생성

C가 `voron24_msgs`를 커밋하면:

`Robotics → Generate ROS Messages...`
1. **ROS message path**: `<repo>/ros2_ws/src/voron24_msgs`
2. `msg/` 아래 항목들 **Build msg** 클릭
3. `Assets/RosMessages/Voron24/msg/*.cs` 생성 확인
4. **생성된 .cs 파일도 커밋** (C가 msg를 바꾸면 재생성 필요 — 계약 변경 시 알림 필수)

---

## Step 8 — 메시 교체 (W4)

A의 실제 메시가 들어오면:

1. `git pull` → `ros2_ws/src/voron24_description/meshes/` 갱신 확인
2. Unity의 `Assets/URDF/voron24_description/` 로 복사 (또는 심볼릭 링크 / 스크립트 자동화)
3. **실제 URDF를 다시 임포트** → 새 GameObject 생성
4. 기존 mock 로봇에 붙어 있던 스크립트 참조를 새 로봇으로 재연결

> **재연결 수작업을 줄이는 팁**: `JointStateSubscriber`의 조인트 할당을 Inspector 수동 배정 대신 **이름으로 자동 탐색**하게 만들어 두면 재임포트해도 코드가 그대로 동작.
> ```csharp
> void AutoBind(Transform root) {
>     foreach (var j in joints) {
>         var t = FindDeep(root, j.rosName);   // URDF-Importer는 링크명으로 GameObject 생성
>         if (t) j.body = t.GetComponent<ArticulationBody>();
>     }
> }
> ```

### 메시 임포트 설정 (Assets에 들어온 STL 선택 후 Inspector)

| 항목 | 값 |
|---|---|
| Scale Factor | **0.001** (mm → m) — URDF에 scale이 있으면 1.0 |
| Read/Write Enabled | ☐ (메모리 절약) |
| Generate Colliders | ☐ (collision STL 따로 씀) |
| Optimize Mesh | ☑ |
| Normals | Calculate, Smoothing Angle 60 |

### 머티리얼

| 부위 | 셰이더 | Metallic | Smoothness | 비고 |
|---|---|---|---|---|
| 알루미늄 익스트루전 | URP/Lit | 0.9 | 0.55 | |
| 프린트 파츠(ABS) | URP/Lit | 0.0 | 0.35 | Voron 컬러 배색 |
| 패널(투명) | URP/Lit, Surface=Transparent | 0.0 | 0.95 | Alpha 0.25 |
| PEI 베드 | URP/Lit | 0.3 | 0.7 | |
| 필라멘트 | URP/Lit | 0.0 | 0.4 | Vertex Color 사용 |

---

## Step 9 — 검증

### 단위 테스트 (Play 모드)

```csharp
// Assets/Scripts/Debug/JointSanityCheck.cs
// 키보드로 조인트를 직접 밀어보고 방향/범위를 확인
void Update() {
    if (Input.GetKey(KeyCode.Alpha1)) Nudge("joint_x",  0.05f);
    if (Input.GetKey(KeyCode.Alpha2)) Nudge("joint_x", -0.05f);
    // ...
}
```

### 체크리스트

- [ ] `joint_x = 0.25` 넣으면 툴헤드가 **오른쪽 끝**으로 감
- [ ] `joint_y = 0.25` 넣으면 X빔이 **뒤쪽 끝**으로 감
- [ ] `joint_z = 0.25` 넣으면 **갠트리가 위로** 올라감 (베드는 그대로!)
- [ ] 세 조인트 모두 0일 때 노즐이 베드 좌전방 코너에 닿음
- [ ] 스트로크 끝에서 부품끼리 뚫고 나가지 않음
- [ ] 50Hz joint_states에서 움직임이 끊기지 않음
- [ ] RViz와 Unity의 자세가 육안으로 일치
- [ ] Profiler에서 60fps 유지 (압출 궤적 5,000 세그먼트 기준)

---

## 산출물 요약

| 경로 | 설명 |
|---|---|
| `unity/Voron24Twin/Assets/Scenes/Voron24Twin.unity` | 메인 씬 |
| `unity/Voron24Twin/Assets/Scripts/Robot/` | JointStateSubscriber, NozzleTracker, PrinterCommandPublisher |
| `unity/Voron24Twin/Assets/Scripts/Viz/` | ExtrusionRenderer, OrbitCamera |
| `unity/Voron24Twin/Assets/Scripts/UI/` | StatusPanel |
| `unity/Voron24Twin/Assets/RosMessages/Voron24/` | 자동 생성 C# 메시지 (커밋 필요) |
| `unity/Voron24Twin/Assets/Materials/` | 머티리얼 |
| `unity/Voron24Twin/Assets/Prefabs/Voron24.prefab` | 로봇 프리팹 |
| `unity/Voron24Twin/ProjectSettings/` | 프로젝트 설정 (커밋 필요) |

---

## 커밋

```bash
git checkout -b feat/unity-joint-driver
git add unity/Voron24Twin/Assets unity/Voron24Twin/ProjectSettings unity/Voron24Twin/Packages
git commit -m "feat(unity): joint_states 구독 + ArticulationBody 드라이버

- 계약 §5 /joint_states 구독, prismatic m / revolute deg 변환
- SmoothDamp 보간, kinematic 모드 스위치
- mock URDF 로 동작 검증 완료"
git push -u origin feat/unity-joint-driver
```

> **씬 파일 충돌 주의**: Unity 씬은 머지가 어려움. B 혼자 씬을 건드리되, 다른 사람이 볼 일이 있으면 프리팹으로 분리. Unity의 **Smart Merge (UnityYAMLMerge)** 를 git mergetool로 등록해 두면 도움이 될 것.

---

## 트러블슈팅

| 증상 | 원인 / 해결 |
|---|---|
| Unity가 ROS에 연결 안 됨 | IP/포트 확인, 방화벽, WSL이면 포트 포워딩. HUD(`Show HUD`)에서 상태 확인 |
| 임포트 후 로봇이 바닥으로 꺼짐 | `base_link` ArticulationBody의 **Immovable** 미체크 |
| 로봇이 격렬하게 진동 | mass/inertia 비현실적, Solver Iterations 부족, Stiffness 과다 |
| 모델이 90° 누워 있음 | 임포트 시 **Axis Type: Y Axis** 미선택 → 재임포트 |
| 모델이 1000배 큼/작음 | URDF scale vs STL 임포트 Scale Factor 이중 적용. 한 곳에서만 |
| 조인트가 반대로 움직임 | URDF `<axis>` 부호. **URDF를 고칠 것** (Unity에서 부호 뒤집지 말 것 — RViz와 어긋남) |
| 목표를 못 따라가고 뒤처짐 | xDrive Stiffness / Force Limit 상향 |
| 움직임이 뚝뚝 끊김 | `/joint_states` rate 확인(C에게). 또는 smoothTime 조정 |
| VHACD 콜리전 생성 실패 | collision STL이 non-manifold. A에게 Evaluate & Repair 요청 |
| 커스텀 메시지 컴파일 에러 | `Robotics → Generate ROS Messages` 재실행. msg 정의 변경 시 필수 |
| 씬 머지 충돌 | Force Text 설정 확인. UnityYAMLMerge 등록 |
