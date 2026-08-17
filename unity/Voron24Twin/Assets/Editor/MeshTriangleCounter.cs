using UnityEditor;
using UnityEngine;

public class MeshTriangleCounter : EditorWindow
{
    private int objectCount;
    private int meshCount;
    private long vertexCount;
    private long triangleCount;

    [MenuItem("Tools/Mesh Triangle Counter")]
    public static void ShowWindow()
    {
        GetWindow<MeshTriangleCounter>("Mesh Triangle Counter");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField(
            "Selected Object Mesh Statistics",
            EditorStyles.boldLabel
        );

        EditorGUILayout.Space();

        if (GUILayout.Button("Count Selected Object"))
        {
            CountSelectedObject();
        }

        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Objects", objectCount.ToString());
        EditorGUILayout.LabelField("Meshes", meshCount.ToString());
        EditorGUILayout.LabelField("Vertices", vertexCount.ToString("N0"));
        EditorGUILayout.LabelField("Triangles", triangleCount.ToString("N0"));
    }

    private void CountSelectedObject()
    {
        objectCount = 0;
        meshCount = 0;
        vertexCount = 0;
        triangleCount = 0;

        GameObject selected = Selection.activeGameObject;

        if (selected == null)
        {
            Debug.LogWarning(
                "MeshTriangleCounter: Hierarchy에서 GameObject를 먼저 선택하세요."
            );
            return;
        }

        Transform[] transforms =
            selected.GetComponentsInChildren<Transform>(true);

        objectCount = transforms.Length;

        MeshFilter[] meshFilters =
            selected.GetComponentsInChildren<MeshFilter>(true);

        foreach (MeshFilter meshFilter in meshFilters)
        {
            if (meshFilter == null ||
                meshFilter.sharedMesh == null)
            {
                continue;
            }

            Mesh mesh = meshFilter.sharedMesh;

            meshCount++;
            vertexCount += mesh.vertexCount;

            // subMesh별 indexCount를 합산하여
            // 여러 Material/SubMesh가 있는 OBJ도 정확히 계산함.
            for (int subMesh = 0;
                 subMesh < mesh.subMeshCount;
                 subMesh++)
            {
                triangleCount +=
                    (long)mesh.GetIndexCount(subMesh) / 3L;
            }
        }

        Debug.Log(
            $"Objects: {objectCount}, " +
            $"Meshes: {meshCount}, " +
            $"Vertices: {vertexCount:N0}, " +
            $"Triangles: {triangleCount:N0}"
        );
    }
}
