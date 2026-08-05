# 01 — CAD 담당 워크플로 (FreeCAD)

> **담당자: A**
> **최종 산출물**: `ros2_ws/src/voron24_description/` 아래의 메시 4~6개 + URDF + 좌표 실측표
> **선행 조건**: [00_interface_contract.md](00_interface_contract.md) 숙지 (특히 §2 좌표계, §3 링크 트리, §4 메시 계약)
> **다른 팀원을 막지 않으려면**: W2 종료 시 `cad/measurements.md`(좌표 실측표)를 먼저 커밋. C가 이 숫자로 URDF를 생성 가능. Mesh는 W4에서 추가할 수 있음.

---

## 준비

### 도구

| 도구 | 버전 | 용도 |
|---|---|---|
| **FreeCAD** | **1.0 이상** | 메인. 0.21 이하는 Assembly/Measure 기능이 부족 |
| Git + Git LFS | — | 대용량 파일 |
| (선택) Blender | 4.x | 데시메이션 품질이 FreeCAD보다 좋음 |
| (선택) Meshlab | — | 메시 검사/수리 |

FreeCAD 워크벤치: **Part**, **Mesh**, **Part Design**(안 씀), **Python 콘솔**(주력)

`View → Panels → Python console` 을 켜두세요. GUI 클릭보다 스크립트가 압도적으로 빠름.

### 원본 확보

```bash
git clone https://github.com/VoronDesign/Voron-2.git
# STEPs/ 디렉토리에 사이즈별 전체 어셈블리 STEP 존재
```

`LICENSE` 파일을 `cad/source/LICENSE_VORON`으로 복사하고 커밋.

> Voron 2.4 전체 어셈블리 STEP은 **파츠 수천 개, 수백 MB** 규모. FreeCAD 임포트에 10~30분 소요 예상, RAM 16GB 이상 권장.

---

## Step 1 — 임포트 설정 (먼저 안 하면 처음부터 다시 해야 함)

`Edit → Preferences → Import-Export → STEP`

| 항목 | 설정 |
|---|---|
| **Enable STEP Compound merge** | ☐ **끄기** ← 켜져 있으면 전부 한 덩어리로 들어옴 |
| **Use LinkGroup** | ☑ 켜기 |
| **Export/Import hierarchy** | ☑ 켜기 |
| Read shape colors | ☑ (파츠 구분에 도움) |

스크립트로 강제하려면:
```python
p = FreeCAD.ParamGet("User parameter:BaseApp/Preferences/Mod/Import/hSTEP")
p.SetBool("ReadShapeCompoundMode", False)
p.SetBool("UseLinkGroup", True)
p.SetBool("ExportHiddenObject", False)
```

`File → Open` 으로 STEP 열기 → **즉시 `cad/working/voron24_raw.FCStd`로 저장** (재임포트 시간 절약).

---

## Step 2 — 파츠 인벤토리

Python 콘솔에서 실행. 출력을 `cad/inventory.txt`로 저장해 커밋.

```python
import FreeCAD as App, Part, csv, os
doc = App.ActiveDocument

rows = []
for o in doc.Objects:
    s = getattr(o, "Shape", None)
    if not s or s.isNull() or not s.Solids:
        continue
    bb = s.BoundBox
    rows.append({
        "name": o.Name, "label": o.Label,
        "solids": len(s.Solids), "faces": len(s.Faces),
        "vol_mm3": round(s.Volume, 1),
        "sx": round(bb.XLength,1), "sy": round(bb.YLength,1), "sz": round(bb.ZLength,1),
        "cx": round(bb.Center.x,1), "cy": round(bb.Center.y,1), "cz": round(bb.Center.z,1),
    })

out = "/path/to/repo/cad/inventory.csv"
with open(out, "w", newline="") as f:
    w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
    w.writeheader(); w.writerows(rows)

print(f"파츠 {len(rows)}개 → {out}")
allbb = Part.makeCompound([o.Shape for o in doc.Objects
                           if getattr(o,"Shape",None) and not o.Shape.isNull()]).BoundBox
print("TOTAL BBOX:", allbb)
print(f"  X: {allbb.XMin:.1f} ~ {allbb.XMax:.1f}  ({allbb.XLength:.1f})")
print(f"  Y: {allbb.YMin:.1f} ~ {allbb.YMax:.1f}  ({allbb.YLength:.1f})")
print(f"  Z: {allbb.ZMin:.1f} ~ {allbb.ZMax:.1f}  ({allbb.ZLength:.1f})")
```

