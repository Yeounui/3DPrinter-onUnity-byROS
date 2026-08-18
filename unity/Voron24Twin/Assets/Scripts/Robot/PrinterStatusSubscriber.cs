using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosStatus = RosMessageTypes.Voron24.PrinterStatusMsg;

namespace Voron24.Robot
{
    /// <summary>
    /// /printer/status 구독 (계약 section 5).
    ///
    /// 필드 구성은 ros2_ws/src/voron24_msgs/msg/PrinterStatus.msg 를 그대로 따른다.
    /// 계약 문서 section 6 의 예시에는 노즐/베드/챔버 온도가 남아 있으나 실제 .msg 에는 없다.
    /// 이 레포는 시뮬레이션 전용이라 온도는 수신할 곳이 없다 (CLAUDE.md — 범위 밖).
    /// 온도를 되살리려면 .msg 변경 = 계약 변경 = PR + 3인 승인 후 C# 재생성부터 해야 한다.
    /// </summary>
    public class PrinterStatusSubscriber : MonoBehaviour
    {
        [SerializeField] string topic = "/printer/status";

        public string State { get; private set; } = "idle";
        public float Progress { get; private set; }
        public uint CurrentLayer { get; private set; }
        public uint TotalLayers { get; private set; }
        public string Filename { get; private set; } = "";

        /// <summary>마지막 수신 시각. 0 이면 아직 한 번도 못 받았다.</summary>
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
