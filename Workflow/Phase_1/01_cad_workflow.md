# 01 — CAD 담당 워크플로 (FreeCAD)

> **담당: A**
> **현재 목표**: Voron 2.4 STEP 모델을 최상위 LinkGroup 단위로 분리하고, Unity/URDF 적용을 위한 경량 링크 메시 및 조인트 실측 기반을 구축함
> **최종 산출물**: 링크 메시 4~8개 + `voron24_params.xacro` 실측값 + `cad/measurements.md`
> **선행**: [00_interface_contract.md](00_interface_contract.md) 숙지 — 특히 §2 좌표계, §3 링크 트리, §4 소유권, §5 메시 계약
> **병렬화 유의**: W2 종료 시 `cad/measurements.md`를 먼저 커밋함. 메시가 없어도 C가 해당 수치로 파라미터를 채울 수 있음. 메시는 W4에 반영해도 무방함.
>
> **URDF 본문은 직접 편집하지 않음.** A의 최종 편집 대상은 `voron24_params.xacro`이며, 이 파일의 실측값을 변경하면 URDF·RViz·Unity에 자동 반영되도록 구성함.

---

## 준비

### 도구

| 도구 | 버전 | 용도 |
|---|---:|---|
| **FreeCAD** | **1.0 이상** | STEP 임포트, LinkGroup 분리, 모델 정리, 좌표 및 조인트 실측에 사용함 |
| Git + Git LFS | — | `.step`, `.FCStd`, `.stl` 등 대용량 파일을 관리함 |
| Unity | 프로젝트 지정 버전 | 링크 메시와 URDF 구조를 가져와 동작을 검증함 |


사용 워크벤치는 **Part**, **Mesh**, **Python 콘솔**을 중심으로 함.

`View → Panels → Python console`을 상시 표시하는 것을 권장함. 반복 작업은 GUI보다 Python 콘솔을 사용하는 편이 효율적임.

---

## Step 1 — 원본 모델 확보 및 저장

Voron 2.4 전체 어셈블리 STEP 모델을 확보하여 FreeCAD에서 열었음.

현재 사용 중인 원본 파일은 다음과 같음.

```text
Voron_2.4r2_Assembly.step
```

전체 어셈블리를 FreeCAD 문서로 저장한 작업 파일은 다음과 같음.

```text
voron_2_4.FCStd
```

> 전체 STEP 파일은 재임포트에 시간이 오래 걸릴 수 있으므로, 최초 임포트 직후 `.FCStd` 형식으로 저장하여 이후 작업에 사용함.

---

## Step 2 — 최상위 LinkGroup 단위 모델 분리

전체 STEP 모델을 FreeCAD로 불러온 후, 트리 뷰의 최상위 LinkGroup 단위로 모델을 분리하였음.

각 최상위 LinkGroup은 독립적인 FreeCAD 프로젝트 파일인 `.FCStd` 형식으로 저장하였음. 이를 통해 모듈별 수정, 불필요 부품 제거, 경량화 및 재분류 작업을 독립적으로 수행할 수 있도록 구성하였음.

### 현재 분리 완료 파일

```text
bed.FCStd
electronics.FCStd
exhaust_filter_assembly.FCStd
frame.FCStd
gantry.FCStd
panels.FCStd
skirt.FCStd
spool_holder.FCStd
z_assembly.FCStd
Voron_2.4r2_Assembly.step
voron_2_4.FCStd
```

### 파일별 예상 역할