**여기서 판단할 것**
- 총 파츠 수 → 수동 그룹핑 가능 규모인지
- 라벨이 의미 있는지 (`Extrusion_MGN12`, `X_Carriage` 등) vs 무의미한지 (`Solid001`)
- 전체 BBOX가 예상(대략 350×350×450mm)과 맞는지
- 현재 원점이 어디인지 → Step 3에서 이동할 양

---

## Step 3 — 좌표계 정렬

계약 §2의 원점 정의로 전체를 옮깁니다: **프레임 하단면 정중앙, Z-up, X=우, Y=후**.

```python
import FreeCAD as App
from FreeCAD import Vector, Rotation, Placement

# 1) 현재 전체 BBOX 확인 후 필요한 변환 계산
#    예: 원점이 프레임 좌하단 뒤쪽 코너에 있고, Z가 위로 이미 맞다면
shift = Vector(-175.0, -175.0, -0.0)     # 실측값으로 교체
rot   = Rotation(Vector(0,0,1), 0)       # 회전 불필요하면 0

fix = Placement(shift, rot)

for o in App.ActiveDocument.Objects:
    if hasattr(o, "Placement") and getattr(o, "Shape", None):
        o.Placement = fix.multiply(o.Placement)
App.ActiveDocument.recompute()
```

**검증 방법** — 원점에 마커 박스를 띄우고 확인:
```python
import Part
Part.show(Part.makeBox(20,20,20, Vector(-10,-10,-10)), "ORIGIN_MARKER")
# 확인 후 삭제: App.ActiveDocument.removeObject("ORIGIN_MARKER")
```

마커가 프레임 바닥 정중앙에 있어야 합니다. 정면에서 X가 오른쪽, Y가 화면 안쪽인지도 확인.

`cad/working/voron24_aligned.FCStd`로 저장.

---

## Step 4 — 링크 그룹핑

Voron 2.4의 4개 링크에 파츠를 배정.

### 배정 가이드

| 링크 | 포함 | 제외 |
|---|---|---|
| **base_link** | 하단/상단 프레임 익스트루전, 수직 익스트루전, 베드 어셈블리 전체(PEI+알루미늄+마운트), 데크 패널, 사이드/후면 패널, 스키트, 전장 베이(PSU, MCU, 라즈베리파이), A/B 스텝모터, Z 스텝모터 4개, Z 벨트 하우징, 아이들러 마운트, Z 드라이브 어셈블리 | 갠트리에 붙는 모든 것 |
| **z_gantry** | 갠트리 Y 익스트루전 2개(좌우), 후면 X 연결 익스트루전, Y 캐리지 4개, XY 조인트 어셈블리 좌우, A/B 벨트 아이들러, Z 벨트 클램프, 갠트리 코너 브라켓 | X빔 본체 |
| **x_beam** | X축 익스트루전, MGN12 레일, X 엔드 브라켓 좌우, X 벨트 아이들러 | 툴헤드 |
| **toolhead** | Stealthburner 하우징, Clockwork 2, 핫엔드(Dragon/Rapido), 팬 2개(파트쿨링+핫엔드), 노즐, X 캐리지 플레이트, LED PCB | 압출 기어(별도) |
| *(선택)* **extruder_gear** | Clockwork2 드라이브 기어 | |
| *(선택)* **door** | 도어 패널 1장 + 힌지 | |

### 방법 A — 3D 뷰 클릭 (라벨이 무의미할 때, 권장)

