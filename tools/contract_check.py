#!/usr/bin/env python3
"""
contract_check.py
=================
URDF 계약(docs/00_interface_contract.md §3) 준수 자동 검증.

3인 병렬 작업 시 링크 이름·축이 조용히 어긋남.
CI/커밋 훅 게이트로 즉시 검출.

사용:
    # xacro 확장 후 검사
    python3 tools/contract_check.py --xacro ros2_ws/src/voron24_description/urdf/voron24.urdf.xacro
    python3 tools/contract_check.py --xacro .../voron24.urdf.xacro --use-meshes

    # 확장 완료 urdf 검사
    python3 tools/contract_check.py --urdf /tmp/voron24.urdf

    # 메시 파일 존재/예산 검사
    python3 tools/contract_check.py --xacro ... --use-meshes --check-meshes

종료 코드 0 = 통과, 1 = 위반.
"""
import argparse
import os
import struct
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

# ======================================================================
# 계약 정의 — docs/00_interface_contract.md §3 동기화 필수
# ======================================================================
REQUIRED_LINKS = ['base_link', 'z_gantry', 'x_beam', 'toolhead', 'nozzle', 'bed_origin']

REQUIRED_JOINTS = {
    #  name              type          parent       child         axis
    'joint_z':          ('prismatic', 'base_link', 'z_gantry',   (0, 0, 1)),
    'joint_y':          ('prismatic', 'z_gantry',  'x_beam',     (0, 1, 0)),
    'joint_x':          ('prismatic', 'x_beam',    'toolhead',   (1, 0, 0)),
    'joint_nozzle':     ('fixed',     'toolhead',  'nozzle',     None),
    'joint_bed_origin': ('fixed',     'base_link', 'bed_origin', None),
}

OPTIONAL_JOINTS = {'joint_e', 'joint_a', 'joint_b'}

STROKE_MIN = 0.200      # m. 250mm 기종이므로 이보다 작으면 의심
STROKE_MAX = 0.400

TRIANGLE_BUDGET = {
    'base_link': 120_000,
    'z_gantry': 60_000,
    'x_beam': 30_000,
    'toolhead': 40_000,
}
TOTAL_BUDGET = 250_000

# 형상 불필요 링크 (프레임 전용)
FRAME_ONLY_LINKS = {'nozzle', 'bed_origin'}


class Report:
    def __init__(self):
        self.errors = []
        self.warnings = []
        self.info = []

    def err(self, m):
        self.errors.append(m)

    def warn(self, m):
        self.warnings.append(m)

    def note(self, m):
        self.info.append(m)

    def dump(self):
        for m in self.info:
            print(f'  ·  {m}')
        for m in self.warnings:
            print(f'  !  WARN  {m}')
        for m in self.errors:
            print(f'  X  ERROR {m}')
        print()
        if self.errors:
            print(f'FAILED — 위반 {len(self.errors)}건, 경고 {len(self.warnings)}건')
            return False
        print(f'PASSED — 경고 {len(self.warnings)}건')
        return True


def expand_xacro(path, use_meshes):
    """xacro 확장. ros2 환경 시 xacro CLI, 미설치 시 python 모듈."""
    args = [path, f'use_meshes:={"true" if use_meshes else "false"}']
    try:
        out = subprocess.run(['xacro'] + args, capture_output=True, text=True, check=True)
        return out.stdout
    except (FileNotFoundError, subprocess.CalledProcessError) as e:
        if isinstance(e, subprocess.CalledProcessError):
            print(e.stderr, file=sys.stderr)
            sys.exit(2)
    try:
        import xacro  # noqa
    except ImportError:
        print('xacro 미발견. ROS2 환경 source 또는 pip install xacro 필요',
              file=sys.stderr)
        sys.exit(2)
    with tempfile.NamedTemporaryFile('w+', suffix='.urdf', delete=False) as f:
        tmp = f.name
    saved = sys.argv
    sys.argv = ['xacro'] + args + ['-o', tmp]
    try:
        xacro.main()
    finally:
        sys.argv = saved
    return open(tmp).read()