| 파일 | 예상 내용 | 이후 처리 방향 |
|---|---|---|
| `bed.FCStd` | 베드 및 베드 지지 구조 | `base_link` 포함 여부와 베드 원점 실측에 사용함 |
| `electronics.FCStd` | 전장 부품 및 배선 관련 구조 | 외관에 필요한 부품만 유지하고 내부 부품은 단순화함 |
| `exhaust_filter_assembly.FCStd` | 배기 및 필터 구조 | 시뮬레이션 중요도에 따라 유지 또는 제거함 |
| `frame.FCStd` | 프레임 익스트루전 및 고정 구조 | `base_link`의 핵심 메시로 사용함 |
| `gantry.FCStd` | XY 갠트리 및 관련 구조 | `z_gantry`, `x_beam`, `toolhead` 분류에 사용함 |
| `panels.FCStd` | 외장 패널 및 도어 | 시각적 필요성과 Polygon 예산을 기준으로 유지 여부를 결정함 |
| `skirt.FCStd` | 하부 스커트 구조 | `base_link` 시각 메시의 일부로 검토함 |
| `spool_holder.FCStd` | 필라멘트 스풀 홀더 | 기본 운동학과 직접 관련이 없어 선택적으로 포함함 |
| `z_assembly.FCStd` | Z축 구동 및 지지 구조 | 고정부와 이동부를 분류하여 `base_link` 및 `z_gantry`에 반영함 |
| `voron_2_4.FCStd` | 전체 어셈블리 작업본 | 좌표계, 위치 관계 및 재조립 검증의 기준으로 사용함 |

### 분리 목적

- 최상위 모듈 단위의 독립 편집 환경을 확보함
- 대규모 전체 어셈블리를 한 번에 편집할 때 발생하는 성능 저하를 줄임
- 불필요 부품 제거 및 링크별 메시 구성을 용이하게 함
- 원본 모델과 경량화 모델의 비교 기준을 유지함

---

## Step 3 — CAD 모델 경량화

원본 STEP 모델에는 실제 장비 제작에는 필요하지만 Unity 기반 URDF 시뮬레이션의 핵심 운동학에는 직접적인 영향을 주지 않는 부품이 다수 포함되어 있음.

케이블 체인과 구동 벨트 등은 실제 Voron 2.4의 구동 구조를 표현하는 중요한 요소이지만, 자연스러운 움직임을 구현하려면 다수의 링크와 조인트, 변형 가능한 메시 또는 별도의 물리 표현이 필요함. 이는 초기 시뮬레이션 범위를 넘어서는 복잡도를 발생시킴.

따라서 초기 모델에서는 운동학적 중요도와 Unity 실행 성능을 기준으로 불필요하거나 구현 난이도가 높은 요소를 제거함.

### 우선 제거 예정 요소

- Cable Chain
- Drive Belt
- 케이블 및 배선
- 나사, 너트, 와셔 및 인서트 등 소형 체결 부품
- 전장 베이 내부의 비가시 부품
- 링크 운동과 직접적인 관련이 없는 장식성 부품
- Polygon 수 대비 시각적 기여도가 낮은 부품

### 유지 대상

- 프레임 및 고정부
- 베드 및 주요 지지 구조
- Z축 갠트리
- X축 빔
- 툴헤드
- 각 축의 레일 및 캐리지
- 조인트 위치와 운동 범위를 판단하는 데 필요한 구조 부품

### 경량화 목적

- Unity 렌더링 부하를 줄임
- URDF 링크 및 조인트 구성을 단순화함
- 메시 파일 크기와 Polygon 수를 줄임
- 충돌 메시 생성을 용이하게 함
- 저사양 작업 환경에서도 모델을 안정적으로 다룰 수 있도록 함

> 제거 대상은 원본 모델에서 삭제하기보다 별도의 경량화 작업 파일에서 숨김 또는 제거하는 방식을 우선 적용함. 원본 `.step` 및 전체 `.FCStd` 파일은 비교와 복구를 위해 유지함.

---

## Step 4 — URDF 링크 단위 재구성

최상위 LinkGroup 분리는 편집 편의를 위한 1차 분류이며, 최종 URDF 링크 구조와 일치하지 않을 수 있음.

따라서 분리된 `.FCStd` 파일을 정리한 후, 최종적으로 다음과 같은 URDF 링크 단위로 메시를 재구성함.

### 기본 링크 구조