```python
# ── 사용법 ──
#  1) 3D 뷰에서 한 링크에 속하는 파츠를 Ctrl+클릭 / 박스 드래그로 다중 선택
#  2) grab("z_gantry") 호출
#  3) 링크마다 반복 → dump() 로 결과 출력 → 스크립트에 붙여넣기
#  요령: 스페이스바로 다른 파츠를 숨겨두면 선택이 훨씬 쉽습니다.

import FreeCADGui as Gui, FreeCAD as App, json
GROUPS = {}

def grab(link):
    names = []
    for s in Gui.Selection.getSelectionEx():
        o = s.Object
        if getattr(o, "Shape", None) and not o.Shape.isNull() and o.Shape.Solids:
            names.append(o.Name)          # Label 아닌 Name 사용 (중복 없음)
    GROUPS.setdefault(link, [])
    added = [n for n in names if n not in GROUPS[link]]
    GROUPS[link] += added
    print(f"{link}: +{len(added)} → 누적 {len(GROUPS[link])}")

def ungrab(link):
    for s in Gui.Selection.getSelectionEx():
        if s.Object.Name in GROUPS.get(link, []):
            GROUPS[link].remove(s.Object.Name)
    print(f"{link}: 누적 {len(GROUPS[link])}")

def dump(path="/path/to/repo/cad/groups.json"):
    used = {n for v in GROUPS.values() for n in v}
    left = [o.Name for o in App.ActiveDocument.Objects
            if getattr(o,"Shape",None) and not o.Shape.isNull()
            and o.Shape.Solids and o.Name not in used]
    GROUPS["_unassigned"] = left
    with open(path, "w") as f:
        json.dump(GROUPS, f, indent=2)
    for k, v in GROUPS.items():
        print(f"  {k:16s} {len(v):5d}")
    print(f"\n→ {path}")

def preview(link):
    """해당 그룹만 보이게 해서 시각 검증"""
    for o in App.ActiveDocument.Objects:
        if hasattr(o, "ViewObject"):
            o.ViewObject.Visibility = (o.Name in GROUPS.get(link, []))
```

**미할당 파츠 처리**: 불필요하게 연산 자원 소모하기에 나사·너트·와셔·케이블·인서트는 대부분 `base_link`에 몰아넣거나 뺄 것.

```python
GROUPS["base_link"] += GROUPS["_unassigned"]   # 몰아넣기
```

### 방법 B — Z 높이 기준 자동 초벌 분류 (파츠가 수천 개일 때)

```python
# 갠트리는 항상 특정 Z 이상에 있으므로 1차 자동 분류 후 손으로 수정
Z_GANTRY_MIN = 380.0    # 실측으로 교체
auto = {"base_link": [], "z_gantry": []}
for o in App.ActiveDocument.Objects:
    s = getattr(o, "Shape", None)
    if not s or s.isNull() or not s.Solids: continue
    key = "z_gantry" if s.BoundBox.Center.z > Z_GANTRY_MIN else "base_link"
    auto[key].append(o.Name)
print({k: len(v) for k, v in auto.items()})
```

이후 `z_gantry`에서 `x_beam`, `toolhead`를 나눌 것.

`cad/groups.json`을 커밋하세요.

---

## Step 5 — 조인트 좌표 실측 ★ 다른 팀원이 기다리는 산출물

계약 §3의 각 조인트에 대해 `<origin xyz>`와 스트로크를 실측해야 함.

### 측정 도구

```python
import FreeCADGui as Gui
def probe():
    """면/엣지/정점을 선택하고 호출. 원기둥이면 축까지 출력"""
    for s in Gui.Selection.getSelectionEx():
        for i, sub in enumerate(s.SubObjects):
            c = sub.BoundBox.Center
            line = f"[{s.ObjectName}.{s.SubElementNames[i]}] center=({c.x:.3f}, {c.y:.3f}, {c.z:.3f})"
            surf = getattr(sub, "Surface", None)
            if surf is not None and hasattr(surf, "Axis"):
                a, o = surf.Axis, surf.Center
                line += f"\n    axis=({a.x:.4f},{a.y:.4f},{a.z:.4f})  axis_pt=({o.x:.3f},{o.y:.3f},{o.z:.3f})"
                if hasattr(surf, "Radius"):
                    line += f"  R={surf.Radius:.3f}"
            print(line)

def group_bbox(link):
    """그룹 전체 BBOX — 스트로크 추정용"""
    import Part
    shp = Part.makeCompound([App.ActiveDocument.getObject(n).Shape
                             for n in GROUPS[link]])
    bb = shp.BoundBox
    print(f"{link}: X {bb.XMin:.1f}~{bb.XMax:.1f}  Y {bb.YMin:.1f}~{bb.YMax:.1f}  Z {bb.ZMin:.1f}~{bb.ZMax:.1f}")
    print(f"  center=({bb.Center.x:.2f}, {bb.Center.y:.2f}, {bb.Center.z:.2f})")
    return bb
```

