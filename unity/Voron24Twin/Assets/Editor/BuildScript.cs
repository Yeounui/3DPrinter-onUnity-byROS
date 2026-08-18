using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace Voron24
{
    /// <summary>
    /// 배치 모드 Linux 빌드 진입점. `tools/build_unity.sh` 가
    /// `-executeMethod Voron24.BuildScript.BuildLinux` 로 부른다.
    ///
    /// 사람이 Editor GUI 에서 손으로 뽑던 산출물을 재현 가능하게 만드는 것이 목적이므로
    /// 출력 경로는 인자 없이도 항상 같은 자리에 떨어진다 — `sim.launch.py` 가 찾는 자리다.
    ///
    /// 인자 (셸이 Unity CLI 뒤에 그대로 붙여 넘긴다):
    ///   -devBuild true|false   Development Build. 기본 true (04a §2 — 골격 검증 단계)
    ///   -buildOutput &lt;경로&gt;    출력 파일 경로. 기본은 아래 OutputRelative
    /// </summary>
    public static class BuildScript
    {
        /// <summary>
        /// 프로젝트 폴더 기준 출력 경로.
        ///
        /// `sim.launch.py:53` 의 UNITY_PLAYER 상수와 **정확히** 같아야 한다. 여기가 어긋나면
        /// launch 가 빌드를 못 찾고 조용히 `unity:=editor` 로 폴백해서, 창이 안 뜨는 이유가
        /// 빌드 실패인지 경로 불일치인지 구분되지 않는다.
        /// </summary>
        const string OutputRelative = "Build/Linux/Voron24Twin.x86_64";

        public static void BuildLinux()
        {
            int code;
            try
            {
                code = Run();
            }
            catch (Exception e)
            {
                Debug.LogError("[Build] 예외로 중단: " + e);
                code = 1;
            }

            Finish(code);
        }

        static int Run()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;

            string output = ArgValue("-buildOutput");
            output = string.IsNullOrEmpty(output)
                ? Path.Combine(projectRoot, OutputRelative)
                : Path.GetFullPath(output);

            bool development = ArgBool("-devBuild", true);

            // Linux Build Support (Mono) 가 없으면 BuildPlayer 가 애매한 메시지만 남기고
            // 끝난다. 04a 0 단계의 전제이므로 먼저 짚어준다.
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone,
                                                      BuildTarget.StandaloneLinux64))
            {
                Debug.LogError("[Build] Linux Build Support (Mono) 모듈이 없다. "
                             + "Unity Hub → " + Application.unityVersion
                             + " → Add modules 에서 설치할 것 (IL2CPP 는 불필요).");
                return 1;
            }

            string[] scenes = EnabledScenes();
            if (scenes.Length == 0)
            {
                Debug.LogError("[Build] EditorBuildSettings 에 활성화된 씬이 없다. "
                             + "File → Build Settings 에서 Assets/Scenes/Voron24Twin.unity 를 "
                             + "등록할 것.");
                return 1;
            }

            WarnPlayerSettings();

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneLinux64)
            {
                // 셸이 -buildTarget Linux64 를 주므로 보통은 여기 안 온다. 사람이 직접
                // 부른 경우에 대비한 방어.
                Debug.Log("[Build] 활성 타깃을 StandaloneLinux64 로 전환한다 (현재 "
                        + EditorUserBuildSettings.activeBuildTarget + ").");
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(
                        BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64))
                {
                    Debug.LogError("[Build] 타깃 전환 실패.");
                    return 1;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(output));

            Debug.Log("[Build] 씬 " + scenes.Length + " 개: " + string.Join(", ", scenes));
            Debug.Log("[Build] 출력 " + output + " (development=" + development + ")");

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                targetGroup = BuildTargetGroup.Standalone,
                target = BuildTarget.StandaloneLinux64,
                options = development ? BuildOptions.Development : BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            Debug.Log("[Build] result=" + summary.result
                    + " errors=" + summary.totalErrors
                    + " warnings=" + summary.totalWarnings
                    + " size=" + (summary.totalSize / (1024UL * 1024UL)) + "MB"
                    + " time=" + summary.totalTime);

            if (summary.result != BuildResult.Succeeded)
            {
                Debug.LogError("[Build] BuildReport 가 실패로 끝났다: " + summary.result);
                return 1;
            }

            // BuildReport 가 성공이라고 해도 실행 파일이 실제로 있는지는 별개다.
            // launch 가 찾는 것은 파일이므로 파일로 판정한다.
            if (!File.Exists(output))
            {
                Debug.LogError("[Build] 성공으로 보고됐지만 산출물이 없다: " + output);
                return 1;
            }

            return 0;
        }

        static void Finish(int code)
        {
            if (code == 0)
                Debug.Log("[Build] 완료.");
            else
                Debug.LogError("[Build] 실패 — exit " + code);

            // ★ 배치 빌드의 silent failure 방지.
            // `-quit` 는 executeMethod 가 무엇을 하든 종료 코드 0 을 내는 경우가 있다.
            // BuildReport 를 본 결과를 여기서 종료 코드로 직접 못박아야 CI 가 조용히
            // 초록불이 되지 않는다.
            // batchmode 가 아닐 때(사람이 Editor 에서 잘못 부른 경우)는 에디터를 통째로
            // 죽이면 안 되므로 로그만 남긴다.
            if (Application.isBatchMode)
                EditorApplication.Exit(code);
        }

        /// <summary>
        /// 빌드 대상 씬. **하드코딩하지 않고 `EditorBuildSettings` 에서 가져온다.**
        ///
        /// 지금 등록된 것은 `Assets/Scenes/Voron24Twin.unity` 하나이고
        /// `Assets/Scenes/Constraint_Test.unity` 는 일부러 빠져 있다 — 조인트 제약 실험용
        /// 씬이라 플레이어에 들어갈 이유가 없다. 파일 목록(`Assets/Scenes/*.unity`)으로
        /// 잡으면 그 실험 씬까지 딸려 들어가고, 이름을 코드에 박으면 씬이 늘 때마다
        /// 빌드 스크립트를 같이 고쳐야 한다. 빌드 대상의 단일 출처는 Build Settings 다.
        /// </summary>
        static string[] EnabledScenes()
        {
            var paths = new List<string>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled && !string.IsNullOrEmpty(scene.path))
                    paths.Add(scene.path);
            }
            return paths.ToArray();
        }

        /// <summary>
        /// 04a §1 의 Player Settings 항목을 점검만 한다.
        ///
        /// 고치지는 않는다. 설정 변경은 §1 의 몫이고 빌드 스크립트가 ProjectSettings.asset 을
        /// 몰래 바꾸면 사람이 Editor 에서 본 설정과 빌드 결과가 갈라진다.
        /// </summary>
        static void WarnPlayerSettings()
        {
            if (PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64))
            {
                Debug.LogWarning("[Build] Linux Graphics APIs 가 기본값(Vulkan 우선)이다. "
                               + "WSLg 에서는 실행 인자 -force-glcore 로 덮이지만 "
                               + "Player Settings 에서 OpenGLCore 만 남기는 편이 안전하다 (04a §1).");
            }
            else
            {
                GraphicsDeviceType[] apis =
                    PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneLinux64);
                if (apis.Length > 0 && apis[0] != GraphicsDeviceType.OpenGLCore)
                {
                    Debug.LogWarning("[Build] Linux Graphics APIs 의 첫 항목이 "
                                   + apis[0] + " 다. OpenGLCore 를 앞에 둘 것 (04a §1).");
                }
            }

            if (PlayerSettings.fullScreenMode != FullScreenMode.Windowed)
            {
                Debug.LogWarning("[Build] fullScreenMode 가 " + PlayerSettings.fullScreenMode
                               + " 다. sim.launch.py 가 -screen-fullscreen 0 을 주긴 하지만 "
                               + "Windowed 가 기본이어야 한다 (04a §1).");
            }

            if (!PlayerSettings.resizableWindow)
                Debug.LogWarning("[Build] resizableWindow 가 꺼져 있다 (04a §1).");
        }

        /// <summary>`-name value` 형태의 CLI 인자를 읽는다. 없으면 null.</summary>
        static string ArgValue(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                    return args[i + 1];
            }
            return null;
        }

        /// <summary>`-name true|false`. 값이 없거나 해석 불가면 fallback.</summary>
        static bool ArgBool(string name, bool fallback)
        {
            string raw = ArgValue(name);
            if (string.IsNullOrEmpty(raw))
                return fallback;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on":
                    return true;
                case "0": case "false": case "no": case "off":
                    return false;
                default:
                    Debug.LogWarning("[Build] " + name + " 값을 못 읽었다: '" + raw
                                   + "' — 기본값 " + fallback + " 을 쓴다.");
                    return fallback;
            }
        }
    }
}
