using System.ComponentModel;
using UnityEngine;

[CreateAssetMenu]
public class AcousticProfile : ScriptableObject
{
    public float dbLevel = 58f;
    public float acousticWeight = 1f;

}

/*     public enum AcousticCategory
    {
        Faint = 1, //0-40 average: ~20 
        Normal = 3, //41-75 average: ~58
        Loud = 10, //76-100 average: ~88  
        Extreme = 20 //101-120 average: ~110
    }
*/