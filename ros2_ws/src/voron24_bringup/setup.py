from glob import glob
from setuptools import setup

package_name = 'voron24_bringup'

setup(
    name=package_name,
    version='0.1.0',
    packages=[package_name],
    data_files=[
        ('share/ament_index/resource_index/packages', ['resource/' + package_name]),
        ('share/' + package_name, ['package.xml']),
        ('share/' + package_name + '/launch', glob('launch/*.launch.py')),
    ],
    install_requires=['setuptools'],
    zip_safe=True,
    maintainer='voron24 team',
    maintainer_email='team@example.com',
    description='Integrated launch files for the Voron 2.4 digital twin',
    license='MIT',
    entry_points={'console_scripts': [
        # sim.launch.py 의 STATE_NODE_CANDIDATES 가 이 이름으로 설치 트리를 훑음.
        'printer_state_node = voron24_bringup.printer_state_node:main',
    ]},
)