### 측정 항목

| 조인트 | 측정 대상 | 얻을 값 |
|---|---|---|
| `joint_z` | 갠트리가 **최하단(Z=0, 노즐이 베드에 닿음)** 일 때의 기준점 | origin xyz. X/Y는 0, Z는 갠트리 기준면 높이 |
| `joint_y` | Y 레일 중심선의 Z 높이, X 중앙 / **X빔이 최전방(Y=0)** 일 때의 Y | origin xyz |
| `joint_x` | X 레일(MGN12) 중심선 / **툴헤드가 좌측 끝(X=0)** 일 때의 X | origin xyz |
| `nozzle` | 노즐 팁 좌표 (toolhead 로컬) | fixed joint origin |
| `bed_origin` | 베드 상면의 프린트 원점 코너 (보통 좌전방) | fixed joint origin, base_link 기준 |

> **STEP은 특정 자세로 고정.** 갠트리가 중간 높이에 있는 상태로 모델링돼 있다면, 그 상태를 "joint_z = 현재값"으로 보고 역산하거나, Placement로 갠트리 그룹을 Z=0 위치로 옮긴 뒤 측정. **후자가 훨씬 안전.**
>
> ```python
> # 갠트리 그룹을 홈 위치로 이동 (측정 전에 수행)
> dz = -123.4   # 현재 갠트리 Z − 목표 Z
> for n in GROUPS["z_gantry"] + GROUPS["x_beam"] + GROUPS["toolhead"]:
>     o = App.ActiveDocument.getObject(n)
>     o.Placement.Base.z += dz
> App.ActiveDocument.recompute()
> ```

### 산출물: `cad/measurements.md`

```markdown
# Voron 2.4 250mm — 조인트 실측 (단위 mm, base_link 기준)

측정일: YYYY-MM-DD / 측정자: A / 원본: Voron-2 repo commit abc1234

## 전체
- 프레임 외형: 350.0 × 350.0 × 465.0
- base_link 원점: 프레임 하단면 정중앙

## joint_z  (base_link → z_gantry, prismatic, axis 0 0 1)
- origin xyz = (0, 0, 214.5)
- 근거: 갠트리 Y익스트루전 하면이 노즐-베드 접촉 시 Z=214.5
- lower = 0, upper = 250.0
- 측정 방법: Y익스트루전 하면 클릭 → probe() → z=214.5

## joint_y  (z_gantry → x_beam, prismatic, axis 0 1 0)
- origin xyz = (0, -125.0, 12.0)
- lower = 0, upper = 250.0

## joint_x  (x_beam → toolhead, prismatic, axis 1 0 0)
- origin xyz = (-125.0, 0, 0)
- lower = 0, upper = 250.0

## nozzle  (toolhead → nozzle, fixed)
- origin xyz = (0, 8.5, -52.0)

## bed_origin  (base_link → bed_origin, fixed)
- origin xyz = (-125.0, -125.0, 60.0)
- 근거: PEI 시트 상면, 좌전방 프린트 원점 코너

## 미해결 / 가정
- Z 스트로크는 스펙(250)을 따름. STEP에서 리드 범위 확인 불가
```

**이 파일을 커밋하고 팀에 알리세요.** C가 이 값을 기반으로 실제 URDF를 작성 가능.

---

## Step 6 — 시각용 메시 추출

계약 §4의 규칙(원점 정렬, mm 단위, 삼각형 예산)을 지킵니다.

`cad/scripts/export_visual.py`:

