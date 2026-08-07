# voron24_msgs 생성 후 활성화할 스크립트

이 폴더의 `.cs.txt` 파일들은 **커스텀 메시지의 C# 클래스가 생성되기 전까지 컴파일되면 안 되므로**
확장자를 막아둔 상태입니다. (`RosMessageTypes.Voron24` 네임스페이스가 없으면 컴파일 에러)

## 활성화 절차

1. C 가 `voron24_msgs` 를 커밋 & 빌드했는지 확인
   ```bash
   colcon build --packages-select voron24_msgs && source install/setup.bash
   ros2 interface show voron24_msgs/msg/PrinterStatus
   ```

2. Unity 상단 메뉴 → **Robotics → Generate ROS Messages...**
   - **ROS message path**: `<repo>/ros2_ws/src/voron24_msgs`
   - `msg/` 아래 3개(PrinterStatus, ExtrusionPoint, PrinterCommand) 각각 **Build msg**
   - `Assets/RosMessages/Voron24/msg/*.cs` 가 생성되는지 확인

3. 이 폴더의 파일 확장자에서 `.txt` 를 제거하고 상위 폴더로 이동
   ```
   _pending_msgs/PrinterStatusSubscriber.cs.txt → ../PrinterStatusSubscriber.cs
   _pending_msgs/PrinterCommandPublisher.cs.txt → ../PrinterCommandPublisher.cs
   ```

4. 생성된 `Assets/RosMessages/` 도 **커밋**할 것 (팀원이 재생성하지 않아도 되도록)

## 주의

C 가 `.msg` 정의를 변경하면 2번을 다시 실행해야 합니다.
계약 section 5 변경은 PR + 3인 승인 사항이므로, 변경 시 반드시 공지를 받으세요.