| 링크 | 주요 포함 대상 |
|---|---|
| `base_link` | 프레임, 베드, 스커트, 고정 패널, 전장부, 고정 모터 및 기타 고정부를 포함함 |
| `z_gantry` | Z축 방향으로 함께 이동하는 XY 갠트리 구조를 포함함 |
| `x_beam` | Y축 방향으로 이동하는 X축 빔과 레일 구조를 포함함 |
| `toolhead` | X축 방향으로 이동하는 툴헤드, 캐리지, 핫엔드 및 노즐 구조를 포함함 |
| `extruder_gear` | [선택] 압출 기어 회전 애니메이션이 필요할 경우 별도 링크로 구성함 |
| `door` | [선택] 도어 개폐 동작이 필요할 경우 별도 링크로 구성함 |

### 분류 원칙

- 동일한 조인트를 따라 함께 이동하는 부품은 하나의 링크로 묶음
- 고정된 부품은 가능한 한 `base_link`에 포함함
- 시각적 표현만 필요한 부품은 Visual Mesh에만 포함할 수 있음
- 충돌 검사가 불필요한 소형 부품은 Collision Mesh에서 제외함
- 링크 간 경계는 실제 운동 관계를 기준으로 결정함

---

## Step 5 — 좌표계 및 링크 원점 정렬

모델 경량화와 링크 분류가 완료되면 계약 문서의 좌표계 정의에 맞춰 전체 모델과 각 링크의 원점을 정렬함.

기준 좌표계는 다음과 같음.

- 원점: 프레임 하단면 정중앙
- Z축: 상방
- X축: 우측
- Y축: 후방

### 검증 항목

- 전체 프레임의 하단 정중앙에 원점이 위치하는지 확인함
- 정면 기준 X축 양의 방향이 우측인지 확인함
- Y축 양의 방향이 장비 후방인지 확인함
- Z축 양의 방향이 상방인지 확인함
- 각 링크의 로컬 원점이 대응하는 조인트 기준점과 일치하는지 확인함

> 최종 메시를 추출할 때 각 링크 메시를 대응 Joint Origin 기준으로 이동하여, URDF에서 `<origin xyz>`를 적용했을 때 원래 형상이 정확히 재조립되도록 구성함.

---

## Step 6 — 조인트 좌표 및 이동 범위 실측

경량화된 링크 모델을 기준으로 FreeCAD에서 조인트의 위치와 이동 범위를 실측함.

### 측정 항목

| 조인트 | 연결 구조 | 형식 | 측정값 |
|---|---|---|---|
| `joint_z` | `base_link → z_gantry` | Prismatic | Origin xyz, Z축 방향, Z Stroke를 측정함 |
| `joint_y` | `z_gantry → x_beam` | Prismatic | Origin xyz, Y축 방향, Y Stroke를 측정함 |
| `joint_x` | `x_beam → toolhead` | Prismatic | Origin xyz, X축 방향, X Stroke를 측정함 |
| `nozzle` | `toolhead → nozzle` | Fixed | 툴헤드 로컬 기준 노즐 팁 좌표를 측정함 |
| `bed_origin` | `base_link → bed_origin` | Fixed | 베드 상면의 프린트 원점 좌표를 측정함 |

### 실측 결과 기록

측정 결과는 다음 파일에 기록함.

```text
cad/measurements.md
```

기록 시 다음 내용을 포함함.

- 측정일
- 측정자
- 사용한 원본 파일 또는 저장소 커밋
- 조인트별 Origin xyz
- Axis
- Lower 및 Upper Limit
- 측정 기준이 된 면, 엣지 또는 중심선
- 추정값과 실측값의 구분
- 미해결 사항 및 가정

### Xacro 반영

실측값은 mm 단위에서 m 단위로 변환하여 다음 파일에 반영함.

```text
ros2_ws/src/voron24_description/urdf/voron24_params.xacro
```

