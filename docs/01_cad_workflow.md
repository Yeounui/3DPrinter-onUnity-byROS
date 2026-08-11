# 01 — CAD 담당 워크플로 (FreeCAD)

> ****담당: A****
> ****현재 목표****: Voron 2.4 STEP 모델을 실제 운동 관계에 따라 `base_link`, `z_gantry`, `x_beam`, `toolhead`의 4개 핵심 링크로 재구성하고, Unity/URDF 적용을 위한 경량 링크 메시 및 조인트 실측 기반을 구축함
> ****최종 산출물****: 링크 메시 4~8개 + `voron24_params.xacro` 실측값 + `cad/measurements.md`
> ****선행****: [00_interface_contract.md]\(00_interface_contract.md) 숙지 — 특히 §2 좌표계, §3 링크 트리, §4 소유권, §5 메시 계약
> ****병렬화 유의****: W2 종료 시 `cad/measurements.md`를 먼저 커밋함. 메시가 없어도 C가 해당 수치로 파라미터를 채울 수 있음. 메시는 W4에 반영해도 무방함.
>
> ****URDF 본문은 직접 편집하지 않음.**** A의 최종 편집 대상은 `voron24_params.xacro`이며, 이 파일의 실측값을 변경하면 URDF·RViz·Unity에 자동 반영되도록 구성함.

---

## 준비

### 도구

| 도구 | 버전 | 용도 |
|---|---:|---|
| ****FreeCAD**** | ****1.0 이상**** | STEP 임포트, LinkGroup 분리, 모델 정리, 좌표 및 조인트 실측에 사용함 |
| Git + Git LFS | — | `.step`, `.FCStd`, `.stl` 등 대용량 파일을 관리함 |
| Unity | 프로젝트 지정 버전 | 링크 메시와 URDF 구조를 가져와 동작을 검증함 |


사용 워크벤치는 ****Part****, ****Mesh****, ****Python 콘솔****을 중심으로 함.

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

## Step 2 — 최상위 LinkGroup 단위 1차 분리

전체 STEP 모델을 FreeCAD로 불러온 후, 트리 뷰의 최상위 LinkGroup 단위로 모델을 분리하였음.

각 최상위 LinkGroup은 독립적인 FreeCAD 프로젝트 파일인 `.FCStd` 형식으로 저장하였음. 이 단계의 결과물은 최종 URDF 링크가 아니라, 이후 `base_link`, `z_gantry`, `x_beam`, `toolhead`의 4개 핵심 링크로 재분류하기 위한 중간 작업본으로 사용함.

### 현재 분류 완료 기본 링크 구조
| `base_link` | 프레임, 베드, 스커트, 고정 패널, 전장부, 고정 모터 및 기타 고정부를 포함함 |
| `z_gantry` | Z축 방향으로 함께 이동하는 XY 갠트리 구조를 포함함 |
| `x_beam` | Y축 방향으로 이동하는 X축 빔과 레일 구조를 포함함 |
| `toolhead` | X축 방향으로 이동하는 툴헤드, 캐리지, 핫엔드 및 노즐 구조를 포함함 |


### 분리 목적

- 최종 4개 링크 재분류 전에 원본 조립 구조를 분해하여 다루기 쉬운 중간 작업 단위를 확보함
- 불필요 부품 제거와 실제 운동 관계 기준의 재분류 작업을 용이하게 함
- 원본 모델과 최종 4개 링크 구성 사이의 비교·추적 기준을 유지함

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
- 향후 물리특성 적용 단계에서 충돌 메시를 단순화하기 쉬운 구조를 확보함
- 저사양 작업 환경에서도 모델을 안정적으로 다룰 수 있도록 함

> 제거 대상은 원본 모델에서 삭제하기보다 별도의 경량화 작업 파일에서 숨김 또는 제거하는 방식을 우선 적용함. 원본 `.step` 및 전체 `.FCStd` 파일은 비교와 복구를 위해 유지함.

---

## Step 4 — URDF 링크 단위 재구성

최상위 LinkGroup 분리는 편집 편의를 위한 1차 분류이며, 최종 URDF 링크 구조와 일치하지 않을 수 있음.

현재는 각 부품이 실제 장비에서 함께 움직이는지 또는 프레임에 고정되는지를 기준으로 ****고정부와 이동부를 구분하였으며****, 인터페이스 계약에서 정의한 `base_link`, `z_gantry`, `x_beam`, `toolhead`의 4개 핵심 링크 구조에 맞추어 부품을 재분류하였음.

