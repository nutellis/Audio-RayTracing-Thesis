using System;
using UnityEngine;

[CreateAssetMenu(fileName = "AcousticMaterial", menuName = "Scriptable Objects/Material")]
public class AcousticMaterial : ScriptableObject
{
    public int materialID;
    
    [Tooltip("Absorption coefficients for different frequency bands. Max 6 entries.")]
    public float[] absorptionCoefficients;

    public float scattering;

    public float[] transmissionCoefficients;
    
    private void OnValidate()
    {
        if (transmissionCoefficients != null && transmissionCoefficients.Length > 3)
        {
            System.Array.Resize(ref transmissionCoefficients, 3);
        }
        if (absorptionCoefficients != null && absorptionCoefficients.Length > 6)
        {
            System.Array.Resize(ref absorptionCoefficients, 6);
        }
    }
}