def parse_xyz(node, attr='xyz', default=(0.0, 0.0, 0.0)):
    if node is None or node.get(attr) is None:
        return default
    return tuple(float(v) for v in node.get(attr).split())


def stl_triangle_count(path):
    """바이너리/ASCII STL 삼각형 수."""
    size = os.path.getsize(path)
    with open(path, 'rb') as f:
        head = f.read(84)
        if len(head) < 84:
            return 0
        n = struct.unpack('<I', head[80:84])[0]
        if 84 + n * 50 == size:          # 바이너리
            return n
    with open(path, 'r', errors='ignore') as f:
        return sum(1 for line in f if line.lstrip().startswith('facet normal'))


def check(urdf_text, rep, check_meshes=False, mesh_root=None):
    root = ET.fromstring(urdf_text)

    name = root.get('name')
    if name != 'voron24':
        rep.warn(f'robot name "voron24" 아님: {name!r}')

    links = {l.get('name'): l for l in root.findall('link')}
    joints = {j.get('name'): j for j in root.findall('joint')}

    # ---- 링크 ----
    for ln in REQUIRED_LINKS:
        if ln not in links:
            rep.err(f'필수 링크 누락: {ln}')
    extra = set(links) - set(REQUIRED_LINKS)
    extra = {e for e in extra if not e.startswith('door_')}
    if extra:
        rep.note(f'추가 링크: {sorted(extra)}')

    # ---- 조인트 ----
    for jn, (jtype, parent, child, axis) in REQUIRED_JOINTS.items():
        j = joints.get(jn)
        if j is None:
            rep.err(f'필수 조인트 누락: {jn}')
            continue
        if j.get('type') != jtype:
            rep.err(f'{jn}: type {jtype} 필요, 현재 {j.get("type")}')
        p = j.find('parent')
        c = j.find('child')
        if p is None or p.get('link') != parent:
            rep.err(f'{jn}: parent {parent} 필요, 현재 '
                    f'{p.get("link") if p is not None else None}')
        if c is None or c.get('link') != child:
            rep.err(f'{jn}: child {child} 필요, 현재 '
                    f'{c.get("link") if c is not None else None}')
        if axis is not None:
            a = parse_xyz(j.find('axis'), default=(1.0, 0.0, 0.0))
            if tuple(round(v) for v in a) != axis:
                rep.err(f'{jn}: axis {axis} 필요, 현재 {a}')

    unknown = set(joints) - set(REQUIRED_JOINTS) - OPTIONAL_JOINTS
    unknown = {u for u in unknown if not u.startswith('joint_door_')}
    if unknown:
        rep.warn(f'계약 미정의 조인트: {sorted(unknown)} — 계약 문서 갱신 필요?')

    # ---- 베드는 base_link 에 고정이어야 함 (Voron 2.4 플라잉 갠트리) ----
    jbo = joints.get('joint_bed_origin')
    if jbo is not None:
        p = jbo.find('parent')
        if p is not None and p.get('link') != 'base_link':
            rep.err('bed_origin parent 가 base_link 아님. '
                    'Voron 2.4 베드 고정 (베드슬링어 혼동 주의)')

    # ---- 스트로크 리밋 ----
    for jn in ('joint_x', 'joint_y', 'joint_z'):
        j = joints.get(jn)
        if j is None:
            continue
        lim = j.find('limit')
        if lim is None:
            rep.err(f'{jn}: <limit> 누락 (prismatic 필수)')
            continue
        lo, hi = float(lim.get('lower', 0)), float(lim.get('upper', 0))
        if abs(lo) > 1e-9:
            rep.warn(f'{jn}: lower 0 아님 ({lo}). '
                     'G-code 좌표 직접 사용 시 0 필수')
        if not (STROKE_MIN <= hi <= STROKE_MAX):
            rep.warn(f'{jn}: upper={hi} 예상 범위 [{STROKE_MIN}, {STROKE_MAX}] 외')
        for req in ('effort', 'velocity'):
            if lim.get(req) is None:
                rep.err(f'{jn}: <limit> 에 {req} 누락')

    # ---- inertial (Unity ArticulationBody 필수) ----
    for ln, l in links.items():
        if ln in FRAME_ONLY_LINKS:
            continue
        inert = l.find('inertial')
        if inert is None:
            rep.err(f'{ln}: <inertial> 누락 → Unity mass=0 으로 시뮬 폭발')
            continue
        m = inert.find('mass')
        if m is None or float(m.get('value', 0)) <= 0:
            rep.err(f'{ln}: mass 0 이하')
        i = inert.find('inertia')
        if i is None:
            rep.err(f'{ln}: <inertia> 누락')
        else:
            for k in ('ixx', 'iyy', 'izz'):
                if float(i.get(k, 0)) <= 0:
                    rep.err(f'{ln}: inertia {k} 0 이하')

    # ---- 형상 ----
    for ln, l in links.items():
        if ln in FRAME_ONLY_LINKS:
            if l.find('visual') is not None:
                rep.warn(f'{ln}: 프레임 전용 링크에 <visual> 존재')
            continue
        if l.find('visual') is None:
            rep.err(f'{ln}: <visual> 누락')
        if l.find('collision') is None:
            rep.warn(f'{ln}: <collision> 누락 (물리 시뮬 필요)')

    # ---- 메시 ----
    meshes = root.findall('.//mesh')
    if meshes:
        rep.note(f'메시 참조 {len(meshes)}개')
        for m in meshes:
            sc = m.get('scale')
            if sc is None:
                rep.warn(f'{m.get("filename")}: scale 없음. '
                         'mm STL 이면 0.001 필요 (계약 §2)')
            elif tuple(float(v) for v in sc.split()) != (0.001, 0.001, 0.001):
                rep.warn(f'{m.get("filename")}: scale={sc} — mm 메시 시 0.001 필요')

        if check_meshes:
            total = 0
            for m in meshes:
                fn = m.get('filename', '')
                rel = fn.replace('package://voron24_description/', '')
                path = os.path.join(mesh_root, rel) if mesh_root else None
                if not path or not os.path.isfile(path):
                    rep.err(f'메시 파일 없음: {fn}')
                    continue
                n = stl_triangle_count(path)
                base = os.path.splitext(os.path.basename(path))[0]
                if '/visual/' in path.replace(os.sep, '/'):
                    total += n
                    budget = TRIANGLE_BUDGET.get(base)
                    if budget and n > budget:
                        rep.err(f'{base}.stl: {n:,} tri > 예산 {budget:,}')
                    else:
                        rep.note(f'{base}.stl (visual): {n:,} tri')
            if total > TOTAL_BUDGET:
                rep.err(f'visual 총 {total:,} tri > 예산 {TOTAL_BUDGET:,}')
            elif total:
                rep.note(f'visual 총계: {total:,} / {TOTAL_BUDGET:,} tri')
    else:
        rep.note('mock 모드 (메시 참조 없음)')

    # ---- 트리 무결성 ----
    children = {j.find('child').get('link') for j in joints.values()
                if j.find('child') is not None}
    roots = set(links) - children
    if roots != {'base_link'}:
        rep.err(f'루트 링크 base_link 단일이어야 함, 현재: {sorted(roots)}')


def main():
    ap = argparse.ArgumentParser()
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument('--xacro')
    g.add_argument('--urdf')
    ap.add_argument('--use-meshes', action='store_true')
    ap.add_argument('--check-meshes', action='store_true',
                    help='메시 파일 존재·삼각형 예산 검사')
    ap.add_argument('--mesh-root', default=None,
                    help='voron24_description 패키지 경로 (기본: xacro 파일 기준 추정)')
    a = ap.parse_args()

    if a.xacro:
        text = expand_xacro(a.xacro, a.use_meshes)
        mesh_root = a.mesh_root or os.path.dirname(os.path.dirname(os.path.abspath(a.xacro)))
        src = a.xacro
    else:
        text = open(a.urdf).read()
        mesh_root = a.mesh_root
        src = a.urdf

    print(f'\n계약 검증: {src}'
          f'{"  (use_meshes)" if a.use_meshes else "  (mock)"}\n')
    rep = Report()
    check(text, rep, check_meshes=a.check_meshes, mesh_root=mesh_root)
    sys.exit(0 if rep.dump() else 1)


if __name__ == '__main__':
    main()
