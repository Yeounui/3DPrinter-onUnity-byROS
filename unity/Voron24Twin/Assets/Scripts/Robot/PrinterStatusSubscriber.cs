using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosStatus = RosMessageTypes.Voron24.PrinterStatusMsg;

namespace Voron24.Robot
{
    /// <summary>
    /// /printer/status 구독 (계약 §5).
    ///
    /// 필드 구성: ros2_ws/src/voron24_msgs/msg/PrinterStatus.msg 그대로.
    /// 계약 §6 예시에 노즐/베드/챔버 온도 있으나 실제 .msg에는 없음.
    /// 시뮬레이션 전용이라 온도 수신 대상 없음 (CLAUDE.md — 범위 밖).
    /// 온도 복원 시 .msg 변경 = 계약 변경 = PR + 3인 승인 후 C# 재생성 필요.
    /// </summary>
    public class PrinterStatusSubscriber : MonoBehaviour
    {
        [SerializeField] string topic = "/printer/status";

        public string State { get; private set; } = "idle";
        public float Progress { get; private set; }
        public uint CurrentLayer { get; private set; }
        public uint TotalLayers { get; private set; }
        public string Filename { get; private set; } = "";

        /// <summary>마지막 수신 시각. 0이면 수신 이력 없음.</summary>
        public float LastMessageTime { get; private set; }

        public System.Action<RosStatus> OnStatus;

        void Start()
        {
            ROSConnection.GetOrCreateInstance().Subscribe<RosStatus>(topic, Handle);
        }

        void Handle(RosStatus m)
        {
            State = m.state;
            Progress = m.progress;
            CurrentLayer = m.current_layer;
            TotalLayers = m.total_layers;
            Filename = m.filename;
            LastMessageTime = Time.time;
            OnStatus?.Invoke(m);
        }
    }
}
