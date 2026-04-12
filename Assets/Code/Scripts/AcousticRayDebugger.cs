#if UNITY_EDITOR
using UnityEngine;

public static class AcousticRayDebugger
{
    private static Ray[] _activeRays;

    public static void SetDebugRays(Ray[] rays) => _activeRays = rays;

    public static void DrawRays()
    {
        if (_activeRays == null) return;

        for (int i = 0; i < _activeRays.Length; i++)
        {
            var ray = _activeRays[i];
            Gizmos.color = Color.red; // ray.hit ? Color.red : Color.green;

            Gizmos.DrawLine(ray.position, ray.position + (ray.direction * 100));

            Gizmos.DrawSphere(ray.position + (ray.direction * 100), 0.05f);
        }
    }
}
#endif