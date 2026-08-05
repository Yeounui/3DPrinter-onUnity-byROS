# 01 — CAD 담당 워크플로 (FreeCAD)
 
> **담당: A**
> **최종 산출물**: 링크 메시 4~8개 + `voron24_params.xacro` 실측값 + `cad/measurements.md`
> **선행**: [00_interface_contract.md](00_interface_contract.md) 숙지 — 특히 §2 좌표계, §3 링크 트리, §4 소유권, §5 메시 계약
> **병렬화 유의**: W2 종료 시 `cad/measurements.md`를 먼저 커밋할 것. 메시가 없어도 C가 그 숫자로 파라미터를 채울 수 있음. 메시는 W4에 와도 무방.
 
> **URDF는 편집하지 않음.** A의 편집 대상은 `voron24_params.xacro` 하나 (계약 §4). 이 파일의 값만 바꾸면 URDF·RViz·Unity가 전부 자동 반영.
 
---
 
## 준비
 
### 도구
 
| 도구 | 버전 | 용도 |
|---|---|---|
| **FreeCAD** | **1.0 이상** | 메인. 0.21 이하는 Assembly/Measure 기능 부족 |
| Git + Git LFS | — | 대용량 파일 |
| Blender | 4.x | [선택] 데시메이션 품질이 FreeCAD보다 우수 |
| Meshlab | — | [선택] 메시 검사/수리 |
 
사용 워크벤치: **Part**, **Mesh**, **Python 콘솔**(주력)
 
`View → Panels → Python console` 상시 표시 권장. GUI 클릭 대비 압도적으로 빠름.
 
### 원본 확보
 
```bash
git clone https://github.com/VoronDesign/Voron-2.git
# STEPs/ 디렉토리에 사이즈별 전체 어셈블리 STEP
```
 
`LICENSE`를 `cad/source/LICENSE_VORON`으로 복사 후 커밋 (계약 §12).
 
> Voron 2.4 전체 어셈블리 STEP은 **파츠 수천 개, 수백 MB** 규모. 임포트에 10~30분 소요, RAM 16GB 이상 권장. 임포트 전 다른 프로그램 종료.
 
---
 
## Step 1 — 임포트 설정
 
**선행 필수.** 미설정 시 처음부터 재작업.
 
`Edit → Preferences → Import-Export → STEP`
 
| 항목 | 설정 |
|---|---|
| **Enable STEP Compound merge** | ☐ **끄기** — 켜져 있으면 전부 한 덩어리 |
| **Use LinkGroup** | ☑ 켜기 |
| **Export/Import hierarchy** | ☑ 켜기 |
| Read shape colors | ☑ — 파츠 구분에 유용 |
 
스크립트 강제:
 
```python
p = FreeCAD.ParamGet("User parameter:BaseApp/Preferences/Mod/Import/hSTEP")
p.SetBool("ReadShapeCompoundMode", False)
p.SetBool("UseLinkGroup", True)
p.SetBool("ExportHiddenObject", False)
```
 
`File → Open`으로 STEP 열기 → **즉시 `cad/working/voron24_raw.FCStd`로 저장** (재임포트 시간 절약).
 
---
 
## Step 2 — 파츠 인벤토리
 
출력을 `cad/inventory.csv`로 저장 후 커밋.
 
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
 
print(f"파츠 {len(rows)}개 -> {out}")
allbb = Part.makeCompound([o.Shape for o in doc.Objects
                           if getattr(o,"Shape",None) and not o.Shape.isNull()]).BoundBox
print("TOTAL BBOX:", allbb)
print(f"  X: {allbb.XMin:.1f} ~ {allbb.XMax:.1f}  ({allbb.XLength:.1f})")
print(f"  Y: {allbb.YMin:.1f} ~ {allbb.YMax:.1f}  ({allbb.YLength:.1f})")
print(f"  Z: {allbb.ZMin:.1f} ~ {allbb.ZMax:.1f}  ({allbb.ZLength:.1f})")
```
 
### 판단 항목
 
- 총 파츠 수 → 수동 그룹핑 가능 규모 여부
- 라벨의 의미 유무 (`X_Carriage` vs `Solid001`)
- 전체 BBOX가 예상치(약 350×350×465mm)와 일치하는지
- 현재 원점 위치 → Step 3의 이동량
---
 
## Step 3 — 좌표계 정렬
 
계약 §2의 원점 정의로 전체 이동: **프레임 하단면 정중앙, Z-up, X=우, Y=후**.
 
```python
import FreeCAD as App
from FreeCAD import Vector, Rotation, Placement
 