이는 기존 최상위 LinkGroup 중심 분류에서 실제 운동 관계와 URDF 링크 구조를 기준으로 한 분류로 전환한 것임.

최종 URDF 링크 단위는 다음과 같음.


### 분류 원칙

- 동일한 조인트를 따라 함께 이동하는 부품은 하나의 링크로 묶음
- 고정된 부품은 가능한 한 `base_link`에 포함함
- 시각적 표현만 필요한 부품은 Visual Mesh에만 포함할 수 있음
- 충돌 검사가 불필요한 소형 부품은 Collision Mesh에서 제외함
- 링크 간 경계는 실제 운동 관계를 기준으로 결정함

### 현재 적용 결과

- `base_link`: 프레임 및 베드 등 장비 동작 중 위치가 변하지 않는 고정부를 배정함
- `z_gantry`: Z축 이동 시 함께 승강하는 갠트리 계통 부품을 배정함
- `x_beam`: Y축 이동 시 함께 이동하는 X축 빔 및 관련 부품을 배정함
- `toolhead`: X축 이동 시 X빔을 따라 이동하는 툴헤드 및 관련 부품을 배정함

> 현재 분류는 인터페이스 계약의 링크 구조를 기준으로 수행하였으며, 이후 메시 생성 및 Joint Origin 실측 역시 이 링크 단위를 기준으로 진행함.

### 단계적 적용 원칙 — 운동학 우선, 물리특성 후속 적용

초기 단계에서는 링크 계층, Joint Origin, Axis, Stroke 및 기본 이동 범위를 먼저 규정하고, 각 링크가 의도한 방향과 범위로 이동하는지 **운동학적 동작을 우선 검증함**.

이 단계에서는 Collision, Mass, Inertia 등 물리특성에 의한 상호작용을 우선 적용하지 않음. 기본 움직임과 가동범위가 확정된 이후 다음 항목을 순차적으로 추가함.

- Collision Mesh 및 Collider 적용
- 질량(Mass) 및 관성(Inertia) 파라미터 적용
- Articulation Body 물리 동작 및 링크 간 충돌 검증
- 필요 시 물리 파라미터 조정 및 안정화

> 본 원칙은 초기 운동학 오류와 물리 파라미터 오류를 분리하여 검증하기 위한 것임. 최종 단계에서는 필요한 물리특성을 적용하되, 기본 운동 범위가 확정되기 전에는 물리효과를 운동학 검증의 전제조건으로 사용하지 않음.

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

> 최종 메시를 추출할 때 각 링크 메시를 대응 Joint Origin 기준으로 이동하여, URDF에서 `\<origin xyz>`를 적용했을 때 원래 형상이 정확히 재조립되도록 구성함.

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

### 실측 전 임시 가동범위

실측이 완료되기 전에는 VoronDesign에서 제공하는 공식 Klipper 설정 템플릿의 250 mm build용 값을 임시 참고값으로 사용함.

| 자유도 | 이동 링크 | 조인트 | 공식 Klipper 250 mm build 참고값 | 현재 문서 적용 |
|---|---|---|---:|---|
| X | `toolhead` | `joint_x` | `position_min: 0`, `position_max: 250` mm | ****실측 전 임시값 0 ~ 250 mm**** |
| Y | `x_beam` | `joint_y` | `position_min: 0`, `position_max: 250` mm | ****실측 전 임시값 0 ~ 250 mm**** |
| Z | `z_gantry` | `joint_z` | `position_max: 210` mm | ****실측 전 임시 상한 210 mm**** |

> ****주의 — 실측 전 임시값임.**** 위 값은 CAD 모델에서 측정한 최종 Stroke가 아니며, 실제 조립 형상과 간섭 조건을 기준으로 반드시 다시 실측해야 함.
>
> 특히 현재 `00_interface_contract.md`에는 `joint_z`의 범위가 `0 ~ 0.250 m`로 정의되어 있으나, VoronDesign 공식 Klipper 설정 템플릿은 250 mm build용 Z `position_max` 선택값으로 `210 mm`를 제시함. ****따라서 Z축은 실측 후 계약값 재검토 필요****. 실측 전에는 `01_cad_workflow\.md`에서 계약값을 임의로 변경하지 않음.

#### 공식 설정 출처

