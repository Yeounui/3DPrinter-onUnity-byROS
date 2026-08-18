# voron24_msgs 생성 후 활성화할 스크립트 (활성화 완료)

이 폴더의 `.cs.txt` 파일들은 **커스텀 메시지의 C# 클래스가 생성되기 전까지 컴파일되면 안 되므로**
확장자를 막아둔 상태였습니다. (`RosMessageTypes.Voron24` 네임스페이스가 없으면 컴파일 에러)

`Assets/RosMessages/Voron24/msg/*.cs` 가 생성·커밋되면서 **활성화는 끝났습니다.**
지금 이 폴더에 남은 것은 이 README 뿐이고, 아래 절차는 `.msg` 가 바뀌었을 때 다시 밟는 절차입니다.

| 원본 | 현재 위치 |
|---|---|
| `PrinterCommandPublisher.cs.txt` | 삭제. 상위 폴더의 `PrinterCommandPublisher.cs` 가 더 최신이었다 |
| `PrinterStatusSubscriber.cs.txt` | `../PrinterStatusSubscriber.cs` 로 승격 |

## 재생성 절차 (`.msg` 가 바뀌었을 때)

1. C 가 `voron24_msgs` 를 커밋 & 빌드했는지 확인
   ```bash
   colcon build --packages-select voron24_msgs && source install/setup.bash
   ros2 interface show voron24_msgs/msg/PrinterStatus
   ```

2. Unity 상단 메뉴 → **Robotics → Generate ROS Messages...**
   - **ROS message path**: `<repo>/ros2_ws/src/voron24_msgs`
   - `msg/` 아래 3개(PrinterStatus, ExtrusionPoint, PrinterCommand) 각각 **Build msg**
   - `Assets/RosMessages/Voron24/msg/*.cs` 가 갱신되는지 확인

3. 생성된 `Assets/RosMessages/` 도 **커밋**할 것 (팀원이 재생성하지 않아도 되도록)

4. 필드가 늘거나 줄었으면 `PrinterStatusSubscriber.cs` / `PrinterCommandPublisher.cs` 를 따라 고칠 것

## 주의

C 가 `.msg` 정의를 변경하면 2번을 다시 실행해야 합니다.
계약 section 5 변경은 PR + 3인 승인 사항이므로, 변경 시 반드시 공지를 받으세요.

`PrinterStatus.msg` 에는 계약 문서 section 6 예시와 달리 **온도 필드가 없습니다.**
이 레포는 시뮬레이션 전용이라 온도를 받아올 곳이 없기 때문입니다 (CLAUDE.md — 범위 밖).