shift = Vector(-175.0, -175.0, 0.0)      # 실측값으로 교체
rot   = Rotation(Vector(0,0,1), 0)       # 회전 불요 시 0
 
fix = Placement(shift, rot)
 
for o in App.ActiveDocument.Objects:
    if hasattr(o, "Placement") and getattr(o, "Shape", None):
        o.Placement = fix.multiply(o.Placement)
App.ActiveDocument.recompute()
```
 
### 검증
 
원점에 마커 박스 생성 후 육안 확인:
 
```python
import Part
Part.show(Part.makeBox(20,20,20, Vector(-10,-10,-10)), "ORIGIN_MARKER")
# 확인 후: App.ActiveDocument.removeObject("ORIGIN_MARKER")
```
 
마커가 프레임 바닥 정중앙에 위치할 것. 정면 뷰에서 X가 우측, Y가 화면 안쪽인지도 확인.
 
`cad/working/voron24_aligned.FCStd`로 저장.
 
---
 
## Step 4 — 링크 그룹핑
 
계약 §3의 4개 링크에 파츠 배정.
 
### 배정 기준
 
| 링크 | 포함 | 제외 |
|---|---|---|
| **base_link** | 하단/상단/수직 프레임 익스트루전, 베드 어셈블리 전체(PEI+알루미늄+마운트), 데크 패널, 사이드/후면 패널, 스키트, 전장 베이(PSU, MCU, RPi), A/B 스텝모터, Z 스텝모터 4개, Z 벨트 하우징, 아이들러 마운트 | 갠트리 부착물 전부 |
| **z_gantry** | 갠트리 Y 익스트루전 2개, 후면 X 연결 익스트루전, Y 캐리지 4개, XY 조인트 어셈블리 좌우, A/B 벨트 아이들러, Z 벨트 클램프, 갠트리 코너 브라켓 | X빔 본체 |
| **x_beam** | X축 익스트루전, MGN12 레일, X 엔드 브라켓 좌우, X 벨트 아이들러 | 툴헤드 |
| **toolhead** | Stealthburner 하우징, Clockwork 2, 핫엔드, 팬 2개, 노즐, X 캐리지 플레이트, LED PCB | 압출 기어 |
| **extruder_gear** [선택] | Clockwork2 드라이브 기어 | |
| **door** [선택] | 도어 패널 1장 + 힌지 (4면 공용) | |
 
### 방법 A — 3D 뷰 클릭 (라벨 무의미 시, 권장)
 
```python
# 사용 절차
#  1) 3D 뷰에서 한 링크에 속하는 파츠를 Ctrl+클릭 / 박스 드래그로 다중 선택
#  2) grab("z_gantry") 호출
#  3) 링크마다 반복 -> dump() 로 결과 출력
#  요령: 스페이스바로 무관한 파츠를 숨기면 선택이 훨씬 수월
 
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
    print(f"{link}: +{len(added)} -> 누적 {len(GROUPS[link])}")
 
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
    print(f"\n-> {path}")
 
def preview(link):
    """해당 그룹만 표시해 시각 검증"""
    for o in App.ActiveDocument.Objects:
        if hasattr(o, "ViewObject"):
            o.ViewObject.Visibility = (o.Name in GROUPS.get(link, []))