- 저장소: `VoronDesign/Voron-2`
- 브랜치: `Voron2.4`
- 파일: `firmware/klipper_configurations/Octopus/Voron2_Octopus_Config.cfg`
- URL: https\://github.com/VoronDesign/Voron-2/blob/Voron2.4/firmware/klipper_configurations/Octopus/Voron2_Octopus_Config.cfg
- `[stepper_x]`: GitHub 렌더링 기준 lines 1465~1473 — `position_min: 0`, 250 mm build용 `position_endstop: 250`, `position_max: 250`
- `[stepper_y]`: GitHub 렌더링 기준 lines 1529~1537 — `position_min: 0`, 250 mm build용 `position_endstop: 250`, `position_max: 250`
- `[stepper_z]`: GitHub 렌더링 기준 lines 1599~1623 — Z endstop 보정 설명, 초기 `position_endstop: -0.5`, 250 mm build용 `position_max: 210`, `position_min: -5`

공식 설정 파일에서 크기별 `position_max` 항목은 주석 처리된 템플릿 형태로 제공됨. 예를 들어 `#position_max: 210`은 250 mm build에서 해당 줄의 주석을 해제하여 사용하는 선택값을 의미함. 따라서 `210 mm`는 모든 장비에 강제되는 고정값이 아니라, 실측 전 참고할 수 있는 ****공식 소프트웨어 기준값****으로 취급함.

### 실제 장비의 기준점 및 이동 제한 개요

실제 장비에서는 endstop을 이용한 homing으로 각 축의 기준 위치를 확보하고, 이후 펌웨어가 스테퍼 이동량을 기준으로 현재 위치를 추적함. 설정된 `position_min`과 `position_max`는 허용 이동 범위를 소프트웨어적으로 제한하는 데 사용됨.

- X/Y축은 endstop을 이용하여 homing 기준 위치를 확보함
- Z축은 Z endstop을 이용한 기준점 보정이 필요함
- Z축의 `position_endstop`은 실제 프린트 표면의 Z0와 endstop trigger 지점 사이의 오프셋 보정값임
- Voron 2.4의 4개 Z 구동부는 별도의 Quad Gantry Leveling 과정을 통해 갠트리 정렬에 사용됨
- 본 문서에서는 위 동작을 펌웨어 구현 절차로 상세 정의하지 않고, CAD의 Joint Origin 및 Stroke 실측에 필요한 배경 기준으로만 사용함

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

## Step 7 — 시각용 메시 생성 및 충돌용 메시의 단계적 적용

링크별 모델 정리가 완료되면 먼저 기본 운동학 검증에 사용할 시각용 메시를 생성함. 충돌용 메시와 Collider는 Step 4의 단계적 적용 원칙에 따라 기본 이동 방향과 가동범위가 확정된 이후 후속 단계에서 적용함.

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

### 충돌용 메시 — 기본 운동학 검증 후 적용

기본 링크 움직임과 Joint Limit 검증이 완료된 이후, 물리 충돌 계산에 사용할 단순화 메시를 다음 경로에 저장함.

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
- 링크 간 위치와 회전 방향을 우선 검증함
- 기본 운동학 검증 완료 후 Collision, Mass, Inertia 등 물리특성을 순차적으로 적용함

### 동작 검증

Unity에서 다음 동작을 확인함.

- `joint_z` 변화 시 갠트리 전체만 Z축으로 이동함
- `joint_y` 변화 시 X축 빔과 툴헤드만 Y축으로 이동함
- `joint_x` 변화 시 툴헤드만 X축으로 이동함
- 베드와 프레임은 고정 상태를 유지함
- 모든 조인트가 기준 위치일 때 노즐과 베드 원점의 관계가 올바름
- 링크 메시 간 불필요한 간격 또는 중첩이 없음
- 초기 검증에서는 각 조인트가 지정한 방향과 범위 내에서 정상적으로 이동하는지 확인함
- 물리특성 적용 후에는 모델의 비정상적인 충돌, 진동 또는 불안정 여부를 추가로 확인함

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

프로젝트에서 제공하는 계약 검사 스크립트를 실행함. 초기 운동학 단계에서는 링크·조인트 이름, 축, 범위 및 메시 경로를 우선 확인하고, 질량·관성 및 Collision 관련 항목은 해당 물리특성을 적용한 이후 최종 검증함.

