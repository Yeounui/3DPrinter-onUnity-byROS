using System;
using System.Globalization;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

namespace Voron24.Robot
{
    /// <summary>
    /// 플레이어 실행 인자로 ROS 엔드포인트 주소를 덮어쓴다.
    ///
    /// Linux 빌드는 launch 가 같은 머신에서 띄우므로 사실상 항상 `127.0.0.1:10000` 이다.
    /// 그래도 인자로 노출해 두면 나중에 ROS 를 다른 머신에 두는 구성이 씬 수정 없이 된다
    /// (04 §4 / 04a §"Unity 부트스트랩").
    ///
    /// <code>
    /// Voron24Twin.x86_64 --ros-ip 192.168.0.7 --ros-port 10000
    /// </code>
    ///
    /// **인자가 하나도 없으면 아무것도 건드리지 않는다.** 씬의 ROSConnection 인스펙터 값이
    /// 그대로 쓰이므로 Editor 에서 Play 하는 기존 경로가 깨지지 않는다.
    ///
    /// 인자 이름을 `--` 두 줄로 시작하게 둔 것은 Unity 플레이어 자신의 인자
    /// (`-force-glcore`, `-screen-fullscreen`, `-logFile` …)와 겹치지 않게 하기 위함이다.
    /// </summary>
    public static class RosBootstrap
    {
        const string ArgIp = "--ros-ip";
        const string ArgPort = "--ros-port";

        static string _ip;
        static int _port = -1;

        /// <summary>
        /// 인자 파싱. 씬이 올라오기 전이라 여기서는 GameObject 를 만지지 않는다.
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
                Debug.LogWarning("[RosBootstrap] 실행 인자를 못 읽었다: " + e.Message);
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
                    Debug.LogWarning("[RosBootstrap] " + ArgPort + " 값이 포트가 아니다: " + port);
            }
        }

        /// <summary>
        /// 파싱한 값을 씬의 ROSConnection 에 얹는다.
        ///
        /// AfterSceneLoad 는 모든 Awake 가 끝나고 **Start 전에** 불린다. ROSConnection 은
        /// 자기 Start() 에서 Connect() 하고 JointStateSubscriber 도 Start() 에서 구독을
        /// 걸므로, 여기서 주소를 바꾸면 첫 연결부터 반영된다.
        ///
        /// BeforeSceneLoad 에서 ROSConnection 을 만지면 안 된다. 그 시점엔 씬 오브젝트가
        /// 없어서 GetOrCreateInstance() 가 Resources 프리팹으로 **두 번째** 인스턴스를
        /// 만들어 버리고, 정작 씬의 ROSConnection 은 싱글턴 자리를 못 잡아 연결이 둘로
        /// 갈라진다.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Apply()
        {
            if (_ip == null && _port < 0)
                return;   // 인자 없음 — 인스펙터 값 유지

            ROSConnection ros = UnityEngine.Object.FindObjectOfType<ROSConnection>();
            if (ros == null)
                ros = ROSConnection.GetOrCreateInstance();

            if (ros == null)
            {
                Debug.LogWarning("[RosBootstrap] ROSConnection 을 못 찾았다. 인자를 무시한다.");
                return;
            }

            if (_ip != null)
                ros.RosIPAddress = _ip;
            if (_port > 0)
                ros.RosPort = _port;

            Debug.Log("[RosBootstrap] ROS 엔드포인트 " + ros.RosIPAddress + ":" + ros.RosPort
                    + " (실행 인자로 덮어씀)");
        }

        /// <summary>`--name value` 와 `--name=value` 둘 다 받는다. 아니면 null.</summary>
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
