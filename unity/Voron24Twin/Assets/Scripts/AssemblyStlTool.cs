using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Play 상태 조립 결과 기준 STL + 위치 로그 생성.
/// assemblyFrame, box_0 중심 규칙, 축 방향 설정 공유.
/// </summary>
public class AssemblyStlTool : MonoBehaviour
{
    [Header("Assembly References")]
    public Transform assemblyFrame;
    public Transform linkToExport;
    public List<Transform> linksForLogs = new List<Transform>();

    [Header("STL Center")]
    public string centerColliderName = "box_0";
    public bool requirePlayMode = true;

    [Header("Coordinate Direction")]
    public bool invertX;
    public bool invertY;
    public bool invertZ;

    [Header("Output Paths")]
    public string stlOutputDirectory = "..";
    public string logOutputDirectory = "../loc_log";
    public string stlFileName = "";
    public string logFileNameSuffix = ".md";
    public float unitsToMillimetres = 1f;

#if UNITY_EDITOR
    public string ProjectRoot => NormalizePath(Directory.GetParent(Application.dataPath).FullName);

    public string ResolvedStlDirectory => ResolveDirectory(stlOutputDirectory);
    public string ResolvedLogDirectory => ResolveDirectory(logOutputDirectory);

    public string ResolveStlPath()
    {
        string fileName = string.IsNullOrWhiteSpace(stlFileName)
            ? (linkToExport ? linkToExport.name : "link") + ".stl"
            : stlFileName;
        return NormalizePath(Path.Combine(ResolvedStlDirectory, fileName));
    }

    public void ExportLinkStl()
    {
        if (!ValidatePlayState() || !ValidateReferences(linkToExport)) return;

        Transform collision = FindDescendant(linkToExport, "Collision");
        BoxCollider centerBox = collision ? FindBoxCollider(collision, centerColliderName) : null;
        if (!centerBox)
        {
            ShowError("STL 중심 계산용 Collision/" + centerColliderName + " 없음");
            return;
        }

        Vector3 centerInAssembly = assemblyFrame.InverseTransformPoint(
            centerBox.transform.TransformPoint(GetMinimumSumCorner(centerBox)));

        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        foreach (BoxCollider box in collision.GetComponentsInChildren<BoxCollider>(true))
            AddBox(box, assemblyFrame, centerInAssembly, vertices, triangles);
        foreach (MeshCollider mesh in collision.GetComponentsInChildren<MeshCollider>(true))
            AddMesh(mesh, assemblyFrame, centerInAssembly, vertices, triangles);

        if (triangles.Count == 0)
        {
            ShowError("내보낼 BoxCollider 또는 읽기 가능한 MeshCollider 없음");
            return;
        }

        ApplyCoordinateDirection(vertices);
        if (invertX ^ invertY ^ invertZ)
            ReverseTriangleWinding(triangles);

        string path = ResolveStlPath();
        try
        {
            Directory.CreateDirectory(ResolvedStlDirectory);
            WriteBinaryStl(path, vertices, triangles);
            Debug.Log("Assembly STL 생성 완료 " + path, this);
            EditorUtility.RevealInFinder(path);
        }
        catch (Exception exception)
        {
            ShowError("STL 저장 실패 " + exception.Message);
        }
    }

