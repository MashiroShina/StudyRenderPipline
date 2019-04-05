using UnityEngine;

public class InstancedMaterialProperties : MonoBehaviour
{
    [SerializeField]
    Color color = Color.white;
    [SerializeField, Range(0f, 1f)]
    float smoothness = 0.5f;
    static MaterialPropertyBlock propertyBlock;
    static int colorID = Shader.PropertyToID("_Color");
    static int smoothnessId = Shader.PropertyToID("_Smoothness");
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
        propertyBlock.SetFloat(smoothnessId, smoothness);
        GetComponent<MeshRenderer>().SetPropertyBlock(propertyBlock);
    }
}
