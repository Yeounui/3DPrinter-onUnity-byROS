#!/usr/bin/env python3
"""
cad_export_meshes.py
====================
`cad/working/` CAD 원자재를 URDF 링크 메시로 변환.

    cad/working/visual/*.obj          →  meshes/visual/*.stl      (경량화 + 재앵커)
    cad/working/collider (stl)/*.stl  →  meshes/collision/*.stl   (재앵커)

CAD 갱신 시 재실행. 일회용 스크립트 아님.

    python3 tools/cad_export_meshes.py            # 실제 기록
    python3 tools/cad_export_meshes.py --dry-run  # 통계만 출력

────────────────────────────────────────────────────────────────────────
1. 변환 필요 이유 — 원점 문제
────────────────────────────────────────────────────────────────────────
계약 §5-3: "메시 원점 = 해당 링크의 조인트 축 위치."
`voron24_macros.xacro` <mesh> 가 <origin xyz="0 0 0"> 고정이라
URDF 측 상쇄 불가. → **메시를 링크 원점으로 bake 후 내보냄.**

원자재 좌표계는 부품마다 상이 (부품별 CAD 로컬 원점).
링크 프레임 변환은 Unity `Voron24Twin.unity` 씬 **확정 영점**
(커밋 a58a03a, 2026-08-18) 에서 역산. `voron24_params.xacro` 조인트
origin 출처가 동일 씬이므로 메시도 같은 출처 사용 필수.

역산 절차 (OBJ_TO_LINK 상수표):
  1. 씬에서 각 링크 월드 포즈 T_link, 하위 `Visuals` 로컬 포즈 T_vis 취득.
  2. Unity→ROS 씬 고유 매핑 `ros = (-Ux, Uz, Uy)`.
     (검증: bed_origin·nozzle·toolhead 이 매핑 적용 시 params 값 일치)
  3. v_link_mm = M · R_link · R_vis · v_obj_mm  +  1000 · M · R_link · t_vis
     (Visuals 의 scale 0.001 과 mm 환산 1000 이 서로 상쇄됨)

────────────────────────────────────────────────────────────────────────
2. 변환식 거울상(det = -1) 이유 — 의도적 유지
────────────────────────────────────────────────────────────────────────
Unity OBJ 임포터 X 반전 + 씬 조립 시 위치 측 재반전
보정. 결과적으로 씬 형상은 **원본 CAD Y 거울상**이고, 확정 영점·
`voron24_params.xacro` 모두 거울상 기준 측정.

det = +1 변환(순수 평행이동)도 바운딩박스는 일치하나,
**노즐 팁이 URDF `nozzle` 링크에서 Y 19.5mm 오차.** 실측:

    toolhead.obj 최하단(노즐 팁) 정점 평균 = (127.00, 131.00, 60.74)
      거울상 변환(채택)  → (12.5, -18.5, -62.76)
      순수 평행이동      → (12.5, -37.87, -62.76)
      params 의 noz      → (12.6, -18.4, -62.80)

TCP 19.5mm 오차가 훨씬 치명적이고 `voron24_params.xacro` 는 A 소유라
수정 불가. **거울상 유지로 씬·params 일관성 확보.**
det < 0 시 삼각형 감김 반전 → 정점 순서 복원으로 법선 외향 유지.

※ 잔여 문제: ROS 모델 전체가 실물 Voron 앞뒤 거울상. 운동학에는 영향
  없으나 (프레임 거의 대칭) 도어/전장 베이 방향 반전. 보정 시
  `noz_y` 등 params 재측정 필요 — A 결정 사항.

────────────────────────────────────────────────────────────────────────
3. 콜리전 메시
────────────────────────────────────────────────────────────────────────
콜리전 STL 원자재는 별도 로컬 원점 사용, 위 거울상 **미적용**
CAD 원본 방향. 저해상도 셸이라 형상 대응점 미확보,
**바운딩박스 중심 visual 정렬 후**, visual 동일 방향 유지를 위해 링크
프레임 한 축 거울반전. 반전축은 변환행렬 소스 Y축 매핑 대상으로
결정 (base/z_gantry/toolhead = Y, x_beam = X).

매 실행 10mm 복셀 점유 일치율로 재확인, 결과 출력.
CAD 변경 시 방향 관계 이상은 수치로 확인 가능.
"""
import argparse
import os
import struct
import sys

import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC_VISUAL = os.path.join(ROOT, 'cad', 'working', 'visual')
SRC_COLLIDER = os.path.join(ROOT, 'cad', 'working', 'collider (stl)')
DST = os.path.join(ROOT, 'ros2_ws', 'src', 'voron24_description', 'meshes')

LINKS = ['base_link', 'z_gantry', 'x_beam', 'toolhead']

# 계약 §5-4 visual 삼각형 예산. contract_check.py TRIANGLE_BUDGET 과 동기화 필수.
TRIANGLE_BUDGET = {'base_link': 120_000, 'z_gantry': 60_000,
                   'x_beam': 30_000, 'toolhead': 40_000}
# 예산 꽉 채우면 CAD 소폭 증가에도 게이트 실패. 8% 여유.
BUDGET_MARGIN = 0.92

