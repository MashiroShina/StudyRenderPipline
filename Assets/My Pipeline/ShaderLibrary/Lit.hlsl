#ifndef MYRP_LIT_INCLUDED
#define MYRP_LIT_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Shadow/ShadowSamplingTent.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ImageBasedLighting.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/EntityLighting.hlsl"
#include "Light.hlsl"

CBUFFER_START(UnityPerFrame)
	float4x4 unity_MatrixVP;
CBUFFER_END

CBUFFER_START(UnityPerDraw)
	float4x4 unity_ObjectToWorld, unity_WorldToObject; 
	float4 unity_LightIndicesOffsetAndCount;//它的y分量表示物体受影响的光源数量。x分量是第二种方法要用到的偏移量。
	float4 unity_4LightIndices0, unity_4LightIndices1;//得到索引，它属于缓存区
	//BoxProjection&&ProbeBlend
	float4 unity_SpecCube0_BoxMin, unity_SpecCube0_BoxMax;
	float4 unity_SpecCube0_ProbePosition, unity_SpecCube0_HDR;
	float4 unity_SpecCube1_BoxMin, unity_SpecCube1_BoxMax;
	float4 unity_SpecCube1_ProbePosition, unity_SpecCube1_HDR;
	float4 unity_LightmapST;
	
	float4 unity_SHAr, unity_SHAg, unity_SHAb;
	float4 unity_SHBr, unity_SHBg, unity_SHBb;
	float4 unity_SHC;
CBUFFER_END

CBUFFER_START(UnityProbeVolume)
	float4 unity_ProbeVolumeParams;
	float4x4 unity_ProbeVolumeWorldToObject;
	float3 unity_ProbeVolumeSizeInv;
	float3 unity_ProbeVolumeMin;
CBUFFER_END

TEXTURE3D_FLOAT(unity_ProbeVolumeSH);
SAMPLER(samplerunity_ProbeVolumeSH);


float3 SampleLightProbes (LitSurface s) {
    if (unity_ProbeVolumeParams.x) {
		return SampleProbeVolumeSH4(
			TEXTURE3D_PARAM(unity_ProbeVolumeSH, samplerunity_ProbeVolumeSH),
			s.position, s.normal, unity_ProbeVolumeWorldToObject,
			unity_ProbeVolumeParams.y, unity_ProbeVolumeParams.z,
			unity_ProbeVolumeMin, unity_ProbeVolumeSizeInv
		);
	}
    else{
	float4 coefficients[7];
	coefficients[0] = unity_SHAr;
	coefficients[1] = unity_SHAg;
	coefficients[2] = unity_SHAb;
	coefficients[3] = unity_SHBr;
	coefficients[4] = unity_SHBg;
	coefficients[5] = unity_SHBb;
	coefficients[6] = unity_SHC;
	return max(0.0, SampleSH9(coefficients, s.normal));
	}
	
}
//Light Buffer======================================================================================================================================================
#define MAX_VISIBLE_LIGHTS 16
CBUFFER_START(_LightBuffer)
	float4 _VisibleLightColors[MAX_VISIBLE_LIGHTS];
	float4 _VisibleLightDirectionsOrPositions[MAX_VISIBLE_LIGHTS];
	float4 _VisibleLightAttenuations[MAX_VISIBLE_LIGHTS];
	float4 _VisibleLightSpotDirections[MAX_VISIBLE_LIGHTS];
CBUFFER_END