    public void GenerateAssemblyLogs()
    {
        if (!ValidatePlayState() || !ValidateReferences(assemblyFrame)) return;
        if (linksForLogs == null || linksForLogs.Count == 0)
        {
            ShowError("위치 로그 대상 링크 목록 비어 있음");
            return;
        }

        var records = new List<CenterRecord>();
        foreach (Transform link in linksForLogs)
        {
            if (!link)
            {
                ShowError("위치 로그 링크 목록에 빈 항목 존재");
                return;
            }

            Transform collision = FindDescendant(link, "Collision");
            BoxCollider centerBox = collision ? FindBoxCollider(collision, centerColliderName) : null;
            if (!centerBox)
            {
                ShowError(link.name + "에서 Collision/" + centerColliderName + " 없음");
                return;
            }

            Vector3 corner = GetMinimumSumCorner(centerBox);
            Vector3 center = assemblyFrame.InverseTransformPoint(centerBox.transform.TransformPoint(corner));
            records.Add(new CenterRecord(link, centerBox, corner, center));
        }

        CenterRecord baseRecord = records.Find(record => record.link.name == "base_link") ?? records[0];
        string directory = ResolvedLogDirectory;
        try
        {
            Directory.CreateDirectory(directory);
            foreach (CenterRecord record in records)
            {
                Vector3 relative = ApplyCoordinateDirection(record.center - baseRecord.center);
                string path = Path.Combine(directory, record.link.name + logFileNameSuffix);
                File.WriteAllText(path, BuildLog(record, baseRecord, relative));
                Debug.Log("Assembly 위치 로그 생성 완료 " + path, record.link);
            }
            EditorUtility.RevealInFinder(directory);
        }
        catch (Exception exception)
        {
            ShowError("위치 로그 저장 실패 " + exception.Message);
        }
    }

    public void AutoAssignByName()
    {
        if (!assemblyFrame) assemblyFrame = FindSceneTransform("base_link");

        // Collision 또는 하위 객체에 컴포넌트를 붙인 경우,
        // 해당 Collision 소유 링크를 STL 대상으로 사용
        Transform componentOwner = FindOwningLink(transform);
        if (componentOwner)
            linkToExport = componentOwner;
        else if (!linkToExport)
            linkToExport = Selection.activeTransform;

        linksForLogs = new List<Transform>();
        foreach (string name in new[] { "base_link", "z_gantry", "x_beam", "toolhead" })
        {
            Transform link = FindSceneTransform(name);
            if (link) linksForLogs.Add(link);
        }
    }

    private bool ValidatePlayState()
    {
        if (!requirePlayMode || EditorApplication.isPlaying) return true;
        ShowError("Play 상태에서만 실행 가능. Play 먼저 시작 필요");
        return false;
    }

    private bool ValidateReferences(Transform required)
    {
        if (assemblyFrame && required) return true;
        ShowError("Assembly Frame과 대상 링크 Inspector 지정 필요");
        return false;
    }

    private Vector3 ApplyCoordinateDirection(Vector3 value)
    {
        if (invertX) value.x = -value.x;
        if (invertY) value.y = -value.y;
        if (invertZ) value.z = -value.z;
        return value;
    }

    private void ApplyCoordinateDirection(List<Vector3> vertices)
    {
        for (int i = 0; i < vertices.Count; i++)
            vertices[i] = ApplyCoordinateDirection(vertices[i]);
    }

    private static void ReverseTriangleWinding(List<int> triangles)
    {
        for (int i = 0; i < triangles.Count; i += 3)
        {
            int temporary = triangles[i + 1];
            triangles[i + 1] = triangles[i + 2];
            triangles[i + 2] = temporary;
        }
    }

