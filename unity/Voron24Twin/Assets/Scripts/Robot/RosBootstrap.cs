using System;
using System.Globalization;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

namespace Voron24.Robot
{
    /// <summary>
    /// 플레이어 실행 인자로 ROS endpoint 주소 override.
    ///
    /// Linux 빌드는 launch가 같은 머신에서 띄우므로 사실상 항상 127.0.0.1:10000.
    /// 인자로 노출해 두면 ROS를 다른 머신에 두는 구성이 씬 수정 없이 가능
    /// (04 §4 / 04a §"Unity 부트스트랩").
    ///
    /// <code>
    /// Voron24Twin.x86_64 --ros-ip 192.168.0.7 --ros-port 10000
    /// </code>
    ///
    /// **인자 없으면 아무것도 건드리지 않음.** 씬 ROSConnection Inspector 값
    /// 그대로 사용되므로 Editor Play 기존 경로 유지.
    ///
    /// 인자 이름 `--` prefix는 Unity 플레이어 자체 인자
    /// (`-force-glcore`, `-screen-fullscreen`, `-logFile` 등)와 충돌 방지 목적.
    /// </summary>
    public static class RosBootstrap
    {
        const string ArgIp = "--ros-ip";
        const string ArgPort = "--ros-port";

        static string _ip;
        static int _port = -1;

        /// <summary>
        /// 인자 파싱. 씬 로드 전이라 여기서 GameObject 접근 안 함.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Parse()
        {
            _ip = null;
            _port = -1;

            string[] args;
            try
            {
                args = Environment.GetCommandLineArgs();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RosBootstrap] 실행 인자 읽기 실패: " + e.Message);
                return;
            }

            for (int i = 0; i < args.Length; i++)
            {
                string ip = ValueOf(args, i, ArgIp);
                if (ip != null)
                {
                    _ip = ip;
                    continue;
                }

                string port = ValueOf(args, i, ArgPort);
                if (port == null)
                    continue;

                if (int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                 out int parsed) && parsed > 0 && parsed < 65536)
                    _port = parsed;
                else
                    Debug.LogWarning("[RosBootstrap] " + ArgPort + " 값이 유효한 port 아님: " + port);
            }
        }

        /// <summary>
        /// 파싱 값을 씬 ROSConnection에 적용.
        ///
        /// AfterSceneLoad는 모든 Awake 완료 후 **Start 전** 호출. ROSConnection은
        /// Start()에서 Connect(), JointStateSubscriber도 Start()에서 구독
        /// 설정하므로, 여기서 주소 변경 시 첫 연결부터 반영.
        ///
        /// BeforeSceneLoad에서 ROSConnection 접근 금지. 그 시점엔 씬 오브젝트
        /// 없어서 GetOrCreateInstance()가 Resources prefab으로 **두 번째** instance를
        /// 생성, 씬 ROSConnection은 singleton 자리 못 잡아 연결이 둘로
        /// 분리됨.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Apply()
        {
            if (_ip == null && _port < 0)
                return;   // 인자 없음 — Inspector 값 유지

            ROSConnection ros = UnityEngine.Object.FindObjectOfType<ROSConnection>();
            if (ros == null)
                ros = ROSConnection.GetOrCreateInstance();

            if (ros == null)
            {
                Debug.LogWarning("[RosBootstrap] ROSConnection 없음. 인자 무시");
                return;
            }

            if (_ip != null)
                ros.RosIPAddress = _ip;
            if (_port > 0)
                ros.RosPort = _port;

            Debug.Log("[RosBootstrap] ROS endpoint " + ros.RosIPAddress + ":" + ros.RosPort
                    + " (실행 인자 override)");
        }

        /// <summary>`--name value` / `--name=value` 양쪽 지원. 불일치 시 null.</summary>
        static string ValueOf(string[] args, int i, string name)
        {
            if (args[i] == name)
                return i + 1 < args.Length ? args[i + 1] : null;

            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
                return args[i].Substring(name.Length + 1);

            return null;
        }
    }
}
