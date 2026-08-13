from setuptools import setup

package_name = 'voron24_gcode'

setup(
    name=package_name,
    version='0.1.0',
    packages=[package_name],
    data_files=[
        ('share/ament_index/resource_index/packages', ['resource/' + package_name]),
        ('share/' + package_name, ['package.xml']),
    ],
    install_requires=['setuptools'],
    zip_safe=True,
    maintainer='voron24 team',
    maintainer_email='team@example.com',
    description='Mock publisher and G-code playback for the Voron 2.4 digital twin',
    license='MIT',
    tests_require=['pytest'],
    entry_points={
        'console_scripts': [
            'mock_publisher = voron24_gcode.mock_publisher_node:main',
            # manual_publisher_node.py 는 A 담당. 파일이 들어오기 전까지 이 엔트리는
            # 빌드는 통과하지만 실행 시 ImportError (04b 선행 커밋).
            'manual_publisher = voron24_gcode.manual_publisher_node:main',
        ],
    },
)