```
 
**미할당 파츠 처리**: 나사·너트·와셔·케이블·인서트는 `base_link`에 몰아넣거나 폐기. 시각적 기여 대비 폴리곤 소모가 큼.
 
```python
GROUPS["base_link"] += GROUPS["_unassigned"]
```
 
### 방법 B — Z 높이 기준 자동 초벌 분류 (파츠 수천 개일 때)
 
```python
Z_GANTRY_MIN = 380.0    # 실측으로 교체
auto = {"base_link": [], "z_gantry": []}
for o in App.ActiveDocument.Objects:
    s = getattr(o, "Shape", None)
    if not s or s.isNull() or not s.Solids: continue
    key = "z_gantry" if s.BoundBox.Center.z > Z_GANTRY_MIN else "base_link"
    auto[key].append(o.Name)
print({k: len(v) for k, v in auto.items()})
```
 
이후 `z_gantry`에서 `x_beam`, `toolhead`를 수동 분리.
 
`cad/groups.json` 커밋.
 
---
 
## Step 5 — 조인트 좌표 실측
 
**다른 팀원이 대기 중인 산출물.** 계약 §3의 각 조인트에 대해 `<origin xyz>`와 스트로크 측정.
 
### 측정 도구
 
```python
import FreeCADGui as Gui
def probe():
    """면/엣지/정점 선택 후 호출. 원기둥이면 축까지 출력"""
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
    shp = Part.makeCompound([App.ActiveDocument.getObject(n).Shape for n in GROUPS[link]])
    bb = shp.BoundBox
    print(f"{link}: X {bb.XMin:.1f}~{bb.XMax:.1f}  Y {bb.YMin:.1f}~{bb.YMax:.1f}  Z {bb.ZMin:.1f}~{bb.ZMax:.1f}")
    print(f"  center=({bb.Center.x:.2f}, {bb.Center.y:.2f}, {bb.Center.z:.2f})")
    return bb
```
 
### 측정 항목
 
| 조인트 | 측정 대상 | 얻을 값 | params 변수 |
|---|---|---|---|
| `joint_z` | 갠트리가 **최하단(Z=0, 노즐이 베드 접촉)** 일 때 기준점 | X/Y는 0, Z는 갠트리 기준면 높이 | `jz_x/y/z` |
| `joint_y` | Y 레일 중심선의 Z 높이, X 중앙 / **X빔이 최전방(Y=0)** 일 때 Y | origin xyz | `jy_x/y/z` |
| `joint_x` | MGN12 레일 중심선 / **툴헤드가 좌측 끝(X=0)** 일 때 X | origin xyz | `jx_x/y/z` |
| `nozzle` | 노즐 팁 좌표 (toolhead 로컬) | fixed origin | `noz_x/y/z` |
| `bed_origin` | 베드 상면의 프린트 원점 코너 (좌전방) | base_link 기준 | `bed_x/y/z` |
 
> **STEP은 특정 자세로 고정된 상태.** 갠트리가 중간 높이에 모델링돼 있다면, 갠트리 그룹을 Z=0 위치로 이동시킨 뒤 측정할 것 (역산보다 안전).
>
> ```python
> dz = -123.4   # 현재 갠트리 Z - 목표 Z
> for n in GROUPS["z_gantry"] + GROUPS["x_beam"] + GROUPS["toolhead"]:
>     App.ActiveDocument.getObject(n).Placement.Base.z += dz
> App.ActiveDocument.recompute()
> ```
 
### 산출물 1: `cad/measurements.md`
 
```markdown
# Voron 2.4 250mm — 조인트 실측 (단위 mm, base_link 기준)
 
측정일: YYYY-MM-DD / 측정자: A / 원본: Voron-2 repo commit abc1234
 
## 전체
- 프레임 외형: 350.0 × 350.0 × 465.0
- base_link 원점: 프레임 하단면 정중앙
 
## joint_z  (base_link -> z_gantry, prismatic, axis 0 0 1)
- origin xyz = (0, 0, 214.5)
- 근거: 갠트리 Y익스트루전 하면이 노즐-베드 접촉 시 Z=214.5
- lower = 0, upper = 250.0
- 방법: Y익스트루전 하면 클릭 -> probe() -> z=214.5
 
## joint_y  (z_gantry -> x_beam, prismatic, axis 0 1 0)
- origin xyz = (0, -125.0, 12.0)
- lower = 0, upper = 250.0
 