//float3 DiffuseLight (int index, float3 normal, float3 worldPos, float shadowAttenuation) 
float3 GenericLight (int index, LitSurface s, float shadowAttenuation){
	float3 lightColor = _VisibleLightColors[index].rgb;
	float4 lightPositionOrDirection = _VisibleLightDirectionsOrPositions[index];
	float4 lightAttenuation = _VisibleLightAttenuations[index];
	float3 spotDirection = _VisibleLightSpotDirections[index].xyz;
	
	float3 lightVector = lightPositionOrDirection.xyz - s.position * lightPositionOrDirection.w;//直射光w分量为0，如果是点光源为1刚好
	//这里的worldpos是 当前物体的，这样子刚好得到点光源对当前物体的向量
	float3 lightDirection = normalize(lightVector);
	//float diffuse = saturate(dot(normal, lightDirection));
	float3 color = LightSurface(s, lightDirection);
	//设置光照范围(点光源)+设置光照衰减========================================================
	//(1-(d^2/r^2)^2)^2)
	float rangeFade = dot(lightVector, lightVector) * lightAttenuation.x;//点光源范围
	rangeFade = saturate(1.0 - rangeFade * rangeFade);
	rangeFade *= rangeFade;
	
	float spotFade = dot(spotDirection, lightDirection);//聚光灯范围
	spotFade = saturate(spotFade * lightAttenuation.z + lightAttenuation.w);
	spotFade *= spotFade;
	
	float distanceSqr = max(dot(lightVector, lightVector), 0.00001);
	color *= shadowAttenuation * spotFade * rangeFade / distanceSqr;
	//======================================================================================

	return color * lightColor;//除了主光源的其他光源
}
float3 BoxProjection (
	float3 direction, float3 position,
	float4 cubemapPosition, float4 boxMin, float4 boxMax
) {
	UNITY_BRANCH
	if (cubemapPosition.w > 0) {
		float3 factors =
			((direction > 0 ? boxMax.xyz : boxMin.xyz) - position) / direction;
		float scalar = min(min(factors.x, factors.y), factors.z);
		direction = direction * scalar + (position - cubemapPosition.xyz);
	}
	return direction;
}
float3 SampleEnvironment (LitSurface s) {
    float3 reflectVector = reflect(-s.viewDir, s.normal);
    float mip = PerceptualRoughnessToMipmapLevel(s.perceptualRoughness);
    float3 uvw = BoxProjection(
		reflectVector, s.position, unity_SpecCube0_ProbePosition,
		unity_SpecCube0_BoxMin, unity_SpecCube0_BoxMax
	);
    float4 sample = SAMPLE_TEXTURECUBE_LOD(
		unity_SpecCube0, samplerunity_SpecCube0, uvw, mip
	);
    float3 color = DecodeHDREnvironment(sample, unity_SpecCube0_HDR);
    
    float blend = unity_SpecCube0_BoxMin.w;
	if (blend < 0.99999) {
		uvw = BoxProjection(
			reflectVector, s.position,
			unity_SpecCube1_ProbePosition,
			unity_SpecCube1_BoxMin, unity_SpecCube1_BoxMax
		);
		sample = SAMPLE_TEXTURECUBE_LOD(
			unity_SpecCube1, samplerunity_SpecCube0, uvw, mip
		);
		color = lerp(DecodeHDREnvironment(sample, unity_SpecCube1_HDR), color, blend);
	}
	return color;
}
//======================================================================================================================================================================

CBUFFER_START(UnityPerCamera)
	float3 _WorldSpaceCameraPos;
CBUFFER_END

//_ShadowBuffer && Sampling Depth=======================================================================================================================================
CBUFFER_START(_ShadowBuffer)
	float4x4 _WorldToShadowMatrices[MAX_VISIBLE_LIGHTS];
	float4 _ShadowData[MAX_VISIBLE_LIGHTS];//x存阴影强度>0表示有. y=0表示硬阴影. zw存放xy的偏移 ,在前面c#中z存是否为直射光然后被偏移覆盖
    float4 _ShadowMapSize;
	float4 _GlobalShadowData;
	float4x4 _WorldToShadowCascadeMatrices[5];
	float4 _CascadedShadowMapSize;
	float _CascadedShadowStrength;
	float4 _CascadeCullingSpheres[4];
CBUFFER_END

CBUFFER_START(UnityPerMaterial)
	float4 _MainTex_ST;
	float _Cutoff;
CBUFFER_END

TEXTURE2D_SHADOW(_ShadowMap);
SAMPLER_CMP(sampler_ShadowMap);

TEXTURE2D_SHADOW(_CascadedShadowMap);
SAMPLER_CMP(sampler_CascadedShadowMap);

TEXTURE2D(_MainTex);
SAMPLER(sampler_MainTex);