# OBJ 로컬 → 링크 프레임 (mm). v_link = A @ v_obj + b. 전부 det = -1 (§2 참고)
_YFLIP = np.diag([1.0, -1.0, 1.0])
_SWAP_XY = np.array([[0., 1., 0.], [1., 0., 0.], [0., 0., 1.]])
OBJ_TO_LINK = {
    'base_link': (_YFLIP,   np.array([-127.01003,  166.99003,  143.10604])),
    'z_gantry':  (_YFLIP,   np.array([-127.00000,  166.96487,  -54.01054])),
    'x_beam':    (_SWAP_XY, np.array([162.99996, -180.71706,  -19.69446])),
    'toolhead':  (_YFLIP,   np.array([-114.50001,  112.50001, -123.50000])),
}

# x_beam_2.stl 은 x_beam.stl 재내보내기 중복본 (정점 최대 오차 4.6e-05 mm).
# 계약 §3 링크 x_beam 하나뿐이므로 x_beam.stl 만 사용.
COLLIDER_SRC = {'base_link': 'base_link.stl', 'z_gantry': 'z_gantry.stl',
                'x_beam': 'x_beam.stl', 'toolhead': 'toolhead.stl'}

VOXEL = 10.0    # mm. 콜리전 방향 검증용 점유 격자


# ======================================================================
# I/O
# ======================================================================
def read_obj(path):
    """OBJ 정점·삼각형만 추출. 재질/법선/UV 는 STL 미지원으로 폐기."""
    verts = []
    faces = []
    with open(path, 'rb') as f:
        for line in f:
            if line[:2] == b'v ':
                a = line.split()
                verts.append((float(a[1]), float(a[2]), float(a[3])))
            elif line[:2] == b'f ':
                idx = [int(tok.split(b'/', 1)[0]) for tok in line.split()[1:]]
                idx = [i - 1 if i > 0 else len(verts) + i for i in idx]
                for k in range(1, len(idx) - 1):      # 다각형 → 팬 삼각분할
                    faces.append((idx[0], idx[k], idx[k + 1]))
    return np.asarray(verts, np.float64), np.asarray(faces, np.int64)


_STL_DT = np.dtype({'names': ['n', 'v', 'a'],
                    'formats': ['<3f4', '<(3,3)f4', '<u2'],
                    'offsets': [0, 12, 48], 'itemsize': 50})


def read_stl(path):
    """바이너리 STL → (T,3,3) 삼각형 배열."""
    data = open(path, 'rb').read()
    n = struct.unpack('<I', data[80:84])[0]
    if 84 + n * 50 != len(data):
        raise ValueError(f'{path}: 바이너리 STL 아님 또는 손상')
    return np.frombuffer(data, dtype=_STL_DT, count=n, offset=84)['v'].astype(np.float64)


def write_stl(path, tri, header=b''):
    """(T,3,3) 삼각형 배열 → 바이너리 STL. 법선은 정점에서 재계산."""
    tri = np.ascontiguousarray(tri, np.float64)
    nrm = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
    ln = np.linalg.norm(nrm, axis=1)
    nrm[ln > 0] /= ln[ln > 0, None]
    rec = np.zeros(len(tri), dtype=_STL_DT)
    rec['n'] = nrm
    rec['v'] = tri
    with open(path, 'wb') as f:
        f.write(header.ljust(80, b'\0')[:80])
        f.write(struct.pack('<I', len(tri)))
        f.write(rec.tobytes())


# ======================================================================
# 경량화 — 정점 클러스터링
# ======================================================================
def cluster_decimate(verts, faces, cell):
    """격자 cell(mm) 정점 클러스터링 → 셀 무게중심 대체.

    Garland QEM 대비 품질 열세이나 numpy 단독 실행 가능,
    각진 형상은 셀 < 재료 두께면 형태 보존.
    """
    key = np.floor((verts - verts.min(0)) / cell).astype(np.int64)
    _, inv = np.unique(key, axis=0, return_inverse=True)
    inv = inv.ravel()
    m = inv.max() + 1
    rep = np.stack([np.bincount(inv, verts[:, k], m) for k in range(3)], 1)
    rep /= np.bincount(inv, minlength=m)[:, None]

    nf = inv[faces]
    keep = (nf[:, 0] != nf[:, 1]) & (nf[:, 1] != nf[:, 2]) & (nf[:, 0] != nf[:, 2])
    nf = nf[keep]
    # 동일 셀 조합 삼각형 중복 제거 (감김 방향 무시)
    _, first = np.unique(np.sort(nf, axis=1), axis=0, return_index=True)
    return rep, nf[np.sort(first)]


def decimate_to_budget(verts, faces, target):
    """목표 삼각형 수 이하 최소 격자를 로그스케일 이분탐색으로 결정."""
    if len(faces) <= target:
        return verts, faces, 0.0
    span = float(np.ptp(verts, axis=0).max())
    lo, hi = span / 4096.0, span / 4.0
    best = None
    for _ in range(24):
        mid = (lo * hi) ** 0.5
        v, f = cluster_decimate(verts, faces, mid)
        if len(f) <= target:
            best = (v, f, mid)
            hi = mid
        else:
            lo = mid
        if hi / lo < 1.02:
            break
    if best is None:
        best = cluster_decimate(verts, faces, hi) + (hi,)
    return best


