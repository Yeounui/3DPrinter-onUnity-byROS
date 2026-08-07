# 3DPrinter-onUnity-byROS
 
3D 프린터(Voron 2.4 (250mm))의 Unity ↔ ROS2 디지털 트윈.
 
```
STEP ──▶ 링크별 메시 ──▶ URDF ──▶ Unity (ArticulationBody)
                          ▲            │
                 joint_states│         │/printer/cmd
                          ROS2 (ROS-TCP-Endpoint)
                          ▲
                 G-code 플레이어
```
 
## 문서
 
| 문서 | 대상 | 내용 |
|---|---|---|
| [00_interface_contract.md](docs/00_interface_contract.md) | **전원 필독** | 좌표계, 단위, 링크/조인트 이름, 토픽, 소유권, 검증 |
| [01_cad_workflow.md](docs/01_cad_workflow.md) | A | FreeCAD — STEP → 링크 메시 + 조인트 실측 |
| [02_unity_workflow.md](docs/02_unity_workflow.md) | B | Unity — URDF 임포트, 조인트 구동, 압출 시각화 |
| [03_ros2_workflow.md](docs/03_ros2_workflow.md) | C | ROS2 — URDF, mock/G-code 노드, 브릿지, launch |
 
## 빠른 시작
 
```bash
git clone <repo> && cd 3DPrinter-onUnity-byROS
git lfs install && git lfs pull
 
cd ros2_ws && colcon build --symlink-install && source install/setup.bash
ros2 launch voron24_bringup mock.launch.py
```
 
별도로 Unity 프로젝트(`unity/Voron24Twin`)를 열고 Play.
 
```bash
# 축 방향 검증
ros2 launch voron24_bringup mock.launch.py pattern:=sweep
# A의 메시 적용
ros2 launch voron24_bringup mock.launch.py use_meshes:=true
```
 
## 검증
 
```bash
# 계약 위반 자동 검출 (커밋 전 필수)
python3 tools/contract_check.py --xacro ros2_ws/src/voron24_description/urdf/voron24.urdf.xacro
 
# 통합 게이트
bash tools/smoke_test.sh
 
# 궤적 로직 단독 테스트 (ROS 불요)
cd ros2_ws/src/voron24_gcode && python3 -m voron24_gcode.patterns
```
 
## 팀 규칙
 
1. **계약 문서(00) 변경은 PR + 3인 승인.** 여기가 흔들리면 병렬 작업 붕괴
2. **파일 소유권** (계약 §4): A는 `voron24_params.xacro` + `meshes/`, B는 `unity/`, C는 URDF 본문 + launch + 나머지 ROS2 패키지
3. **Mock 우선**: 상대 산출물 대기 금지. 교체는 마지막에
4. **커밋 전 `contract_check.py`** — pre-commit 훅 등록 권장

## 대상 기종 요약
 
Voron 2.4 R2 / 250mm — **CoreXY + 플라잉 갠트리**. 베드 고정, 갠트리가 Z로 승강.
 
```
base_link (프레임 + 베드 + 전장)
└── z_gantry   prismatic Z
    └── x_beam prismatic Y
        └── toolhead prismatic X
            └── nozzle (fixed, TCP)
bed_origin (fixed to base_link) ← G-code (0,0,0)
```
 
## 라이선스
 
Voron 2.4 CAD 원본은 [VoronDesign/Voron-2](https://github.com/VoronDesign/Voron-2) (GPLv3).
파생물 배포 시 라이선스 및 상표 정책 확인 필수 — [00 §12](docs/00_interface_contract.md) 참조.