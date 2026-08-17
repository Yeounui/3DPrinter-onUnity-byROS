using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GeometrySliderTravelLimitReport))]
public class GeometrySliderTravelLimitReportEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        GeometrySliderTravelLimitReport report =
            (GeometrySliderTravelLimitReport)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Resolved Paths", EditorStyles.boldLabel);
        EditorGUILayout.SelectableLabel(
            "Default directory:\n" + report.DefaultReportDirectory,
            EditorStyles.helpBox,
            GUILayout.Height(34)
        );
        EditorGUILayout.SelectableLabel(
            "Current report file:\n" + report.GetResolvedReportPath(),
            EditorStyles.helpBox,
            GUILayout.Height(34)
        );
    }
}