//lightMap
TEXTURE2D(unity_Lightmap);
SAMPLER(samplerunity_Lightmap);

//判断是否在剔除球里
float InsideCascadeCullingSphere (int index, float3 worldPos) {
	float4 s = _CascadeCullingSpheres[index];
	return dot(worldPos - s.xyz, worldPos - s.xyz) < s.w;
}

float DistanceToCameraSqr (float3 worldPos) {
	float3 cameraToFragment = worldPos - _WorldSpaceCameraPos;
	return dot(cameraToFragment, cameraToFragment);
}

float HardShadowAttenuation (float4 shadowPos,bool cascade = false) {

    if (cascade) {
		return SAMPLE_TEXTURE2D_SHADOW(
			_CascadedShadowMap, sampler_CascadedShadowMap, shadowPos.xyz
		);
	}
	else{
	//与位置坐标（position）的Z值比较来进行深度测试。如果该点位置的z值比在阴影贴图中对应点的值要小就会返回1，这说明他比任何投射阴影的物体离光源都要近。
	//反之，在阴影投射物后面就会返回0。              它需要一张贴图，一个采样器状态，以及对应的阴影空间位置作为参数。
	float attenuation;
    return  attenuation = SAMPLE_TEXTURE2D_SHADOW(_ShadowMap, sampler_ShadowMap, shadowPos.xyz);
	}
}

float SoftShadowAttenuation (float4 shadowPos,bool cascade = false) {
    //#if defined(_SHADOWS_SOFT)
		real tentWeights[9];
		real2 tentUVs[9];
		
		float4 size = cascade ? _CascadedShadowMapSize : _ShadowMapSize;
		
		SampleShadow_ComputeSamples_Tent_5x5(
			size, shadowPos.xy, tentWeights, tentUVs
		);		
	    float attenuation = 0;
		for (int i = 0; i < 9; i++) {
			//attenuation += tentWeights[i] * SAMPLE_TEXTURE2D_SHADOW(
			//	_ShadowMap, sampler_ShadowMap, float3(tentUVs[i].xy, shadowPos.z)
			//);
			attenuation += tentWeights[i] * HardShadowAttenuation(
			float4(tentUVs[i].xy, shadowPos.z, 0), cascade
		    );
		}
	//#endif	
	return attenuation;
}

