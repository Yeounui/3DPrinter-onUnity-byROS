using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(AssemblyStlTool))]
public class AssemblyStlToolEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        AssemblyStlTool tool = (AssemblyStlTool)target;
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Resolved Paths", EditorStyles.boldLabel);
        EditorGUILayout.SelectableLabel("Project root:\n" + tool.ProjectRoot, EditorStyles.helpBox, GUILayout.Height(34));
        EditorGUILayout.SelectableLabel("STL directory:\n" + tool.ResolvedStlDirectory, EditorStyles.helpBox, GUILayout.Height(34));
        EditorGUILayout.SelectableLabel("Log directory:\n" + tool.ResolvedLogDirectory, EditorStyles.helpBox, GUILayout.Height(34));
        EditorGUILayout.SelectableLabel("Selected STL path:\n" + tool.ResolveStlPath(), EditorStyles.helpBox, GUILayout.Height(34));

        EditorGUILayout.Space();
        if (GUILayout.Button("Auto Assign: Collision Parent + Assembly Links"))
        {
            Undo.RecordObject(tool, "Auto Assign Assembly STL Tool");
            tool.AutoAssignByName();
            EditorUtility.SetDirty(tool);
        }

        using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying))
        {
            if (GUILayout.Button("Export Link STL")) tool.ExportLinkStl();
            if (GUILayout.Button("Generate Assembly Location Logs")) tool.GenerateAssemblyLogs();
        }

        if (!EditorApplication.isPlaying)
            EditorGUILayout.HelpBox("STL과 로그는 Unity Play 상태의 조립 위치를 사용합니다.", MessageType.Info);
    }
}
