using System;
using System.Runtime.InteropServices;
using UnityEngine;

public class ListenerController : MonoBehaviour
{



    // Start is called once before the first execution of Update after the MonoBehaviour is created

    // protected override void Awake()
    // {
    //     base.Awake();
    //
    // }
    
    void Start()
    {
       

    }

    // Update is called once per frame
    void Update()
    { }

    

#if UNITY_EDITOR
    /*private void OnDrawGizmos()
    {
        if(rayBuffer != null)
        {

            Ray[] data = new Ray[rayBuffer.count];

            rayBuffer.GetData(data);

            if (data == null) return;

            for (int i = 0; i < data.Length; i++)
            {
                var ray = data[i];
                Gizmos.color = Color.red;// ray.hit ? Color.red : Color.green;

                 Gizmos.DrawLine(ray.position, ray.position + (ray.direction * 10));

                // Draw a small tip to show directionality
                Gizmos.DrawSphere(ray.position + (ray.direction * 10), 0.05f);
            }
        }
    }*/
#endif
}
