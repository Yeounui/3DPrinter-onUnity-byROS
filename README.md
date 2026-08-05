# Voron 2.4 Digital Twin

Voron 2.4 (250mm) 3D 프린터의 Unity ↔ ROS2 디지털 트윈 프로젝트.

```
STEP ──▶ 링크별 메시 ──▶ URDF ──▶ Unity (ArticulationBody)
                          ▲            │
                 joint_states│         │/printer/cmd
                          ROS2 (ROS-TCP-Endpoint)
                          ▲
              G-code 플레이어 / Moonraker(실기)
```

## 문서

| 문서 | 대상 | 내용 |
|---|---|---|
| [00_interface_contract.md](docs/00_interface_contract.md) | **전원 필독** | 좌표계, 단위, 링크/조인트 이름, 토픽, 레포 규칙 |
| [01_cad_workflow.md](docs/01_cad_workflow.md) | A | FreeCAD — STEP → 링크 메시 + 조인트 실측 |
| [02_unity_workflow.md](docs/02_unity_workflow.md) | B | Unity — URDF 임포트, 조인트 구동, 압출 시각화 |
| [03_ros2_workflow.md](docs/03_ros2_workflow.md) | C | ROS2 — URDF, G-code 파서, 브릿지, launch |

## 빠른 시작

```bash
git clone <repo> && cd voron24-digital-twin
git lfs install && git lfs pull

cd ros2_ws && colcon build --symlink-install && source install/setup.bash
ros2 launch voron24_bringup mock.launch.py
```

별도로 Unity 프로젝트(`unity/Voron24Twin`)를 열고 Play.

## 팀 규칙

1. **계약 문서(00) 변경은 PR + 3인 승인.**
2. **파일 소유권**: `cad/`와 `meshes/`는 A, `unity/`는 B, `urdf/`·`launch/`·나머지 ROS2 패키지는 C.
3. **Mock 우선**: 상대 산출물을 기다리지 말고 mock으로 진행. 교체는 마지막에.
4. **주 1회 통합 세션**: 마일스톤 게이트를 셋이 함께 확인.

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

Voron 2.4 CAD 원본 [VoronDesign/Voron-2](https://github.com/VoronDesign/Voron-2) (GPLv3). 파생물 배포 시 라이선스 및 상표 정책 확인 필수 — [00 §10](docs/00_interface_contract.md) 참고.
