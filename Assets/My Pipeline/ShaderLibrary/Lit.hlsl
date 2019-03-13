#ifndef MYRP_LIT_INCLUDED
#define MYRP_LIT_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Shadow/ShadowSamplingTent.hlsl"
CBUFFER_START(UnityPerFrame)
	float4x4 unity_MatrixVP;
CBUFFER_END

CBUFFER_START(UnityPerDraw)
	float4x4 unity_ObjectToWorld; 
	float4 unity_LightIndicesOffsetAndCount;//它的y分量表示物体受影响的光源数量。x分量是第二种方法要用到的偏移量。
	float4 unity_4LightIndices0, unity_4LightIndices1;//得到索引，它属于缓存区
CBUFFER_END

//Light Buffer===========================================================================
#define MAX_VISIBLE_LIGHTS 16
CBUFFER_START(_LightBuffer)
	float4 _VisibleLightColors[MAX_VISIBLE_LIGHTS];
	float4 _VisibleLightDirectionsOrPositions[MAX_VISIBLE_LIGHTS];
	float4 _VisibleLightAttenuations[MAX_VISIBLE_LIGHTS];
	float4 _VisibleLightSpotDirections[MAX_VISIBLE_LIGHTS];
CBUFFER_END


float3 DiffuseLight (int index, float3 normal, float3 worldPos, float shadowAttenuation) {
	float3 lightColor = _VisibleLightColors[index].rgb;
	float4 lightPositionOrDirection = _VisibleLightDirectionsOrPositions[index];
	float4 lightAttenuation = _VisibleLightAttenuations[index];
	float3 spotDirection = _VisibleLightSpotDirections[index].xyz;
	
	float3 lightVector = lightPositionOrDirection.xyz - worldPos * lightPositionOrDirection.w;//直射光w分量为0，如果是点光源为1刚好
	//这里的worldpos是 当前物体的，这样子刚好得到点光源对当前物体的向量
	float3 lightDirection = normalize(lightVector);
	float diffuse = saturate(dot(normal, lightDirection));
	//设置光照范围(点光源)+设置光照衰减========================================================
	//(1-(d^2/r^2)^2)^2)
	float rangeFade = dot(lightVector, lightVector) * lightAttenuation.x;//点光源范围
	rangeFade = saturate(1.0 - rangeFade * rangeFade);
	rangeFade *= rangeFade;
	
	float spotFade = dot(spotDirection, lightDirection);//聚光灯范围
	spotFade = saturate(spotFade * lightAttenuation.z + lightAttenuation.w);
	spotFade *= spotFade;
	
	float distanceSqr = max(dot(lightVector, lightVector), 0.00001);
	diffuse *= shadowAttenuation * spotFade * rangeFade / distanceSqr;
	//======================================================================================

	return diffuse * lightColor;
}
//===========================================================================================

//_ShadowBuffer && Sampling Depth============================================================
CBUFFER_START(_ShadowBuffer)
	//float4x4 _WorldToShadowMatrix;
	//float _ShadowStrength;
	float4x4 _WorldToShadowMatrices[MAX_VISIBLE_LIGHTS];
	float4 _ShadowData[MAX_VISIBLE_LIGHTS];
	float4 _ShadowMapSize;
CBUFFER_END

TEXTURE2D_SHADOW(_ShadowMap);
SAMPLER_CMP(sampler_ShadowMap);

float HardShadowAttenuation (float4 shadowPos) {
	//与位置坐标（position）的Z值比较来进行深度测试。如果该点位置的z值比在阴影贴图中对应点的值要小就会返回1，这说明他比任何投射阴影的物体离光源都要近。
	//反之，在阴影投射物后面就会返回0。              它需要一张贴图，一个采样器状态，以及对应的阴影空间位置作为参数。
	float attenuation;
return  attenuation = SAMPLE_TEXTURE2D_SHADOW(_ShadowMap, sampler_ShadowMap, shadowPos.xyz);
}

float SoftShadowAttenuation (float4 shadowPos) {
    //#if defined(_SHADOWS_SOFT)
		real tentWeights[9];
		real2 tentUVs[9];
		SampleShadow_ComputeSamples_Tent_5x5(
			_ShadowMapSize, shadowPos.xy, tentWeights, tentUVs
		);
	    float attenuation = 0;
		for (int i = 0; i < 9; i++) {
			attenuation += tentWeights[i] * SAMPLE_TEXTURE2D_SHADOW(
				_ShadowMap, sampler_ShadowMap, float3(tentUVs[i].xy, shadowPos.z)
			);
		}
	//#endif	
	return attenuation;
}