```python
# FreeCAD Python 콘솔에서:  exec(open("/path/cad/scripts/export_visual.py").read())
import FreeCAD as App, Mesh, MeshPart, Part, json, os
from FreeCAD import Vector

REPO = "/path/to/repo"
OUT  = os.path.join(REPO, "ros2_ws/src/voron24_description/meshes/visual")
os.makedirs(OUT, exist_ok=True)
doc = App.ActiveDocument
GROUPS = json.load(open(os.path.join(REPO, "cad/groups.json")))

# measurements.md 의 origin 값 (mm, base_link 기준 누적 월드좌표)
ORIGIN = {
    "base_link":  Vector(0,      0,      0),
    "z_gantry":   Vector(0,      0,      214.5),
    "x_beam":     Vector(0,     -125.0,  226.5),   # z_gantry origin + joint_y origin
    "toolhead":   Vector(-125.0, -125.0, 226.5),   # + joint_x origin
}

# 링크별 메시 품질 (LinearDeflection mm, AngularDeflection rad)
QUALITY = {
    "base_link": (0.30, 0.60),    # 큼 → 거칠게
    "z_gantry":  (0.20, 0.50),
    "x_beam":    (0.20, 0.50),
    "toolhead":  (0.08, 0.35),    # 가까이서 보임 → 곱게
}
BUDGET = {"base_link":120000, "z_gantry":60000, "x_beam":30000, "toolhead":40000}

for link, origin in ORIGIN.items():
    names = GROUPS.get(link, [])
    if not names:
        print(f"[skip] {link}: 파츠 없음"); continue
    lin, ang = QUALITY[link]
    m = Mesh.Mesh()
    missing = 0
    for n in names:
        o = doc.getObject(n)
        if o is None or not getattr(o, "Shape", None) or o.Shape.isNull():
            missing += 1; continue
        m.addMesh(MeshPart.meshFromShape(Shape=o.Shape, LinearDeflection=lin,
                                         AngularDeflection=ang, Relative=False))
    m.translate(-origin.x, -origin.y, -origin.z)        # ★ 원점 재정렬
    m.harmonizeNormals()
    m.removeDuplicatedPoints()

    path = os.path.join(OUT, f"{link}.stl")
    m.write(path)
    mb = os.path.getsize(path)/1e6
    flag = "OK " if m.CountFacets <= BUDGET[link] else "OVER"
    print(f"[{flag}] {link:12s} tri={m.CountFacets:7d} / {BUDGET[link]:6d}  "
          f"{mb:5.1f}MB  missing={missing}")
```

### 예산 초과 시

1. **`LinearDeflection` / `AngularDeflection` 키우기** (0.3 → 0.6)
2. **안 보이는 파츠 제거** — 나사, 인서트, 케이블, 케이블체인, 전장 베이 내부, 데크 하부. `GROUPS`에서 빼면 됩니다
3. **FreeCAD Mesh 워크벤치 → Decimation** (Meshes → Decimation, 목표 삼각형 수 지정)
4. **Blender 데시메이션** (품질이 가장 좋음)
   ```python
   # Blender Scripting 탭
   import bpy
   bpy.ops.import_mesh.stl(filepath="/path/base_link.stl")
   ob = bpy.context.active_object
   mod = ob.modifiers.new("dec", 'DECIMATE')
   mod.ratio = 0.35
   bpy.ops.object.modifier_apply(modifier="dec")
   bpy.ops.export_mesh.stl(filepath="/path/base_link.stl", use_selection=True,
                           global_scale=1.0, ascii=False)
   ```
   > Blender STL 임포트/익스포트 시 **스케일 1.0, Z-up** 유지 필수. 기본 설정이 바뀌면 mm이 m가 됩니다.

---

## Step 7 — 콜리전 메시 추출

`cad/scripts/export_collision.py`:

```python
import FreeCAD as App, Mesh, MeshPart, Part, json, os
from FreeCAD import Vector
# ... REPO, GROUPS, ORIGIN 은 위와 동일 ...
OUT = os.path.join(REPO, "ros2_ws/src/voron24_description/meshes/collision")
os.makedirs(OUT, exist_ok=True)

MODE = {                      # "box" | "hull"
    "base_link": "box",
    "z_gantry":  "hull",
    "x_beam":    "box",
    "toolhead":  "hull",
}

for link, origin in ORIGIN.items():
    shapes = [doc.getObject(n).Shape for n in GROUPS.get(link, [])
              if doc.getObject(n) and getattr(doc.getObject(n),"Shape",None)]
    if not shapes: continue
    comp = Part.makeCompound(shapes)

    if MODE[link] == "box":
        bb = comp.BoundBox
        shp = Part.makeBox(bb.XLength, bb.YLength, bb.ZLength,
                           Vector(bb.XMin, bb.YMin, bb.ZMin))
    else:
        shp = comp.makeConvexHull()          # FreeCAD 1.0+
    m = Mesh.Mesh(MeshPart.meshFromShape(Shape=shp, LinearDeflection=1.0,
                                         AngularDeflection=1.0, Relative=False))
    m.translate(-origin.x, -origin.y, -origin.z)
    m.write(os.path.join(OUT, f"{link}.stl"))
    print(f"{link:12s} tri={m.CountFacets:6d}  mode={MODE[link]}")
```

