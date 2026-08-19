# voron24_description

Voron 2.4 R2 250mm URDF + 메시.

## 파일 소유권 (계약 §4)

| 파일 | 수정 권한 |
|---|---|
| `urdf/voron24_params.xacro` | **A 전용** — 모든 치수의 단일 출처 |
| `urdf/voron24.urdf.xacro` | C 전용 — 링크 트리 |
| `urdf/voron24_macros.xacro` | C 전용 |
| `meshes/` | **A 전용** |
| `launch/`, `rviz/` | C 전용 |

URDF는 한 벌. `use_meshes` 인자로 mock/real 전환.

    xacro voron24.urdf.xacro use_meshes:=false   # 박스
    xacro voron24.urdf.xacro use_meshes:=true    # 실제 메시

수정 후 반드시:

    python3 ../../../tools/contract_check.py --xacro urdf/voron24.urdf.xacro