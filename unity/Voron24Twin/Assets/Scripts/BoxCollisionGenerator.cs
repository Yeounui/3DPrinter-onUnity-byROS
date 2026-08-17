using UnityEngine;

public class BoxCollisionGenerator : MonoBehaviour
{
    public Transform baseLink;
    public Transform collisionParent;
    [Tooltip("면 선택 대상으로 검색할 루트. 비워 두면 생성기 오브젝트 아래를 검색합니다.")]
    public Transform selectionRoot;
    public string collisionNamePrefix = "box";
    public bool persistNameCounter = true;
    public int nextIndex;
    public float commonOffset = 0.01f;
    public bool useIndividualAxisOffsets;
    public Vector3 individualAxisOffsets = Vector3.one * 0.01f;
    [Range(0.1f, 0.999f)] public float axisAlignment = 0.85f;
    public float minimumThickness = 0.001f;
    [Tooltip("끄면 클릭한 삼각형 하나만 사용합니다. 켜면 연결된 공면 삼각형 영역까지 확장합니다.")]
    public bool expandPlanarSelection;
    public bool showSelectedFaces = true;
    public bool showSelectedFaceOutline;
}