> `base_link`를 통짜 박스로 만들면 갠트리와 항상 충돌하기에, 물리 시뮬을 켤 계획이면 base_link collision은 **프레임 기둥 4개 + 베드 판**만 따로 박스로 만들어 여러 `<collision>` 태그로 넣거나, 아예 충돌 검사를 끌 것(`<disable_collisions>` 또는 Unity 레이어 분리). 3D 프린터는 자기충돌 검사가 사실상 불 필요.

---

## Step 8 — 질량·관성 산출

```python
import Part, json
DENSITY = {      # kg/m^3
    "base_link": 2000,    # 알루미늄+플라스틱+전장 혼합. 실측 무게로 보정 권장
    "z_gantry":  2400,
    "x_beam":    2600,
    "toolhead":  1500,
}
REAL_MASS = {    # 실측값이 있으면 우선 사용 (kg)
    "base_link": 12.0, "z_gantry": 2.2, "x_beam": 0.9, "toolhead": 0.45,
}

for link, origin in ORIGIN.items():
    shapes = [doc.getObject(n).Shape for n in GROUPS.get(link, []) if doc.getObject(n)]
    comp = Part.makeCompound(shapes)
    vol_m3 = comp.Volume * 1e-9
    mass = REAL_MASS.get(link) or vol_m3 * DENSITY[link]
    com  = (comp.CenterOfMass - origin) * 0.001         # m, 링크 로컬
    bb   = comp.BoundBox
    # 박스 근사 관성 (충분히 안정적)
    lx, ly, lz = bb.XLength*1e-3, bb.YLength*1e-3, bb.ZLength*1e-3
    ixx = mass*(ly*ly+lz*lz)/12; iyy = mass*(lx*lx+lz*lz)/12; izz = mass*(lx*lx+ly*ly)/12
    print(f"""<!-- {link} -->
    <inertial>
      <origin xyz="{com.x:.5f} {com.y:.5f} {com.z:.5f}"/>
      <mass value="{mass:.4f}"/>
      <inertia ixx="{ixx:.6f}" iyy="{iyy:.6f}" izz="{izz:.6f}" ixy="0" ixz="0" iyz="0"/>
    </inertial>""")
```

출력을 `cad/inertial_snippets.xml`로 저장 → C에게 전달.

> **`<inertial>` 누락은 치명적.** URDF-Importer가 mass=0인 ArticulationBody를 만들어 Unity에서 모델이 폭발.

---

## Step 9 — 검증 (커밋 전 필수)

### 9-1. 재조립 테스트

```python
import Mesh, FreeCAD as App, os
d2 = App.newDocument("verify")
for link, origin in ORIGIN.items():
    m = Mesh.Mesh(os.path.join(OUT_VISUAL, f"{link}.stl"))
    m.translate(origin.x, origin.y, origin.z)      # 모든 조인트=0 인 자세
    Mesh.show(m, link)
d2.recompute()
Gui.SendMsgToActiveView("ViewFit")
```

**원본 프린터 형상과 똑같이 보이면 성공.** 어긋나면 `ORIGIN` 값이 틀린 것.

### 9-2. 개별 메시 원점 확인

각 STL을 새 문서에 그냥 임포트했을 때, 링크가 **원점 근처**에 있어야함. 멀리 떨어져 있으면 Step 6의 `translate`가 잘못 된 것.

### 9-3. 메시 무결성