float ShadowAttenuation (int index, float3 worldPos) {

    #if !defined(_SHADOWS_HARD) && !defined(_SHADOWS_SOFT)
		return 1.0;
	#endif
    
    if (_ShadowData[index].x <= 0) //x存阴影强度>0表示有
    {
		return 1.0;
	}
    //世界位置转为阴影空间位置。
	float4 shadowPos = mul(_WorldToShadowMatrices[index], float4(worldPos, 1.0));
	shadowPos.xyz /= shadowPos.w;// NDC
	float attenuation;
	
	#if defined(_SHADOWS_HARD)
	#if defined(_SHADOWS_SOFT)
	
	if(_ShadowData[index].y == 0)//0表示硬阴影
	{
	     attenuation = HardShadowAttenuation(shadowPos);
	}
	else
	{
         attenuation = SoftShadowAttenuation(shadowPos);
	}
	
	#else
			attenuation = HardShadowAttenuation(shadowPos);
	#endif
	#else
		    attenuation = SoftShadowAttenuation(shadowPos);
	#endif
	
	return lerp(1, attenuation, _ShadowData[index].x);
}

//==========================================================================================



//instance必要的宏===========================================================================
#define UNITY_MATRIX_M unity_ObjectToWorld
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityInstancing.hlsl"
//==========================================================================================

UNITY_INSTANCING_BUFFER_START(PerInstance)      //CBUFFER_START(UnityPerMaterial)
	UNITY_DEFINE_INSTANCED_PROP(float4, _Color) //float4 _Color;
UNITY_INSTANCING_BUFFER_END(PerInstance)        //CBUFFER_END

struct VertexInput {
	float4 pos : POSITION;
	float3 normal : NORMAL;
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct VertexOutput {
	float4 clipPos : SV_POSITION;
	float3 normal : TEXCOORD0;
	float3 worldPos : TEXCOORD1;
	float3 vertexLighting : TEXCOORD2;
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

VertexOutput LitPassVertex (VertexInput input) {
	VertexOutput output;
	UNITY_SETUP_INSTANCE_ID(input);
	UNITY_TRANSFER_INSTANCE_ID(input, output);//在Vertex Shader中把Instance ID从输入结构拷贝至输出结构中
	float4 worldPos = mul(UNITY_MATRIX_M, float4(input.pos.xyz, 1.0));
	output.clipPos = mul(unity_MatrixVP,worldPos);
	output.normal = mul((float3x3)UNITY_MATRIX_M, input.normal);
	output.worldPos = worldPos.xyz;
	
	output.vertexLighting = 0;//顶点光照 四个之后影响的光源当不重要的处理 
	for (int i = 4; i < min(unity_LightIndicesOffsetAndCount.y, 8); i++) {
		int lightIndex = unity_4LightIndices1[i - 4];
		output.vertexLighting += DiffuseLight(lightIndex, output.normal, output.worldPos,1);//顶点光源不会有阴影，所以在LitPassVertex.中将阴影衰减设为1
	}
	
	return output;
}

float4 LitPassFragment (VertexOutput input) : SV_TARGET {
    UNITY_SETUP_INSTANCE_ID(input);
    input.normal = normalize(input.normal);
    float3 albedo = UNITY_ACCESS_INSTANCED_PROP(PerInstance, _Color).rgb;//放入instance缓存
	
	float3 diffuseLight = 0;
	//for (int i = 0; i < MAX_VISIBLE_LIGHTS; i++) {
	//	diffuseLight += DiffuseLight(i, input.normal, input.worldPos);
	//}
	
	//第一个unity_4LightIndices0包含了前4个光源,unity_LightIndicesOffsetAndCount.y可以看到受到到的光照数量
	diffuseLight = input.vertexLighting;
	for (int i = 0; i < min(unity_LightIndicesOffsetAndCount.y,4); i++) {
		int lightIndex = unity_4LightIndices0[i];
		float shadowAttenuation = ShadowAttenuation(lightIndex, input.worldPos);
		diffuseLight += DiffuseLight(lightIndex, input.normal, input.worldPos, shadowAttenuation);
	}
	//for (int i = 4; i < min(unity_LightIndicesOffsetAndCount.y, 8); i++) {
	//	int lightIndex = unity_4LightIndices1[i - 4];
	//	diffuseLight += DiffuseLight(lightIndex, input.normal, input.worldPos);
	//}
	float3 color = diffuseLight*albedo;
	return float4(color, 1);
}

#endif // MYRP_UNLIT_INCLUDED