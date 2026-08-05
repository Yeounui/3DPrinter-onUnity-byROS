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
        ],
    },
)