float ShadowAttenuation (int index, float3 worldPos) {

   #if !defined(_RECEIVE_SHADOWS)
		return 1.0;
	#elif !defined(_SHADOWS_HARD) && !defined(_SHADOWS_SOFT)
		return 1.0;
	#endif
	
	if (DistanceToCameraSqr(worldPos) > _GlobalShadowData.y) {
		return 1.0;
	}
    
    if (_ShadowData[index].x <= 0 || DistanceToCameraSqr(worldPos) > _GlobalShadowData.y) //x存阴影强度>0表示有
    {
		return 1.0;
	}
    //世界位置转为阴影空间位置。
	float4 shadowPos = mul(_WorldToShadowMatrices[index], float4(worldPos, 1.0));
	shadowPos.xyz /= shadowPos.w;// NDC
	shadowPos.xy = saturate(shadowPos.xy); 
	//这里是采样的图块的位置（有图案）
	//* _GlobalShadowData.x 是让大图块变成小图块//new Vector4(tileScale, shadowDistance*shadowDistance)
	//+ _ShadowData[index].zw是移动小图块
	//0～1
	shadowPos.xy = shadowPos.xy * _GlobalShadowData.x + _ShadowData[index].zw;//z=tileOffsetX * tileScale;&&w=tileOffsetY * tileScale;
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

float CascadedShadowAttenuation (float3 worldPos) {
    #if !defined(_RECEIVE_SHADOWS)
		return 1.0;
	#elif !defined(_CASCADED_SHADOWS_HARD) && !defined(_CASCADED_SHADOWS_SOFT)
		return 1.0;
	#endif
	float4 cascadeFlags = float4(//(1,1,1,1), (0,1,1,1), (0,0,1,1), (0,0,0,1),(0,0,0,0)
		InsideCascadeCullingSphere(0, worldPos),
		InsideCascadeCullingSphere(1, worldPos),
		InsideCascadeCullingSphere(2, worldPos),
		InsideCascadeCullingSphere(3, worldPos)
	);
	//return dot(cascadeFlags, .8);
	cascadeFlags.yzw = saturate(cascadeFlags.yzw - cascadeFlags.xyz);
	float cascadeIndex = 4 - dot(cascadeFlags, float4(4, 3, 2, 1));//0,1,2,3,4
	float4 shadowPos = mul(
		_WorldToShadowCascadeMatrices[cascadeIndex], float4(worldPos, 1.0)
	);
	float attenuation;
	#if defined(_CASCADED_SHADOWS_HARD)
		attenuation = HardShadowAttenuation(shadowPos, true);
	#else
	    attenuation = SoftShadowAttenuation(shadowPos, true);
	#endif
	
	return lerp(1, attenuation, _CascadedShadowStrength);
}
float3 MainLight (LitSurface s){
    float shadowAttenuation = CascadedShadowAttenuation(s.position);
    float3 lightColor = _VisibleLightColors[0].rgb;
    float3 lightDirection = _VisibleLightDirectionsOrPositions[0].xyz;
    //float diffuse = saturate(dot(normal, lightDirection));
    float3 color = LightSurface(s, lightDirection);
    color *= shadowAttenuation;
    return color * lightColor;
}
//=====================================================================================================================================================================



//instance必要的宏===========================================================================
#define UNITY_MATRIX_M unity_ObjectToWorld
#define UNITY_MATRIX_I_M unity_WorldToObject
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityInstancing.hlsl"
//==========================================================================================

UNITY_INSTANCING_BUFFER_START(PerInstance)      //CBUFFER_START(UnityPerMaterial)
	UNITY_DEFINE_INSTANCED_PROP(float4, _Color) //float4 _Color;
	UNITY_DEFINE_INSTANCED_PROP(float, _Metallic)
	UNITY_DEFINE_INSTANCED_PROP(float, _Smoothness)
	UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionColor)
UNITY_INSTANCING_BUFFER_END(PerInstance)        //CBUFFER_END