> URDF 본문을 직접 수정하기보다 `voron24_params.xacro`의 실측 파라미터를 수정하는 것을 원칙으로 함.

---

## Step 7 — 시각용 및 충돌용 메시 생성

링크별 모델 정리가 완료되면 Unity와 ROS에서 사용할 메시를 생성함.

### 시각용 메시

시각적 형상을 표현하는 메시를 다음 경로에 저장함.

```text
ros2_ws/src/voron24_description/meshes/visual/
```

예상 파일은 다음과 같음.

```text
base_link.stl
z_gantry.stl
x_beam.stl
toolhead.stl
```

### 충돌용 메시

물리 충돌 계산에 사용할 단순화 메시를 다음 경로에 저장함.

```text
ros2_ws/src/voron24_description/meshes/collision/
```

충돌 메시에는 다음 원칙을 적용함.

- 프레임은 필요한 구조물별 박스 또는 단순 형상으로 구성함
- 이동 링크는 Box 또는 Convex Hull을 우선 사용함
- 나사, 케이블, 벨트 및 장식 부품은 제외함
- 시각 메시보다 훨씬 낮은 Polygon 수를 유지함
- 자기충돌 검사가 불필요한 경우 Unity 및 URDF 설정에서 비활성화 여부를 검토함

---

## Step 8 — Unity 프로젝트 적용

경량화와 링크별 메시 추출이 완료되면 Unity 3D 프로젝트에 모델을 적용함.

### 수행 작업

- URDF Importer를 통해 모델을 불러옴
- 링크별 Visual Mesh를 확인함
- 링크 계층이 설계한 URDF 구조와 일치하는지 확인함
- Joint Origin과 Axis를 적용함
- Prismatic 및 Fixed Joint를 구성함
- Joint Limit 및 Stroke를 적용함
- Articulation Body의 질량 및 관성값을 확인함
- 링크 간 위치와 회전 방향을 검증함

### 동작 검증

Unity에서 다음 동작을 확인함.

- `joint_z` 변화 시 갠트리 전체만 Z축으로 이동함
- `joint_y` 변화 시 X축 빔과 툴헤드만 Y축으로 이동함
- `joint_x` 변화 시 툴헤드만 X축으로 이동함
- 베드와 프레임은 고정 상태를 유지함
- 모든 조인트가 기준 위치일 때 노즐과 베드 원점의 관계가 올바름
- 링크 메시 간 불필요한 간격 또는 중첩이 없음
- 모델이 비정상적으로 폭발하거나 흔들리지 않음

---

## Step 9 — 검증

### 9-1. FreeCAD 재조립 검증

각 링크 메시를 대응 Joint Origin에 다시 배치하여 원본 Voron 2.4 형상과 일치하는지 확인함.

링크가 어긋날 경우 다음 항목을 점검함.

- 링크 메시 원점
- 조인트 Origin
- 링크 로컬 좌표계
- mm/m 단위 변환
- STL 내보내기 스케일

### 9-2. 메시 무결성 검증

다음 문제를 확인함.

- Non-manifold
- Self-intersection
- Degenerated Face
- 뒤집힌 Normal
- 중복 Vertex
- 불필요한 고밀도 Polygon

필요 시 FreeCAD Mesh Workbench, Blender 또는 MeshLab을 이용하여 수리함.

### 9-3. ROS/RViz 검증

가능한 경우 RViz의 Joint State Publisher를 이용하여 각 조인트의 운동을 확인함.

- `joint_z` 증가 시 갠트리만 상승함
- `joint_y` 증가 시 X축 빔과 툴헤드가 함께 이동함
- `joint_x` 증가 시 툴헤드만 이동함
- 조인트가 0일 때 기준 위치가 설계와 일치함

### 9-4. 계약 검증

프로젝트에서 제공하는 계약 검사 스크립트를 실행함.

```bash
python3 tools/contract_check.py \
  --xacro ros2_ws/src/voron24_description/urdf/voron24.urdf.xacro \
  --use-meshes --check-meshes
```

