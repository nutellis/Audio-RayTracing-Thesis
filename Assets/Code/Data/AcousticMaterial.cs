using UnityEngine;

[CreateAssetMenu(fileName = "AcousticMaterial", menuName = "Scriptable Objects/Material")]
public class AcousticMaterial : ScriptableObject
{
    public int materialID;

    public int absorption;

    public int scattering;

    public int transmission;
}
