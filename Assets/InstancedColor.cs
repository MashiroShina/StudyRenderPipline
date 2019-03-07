using UnityEngine;

public class InstancedColor : MonoBehaviour
{
    [SerializeField]
    Color color = Color.white;
    static MaterialPropertyBlock propertyBlock;
    static int colorID = Shader.PropertyToID("_Color");
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
        GetComponent<MeshRenderer>().SetPropertyBlock(propertyBlock);
    }
}
