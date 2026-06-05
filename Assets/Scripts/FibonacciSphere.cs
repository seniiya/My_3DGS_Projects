using UnityEngine;

public class FibonacciSphere : MonoBehaviour
{
    [Header("Sampling")]
    public int pointCount = 200;
    public float radius = 3f;

    [Header("Optional: subject anchor")]
    public Transform center;   // 비워두면 자기 자신 transform 기준

    [Header("Gizmo")]
    public float gizmoSize = 0.05f;
    public bool drawLinesToCenter = false;

    Vector3 Center => center != null ? center.position : transform.position;

    public Vector3[] GeneratePoints()
    {
        var pts = new Vector3[pointCount];
        float goldenAngle = Mathf.PI * (3f - Mathf.Sqrt(5f)); // ≈ 2.39996 rad

        for (int i = 0; i < pointCount; i++)
        {
            float y = 1f - (i / (float)(pointCount - 1)) * 2f; // 1 → -1
            float r = Mathf.Sqrt(1f - y * y);
            float theta = goldenAngle * i;

            float x = Mathf.Cos(theta) * r;
            float z = Mathf.Sin(theta) * r;

            pts[i] = Center + new Vector3(x, y, z) * radius;
        }
        return pts;
    }

    void OnDrawGizmos()
    {
        if (pointCount < 2) return;

        var pts = GeneratePoints();
        Gizmos.color = Color.cyan;

        foreach (var p in pts)
        {
            Gizmos.DrawSphere(p, gizmoSize);
            if (drawLinesToCenter)
                Gizmos.DrawLine(Center, p);
        }
    }
}