## joint_x  (x_beam -> toolhead, prismatic, axis 1 0 0)
- origin xyz = (-125.0, 0, 0)
- lower = 0, upper = 250.0
 
## nozzle  (toolhead -> nozzle, fixed)
- origin xyz = (0, 8.5, -52.0)
 
## bed_origin  (base_link -> bed_origin, fixed)
- origin xyz = (-125.0, -125.0, 60.0)
- 근거: PEI 시트 상면, 좌전방 프린트 원점 코너
 
## 미해결 / 가정
- Z 스트로크는 스펙(250) 준용. STEP에서 리드 범위 확인 불가
```
 
### 산출물 2: `voron24_params.xacro` 값 갱신
 
**mm를 1000으로 나눠 m로 입력.** 이 파일만 수정 (계약 §4).
 
```xml
<xacro:property name="MEASURED"  value="true"/>
<xacro:property name="MEAS_DATE" value="2026-08-15"/>
<xacro:property name="MEAS_BY"   value="A"/>
 
<!-- joint_z : 근거 = 갠트리 Y익스트루전 하면 -->
<xacro:property name="jz_x" value="0.0"/>
<xacro:property name="jz_y" value="0.0"/>
<xacro:property name="jz_z" value="0.2145"/>
```
 
`(추정)` 주석을 지우고 근거를 기입할 것.
 
### 즉시 검증
 
```bash
python3 tools/contract_check.py --xacro ros2_ws/src/voron24_description/urdf/voron24.urdf.xacro
ros2 launch voron24_description display.launch.py
```
 
RViz 슬라이더로 확인:
- `joint_z` ↑ → 갠트리만 상승, **베드는 정지**
- 세 조인트 0일 때 노즐이 베드 좌전방 코너
**커밋 후 팀에 통지.** C가 이 값으로 실제 URDF 검증 가능.
 
---
 
## Step 6 — 시각용 메시 추출
 
계약 §5 준수 (원점 정렬, mm 단위, 삼각형 예산).
 
`cad/scripts/export_visual.py`:
 
```python
# FreeCAD Python 콘솔:  exec(open("/path/cad/scripts/export_visual.py").read())
import FreeCAD as App, Mesh, MeshPart, Part, json, os
from FreeCAD import Vector
 
REPO = "/path/to/repo"
OUT  = os.path.join(REPO, "ros2_ws/src/voron24_description/meshes/visual")
os.makedirs(OUT, exist_ok=True)
doc = App.ActiveDocument
GROUPS = json.load(open(os.path.join(REPO, "cad/groups.json")))
 
# measurements.md 의 origin 누적 월드좌표 (mm)
ORIGIN = {
    "base_link":  Vector(0,      0,      0),
    "z_gantry":   Vector(0,      0,      214.5),
    "x_beam":     Vector(0,     -125.0,  226.5),   # z_gantry + joint_y
    "toolhead":   Vector(-125.0, -125.0, 226.5),   # + joint_x
}
 