# ======================================================================
def bbox_str(pts):
    lo, hi = pts.min(0), pts.max(0)
    return (f'x[{lo[0]:8.2f},{hi[0]:8.2f}] y[{lo[1]:8.2f},{hi[1]:8.2f}] '
            f'z[{lo[2]:8.2f},{hi[2]:8.2f}]  ext=({hi[0]-lo[0]:6.1f},'
            f'{hi[1]-lo[1]:6.1f},{hi[2]-lo[2]:6.1f})')


def occupancy(pts, cell=VOXEL):
    return set(map(tuple, np.floor(pts / cell).astype(np.int64)))


def fit_score(pts, occ, cell=VOXEL):
    k = np.floor(pts / cell).astype(np.int64)
    return float(np.mean([tuple(r) in occ for r in k]))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--dry-run', action='store_true', help='파일 미기록, 통계만 출력')
    ap.add_argument('--only', nargs='*', default=LINKS, help='특정 링크만 처리')
    args = ap.parse_args()

    if not args.dry_run:
        os.makedirs(os.path.join(DST, 'visual'), exist_ok=True)
        os.makedirs(os.path.join(DST, 'collision'), exist_ok=True)

    total_tri = 0
    vis_pts = {}

    print('\n=== visual (OBJ → 경량화 → 링크 프레임 STL, mm) ===')
    for link in args.only:
        A, b = OBJ_TO_LINK[link]
        v, f = read_obj(os.path.join(SRC_VISUAL, f'{link}.obj'))
        raw = len(f)
        v = v @ A.T + b                                   # 링크 프레임 bake
        if np.linalg.det(A) < 0:
            f = f[:, ::-1]                                # 거울상 → 감김 복원
        v, f, cell = decimate_to_budget(v, f, int(TRIANGLE_BUDGET[link] * BUDGET_MARGIN))
        tri = v[f]
        total_tri += len(tri)
        vis_pts[link] = tri.reshape(-1, 3)
        if not args.dry_run:
            write_stl(os.path.join(DST, 'visual', f'{link}.stl'), tri,
                      f'voron24 {link} visual (mm)'.encode())
        print(f'{link:10s} {raw:9,} → {len(tri):8,} tri  '
              f'(예산 {TRIANGLE_BUDGET[link]:,}, 격자 {cell:.2f}mm, '
              f'{len(tri) * 50 + 84:,}B)')
        print(f'{"":10s} {bbox_str(tri.reshape(-1, 3))}')
    print(f'{"합계":10s} visual {total_tri:,} tri / 예산 250,000')

    print('\n=== collision (STL → 링크 프레임 재앵커, mm) ===')
    for link in args.only:
        tri = read_stl(os.path.join(SRC_COLLIDER, COLLIDER_SRC[link]))
        pts = tri.reshape(-1, 3)
        lo, hi = pts.min(0), pts.max(0)

        if link in vis_pts:
            vp = vis_pts[link]
            vlo, vhi = vp.min(0), vp.max(0)
            tri = tri + ((vlo + vhi) / 2 - (lo + hi) / 2)     # 바운딩박스 중심 정렬
            d = np.abs((hi - lo) - (vhi - vlo))
            if d.max() > 15.0:
                print(f'  !  WARN {link}: visual 축 크기 차 {d.round(1)} mm '
                      '— 축 대응 오류 가능')
            elif d.max() > 2.0:
                print(f'  ·  {link}: 콜리전 셸 visual 대비 {d.round(1)} mm 축소 (단순화)')

            # visual 동일 방향 확인: 변환행렬 소스 Y축 대상축에서 거울반전
            axis = int(np.argmax(np.abs(OBJ_TO_LINK[link][0] @ np.array([0., 1., 0.]))))
            occ = occupancy(vp)
            c = (tri.reshape(-1, 3).min(0) + tri.reshape(-1, 3).max(0)) / 2
            mir = tri.copy()
            mir[:, :, axis] = 2 * c[axis] - mir[:, :, axis]
            s0 = fit_score(tri.reshape(-1, 3), occ)
            s1 = fit_score(mir.reshape(-1, 3), occ)
            print(f'  ·  {link}: 점유 일치율 원본 {s0 * 100:.1f}% / '
                  f'{"xyz"[axis]}거울 {s1 * 100:.1f}% → '
                  f'{"거울 채택" if s1 > s0 else "원본 채택"}')
            if s1 > s0:
                tri = mir[:, ::-1]                          # 거울 → 감김 복원

        if not args.dry_run:
            write_stl(os.path.join(DST, 'collision', f'{link}.stl'), tri,
                      f'voron24 {link} collision (mm)'.encode())
        print(f'{link:10s} {len(tri):6,} tri  {bbox_str(tri.reshape(-1, 3))}')

    if args.dry_run:
        print('\n(dry-run — 파일 미기록)')
    return 0


if __name__ == '__main__':
    sys.exit(main())