    private string BuildLog(CenterRecord record, CenterRecord baseRecord, Vector3 relative)
    {
        Transform link = record.link;
        StringBuilder log = new StringBuilder();
        log.AppendLine("# " + link.name + ".stl Assembly Center Log");
        log.AppendLine();
        log.AppendLine("- Log mode: PLAY_ASSEMBLY_BAKED");
        log.AppendLine("- Unity play state: " + EditorApplication.isPlaying);
        log.AppendLine("- Link: " + link.name);
        log.AppendLine("- Link path: " + GetHierarchyPath(link));
        log.AppendLine("- Assembly frame: " + assemblyFrame.name);
        log.AppendLine("- Assembly frame path: " + GetHierarchyPath(assemblyFrame));
        log.AppendLine("- Center collider: " + GetHierarchyPath(record.centerBox.transform));
        log.AppendLine("- Center rule: Collision/" + centerColliderName + " local corner with minimum x+y+z");
        log.AppendLine("- Coordinate conversion: collider local -> world -> assembly local");
        log.AppendLine("- Axis inversion: X=" + (invertX ? "-1" : "+1") +
                       ", Y=" + (invertY ? "-1" : "+1") +
                       ", Z=" + (invertZ ? "-1" : "+1"));
        log.AppendLine();
        log.AppendLine("## STL Center");
        log.AppendLine();
        log.AppendLine("- Selected box_0 local corner: " + record.cornerLocal);
        log.AppendLine("- Center in raw assembly frame: " + record.center);
        log.AppendLine("- Relative to base_link STL center: " + relative);
        log.AppendLine();
        log.AppendLine("## Orientation");
        log.AppendLine();
        log.AppendLine("- Relative rotation quaternion (raw): " +
                       (Quaternion.Inverse(assemblyFrame.rotation) * link.rotation));
        log.AppendLine("- Local X in raw assembly frame: " + assemblyFrame.InverseTransformDirection(link.right));
        log.AppendLine("- Local Y in raw assembly frame: " + assemblyFrame.InverseTransformDirection(link.up));
        log.AppendLine("- Local Z in raw assembly frame: " + assemblyFrame.InverseTransformDirection(link.forward));
        log.AppendLine("- Local X after axis setting: " + ApplyCoordinateDirection(assemblyFrame.InverseTransformDirection(link.right)));
        log.AppendLine("- Local Y after axis setting: " + ApplyCoordinateDirection(assemblyFrame.InverseTransformDirection(link.up)));
        log.AppendLine("- Local Z after axis setting: " + ApplyCoordinateDirection(assemblyFrame.InverseTransformDirection(link.forward)));
        log.AppendLine("- World position: " + link.position);
        log.AppendLine("- World rotation: " + link.rotation);
        log.AppendLine();
        log.AppendLine("## Reference");
        log.AppendLine();
        log.AppendLine("- Base STL center in raw assembly frame: " + baseRecord.center);
        log.AppendLine("- STL output units: Unity project units x " + unitsToMillimetres + " millimetres");
        return log.ToString();
    }

    private void AddBox(BoxCollider box, Transform frame, Vector3 center, List<Vector3> vertices, List<int> triangles)
    {
        Vector3 half = box.size * 0.5f;
        Vector3[] corners =
        {
            new Vector3(-half.x, -half.y, -half.z), new Vector3(half.x, -half.y, -half.z),
            new Vector3(half.x, half.y, -half.z), new Vector3(-half.x, half.y, -half.z),
            new Vector3(-half.x, -half.y, half.z), new Vector3(half.x, -half.y, half.z),
            new Vector3(half.x, half.y, half.z), new Vector3(-half.x, half.y, half.z)
        };
        int start = vertices.Count;
        foreach (Vector3 corner in corners)
        {
            Vector3 point = frame.InverseTransformPoint(box.transform.TransformPoint(box.center + corner));
            vertices.Add(point - center);
        }

        int[,] faces =
        {
            { 0, 3, 2, 1 }, { 4, 5, 6, 7 }, { 0, 1, 5, 4 },
            { 3, 7, 6, 2 }, { 1, 2, 6, 5 }, { 0, 4, 7, 3 }
        };
        for (int i = 0; i < 6; i++) AddQuad(triangles, start, faces[i, 0], faces[i, 1], faces[i, 2], faces[i, 3]);
    }

    private void AddMesh(MeshCollider collider, Transform frame, Vector3 center, List<Vector3> vertices, List<int> triangles)
    {
        Mesh mesh = collider.sharedMesh;
        if (!mesh || !mesh.isReadable) return;
        int start = vertices.Count;
        foreach (Vector3 point in mesh.vertices)
        {
            Vector3 assemblyPoint = frame.InverseTransformPoint(collider.transform.TransformPoint(point));
            vertices.Add(assemblyPoint - center);
        }
        int[] source = mesh.triangles;
        for (int i = 0; i < source.Length; i += 3)
        {
            triangles.Add(start + source[i]);
            triangles.Add(start + source[i + 2]);
            triangles.Add(start + source[i + 1]);
        }
    }

    private static void AddQuad(List<int> triangles, int start, int a, int b, int c, int d)
    {
        triangles.Add(start + a); triangles.Add(start + b); triangles.Add(start + c);
        triangles.Add(start + a); triangles.Add(start + c); triangles.Add(start + d);
    }