다음 항목을 확인함.

- 파일명 일치 여부
- 필수 링크와 조인트 존재 여부
- 메시 경로 존재 여부
- Scale 누락 여부
- 질량 및 관성값 누락 여부
- Polygon 예산 초과 여부

---

## Step 10 — 진행 현황

| 작업 | 상태 |
|---|---|
| Voron 2.4 STEP 모델 확보 | ✅ 완료 |
| 전체 어셈블리 `.FCStd` 저장 | ✅ 완료 |
| 최상위 LinkGroup 단위 분리 | ✅ 완료 |
| 모듈별 `.FCStd` 저장 | ✅ 완료 |
| 경량화 대상 선정 | ✅ 완료 |
| 케이블 체인 및 구동 벨트 제거 | ⏳ 진행 예정 |
| 세부 부품 정리 | ⏳ 진행 예정 |
| URDF 링크 단위 재분류 | ⏳ 진행 예정 |
| 좌표계 정렬 | ⏳ 진행 예정 |
| Joint Origin 및 Stroke 실측 | ⏳ 진행 예정 |
| Visual Mesh 생성 | ⏳ 진행 예정 |
| Collision Mesh 생성 | ⏳ 진행 예정 |
| `cad/measurements.md` 작성 | ⏳ 진행 예정 |
| `voron24_params.xacro` 반영 | ⏳ 진행 예정 |
| Unity Import 및 동작 검증 | ⏳ 진행 예정 |
| RViz 및 계약 검사 | ⏳ 진행 예정 |

---

## Step 11 — 체크리스트

- [x] 원본 STEP 모델을 확보함
- [x] 전체 어셈블리를 `.FCStd`로 저장함
- [x] 최상위 LinkGroup 단위로 분리함
- [x] 분리 모델을 개별 `.FCStd` 파일로 저장함
- [x] 우선 경량화 대상을 선정함
- [ ] 케이블 체인을 제거함
- [ ] 구동 벨트를 제거함
- [ ] 케이블, 체결 부품 및 비가시 부품을 정리함
- [ ] 최종 URDF 링크 단위로 파츠를 재분류함
- [ ] 전체 좌표계를 계약 기준으로 정렬함
- [ ] 링크별 로컬 원점을 정렬함
- [ ] Joint Origin을 실측함
- [ ] Joint Axis와 Stroke를 확인함
- [ ] `cad/measurements.md`를 작성함
- [ ] `voron24_params.xacro`에 실측값을 반영함
- [ ] 시각용 메시를 생성함
- [ ] 충돌용 메시를 생성함
- [ ] Unity에 URDF와 메시를 불러옴
- [ ] Articulation Body 동작을 검증함
- [ ] RViz 동작을 검증함
- [ ] 계약 검사 스크립트를 통과함
- [ ] Git LFS 추적 상태를 확인함

---

## Step 12 — 산출물 요약

