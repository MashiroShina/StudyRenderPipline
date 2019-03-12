using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using Conditional = System.Diagnostics.ConditionalAttribute;

public class MyPipeline : RenderPipeline
{
	
	CullResults cull;
	Material errorMaterial;
	
	
	
	
	//传递光参数======================================================
	private const int maxVisibleLights = 16;
	private static int visibleLightColorsId = Shader.PropertyToID("_VisibleLightColors");//所有光颜色
	private static int visibleLightDirectionsOrPositionsId = Shader.PropertyToID("_VisibleLightDirectionsOrPositions");//直射光
	static int visibleLightAttenuationsId = Shader.PropertyToID("_VisibleLightAttenuations");//点光源
	static int visibleLightSpotDirectionsId = Shader.PropertyToID("_VisibleLightSpotDirections");//聚光灯
	static int lightIndicesOffsetAndCountID = Shader.PropertyToID("unity_LightIndicesOffsetAndCount");
	Vector4[] visibleLightColors = new Vector4[maxVisibleLights];
	Vector4[] visibleLightDirectionsOrPositions = new Vector4[maxVisibleLights];
	Vector4[] visibleLightAttenuations = new Vector4[maxVisibleLights];
	Vector4[] visibleLightSpotDirections = new Vector4[maxVisibleLights];
	//===============================================================
	
	//传递阴影参数=====================================================
	RenderTexture shadowMap;
	int shadowMapSize;
	static int shadowMapId = Shader.PropertyToID("_ShadowMap");
	static int worldToShadowMatrixId = Shader.PropertyToID("_WorldToShadowMatrix");
	static int shadowBiasId = Shader.PropertyToID("_ShadowBias");
	static int shadowStrengthId = Shader.PropertyToID("_ShadowStrength");
	static int shadowMapSizeId = Shader.PropertyToID("_ShadowMapSize");//soft shadow
	
	const string shadowsSoftKeyword = "_SHADOWS_SOFT";
	Vector4[] shadowData = new Vector4[maxVisibleLights];
	//===============================================================
	
	DrawRendererFlags drawFlags;//设置渲染处理方式
	public MyPipeline(bool dynamicBatching,bool instancing,int shadowMapSize)
	{
		GraphicsSettings.lightsUseLinearIntensity = true;//gamma 才有的光强度到 liner
		if (dynamicBatching)
		{
			drawFlags = DrawRendererFlags.EnableDynamicBatching;
		}

		if (instancing)
		{
			drawFlags |= DrawRendererFlags.EnableInstancing;
		}
		this.shadowMapSize = shadowMapSize;
	}

	CommandBuffer cameraBuffer = new CommandBuffer {
		name = "Render Camera"
	};
	CommandBuffer shadowBuffer = new CommandBuffer {
		name = "Render Shadows"
	};

	public override void Render (ScriptableRenderContext renderContext, Camera[] cameras) 
	{
		base.Render(renderContext, cameras);

		foreach (var camera in cameras) {
			Render(renderContext, camera);
		}
	}

	void Render (ScriptableRenderContext context, Camera camera) {
		ScriptableCullingParameters cullingParameters;
		if (!CullResults.GetCullingParameters(camera, out cullingParameters)) {
			return;
		}

#if UNITY_EDITOR
		if (camera.cameraType == CameraType.SceneView) {
			ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
		}
#endif

		CullResults.Cull(ref cullingParameters, context, ref cull);//ref cull来减少new cull

		if (cull.visibleLights.Count > 0)
		{
			ConfigureLights();
			RenderShadows(context);
		}else 
		{
			cameraBuffer.SetGlobalVector(
				lightIndicesOffsetAndCountID, Vector4.zero
			);
		}
		ConfigureLights();

		context.SetupCameraProperties(camera);//相当于mvp Setup camera specific global shader variables.

		CameraClearFlags clearFlags = camera.clearFlags;
		cameraBuffer.ClearRenderTarget(
			(clearFlags & CameraClearFlags.Depth) != 0,
			(clearFlags & CameraClearFlags.Color) != 0,
			camera.backgroundColor
		);
		
//		if (cull.visibleLights.Count > 0) {
//			ConfigureLights();
//		}
//		else //这里是告诉lightIndicesOffsetAndCountID.y为0 这样子是0灯光的时候
//		{
//			cameraBuffer.SetGlobalVector(
//				lightIndicesOffsetAndCountID, Vector4.zero
//			);
//		}
		
		//cameraBuffer.ClearRenderTarget(true, false, Color.clear);
		cameraBuffer.BeginSample("Render Camera");
		//传递光参数=========
		cameraBuffer.SetGlobalVectorArray(visibleLightColorsId, visibleLightColors);//传递数组到shader里通过ID	
		cameraBuffer.SetGlobalVectorArray(visibleLightDirectionsOrPositionsId, visibleLightDirectionsOrPositions);
		cameraBuffer.SetGlobalVectorArray(visibleLightAttenuationsId, visibleLightAttenuations);
		cameraBuffer.SetGlobalVectorArray(visibleLightSpotDirectionsId, visibleLightSpotDirections);
		//==================
		context.ExecuteCommandBuffer(cameraBuffer);
		cameraBuffer.Clear();

		var drawSettings = new DrawRendererSettings(
			camera, new ShaderPassName("SRPDefaultUnlit")
		)
		{
			flags = drawFlags, 
		};
		if (cull.visibleLights.Count > 0)//设置光源索引。light数量=0时 设置Unity会奔溃的     
		{
			drawSettings.rendererConfiguration = RendererConfiguration.PerObjectLightIndices8;
		}           
		//drawSettings.flags = drawFlags;//设置动态批处理和GPUinstance
		drawSettings.sorting.flags = SortFlags.CommonOpaque;//设置渲染顺序

		var filterSettings = new FilterRenderersSettings(true) {
			renderQueueRange = RenderQueueRange.opaque
		};
		//start Render opaque
		context.DrawRenderers(
			cull.visibleRenderers, ref drawSettings, filterSettings
		);
		//start Render Skybox
		context.DrawSkybox(camera);
	
		drawSettings.sorting.flags = SortFlags.CommonTransparent;
		//start Render transparent
		filterSettings.renderQueueRange = RenderQueueRange.transparent;
		context.DrawRenderers(
			cull.visibleRenderers, ref drawSettings, filterSettings
		);

		DrawDefaultPipeline(context, camera);

		cameraBuffer.EndSample("Render Camera");
		context.ExecuteCommandBuffer(cameraBuffer);
		cameraBuffer.Clear();

		context.Submit();
		if (shadowMap)
		{
			RenderTexture.ReleaseTemporary(shadowMap);
			shadowMap = null;
		}
	}
	