    private void WriteBinaryStl(string path, List<Vector3> vertices, List<int> triangles)
    {
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new byte[80]);
            writer.Write(triangles.Count / 3);
            for (int i = 0; i < triangles.Count; i += 3)
            {
                Vector3 a = vertices[triangles[i]] * unitsToMillimetres;
                Vector3 b = vertices[triangles[i + 1]] * unitsToMillimetres;
                Vector3 c = vertices[triangles[i + 2]] * unitsToMillimetres;
                Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
                writer.Write(normal.x); writer.Write(normal.y); writer.Write(normal.z);
                WriteVector(writer, a); WriteVector(writer, b); WriteVector(writer, c);
                writer.Write((ushort)0);
            }
        }
    }

    private static void WriteVector(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.x); writer.Write(value.y); writer.Write(value.z);
    }

    private static Transform FindSceneTransform(string name)
    {
        foreach (Transform candidate in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (candidate.gameObject.scene.IsValid() && candidate.gameObject.activeInHierarchy && candidate.name == name)
                return candidate;
        }
        return null;
    }

    private static Transform FindDescendant(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform result = FindDescendant(root.GetChild(i), name);
            if (result) return result;
        }
        return null;
    }

    private static Transform FindOwningLink(Transform componentTransform)
    {
        Transform current = componentTransform;
        while (current)
        {
            if (current.name == "Collision")
                current = current.parent;

            if (current && FindDescendant(current, "Collision"))
                return current;

            current = current.parent;
        }
        return null;
    }

    private static BoxCollider FindBoxCollider(Transform collision, string name)
    {
        foreach (BoxCollider box in collision.GetComponentsInChildren<BoxCollider>(true))
            if (box.name == name) return box;
        return null;
    }

    private static Vector3 GetMinimumSumCorner(BoxCollider box)
    {
        Vector3 half = box.size * 0.5f;
        Vector3 minimum = box.center;
        float minimumSum = float.PositiveInfinity;
        foreach (Vector3 offset in new[]
        {
            new Vector3(-half.x, -half.y, -half.z), new Vector3(half.x, -half.y, -half.z),
            new Vector3(half.x, half.y, -half.z), new Vector3(-half.x, half.y, -half.z),
            new Vector3(-half.x, -half.y, half.z), new Vector3(half.x, -half.y, half.z),
            new Vector3(half.x, half.y, half.z), new Vector3(-half.x, half.y, half.z)
        })
        {
            Vector3 corner = box.center + offset;
            float sum = corner.x + corner.y + corner.z;
            if (sum < minimumSum)
            {
                minimumSum = sum;
                minimum = corner;
            }
        }
        return minimum;
    }

    private string ResolveDirectory(string directory)
    {
        string value = string.IsNullOrWhiteSpace(directory) ? ProjectRoot : directory;
        if (!Path.IsPathRooted(value)) value = Path.Combine(ProjectRoot, value);
        return NormalizePath(value);
    }

    private static string NormalizePath(string path)
    {
        char separator = Path.DirectorySeparatorChar;
        return path.Replace('/', separator).Replace('\\', separator);
    }

    private static string GetHierarchyPath(Transform transform)
    {
        var names = new List<string>();
        Transform current = transform;
        while (current) { names.Add(current.name); current = current.parent; }
        names.Reverse();
        return string.Join("/", names);
    }

    private static void ShowError(string message)
    {
        Debug.LogWarning("[AssemblyStlTool] " + message);
        EditorUtility.DisplayDialog("Assembly STL Tool", message, "OK");
    }

    private sealed class CenterRecord
    {
        public readonly Transform link;
        public readonly BoxCollider centerBox;
        public readonly Vector3 cornerLocal;
        public readonly Vector3 center;

        public CenterRecord(Transform link, BoxCollider centerBox, Vector3 cornerLocal, Vector3 center)
        {
            this.link = link;
            this.centerBox = centerBox;
            this.cornerLocal = cornerLocal;
            this.center = center;
        }
    }
#endif
}
