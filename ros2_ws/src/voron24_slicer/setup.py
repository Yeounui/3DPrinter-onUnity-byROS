from glob import glob
from setuptools import setup

package_name = 'voron24_slicer'

setup(
    name=package_name,
    version='0.1.0',
    packages=[package_name],
    data_files=[
        ('share/ament_index/resource_index/packages', ['resource/' + package_name]),
        ('share/' + package_name, ['package.xml']),
        # 프로파일의 단일 출처는 레포의 tools/slicer/ 다 (04b §294). 여기서는 설치
        # 트리에서도 닿게 share 로 복사만 한다 — profiles.py 의 탐색 순서상 레포
        # 원본이 있으면 그쪽이 이기므로 값이 갈라지지 않는다.
        ('share/' + package_name + '/profiles', glob('../../../tools/slicer/*.ini')),
    ],
    install_requires=['setuptools'],
    zip_safe=True,
    maintainer='voron24 team',
    maintainer_email='team@example.com',
    description='STL -> G-code slicing action server for the Voron 2.4 digital twin',
    license='MIT',
    tests_require=['pytest'],
    entry_points={
        'console_scripts': [
            # sim.launch.py 의 executable_path() 가 lib/<pkg>/<exe> 로 훑는 이름들.
            'slicer_node = voron24_slicer.slicer_node:main',
            'job_starter = voron24_slicer.job_starter_node:main',
        ],
    },
)