struct VertexInput {
	float4 pos : POSITION;
	float3 normal : NORMAL;
	float2 uv : TEXCOORD0;
	float2 lightmapUV : TEXCOORD1;
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct VertexOutput {
	float4 clipPos : SV_POSITION;
	float3 normal : TEXCOORD0;
	float3 worldPos : TEXCOORD1;
	float3 vertexLighting : TEXCOORD2;
	float2 uv : TEXCOORD3;
	#if defined(LIGHTMAP_ON)
		float2 lightmapUV : TEXCOORD4;
	#endif
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

float3 SampleLightmap (float2 uv) {
	return SampleSingleLightmap(
		TEXTURE2D_PARAM(unity_Lightmap, samplerunity_Lightmap), uv,
		float4(1, 1, 0, 0),
		#if defined(UNITY_LIGHTMAP_FULL_HDR)
			false,
		#else
			true,
		#endif
		float4(LIGHTMAP_HDR_MULTIPLIER, LIGHTMAP_HDR_EXPONENT, 0.0, 0.0)
	);
}

float3 GlobalIllumination (VertexOutput input, LitSurface surface) {
	#if defined(LIGHTMAP_ON)
		return SampleLightmap(input.lightmapUV);
	#else
		return SampleLightProbes(surface);
	#endif
}

VertexOutput LitPassVertex (VertexInput input) {
	VertexOutput output;
	UNITY_SETUP_INSTANCE_ID(input);
	UNITY_TRANSFER_INSTANCE_ID(input, output);//在Vertex Shader中把Instance ID从输入结构拷贝至输出结构中
	float4 worldPos = mul(UNITY_MATRIX_M, float4(input.pos.xyz, 1.0));
	output.clipPos = mul(unity_MatrixVP,worldPos);
	//float4 screens=(1024,1024,1+1.0/1024,1+1.0/1024);
	//output.worldPos=output.clipPos/screens;
	#if defined(UNITY_ASSUME_UNIFORM_SCALING)
		output.normal = mul((float3x3)UNITY_MATRIX_M, input.normal);
	#else
		output.normal = normalize(mul(input.normal, (float3x3)UNITY_MATRIX_I_M));
	#endif
	output.worldPos = worldPos.xyz;
	output.uv = TRANSFORM_TEX(input.uv, _MainTex);
	
	LitSurface surface = GetLitSurfaceVertex(output.normal, output.worldPos);
	output.vertexLighting = 0;//顶点光照 四个之后影响的光源当不重要的处理 
	for (int i = 4; i < min(unity_LightIndicesOffsetAndCount.y, 8); i++) {
		int lightIndex = unity_4LightIndices1[i - 4];
		//output.vertexLighting += DiffuseLight(lightIndex, output.normal, output.worldPos,1);//顶点光源不会有阴影，所以在LitPassVertex.中将阴影衰减设为1
		output.vertexLighting += GenericLight(lightIndex, surface, 1);
	}
	#if defined(LIGHTMAP_ON)
		output.lightmapUV =
			input.lightmapUV * unity_LightmapST.xy + unity_LightmapST.zw;
	#endif
	return output;
}

float4 LitPassFragment (VertexOutput input, FRONT_FACE_TYPE isFrontFace : FRONT_FACE_SEMANTIC) : SV_TARGET {
    UNITY_SETUP_INSTANCE_ID(input);
    input.normal = normalize(input.normal);
    input.normal = IS_FRONT_VFACE(isFrontFace, input.normal, -input.normal);
    //float3 albedo = UNITY_ACCESS_INSTANCED_PROP(PerInstance, _Color).rgb;//放入instance缓存
	float4 albedoAlpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
	albedoAlpha *= UNITY_ACCESS_INSTANCED_PROP(PerInstance, _Color);
	#if defined(_CLIPPING_ON)
	clip(albedoAlpha.a - _Cutoff);
	#endif
	
	//for (int i = 0; i < MAX_VISIBLE_LIGHTS; i++) {
	//	diffuseLight += DiffuseLight(i, input.normal, input.worldPos);
	//}
	//第一个unity_4LightIndiczes0包含了前4个光源,unity_LightIndicesOffsetAndCount.y可以看到受到到的光照数量
	float3 viewDir = normalize(_WorldSpaceCameraPos - input.worldPos.xyz);
	LitSurface surface = GetLitSurface(
		input.normal, input.worldPos, viewDir,
		albedoAlpha.rgb,
		UNITY_ACCESS_INSTANCED_PROP(PerInstance, _Metallic),
		UNITY_ACCESS_INSTANCED_PROP(PerInstance, _Smoothness)
	);
	#if defined(_PREMULTIPLY_ALPHA)
		PremultiplyAlpha(surface, albedoAlpha.a);
	#endif
	//diffuse
	float3 color = input.vertexLighting * surface.diffuse;
	
	#if defined(_CASCADED_SHADOWS_HARD) || defined(_CASCADED_SHADOWS_SOFT)
		color += MainLight(surface);//lightDirection
	#endif
	for (int i = 0; i < min(unity_LightIndicesOffsetAndCount.y,4); i++) {
		int lightIndex = unity_4LightIndices0[i];
		float shadowAttenuation = ShadowAttenuation(lightIndex, input.worldPos);
		//diffuseLight += DiffuseLight(lightIndex, input.normal, input.worldPos, shadowAttenuation);
		color += GenericLight(lightIndex, surface, shadowAttenuation);
	}
	//for (int i = 4; i < min(unity_LightIndicesOffsetAndCount.y, 8); i++) {
	//	int lightIndex = unity_4LightIndices1[i - 4];
	//	diffuseLight += DiffuseLight(lightIndex, input.normal, input.worldPos);
	//}
	//float3 color = diffuseLight*albedoAlpha.rgb;
	color += ReflectEnvironment(surface, SampleEnvironment(surface));
	color += GlobalIllumination(input, surface) * surface.diffuse;
	color += UNITY_ACCESS_INSTANCED_PROP(PerInstance, _EmissionColor).rgb;
	return float4(color, albedoAlpha.a);
}

#endif // MYRP_UNLIT_INCLUDED