	void RenderShadows (ScriptableRenderContext context) {
		shadowMap = RenderTexture.GetTemporary(
			shadowMapSize, shadowMapSize, 16, RenderTextureFormat.Shadowmap
		);
		shadowMap.filterMode = FilterMode.Bilinear;
		shadowMap.wrapMode = TextureWrapMode.Clamp;
		
		
		CoreUtils.SetRenderTarget(
			shadowBuffer, shadowMap,
			RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
			ClearFlag.Depth
		);
		
		
		shadowBuffer.BeginSample("Render Shadows");
		context.ExecuteCommandBuffer(shadowBuffer);
		shadowBuffer.Clear();

		for (int i = 0; i < cull.visibleLights.Count; i++)
		{
			if (i == maxVisibleLights)
			{
				break;
			}

			//绘制RenderTexture,配置视角投影矩阵==========================================
			Matrix4x4 viewMatrix, projectionMatrix;
			ShadowSplitData splitData;
			cull.ComputeSpotShadowMatricesAndCullingPrimitives( //调用阴影命令缓冲区的SetViewProjectionMatrices 方法，然后执行命令并清理该缓存区。
				i, out viewMatrix, out projectionMatrix, out splitData
			);
			shadowBuffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
			//设置深度偏移让阴影平顺点============================================
			shadowBuffer.SetGlobalFloat(
				shadowBiasId, cull.visibleLights[0].light.shadowBias
			);
			//设置阴影强度//把阴影强度传递给shader
			shadowBuffer.SetGlobalFloat(
				shadowStrengthId, cull.visibleLights[0].light.shadowStrength
			);
			//=================================================================

			context.ExecuteCommandBuffer(shadowBuffer);
			shadowBuffer.Clear();

			var shadowSettings = new DrawShadowsSettings(cull, 0);
			context.DrawShadows(ref shadowSettings);
			//=========================================================================

			//得到世界位置转为阴影空间位置的矩阵。得到view-projection-texture===============================================
			if (SystemInfo.usesReversedZBuffer)
			{
				projectionMatrix.m20 = -projectionMatrix.m20;
				projectionMatrix.m21 = -projectionMatrix.m21;
				projectionMatrix.m22 = -projectionMatrix.m22;
				projectionMatrix.m23 = -projectionMatrix.m23;
			}

			//要映射至该范围就得就得再额外乘一个能在所有维度缩放和偏移 0.5个单位的转换矩阵。我们可以用Matrix4x4.TRS方法来得到想要的缩放、旋转或偏移。
			var scaleOffset = Matrix4x4.identity;
			scaleOffset.m00 = scaleOffset.m11 = scaleOffset.m22 = 0.5f;
			scaleOffset.m03 = scaleOffset.m13 = scaleOffset.m23 = 0.5f;
			//得到 光源的视点-投影-纹理view-projection-texture （到texture空间的变化）（-1，1）-> (0,1)
			Matrix4x4 worldToShadowMatrix =
				(scaleOffset) * (projectionMatrix * viewMatrix); //这个矩阵通过将渲染阴影是用的的视角矩阵和投影矩阵相乘得到。用SetGlobalMatrix将它传给GPU
			//传递转换矩阵和shadowMap
			shadowBuffer.SetGlobalMatrix(worldToShadowMatrixId, worldToShadowMatrix);
		}

		shadowBuffer.SetGlobalTexture(shadowMapId, shadowMap);
		float invShadowMapSize = 1f / shadowMapSize;
		shadowBuffer.SetGlobalVector(
			shadowMapSizeId, new Vector4(
				invShadowMapSize, invShadowMapSize, shadowMapSize, shadowMapSize
			)
		);
		//=========================================================================
		
		CoreUtils.SetKeyword(
			shadowBuffer, shadowsSoftKeyword,
			cull.visibleLights[0].light.shadows == LightShadows.Soft
		);
		
		shadowBuffer.EndSample("Render Shadows");
		context.ExecuteCommandBuffer(shadowBuffer);
		shadowBuffer.Clear();
		
	}
	
	
	/// <summary>
	/// 配置光参数
	/// </summary>
	void ConfigureLights ()
	{
		
		for (int i = 0; i < cull.visibleLights.Count; i++) 
		{
			if (i==maxVisibleLights)
			{
				break;
			}
			
			VisibleLight light = cull.visibleLights[i];
			visibleLightColors[i] = light.finalColor;//颜色*强度 显示颜色
			Vector4 attenuation = Vector4.zero;
			attenuation.w = 1f;
			
			Vector4 shadow = Vector4.zero;//设置阴影
			
			if (light.lightType==LightType.Directional)
			{
				Vector4 v = light.localToWorld.GetColumn(2);//第三列为光在空间中的向量(平行光)
				v.x = -v.x;
				v.y = -v.y;
				v.z = -v.z;
				visibleLightDirectionsOrPositions[i] = v;//负平行光向量的
			}
			else //这里不是平行光了所以现在需要光衰减和光的位置，如点光源聚光灯
			{
				//光源=================
				//光源位置信息传递过去
				visibleLightDirectionsOrPositions[i] = light.localToWorld.GetColumn(3); //its local-to-world matrix.
				attenuation.x = 1f / Mathf.Max(light.range * light.range, 0.00001f); //光源范围
				//=======================
				if (light.lightType == LightType.Spot) //聚光灯也需要用方向
				{
					Vector4 v = light.localToWorld.GetColumn(2);
					v.x = -v.x;
					v.y = -v.y;
					v.z = -v.z;
					visibleLightSpotDirections[i] = v; 

					float outerRad = Mathf.Deg2Rad * 0.5f * light.spotAngle;
					float outerCos = Mathf.Cos(outerRad);
					float outerTan = Mathf.Tan(outerRad);
					float innerCos =
						Mathf.Cos(Mathf.Atan((46f / 64f) * outerTan));
					float angleRange = Mathf.Max(innerCos - outerCos, 0.001f);
					attenuation.z = 1f / angleRange;
					attenuation.w = -outerCos * attenuation.z;
					
					Light shadowLight = light.light;
					Bounds shadowBounds;
					if (shadowLight.shadows != LightShadows.None && cull.GetShadowCasterBounds(i, out shadowBounds))
					{
						shadow.x = shadowLight.shadowStrength;
						shadow.y = shadowLight.shadows == LightShadows.Soft ? 1f : 0f;//1表示软阴影，0表示硬阴影。
					}
				}
			}
			
			visibleLightAttenuations[i] = attenuation;
			shadowData[i] = shadow;
		}

		
		int[] lightIndices = cull.GetLightIndexMap();
		if (cull.visibleLights.Count>maxVisibleLights)
		{
			for (int i = maxVisibleLights; i < cull.visibleLights.Count; i++)
			{  
				lightIndices[i] = -1;
				//Debug.Log(lightIndices.Length+"+"+i+"+"+cull.visibleLights.Count+"");
				//假设这里有30盏灯这里数值则为30+16～29+30把多余的灯光全部清空为-1而跳过
			}
			cull.SetLightIndexMap(lightIndices);
		}
	}
	
