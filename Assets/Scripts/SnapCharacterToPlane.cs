using UnityEngine;

public class SnapCharacterToPlane : MonoBehaviour
{
    public Transform floorPlane;
    public Transform visualRoot;   // 여기에 안쪽 Man_01 넣기
    public float footOffset = 0.02f;

    [ContextMenu("Snap Feet To Plane")]
    public void SnapFeetToPlane()
    {
        if (floorPlane == null || visualRoot == null)
        {
            Debug.LogWarning("Floor Plane 또는 Visual Root가 연결되지 않았습니다.");
            return;
        }

        Renderer[] renderers = visualRoot.GetComponentsInChildren<Renderer>();

        if (renderers.Length == 0)
        {
            Debug.LogWarning("캐릭터 Renderer를 찾지 못했습니다.");
            return;
        }

        Bounds bounds = renderers[0].bounds;

        foreach (Renderer r in renderers)
        {
            bounds.Encapsulate(r.bounds);
        }

        float characterBottomY = bounds.min.y;
        float planeY = floorPlane.position.y;
        float moveY = planeY - characterBottomY + footOffset;

        transform.position += new Vector3(0f, moveY, 0f);

        Debug.Log($"Snap 완료 / BottomY: {characterBottomY}, PlaneY: {planeY}, MoveY: {moveY}");
    }
}