| 경로 | 확장자 | 설명 | 소비자 |
|---|---|---|---|
| `cad/source/Voron_2.4r2_Assembly.step` | `.step` | Voron 2.4 원본 전체 어셈블리임 | A |
| `cad/working/voron_2_4.FCStd` | `.FCStd` | 전체 어셈블리 FreeCAD 작업본임 | A |
| `cad/working/bed.FCStd` | `.FCStd` | 베드 모듈 작업본임 | A |
| `cad/working/electronics.FCStd` | `.FCStd` | 전장 모듈 작업본임 | A |
| `cad/working/exhaust_filter_assembly.FCStd` | `.FCStd` | 배기·필터 모듈 작업본임 | A |
| `cad/working/frame.FCStd` | `.FCStd` | 프레임 모듈 작업본임 | A |
| `cad/working/gantry.FCStd` | `.FCStd` | 갠트리 모듈 작업본임 | A |
| `cad/working/panels.FCStd` | `.FCStd` | 패널 모듈 작업본임 | A |
| `cad/working/skirt.FCStd` | `.FCStd` | 스커트 모듈 작업본임 | A |
| `cad/working/spool_holder.FCStd` | `.FCStd` | 스풀 홀더 모듈 작업본임 | A |
| `cad/working/z_assembly.FCStd` | `.FCStd` | Z축 어셈블리 작업본임 | A |
| `cad/groups.json` | `.json` | 최종 URDF 링크별 파츠 배정 결과임 | A |
| `cad/inventory.csv` | `.csv` | 파츠 및 형상 인벤토리임 | A |
| **`cad/measurements.md`** | `.md` | **조인트 좌표, 이동 범위 및 측정 근거임** | **전원** |
| **`urdf/voron24_params.xacro`** | `.xacro` | **실측 파라미터 반영 파일임** | **B, C** |
| **`meshes/visual/*.stl`** | `.stl` | **링크별 시각용 메시임** | **B, C** |
| **`meshes/collision/*.stl`** | `.stl` | **링크별 충돌용 메시임** | **B, C** |

> 실제 저장 경로는 저장소의 기존 디렉토리 구조와 인터페이스 계약을 기준으로 조정함. 현재 분리한 `.FCStd` 파일이 저장소 외부에 있을 경우, Git LFS 설정 후 `cad/working/`으로 이동함.

---

## Step 13 — 커밋 및 PR 계획

대용량 CAD 및 메시 파일은 Git LFS로 추적함.

```bash
git lfs track "*.stl" "*.step" "*.FCStd"
git add .gitattributes
```

작업 단계별로 다음과 같이 커밋하는 것을 권장함.

```bash
git add cad/working/ cad/source/
git commit -m "feat(cad): Voron 2.4 최상위 모듈 분리"
```

경량화와 링크 분류 후 다음 내용을 커밋함.

```bash
git add cad/groups.json cad/inventory.csv cad/measurements.md
git add cad/scripts/
git commit -m "feat(cad): 링크 분류 및 조인트 실측값 추가"
```

메시 및 Xacro 반영 후 다음 내용을 커밋함.

```bash
git add ros2_ws/src/voron24_description/meshes/
git add ros2_ws/src/voron24_description/urdf/voron24_params.xacro
git commit -m "feat(cad): Unity URDF용 링크 메시 및 실측 파라미터 반영"
```

현재 작업 브랜치는 다음과 같음.

```text
JSY_0805_12_00
```

원격 저장소에 최초 업로드할 때 다음 명령을 사용함.

```bash
git push -u origin JSY_0805_12_00
```

---

## 참고 — 원본 워크플로와 현재 작업 계획의 차이

원본 CAD 워크플로는 Voron 2.4 전체 어셈블리를 하나의 FreeCAD 문서에서 분석한 후, 개별 파츠를 직접 선택하여 최종 URDF 링크 구조인 `base_link`, `z_gantry`, `x_beam`, `toolhead` 등으로 분류하는 방식을 기준으로 작성되어 있음.

현재 작업 계획은 전체 STEP 파일의 트리 구조에 이미 존재하는 최상위 LinkGroup을 먼저 분리하여 독립적인 `.FCStd` 작업 파일로 저장한 후, 각 모듈을 경량화하고 최종 URDF 링크 단위로 다시 구성하는 방식임.

### 주요 차이점

