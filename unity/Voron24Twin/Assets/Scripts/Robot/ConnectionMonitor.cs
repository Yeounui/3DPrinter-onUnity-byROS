using UnityEngine;

namespace Voron24.Robot
{
    /// <summary>
    /// ROS 연결 상태를 화면 좌상단에 표시한다.
    /// 통합 디버깅 때 "Unity 문제인가 ROS 문제인가" 를 즉시 판별하기 위한 것.
    /// </summary>
    public class ConnectionMonitor : MonoBehaviour
    {
        [SerializeField] JointStateSubscriber subscriber;
        [SerializeField] NozzleTracker nozzle;
        [SerializeField] bool show = true;

        GUIStyle _style;

        void OnGUI()
        {
            if (!show || subscriber == null) return;

            _style ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                normal = { textColor = Color.white },
            };

            bool ok = subscriber.IsConnected;
            GUI.color = ok ? Color.green : Color.red;
            GUI.Box(new Rect(10, 10, 300, ok ? 110 : 60), "");
            GUI.color = Color.white;

            string txt = ok
                ? $"ROS: CONNECTED\n" +
                  $"joint_x {subscriber.GetTarget("joint_x") * 1000f,7:F1} mm\n" +
                  $"joint_y {subscriber.GetTarget("joint_y") * 1000f,7:F1} mm\n" +
                  $"joint_z {subscriber.GetTarget("joint_z") * 1000f,7:F1} mm"
                : $"ROS: DISCONNECTED\n" +
                  $"last msg {subscriber.TimeSinceLastMessage:F1}s ago";

            if (ok && nozzle != null)
            {
                var g = nozzle.NozzleGcodeMm();
                txt += $"\nnozzle  X{g.x:F1} Y{g.y:F1} Z{g.z:F1}";
            }

            GUI.Label(new Rect(20, 16, 290, 100), txt, _style);
        }
    }
}
