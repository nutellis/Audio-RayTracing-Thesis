using System;
using UnityEngine;
using UnityEngine.Events;


public class AcousticBase: MonoBehaviour
{
    public int InstanceID { get; private set; }
    public Bounds bounds;

    public new Collider collider;

    protected virtual void Awake()
    {
        InstanceID = gameObject.GetInstanceID();

        collider = gameObject.GetComponent<Collider>();
         if (collider == null)
         {
             Debug.LogError($"GameObject {gameObject.name} must have a Collider component.");
         }
         else
         {
             bounds = collider.bounds;
         }
    }

    protected void OnEnable()
    {
        ObjectRegistry<AcousticBase>.Instance.RegisterObject(InstanceID, this);
    }

    protected void OnDisable()
    {
        ObjectRegistry<AcousticBase>.Instance.UnregisterObject(InstanceID);
    }
    
    protected  void LocalBoundsToWorld(Vector3 bMin, Vector3 bMax)
    {
        Vector3 localCenter = (bMin + bMax) * 0.5f;
        Vector3 localExtents = (bMax -bMin) * 0.5f;

        Matrix4x4 absMatrix = transform.localToWorldMatrix;
        // Transform center
        Vector3 worldCenter = absMatrix.MultiplyPoint(localCenter);

        // Transform extents (using absolute values of the matrix basis vectors)
        for (int i = 0; i < 16; i++) absMatrix[i] = Mathf.Abs(absMatrix[i]);
    
        Vector3 worldExtents = absMatrix.MultiplyVector(localExtents);
        
        bounds = new Bounds(worldCenter, worldExtents * 2f);
    }

}