| 구분 | 원본 워크플로 | 현재 작업 계획 |
|---|---|---|
| 초기 모델 처리 | 전체 STEP 어셈블리를 하나의 FreeCAD 문서에서 관리함 | 최상위 LinkGroup을 먼저 분리하여 개별 `.FCStd` 파일로 저장함 |
| 작업 단위 | 개별 Solid 또는 Part를 직접 선택함 | `bed`, `frame`, `gantry`, `z_assembly` 등의 대분류 모듈을 우선 사용함 |
| 링크 분류 시점 | 좌표계 정렬 후 바로 URDF 링크 단위로 분류함 | 모듈별 경량화를 먼저 수행한 후 URDF 링크 단위로 재분류함 |
| 파츠 인벤토리 | 전체 어셈블리에서 즉시 `cad/inventory.csv`를 생성함 | 최상위 모듈 분리를 우선 완료했으며 세부 인벤토리는 이후 생성함 |
| 경량화 대상 | 나사, 인서트, 케이블 및 비가시 파츠를 링크 분류 중 제거함 | 케이블 체인과 구동 벨트처럼 구현 난이도가 높은 요소를 우선 제거함 |
| 좌표계 정렬 | 링크 그룹핑 전에 전체 모델을 계약 좌표계로 정렬함 | 분리 모델을 정리한 후 링크 재구성 단계에서 좌표계와 로컬 원점을 정렬함 |
| 조인트 실측 | 링크 그룹핑 직후 FreeCAD에서 실측함 | 경량화 및 최종 링크 메시 구성이 완료된 후 실측함 |
| 메시 생성 | FreeCAD 스크립트로 링크별 STL을 직접 생성함 | 동일한 방식을 기본으로 하되 Unity 성능을 고려하여 제거 및 데시메이션을 강화함 |
| Unity 적용 | 메시와 Xacro 실측값이 완료된 후 최종 검증에 사용함 | 경량화 이후 Unity에 단계적으로 적용하여 링크 구조와 동작을 반복 검증함 |
| URDF 편집 | CAD 담당자는 URDF 본문을 편집하지 않고 Xacro 파라미터만 수정함 | Unity에서 링크·조인트 구성을 검토하되 최종 실측값은 Xacro에 반영함 |
| 검증 우선순위 | RViz, 재조립, 계약 검사를 중심으로 함 | Unity Articulation Body 검증을 추가로 강조함 |

### 현재 계획에서 추가로 강조하는 사항

현재 계획에서는 원본 워크플로보다 Unity 실행 성능과 URDF 구현 난이도를 적극적으로 고려함.

케이블 체인과 구동 벨트를 실제와 유사하게 표현하려면 다수의 링크와 조인트 또는 변형 가능한 물리 모델이 필요함. 해당 요소는 초기 프로젝트의 핵심 목표인 프레임, 갠트리, X축 빔 및 툴헤드의 운동학 재현과 직접적인 관련이 낮으므로 초기 모델에서 제외함.

다만 향후 시스템 성능과 연구 범위가 허용될 경우 다음 요소를 별도 기능으로 추가할 수 있음.

- 케이블 체인 링크 애니메이션
- 구동 벨트의 시각적 이동 표현
- 팬 블레이드 회전
- 압출 기어 회전
- 도어 개폐
- 재질별 메시 분리
- Unity용 LOD 메시

### 원본 워크플로에서 유지하는 원칙

작업 순서와 중간 산출물은 현재 상황에 맞게 조정하더라도 다음 원칙은 유지함.

- 최종 모델을 URDF 링크 구조에 맞게 분리함
- 링크 메시의 원점을 대응 Joint Origin 기준으로 정렬함
- Joint Origin, Axis 및 Stroke를 CAD 모델에서 실측함
- 시각용 메시와 충돌용 메시를 구분함
- 메시의 Polygon 수와 파일 크기를 제한함
- 질량과 관성값을 누락하지 않음
- 실측값과 근거를 `cad/measurements.md`에 기록함
- 최종 실측 파라미터를 `voron24_params.xacro`에 반영함
- Unity와 ROS/RViz에서 동일한 운동학 구조가 재현되는지 확인함
- 원본 모델과 경량화 모델을 별도로 보존함

> **정리:** 현재 작업 계획은 원본 워크플로를 대체하는 계획이 아님. 이미 수행한 최상위 LinkGroup 분리 작업을 시작점으로 삼아, 원본 워크플로가 요구하는 최종 링크 메시와 조인트 실측값에 도달하도록 작업 순서를 조정한 계획임.
