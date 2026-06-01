using UnityEngine;

public class SimpleWaypointWalker : MonoBehaviour
{
    public Transform[] waypoints;
    public float moveSpeed = 0.7f;
    public float turnSpeed = 8f;
    public float stopDistance = 0.05f;

    public Animator animator;

    private int currentIndex = 0;

    void Update()
    {
        if (waypoints == null || waypoints.Length == 0) return;

        Transform target = waypoints[currentIndex];

        Vector3 targetPos = target.position;
        targetPos.y = transform.position.y;

        Vector3 dir = targetPos - transform.position;
        float dist = dir.magnitude;

        bool isWalking = dist > stopDistance;

        if (isWalking)
        {
            Vector3 moveDir = dir.normalized;

            Quaternion targetRot = Quaternion.LookRotation(moveDir);
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                targetRot,
                turnSpeed * Time.deltaTime
            );

            transform.position += moveDir * moveSpeed * Time.deltaTime;
        }
        else
        {
            currentIndex++;

            if (currentIndex >= waypoints.Length)
                currentIndex = 0;
        }

        if (animator != null)
        {
            animator.SetBool("IsWalking", isWalking);
        }
    }
}