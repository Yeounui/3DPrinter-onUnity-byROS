#!/usr/bin/env python3
"""
profiles.py
===========
슬라이서 실행파일과 .ini 프로파일을 찾는 순수 파이썬 헬퍼. rclpy import 없이
ROS 없이 단독 실행 가능 (python3 -m voron24_slicer.profiles).

    patterns.py / corexy.py 가 rclpy 없이 단독 테스트되는 관례 그대로.

프로파일 단일 출처는 레포의 tools/slicer/ (04b §294). 설치 트리에서도 닿게
setup.py data_files 가 share 로 복사하지만, 어느 쪽이 깔려 있든 레포 원본이 이기도록
탐색 순서를 아래처럼 고정.
"""
import os

# apt prusa-slicer, 공식 AppImage, 윈도우 콘솔 빌드까지 같은 CLI.
# 04b §283 이 apt prusa-slicer 를 결정으로 못박았고 나머지는 폴백.
SLICER_CANDIDATES = ('prusa-slicer', 'prusa-slicer-console', 'PrusaSlicer',
                     'PrusaSlicer-console', 'prusaslicer')

DEFAULT_PROFILE = 'voron24_250'          # 04b §270 의 goal 기본값

# 환경변수 탈출구. CI 나 AppImage 사용자가 소스를 안 고쳐도 되게.
ENV_SLICER = 'VORON24_SLICER'
ENV_PROFILE_DIR = 'VORON24_SLICER_PROFILE_DIR'


def find_slicer(explicit=''):
    """슬라이서 실행파일 절대경로. 못 찾으면 None.

    explicit → $VORON24_SLICER → PATH 순. 하드코딩 금지 이유는 04b §281.
    """
    for candidate in (explicit, os.environ.get(ENV_SLICER, '')):
        if not candidate:
            continue
        if os.path.isabs(candidate):
            return candidate if os.access(candidate, os.X_OK) else None
        found = _which(candidate)
        if found:
            return found
    for name in SLICER_CANDIDATES:
        found = _which(name)
        if found:
            return found
    return None


def _which(name):
    import shutil
    return shutil.which(name)


def profile_dirs(explicit=''):
    """프로파일 .ini 를 찾을 디렉터리 목록. 앞이 이김."""
    dirs = []
    for candidate in (explicit, os.environ.get(ENV_PROFILE_DIR, '')):
        if candidate:
            dirs.append(candidate)
    repo = repo_profile_dir()
    if repo:
        dirs.append(repo)
    share = share_profile_dir()
    if share:
        dirs.append(share)
    return dirs


def repo_profile_dir():
    """레포 트리의 tools/slicer. 못 찾으면 None.

    sim.launch.py find_unity_player() 와 같은 관용구 — realpath 로 심볼릭 링크
    풀고 위로 올라가며 찾음. --symlink-install 로 깔린 site-packages 에서
    출발해도 소스 트리에 닿음.
    """
    here = os.path.realpath(__file__)
    for _ in range(10):
        parent = os.path.dirname(here)
        if parent == here:
            break
        here = parent
        path = os.path.join(here, 'tools', 'slicer')
        if os.path.isdir(path):
            return path
    return None


def share_profile_dir():
    """설치 트리의 share/voron24_slicer/profiles. ament 인덱스 없으면 None."""
    try:
        from ament_index_python.packages import get_package_share_directory
        path = os.path.join(get_package_share_directory('voron24_slicer'), 'profiles')
    except Exception:
        return None
    return path if os.path.isdir(path) else None


def resolve_profile(name='', explicit_dir=''):
    """프로파일 이름 → .ini 절대경로. 못 찾으면 None.

    이름 대신 .ini 절대경로를 그대로 줘도 받음. Unity 나 CLI 가 임시 프로파일
    던지는 경우를 막을 이유 없음.
    """
    name = name or DEFAULT_PROFILE
    if name.endswith('.ini') and os.path.isabs(name):
        return name if os.path.isfile(name) else None
    stem = name[:-4] if name.endswith('.ini') else name
    for directory in profile_dirs(explicit_dir):
        path = os.path.join(directory, stem + '.ini')
        if os.path.isfile(path):
            return path
    return None


def missing_slicer_message():
    """슬라이서 없을 때 사용자에게 그대로 보여줄 한 덩어리.

    launch 를 죽이지 않고 여기서 설치 안내를 내보내는 게 이 레포의 태도
    (sim.launch.py 모듈 docstring "빠진 부품에 대한 태도").
    """
    return ('Cannot find slicer executable ({}). '
            'Retry after `sudo apt install prusa-slicer`, '
            'Set environmental variable `{}=/abs/PrusaSlicer.AppImage` or designate the node parameter '
            '`slicer_executable` to the path if you use AppImage of the slicer. (04b §283)'
            .format(' | '.join(SLICER_CANDIDATES), ENV_SLICER))


def _main():
    slicer = find_slicer()
    print('slicer      :', slicer or '(없음)')
    if slicer is None:
        print('             ', missing_slicer_message())
    print('profile dirs:', profile_dirs() or '(없음)')
    for name in (DEFAULT_PROFILE,):
        print('profile {:<12}: {}'.format(name, resolve_profile(name) or '(없음)'))


if __name__ == '__main__':
    _main()