	[Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
	void DrawDefaultPipeline (ScriptableRenderContext context, Camera camera) {
//		var drawSettings = new DrawRendererSettings(
//			camera, new ShaderPassName("ForwardBase")
//		);
//		
//		var filterSettings = new FilterRenderersSettings(true);
//		
//		context.DrawRenderers(
//			cull.visibleRenderers, ref drawSettings, filterSettings
//		);
		
		if (errorMaterial == null) {
			Shader errorShader = Shader.Find("Hidden/InternalErrorShader");
			errorMaterial = new Material(errorShader) {
				hideFlags = HideFlags.HideAndDontSave
			};
		}

		var drawSettings = new DrawRendererSettings(
			camera, new ShaderPassName("ForwardBase")
		);
		drawSettings.SetShaderPassName(1, new ShaderPassName("PrepassBase"));
		drawSettings.SetShaderPassName(2, new ShaderPassName("Always"));
		drawSettings.SetShaderPassName(3, new ShaderPassName("Vertex"));
		drawSettings.SetShaderPassName(4, new ShaderPassName("VertexLMRGBM"));
		drawSettings.SetShaderPassName(5, new ShaderPassName("VertexLM"));
		drawSettings.SetOverrideMaterial(errorMaterial, 0);

		var filterSettings = new FilterRenderersSettings(true);

		context.DrawRenderers(
			cull.visibleRenderers, ref drawSettings, filterSettings
		);
	}
}