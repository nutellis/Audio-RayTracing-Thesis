using System;
using UnityEngine;

[CreateAssetMenu(fileName = "AcousticMaterial", menuName = "Scriptable Objects/Material")]
public class AcousticMaterial : ScriptableObject
{
    public int materialID;

    // max absorption is 6 bands
    readonly int maxEntries = 6;
    [Tooltip("Absorption coefficients for different frequency bands. Max 6 entries.")]
    public float[] absorptionCoefficients;

    public float scattering;

    public int transmission;
    
    private void OnValidate()
    {
        if (absorptionCoefficients != null && absorptionCoefficients.Length > maxEntries)
        {
            System.Array.Resize(ref absorptionCoefficients, maxEntries);
        }
    }
}