```bash
python3 tools/contract_check.py \\
  --xacro ros2_ws/src/voron24_description/urdf/voron24.urdf.xacro \\
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
| 최상위 LinkGroup 단위 1차 분리 | ✅ 완료 — 중간 작업본 |
| 1차 분리 `.FCStd` 저장 | ✅ 완료 — 최종 링크 구성 전 작업본 |
| 경량화 대상 선정 | ✅ 완료 |
| 케이블 체인 및 구동 벨트 제거 | ⏳ 진행 예정 |
| 세부 부품 정리 | ⏳ 진행 예정 |
| URDF 링크 단위 재분류 | ✅ 완료 |
| 좌표계 정렬 | ⏳ 진행 예정 |
| Joint Origin 및 Stroke 실측 | ⏳ 진행 예정 — 공식 설정값을 임시 기준으로 사용 |
| Visual Mesh 생성 | ⏳ 진행 예정 |
| Collision Mesh 생성 | ⏳ 기본 운동학 검증 완료 후 진행 |
| `cad/measurements.md` 작성 | ⏳ 진행 예정 |
| `voron24_params.xacro` 반영 | ⏳ 진행 예정 |
| Unity Import 및 기본 운동학 검증 | ⏳ 진행 예정 |
| RViz 및 계약 검사 | ⏳ 진행 예정 |

---

## Step 11 — 체크리스트

- [x] 원본 STEP 모델을 확보함
- [x] 전체 어셈블리를 `.FCStd`로 저장함
- [x] 최상위 LinkGroup 단위로 1차 분리함
- [x] 1차 분리 모델을 중간 작업용 `.FCStd`로 저장함
- [x] 우선 경량화 대상을 선정함
- [ ] 케이블 체인을 제거함
- [ ] 구동 벨트를 제거함
- [ ] 케이블, 체결 부품 및 비가시 부품을 정리함
- [x] 최종 URDF 링크 단위로 파츠를 재분류함
- [ ] 전체 좌표계를 계약 기준으로 정렬함
- [ ] 링크별 로컬 원점을 정렬함
- [ ] Joint Origin을 실측함
- [ ] Joint Axis와 Stroke를 실측하고 임시 가동범위와 비교함
- [ ] `cad/measurements.md`를 작성함
- [ ] `voron24_params.xacro`에 실측값을 반영함
- [ ] 시각용 메시를 생성함
- [ ] 기본 운동학 검증 완료 후 충돌용 메시를 생성함
- [ ] Unity에 URDF와 시각용 메시를 불러옴
- [ ] Joint Origin, Axis, Stroke 및 기본 이동범위를 검증함
- [ ] 기본 운동학 검증 완료 후 Collision, Mass, Inertia를 적용함
- [ ] 물리특성 적용 후 Articulation Body 동작과 충돌 안정성을 검증함
- [ ] RViz 동작을 검증함
- [ ] 계약 검사 스크립트를 통과함
- [ ] Git LFS 추적 상태를 확인함

---

## Step 12 — 산출물 요약

| 경로 | 확장자 | 설명 | 소비자 |
|---|---|---|---|
| `cad/source/Voron_2.4r2_Assembly.step` | `.step` | Voron 2.4 원본 전체 어셈블리임 | A |
| `cad/working/voron_2_4.FCStd` | `.FCStd` | 전체 어셈블리 FreeCAD 작업본임 | A |
| `cad/working/base_link.FCStd` | `.FCStd` | 최종 `base_link` 구성 작업본임 | A |
| `cad/working/z_gantry.FCStd` | `.FCStd` | 최종 `z_gantry` 구성 작업본임 | A |
| `cad/working/x_beam.FCStd` | `.FCStd` | 최종 `x_beam` 구성 작업본임 | A |
| `cad/working/toolhead.FCStd` | `.FCStd` | 최종 `toolhead` 구성 작업본임 | A |
| `cad/groups.json` | `.json` | 최종 URDF 링크별 파츠 배정 결과임 | A |
| `cad/inventory.csv` | `.csv` | 파츠 및 형상 인벤토리임 | A |
| ****`cad/measurements.md`**** | `.md` | ****조인트 좌표, 이동 범위 및 측정 근거임**** | ****전원**** |
| ****`urdf/voron24_params.xacro`**** | `.xacro` | ****실측 파라미터 반영 파일임**** | ****B, C**** |
| ****`meshes/visual/*.stl`**** | `.stl` | ****링크별 시각용 메시임**** | ****B, C**** |
| ****`meshes/collision/*.stl`**** | `.stl` | ****링크별 충돌용 메시임**** | ****B, C**** |

> 실제 저장 경로는 저장소의 기존 디렉토리 구조와 인터페이스 계약을 기준으로 조정함. 최상위 LinkGroup별 `.FCStd`는 재분류를 위한 중간 작업본으로 보존할 수 있으나, 최종 협업 기준은 `base_link`, `z_gantry`, `x_beam`, `toolhead`의 4개 링크 구성과 해당 메시·실측값임.

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
git commit -m "feat(cad): Voron 2.4 링크 재구성 작업본 정리"
```

경량화와 링크 분류 후 다음 내용을 커밋함.

```bash
git add cad/groups.json cad/inventory.csv cad/measurements.md
git add cad/scripts/
git commit -m "feat(cad): 4개 핵심 링크 분류 및 조인트 실측값 추가"
```

메시 및 Xacro 반영 후 다음 내용을 커밋함.

```bash
git add ros2_ws/src/voron24_description/meshes/
git add ros2_ws/src/voron24_description/urdf/voron24_params.xacro
git commit -m "feat(cad): Unity URDF용 링크 메시 및 실측 파라미터 반영"
```

현재 작업 브랜치는 다음과 같음.

```text
JSY_0810
```

원격 저장소에 최초 업로드할 때 다음 명령을 사용함.

```bash
git push -u origin JSY_0810
```

---

## 참고 — 스크립트 기반 조립·재배치 가능성 검증

FreeCAD에서 다른 CAD 도구의 Assembly 기능과 유사하게, 부품 또는 링크 단위의 위치·회전을 스크립트로 제어하여 재배치하는 방식이 실현 가능한지 예제를 통해 확인하였음.

이 검증은 현재 프로젝트의 필수 산출물이나 인터페이스 계약을 변경하는 사항은 아니며, 향후 반복적인 링크 재배치, 원점 정렬 및 재조립 검증 작업을 자동화할 수 있는 가능성을 확인한 참고 결과로 기록함.

---

## 참고 - 현재 계획에서 추가로 강조하는 사항

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
- 기본 운동학을 먼저 검증한 후 충돌용 메시와 물리특성을 순차적으로 적용함
- 메시의 Polygon 수와 파일 크기를 제한함
- 최종 물리특성 적용 단계에서는 질량과 관성값을 누락하지 않음
- 실측값과 근거를 `cad/measurements.md`에 기록함
- 최종 실측 파라미터를 `voron24_params.xacro`에 반영함
- Unity와 ROS/RViz에서 동일한 운동학 구조가 재현되는지 확인함
- 원본 모델과 경량화 모델을 별도로 보존함

> ****정리:**** 최상위 LinkGroup 분리는 작업 편의를 위한 1차 분류 단계임. 현재 협업 기준과 최종 CAD 구성은 실제 운동 관계를 반영한 `base_link`, `z_gantry`, `x_beam`, `toolhead`의 4개 핵심 링크이며, 이후 메시 생성·조인트 실측·Unity/URDF 검증도 이 구조를 기준으로 진행함.


### !! 협의 필요 !! — Visual Mesh 형식 변경 제안
현재 프로젝트의 Visual Mesh는 STL 형식을 기준으로 정의되어 있으나, STL은 재질 및 색상 정보를 직접 보존하지 못함. FreeCAD에서 확인한 결과 Unity에서 원본 모델의 색상 정보를 유지하기 위해서는 OBJ와 MTL을 함께 사용하는 방식이 적합함. 따라서 Visual Mesh 형식을 기존 STL에서 OBJ+MTL 조합으로 변경하는 방안을 팀 협의 대상으로 제안함. 본 변경은 Unity 및 URDF 측의 메시 경로·임포트 방식에 영향을 줄 수 있으므로 팀 합의 전까지 기존 인터페이스 계약을 임의로 변경하지 않음. Collision Mesh는 기존 계획대로 단순화된 STL 사용을 유지함.


### !! Visual Mesh 경량화 추가 작업 필요 !!
현재 4개 링크 기준으로 재구성한 OBJ+MTL 모델은 링크 구조와 기본 운동학 검증을 우선하기 위한 작업본임. Unity에서 확인한 결과 일부 Visual Mesh의 Triangle 수가 현재 프로젝트의 메시 예산을 초과할 가능성이 확인되었으며, 예를 들어 base_link의 현재 모델은 약 1,123,419 triangles로 측정되었음. 따라서 현 OBJ+MTL 모델을 최종 경량 Visual Mesh로 간주하지 않으며, 기본 링크 구조와 가동범위 검증 이후 형상 단순화 및 Triangle 수 감소 작업을 추가 수행할 예정임. 경량화 과정에서는 외형 식별성과 조인트 운동 판단에 필요한 형상을 우선 보존하고, 소형 체결부품 및 시각적 기여도가 낮은 세부 형상을 우선 정리함.