Mesh 워크벤치 → **Meshes → Analyze → Evaluate & Repair**
- Self-intersections, Non-manifold, Degenerated faces 확인
- 문제 있으면 Repair 실행. Unity VHACD 콜리전 생성 실패의 주원인

### 9-4. 체크리스트

- [ ] 파일명이 계약 §4와 정확히 일치 (`base_link.stl` 등)
- [ ] visual/ 과 collision/ 양쪽 다 존재
- [ ] 총 삼각형 250,000 이하
- [ ] 각 STL 임포트 시 원점 근처
- [ ] 재조립 테스트 통과
- [ ] `measurements.md` 최신
- [ ] `inertial_snippets.xml` 최신
- [ ] Git LFS로 추적되고 있는지: `git lfs ls-files | grep stl`

---

## Step 10 — 커밋 & PR

```bash
cd /path/to/repo
git checkout -b feat/cad-meshes-v1

git lfs track "*.stl" "*.step" "*.FCStd"
git add .gitattributes

git add cad/groups.json cad/measurements.md cad/inventory.csv \
        cad/inertial_snippets.xml cad/scripts/
git add ros2_ws/src/voron24_description/meshes/

git commit -m "feat(cad): Voron 2.4 250mm 링크 메시 v1 + 조인트 실측값

- 링크 4개 (base_link, z_gantry, x_beam, toolhead)
- visual 총 238k tri, collision box/hull
- 조인트 origin 실측 → cad/measurements.md
- 재조립 검증 통과"

git push -u origin feat/cad-meshes-v1
```

**PR 본문에 반드시 포함**:
- 링크별 삼각형 수 표
- `measurements.md`의 origin 값 요약
- 재조립 검증 스크린샷
- 알려진 문제 / 가정

---

## 산출물 요약

| 경로 | 확장자 | 설명 | 소비자 |
|---|---|---|---|
| `cad/working/*.FCStd` | .FCStd | FreeCAD 작업 파일 (LFS) | A 본인 |
| `cad/source/*.step` | .step | 원본 (LFS) | A |
| `cad/scripts/*.py` | .py | 추출 매크로 | A, 재현성 |
| `cad/groups.json` | .json | 링크별 파츠 배정 | A |
| `cad/inventory.csv` | .csv | 파츠 인벤토리 | A |
| **`cad/measurements.md`** | .md | **조인트 좌표 실측** | **C (URDF 작성)** |
| **`cad/inertial_snippets.xml`** | .xml | **질량/관성** | **C** |
| **`.../meshes/visual/*.stl`** | .stl | **시각 메시 (mm)** | **B, C** |
| **`.../meshes/collision/*.stl`** | .stl | **콜리전 메시** | **B, C** |

---

## 이후 작업 (W5+)

- **도어 링크** — 자석 도어 4면 공용 메시 1개 + 힌지 좌표 4세트
- **LOD** — Unity용 저폴리 버전 (visual의 30%) 별도 추출
- **머티리얼 분리** — 알루미늄/플라스틱/PEI/투명패널을 별도 STL로 쪼개면 B가 재질을 다르게 줄 수 있음. 파일명은 `base_link_frame.stl`, `base_link_panel.stl` 식으로. **B와 먼저 상의**
- **압출 기어 / 팬 블레이드** — 회전 애니메이션용 별도 링크

---

## 트러블슈팅

| 증상 | 원인 / 해결 |
|---|---|
| STEP 임포트가 안 끝남 | 파츠 수천 개. 30분 기다리거나 사이즈 작은 STEP 사용. RAM 확인 |
| 모든 파츠가 하나로 합쳐짐 | Compound merge 설정. 끄고 재임포트 (Step 1) |
| 재조립했더니 링크가 어긋남 | `ORIGIN` 값 오류. `measurements.md`와 대조 |
| STL이 100MB 넘음 | Deflection 값 상향 + 불필요 파츠 제거 |
| `makeConvexHull` 없음 | FreeCAD 0.21 이하. 1.0으로 업그레이드하거나 box 모드 사용 |
| `Shape.Solids`가 비어 있음 | 서피스 모델. `Part → Convert to solid` 필요하거나 무시 |
| Blender 거쳐온 STL이 1000배 큼 | 임포트/익스포트 스케일 설정. `global_scale=1.0` 명시 |
