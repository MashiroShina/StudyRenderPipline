using UnityEngine;

public class InstancedMaterialProperties : MonoBehaviour
{
    
    [SerializeField]
    Color color = Color.white;
    [SerializeField, Range(0f, 1f)]
    float metallic;
    [SerializeField, Range(0f, 1f)]
    float smoothness = 0.5f;
    [SerializeField, ColorUsage(false, true)]
    Color emissionColor = Color.black;
    
    static MaterialPropertyBlock propertyBlock;
    
    static int colorID = Shader.PropertyToID("_Color");
    static int metallicId = Shader.PropertyToID("_Metallic");
    static int smoothnessId = Shader.PropertyToID("_Smoothness");
    static int emissionColorId = Shader.PropertyToID("_EmissionColor");

    private void Awake()
    {
        OnValidate();
    }

    void OnValidate()
    {
        if (propertyBlock == null) {
            propertyBlock = new MaterialPropertyBlock();
        } 
        propertyBlock.SetColor(colorID, color);
        propertyBlock.SetFloat(metallicId, metallic);
        propertyBlock.SetFloat(smoothnessId, smoothness);
        propertyBlock.SetColor(emissionColorId, emissionColor);
        GetComponent<MeshRenderer>().SetPropertyBlock(propertyBlock);
    }
}