# (LinearDeflection mm, AngularDeflection rad)
QUALITY = {
    "base_link": (0.30, 0.60),    # 큼 -> 거칠게
    "z_gantry":  (0.20, 0.50),
    "x_beam":    (0.20, 0.50),
    "toolhead":  (0.08, 0.35),    # 근접 관찰 대상 -> 곱게
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
    m.translate(-origin.x, -origin.y, -origin.z)        # 원점 재정렬
    m.harmonizeNormals()
    m.removeDuplicatedPoints()
 
    path = os.path.join(OUT, f"{link}.stl")
    m.write(path)
    mb = os.path.getsize(path)/1e6
    flag = "OK " if m.CountFacets <= BUDGET[link] else "OVER"
    print(f"[{flag}] {link:12s} tri={m.CountFacets:7d} / {BUDGET[link]:6d}  "
          f"{mb:5.1f}MB  missing={missing}")
```
 
### 예산 초과 대응
 
1. `LinearDeflection` / `AngularDeflection` 상향 (0.3 → 0.6)
2. 비가시 파츠 제거 — 나사, 인서트, 케이블, 케이블체인, 전장 베이 내부, 데크 하부
3. FreeCAD Mesh 워크벤치 → **Decimation**
4. Blender 데시메이션 (품질 최상):
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
 
> Blender 임포트/익스포트 시 **스케일 1.0, Z-up 유지 필수.** 기본값 변경 시 mm이 m로 변질.
 
---
 
## Step 7 — 콜리전 메시 추출
 
`cad/scripts/export_collision.py`:
 
```python
import FreeCAD as App, Mesh, MeshPart, Part, json, os
from FreeCAD import Vector
# REPO, GROUPS, ORIGIN 은 Step 6 과 동일
OUT = os.path.join(REPO, "ros2_ws/src/voron24_description/meshes/collision")
os.makedirs(OUT, exist_ok=True)
 
MODE = {"base_link": "box", "z_gantry": "hull", "x_beam": "box", "toolhead": "hull"}
 
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
 
> `base_link`를 통짜 박스로 만들면 갠트리와 상시 충돌. 물리 시뮬 예정이면 프레임 기둥 4개 + 베드 판만 개별 박스로 다중 `<collision>` 구성하거나, 충돌 검사 자체를 비활성화할 것. 3D 프린터는 자기충돌 검사가 사실상 불요.
 
---
 
## Step 8 — 질량·관성 산출
 
```python
import Part
DENSITY = {      # kg/m^3
    "base_link": 2000,    # 알루미늄+플라스틱+전장 혼합. 실측 무게로 보정 권장
    "z_gantry":  2400, "x_beam": 2600, "toolhead": 1500,
}
REAL_MASS = {    # 실측값 우선 (kg)
    "base_link": 12.0, "z_gantry": 2.2, "x_beam": 0.9, "toolhead": 0.45,
}
 
for link, origin in ORIGIN.items():
    comp = Part.makeCompound([doc.getObject(n).Shape for n in GROUPS.get(link, [])
                              if doc.getObject(n)])
    vol_m3 = comp.Volume * 1e-9
    mass = REAL_MASS.get(link) or vol_m3 * DENSITY[link]
    print(f'<xacro:property name="m_{link}" value="{mass:.4f}"/>')
```
 
출력을 `voron24_params.xacro`의 질량 섹션에 반영.
 
> **`<inertial>` 누락은 치명적.** URDF-Importer가 mass=0인 ArticulationBody를 생성해 Unity에서 모델 폭발. `contract_check.py`가 자동 검출하나, 값 자체는 A가 제공.
 
---
 
## Step 9 — 검증
 
### 9-1. 재조립 테스트
 
```python
import Mesh, FreeCAD as App, os
d2 = App.newDocument("verify")
for link, origin in ORIGIN.items():
    m = Mesh.Mesh(os.path.join(OUT_VISUAL, f"{link}.stl"))
    m.translate(origin.x, origin.y, origin.z)      # 모든 조인트=0 자세
    Mesh.show(m, link)
d2.recompute()
Gui.SendMsgToActiveView("ViewFit")
```
 
원본 프린터 형상과 일치하면 통과. 어긋나면 `ORIGIN` 값 오류.
 
### 9-2. 개별 메시 원점
 
각 STL 단독 임포트 시 **원점 근처** 위치 확인. 멀리 떨어져 있으면 Step 6의 `translate` 오류.
 
### 9-3. 메시 무결성
 
Mesh 워크벤치 → **Meshes → Analyze → Evaluate & Repair**
 
Self-intersections, Non-manifold, Degenerated faces 확인 후 Repair. Unity VHACD 콜리전 생성 실패의 주원인.
 
### 9-4. 계약 검증
 
```bash
python3 tools/contract_check.py \
  --xacro ros2_ws/src/voron24_description/urdf/voron24.urdf.xacro \
  --use-meshes --check-meshes
```
 
파일명 불일치, 삼각형 예산 초과, scale 누락을 자동 검출.
 
### 9-5. 체크리스트
 
- [ ] 파일명이 계약 §5와 정확히 일치
- [ ] visual/ 과 collision/ 양쪽 존재
- [ ] 총 삼각형 250,000 이하
- [ ] 각 STL 단독 임포트 시 원점 근처
- [ ] 재조립 테스트 통과
- [ ] `measurements.md` 최신
- [ ] `voron24_params.xacro`의 `MEASURED="true"`
- [ ] `contract_check.py` 통과
- [ ] Git LFS 추적 확인: `git lfs ls-files | grep stl`
---
 
## Step 10 — 커밋 & PR
 
```bash
git checkout -b feat/cad-meshes-v1
 
git lfs track "*.stl" "*.step" "*.FCStd"
git add .gitattributes
 
git add cad/groups.json cad/measurements.md cad/inventory.csv cad/scripts/
git add ros2_ws/src/voron24_description/meshes/
git add ros2_ws/src/voron24_description/urdf/voron24_params.xacro
 
git commit -m "feat(cad): Voron 2.4 250mm 링크 메시 v1 + 조인트 실측값
 
- 링크 4개 (base_link, z_gantry, x_beam, toolhead)
- visual 총 238k tri, collision box/hull
- voron24_params.xacro 실측값 반영, MEASURED=true
- contract_check --use-meshes --check-meshes 통과"
 
git push -u origin feat/cad-meshes-v1
```
 
PR 본문 포함 항목:
- 링크별 삼각형 수 표
- `measurements.md` origin 값 요약
- 재조립 검증 스크린샷
- `contract_check.py` 출력
- 알려진 문제 / 가정
---
 
## 산출물 요약
 
| 경로 | 확장자 | 설명 | 소비자 |
|---|---|---|---|
| `cad/working/*.FCStd` | .FCStd | FreeCAD 작업 파일 (LFS) | A |
| `cad/source/*.step` | .step | 원본 (LFS) | A |
| `cad/scripts/*.py` | .py | 추출 매크로 (재현성) | A |
| `cad/groups.json` | .json | 링크별 파츠 배정 | A |
| `cad/inventory.csv` | .csv | 파츠 인벤토리 | A |
| **`cad/measurements.md`** | .md | **조인트 좌표 실측 + 근거** | **전원** |
| **`urdf/voron24_params.xacro`** | .xacro | **실측값 반영** | **C, B (자동)** |
| **`meshes/visual/*.stl`** | .stl | **시각 메시 (mm)** | **B, C** |
| **`meshes/collision/*.stl`** | .stl | **콜리전 메시** | **B, C** |
 
---
 
## 이후 작업 (W5+)
 
- **도어 링크** — 4면 공용 메시 1개 + 힌지 좌표 4세트
- **LOD** — Unity용 저폴리 버전 (visual의 30%)
- **머티리얼 분리** — 알루미늄/플라스틱/PEI/투명패널을 별도 STL로 분할 시 B가 재질 차등 적용 가능. 파일명 `base_link_frame.stl`, `base_link_panel.stl` 형식. **B와 사전 협의 필수**
- **압출 기어 / 팬 블레이드** — 회전 애니메이션용 별도 링크
---
 
## 트러블슈팅
 
| 증상 | 원인 / 대응 |
|---|---|
| STEP 임포트 미완료 | 파츠 수천 개. 30분 대기 또는 소형 STEP 사용. RAM 확인 |
| 모든 파츠가 한 덩어리 | Compound merge 설정. 끄고 재임포트 (Step 1) |
| 재조립 시 링크 어긋남 | `ORIGIN` 값 오류. `measurements.md`와 대조 |
| STL 100MB 초과 | Deflection 상향 + 불필요 파츠 제거 |
| `makeConvexHull` 부재 | FreeCAD 0.21 이하. 1.0 업그레이드 또는 box 모드 |
| `Shape.Solids` 비어 있음 | 서피스 모델. `Part → Convert to solid` 또는 제외 |
| Blender 경유 STL이 1000배 | 임포트/익스포트 스케일. `global_scale=1.0` 명시 |
| `contract_check` 메시 파일 없음 오류 | 파일명 오타. 계약 §5의 링크명과 정확히 일치시킬 것 |