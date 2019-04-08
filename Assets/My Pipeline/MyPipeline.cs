using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using Conditional = System.Diagnostics.ConditionalAttribute;

public class MyPipeline : RenderPipeline
{
	
	CullResults cull;
	Material errorMaterial;

	//传递光参数===========================================================================================================
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
	
	bool mainLightExists;
	//====================================================================================================================
	
	//传递阴影参数==========================================================================================================
	RenderTexture shadowMap, cascadedShadowMap;
	int shadowMapSize;
	int shadowTileCount;//需要多少个平铺块
	float shadowDistance;//直射光阴影需要
	
	int shadowCascades;
	Vector3 shadowCascadeSplit;
	
	static int worldToShadowCascadeMatricesId = Shader.PropertyToID("_WorldToShadowCascadeMatrices");
	static int cascadedShadowMapId = Shader.PropertyToID("_CascadedShadowMap");
	static int shadowMapId = Shader.PropertyToID("_ShadowMap");
	static int worldToShadowMatricesId = Shader.PropertyToID("_WorldToShadowMatrices");
	static int shadowBiasId = Shader.PropertyToID("_ShadowBias");
	static int shadowDataId = Shader.PropertyToID("_ShadowData");
	static int shadowMapSizeId = Shader.PropertyToID("_ShadowMapSize");//soft shadow
	static int globalShadowDataId = Shader.PropertyToID("_GlobalShadowData");
	static int cascadedShadowMapSizeId = Shader.PropertyToID("_CascadedShadowMapSize");
	static int cascadedShadoStrengthId = Shader.PropertyToID("_CascadedShadowStrength");
	static int cascadeCullingSpheresId = Shader.PropertyToID("_CascadeCullingSpheres");
	
	const string shadowsSoftKeyword = "_SHADOWS_SOFT";
	const string shadowsHardKeyword = "_SHADOWS_HARD";
	const string cascadedShadowsHardKeyword = "_CASCADED_SHADOWS_HARD";
	const string cascadedShadowsSoftKeyword = "_CASCADED_SHADOWS_SOFT";
	
	
	Vector4[] shadowData = new Vector4[maxVisibleLights];//x光强，y软硬阴影，z先判断直射光然后被tileOffsetX * tileScale;缩放覆盖,w则为y缩放
	
	Matrix4x4[] worldToShadowMatrices = new Matrix4x4[maxVisibleLights];
	Matrix4x4[] worldToShadowCascadeMatrices = new Matrix4x4[5];
	Vector4[] cascadeCullingSpheres = new Vector4[4];//xyz分量描述球的位置。w分量定义球的半径。
	//====================================================================================================================
	
	DrawRendererFlags drawFlags;//设置渲染处理方式
	public MyPipeline(bool dynamicBatching,bool instancing,int shadowMapSize, float shadowDistance, int shadowCascades, Vector3 shadowCascadeSplit)
	{
		GraphicsSettings.lightsUseLinearIntensity = true;//gamma 才有的光强度到 liner
		//在世界-阴影矩阵中添加一个零矩阵作为第五个数组元素来表示不存在的那个联级贴图。零矩阵会将阴影位置转换到近平面，自然就不可能产生阴影了
		if (SystemInfo.usesReversedZBuffer) {
			worldToShadowCascadeMatrices[4].m33 = 1f;
		}
		if (dynamicBatching)
		{
			drawFlags = DrawRendererFlags.EnableDynamicBatching;
		}

		if (instancing)
		{
			drawFlags |= DrawRendererFlags.EnableInstancing;
		}
		this.shadowMapSize = shadowMapSize;
		this.shadowDistance = shadowDistance;
		
		this.shadowCascades = shadowCascades;
		this.shadowCascadeSplit = shadowCascadeSplit;
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

	cullingParameters.shadowDistance = Mathf.Min(shadowDistance, camera.farClipPlane);
		
#if UNITY_EDITOR
		if (camera.cameraType == CameraType.SceneView) {
			ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
		}
#endif

		CullResults.Cull(ref cullingParameters, context, ref cull);//ref cull来减少new cull

		if (cull.visibleLights.Count > 0)
		{
			ConfigureLights();
			if (mainLightExists) {
				RenderCascadedShadows(context);
			}
			else 
			{
				cameraBuffer.DisableShaderKeyword(cascadedShadowsHardKeyword);
				cameraBuffer.DisableShaderKeyword(cascadedShadowsSoftKeyword);
			}
			if (shadowTileCount>0)
			{
				RenderShadows(context);
			}
			else {
				cameraBuffer.DisableShaderKeyword(shadowsHardKeyword);
				cameraBuffer.DisableShaderKeyword(shadowsSoftKeyword);
			}
			
		}
		else 
		{
			cameraBuffer.SetGlobalVector(
				//这里是告诉lightIndicesOffsetAndCountID.y为0 这样子是0灯光的时候
				lightIndicesOffsetAndCountID, Vector4.zero
			);
			cameraBuffer.DisableShaderKeyword(shadowsHardKeyword);
			cameraBuffer.DisableShaderKeyword(shadowsSoftKeyword);
			cameraBuffer.DisableShaderKeyword(cascadedShadowsHardKeyword);
			cameraBuffer.DisableShaderKeyword(cascadedShadowsSoftKeyword);
		}
		ConfigureLights();

		context.SetupCameraProperties(camera);//相当于mvp Setup camera specific global shader variables.

		CameraClearFlags clearFlags = camera.clearFlags;
		cameraBuffer.ClearRenderTarget(
			(clearFlags & CameraClearFlags.Depth) != 0,
			(clearFlags & CameraClearFlags.Color) != 0,
			camera.backgroundColor
		);

		
		//cameraBuffer.ClearRenderTarget(true, false, Color.clear);
		cameraBuffer.BeginSample("Render Camera");
		//传递光参数=========
		cameraBuffer.SetGlobalVectorArray(visibleLightColorsId, visibleLightColors);//传递数组到shader里通过ID	
		cameraBuffer.SetGlobalVectorArray(visibleLightDirectionsOrPositionsId, visibleLightDirectionsOrPositions);
		cameraBuffer.SetGlobalVectorArray(visibleLightAttenuationsId, visibleLightAttenuations);
		cameraBuffer.SetGlobalVectorArray(visibleLightSpotDirectionsId, visibleLightSpotDirections);
		cameraBuffer.SetGlobalMatrix(Shader.PropertyToID("_PojectMatrixForme"),Camera.main.projectionMatrix);
		//Debug.Log(Camera.main.projectionMatrix);
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
		drawSettings.rendererConfiguration |=
			RendererConfiguration.PerObjectReflectionProbes;
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
		if (cascadedShadowMap) {
			RenderTexture.ReleaseTemporary(cascadedShadowMap);
			cascadedShadowMap = null;
		}
	}

	RenderTexture SetShadowRenderTarget()
	{
		//黑色大块
		RenderTexture texture = RenderTexture.GetTemporary(
			shadowMapSize, shadowMapSize, 16, RenderTextureFormat.Shadowmap
		);
		texture.filterMode = FilterMode.Bilinear;
		texture.wrapMode = TextureWrapMode.Clamp;

		CoreUtils.SetRenderTarget(
			shadowBuffer, texture,
			RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
			ClearFlag.Depth
		);
		return texture;
	}

	Vector2 ConfigureShadowTile(int tileIndex, int split, float tileSize)
	{
		Vector2 tileOffset;
		tileOffset.x = tileIndex % split;//0123
		tileOffset.y = tileIndex / split;
		var tileViewport = new Rect(//x=tileOffset.x*(shadowMapSize / split) 0*1024/2.....1*1024/2
			tileOffset.x * tileSize, tileOffset.y * tileSize, tileSize, tileSize
		);//划分小图块的位置（黑色块）
		
		//在我们设置视口和投影矩阵前，用SetViewport通知GPU使用合适的视口大小。
		shadowBuffer.SetViewport(tileViewport);
		shadowBuffer.EnableScissorRect(new Rect(
			tileViewport.x + 4f, tileViewport.y + 4f,
			tileSize - 8f, tileSize - 8f
		));
		return tileOffset;
	}

	void CalculateWorldToShadowMatrix(ref Matrix4x4 viewMatrix,ref Matrix4x4 projectionMatrix,out Matrix4x4 worldToShadowMatrix)
	{
		//得到view-projection-texture
		if (SystemInfo.usesReversedZBuffer) {
			projectionMatrix.m20 = -projectionMatrix.m20;
			projectionMatrix.m21 = -projectionMatrix.m21;
			projectionMatrix.m22 = -projectionMatrix.m22;
			projectionMatrix.m23 = -projectionMatrix.m23;
		}
		//裁减空间范围是-1到1，但我们的纹理坐标和深度范围在0到1。要映射至该范围就得就得再额外乘一个能在所有维度缩放和偏移 0.5个单位的转换矩阵
		var scaleOffset = Matrix4x4.identity;
		scaleOffset.m00 = scaleOffset.m11 = scaleOffset.m22 = 0.5f;
		scaleOffset.m03 = scaleOffset.m13 = scaleOffset.m23 = 0.5f;
		worldToShadowMatrix =
			scaleOffset * (projectionMatrix * viewMatrix);
	}

	void RenderShadows (ScriptableRenderContext context) {
		int split;//表示一行划分成几个小块
		if (shadowTileCount <= 1) {
			split = 1;
		}
		else if (shadowTileCount <= 4) {
			split = 2;
		}
		else if (shadowTileCount <= 9) {
			split = 3;
		}
		else {
			split = 4;
		}

		shadowMap = SetShadowRenderTarget();
		shadowBuffer.BeginSample("Render Shadows");
		float tileSize = shadowMapSize / split;
		float tileScale = 1f / split;
		//Rect tileViewport = new Rect(0f, 0f, tileSize, tileSize);
		
		shadowBuffer.SetGlobalVector(
			globalShadowDataId, new Vector4(tileScale, shadowDistance*shadowDistance)
		);
		context.ExecuteCommandBuffer(shadowBuffer);
		shadowBuffer.Clear();

		int tileIndex = 0;//等于有阴影的光的数量
		bool hardShadows = false;
		bool softShadows = false;
		for (int i = mainLightExists ? 1 : 0; i < cull.visibleLights.Count; i++)
		{
			if (i == maxVisibleLights)
			{
				break;
			}
			//跳过不需要阴影贴图的光源
			if (shadowData[i].x <= 0f)
			{
				continue;
			}

			//绘制RenderTexture,配置视角投影矩阵=========================================================================
			Matrix4x4 viewMatrix, projectionMatrix;
			ShadowSplitData splitData;
			
			bool validShadows;
			if (shadowData[i].z>0)
			{
				validShadows = cull.ComputeDirectionalShadowMatricesAndCullingPrimitives(i, 0, 1, Vector3.right,
					(int) tileSize, cull.visibleLights[i].light.shadowNearPlane, out viewMatrix, out projectionMatrix,
					out splitData);
			}
			else
			{
				//ComputeSpotShadowMatricesAndCullingPrimitives方法返回是否可以生成有效的矩阵的布尔值。
                validShadows = cull.ComputeSpotShadowMatricesAndCullingPrimitives(
                	i, out viewMatrix, out projectionMatrix, out splitData);
			}
			//这里i是光照序列一开始我们只有一个光是0现在多个灯光则为i
			if (!validShadows) {
				shadowData[i].x = 0f;
				continue;
			} 
			Vector2 tileOffset = ConfigureShadowTile(tileIndex, split, tileSize);
			shadowData[i].z = tileOffset.x * tileScale;//此时z已经判断过直射光了覆盖即可 0123 * 1/4
			shadowData[i].w = tileOffset.y * tileScale;
			//调用阴影命令缓冲区的SetViewProjectionMatrices 方法，然后执行命令并清理该缓存区。
			shadowBuffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);//这里是在黑色的texture上绘制图片上去
			//设置深度偏移让阴影平顺点
			shadowBuffer.SetGlobalFloat(
				shadowBiasId, cull.visibleLights[i].light.shadowBias
			);
			context.ExecuteCommandBuffer(shadowBuffer);
			shadowBuffer.Clear();

			var shadowSettings = new DrawShadowsSettings(cull, i);
			shadowSettings.splitData.cullingSphere = splitData.cullingSphere;
			context.DrawShadows(ref shadowSettings);
			//========================================================================================================

			//得到世界位置转为阴影空间位置的矩阵。得到view-projection-texture
			CalculateWorldToShadowMatrix(
				ref viewMatrix, ref projectionMatrix, out worldToShadowMatrices[i]
			);
			
			tileIndex += 1;
			if (shadowData[i].y <= 0f) {
				hardShadows = true;
			}
			else {
				softShadows = true;
			}
		}

		shadowBuffer.DisableScissorRect();
		////传递转换矩阵和shadowMap
		shadowBuffer.SetGlobalTexture(shadowMapId, shadowMap);
		shadowBuffer.SetGlobalMatrixArray(
			worldToShadowMatricesId, worldToShadowMatrices
		);
		
		shadowBuffer.SetGlobalVectorArray(shadowDataId, shadowData);
		float invShadowMapSize = 1f / shadowMapSize;
		shadowBuffer.SetGlobalVector(
			shadowMapSizeId, new Vector4(
				invShadowMapSize, invShadowMapSize, shadowMapSize, shadowMapSize
			)
		);
		//============================================================================================================
		CoreUtils.SetKeyword(shadowBuffer, shadowsHardKeyword, hardShadows);
		CoreUtils.SetKeyword(shadowBuffer, shadowsSoftKeyword, softShadows);
		
		shadowBuffer.EndSample("Render Shadows");
		context.ExecuteCommandBuffer(shadowBuffer);
		shadowBuffer.Clear();
		
	}
	
	void RenderCascadedShadows (ScriptableRenderContext context) {
		float tileSize = shadowMapSize / 2;
		cascadedShadowMap = SetShadowRenderTarget();
		shadowBuffer.BeginSample("Render Shadows");
		context.ExecuteCommandBuffer(shadowBuffer);
		shadowBuffer.Clear();
		Light shadowLight = cull.visibleLights[0].light;
		shadowBuffer.SetGlobalFloat(
			shadowBiasId, shadowLight.shadowBias
		);
		var shadowSettings = new DrawShadowsSettings(cull, 0);
		var tileMatrix = Matrix4x4.identity;
			tileMatrix.m00 = tileMatrix.m11 = 0.5f;//xy缩放
		for (int i = 0; i < shadowCascades; i++)
		{
			Matrix4x4 viewMatrix, projectionMatrix;
			ShadowSplitData splitData;
			cull.ComputeDirectionalShadowMatricesAndCullingPrimitives(
				0, i, shadowCascades, shadowCascadeSplit, (int)tileSize,
				shadowLight.shadowNearPlane,
				out viewMatrix, out projectionMatrix, out splitData
			);
			Vector2 tileOffset = ConfigureShadowTile(i, 2, tileSize);
			shadowBuffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
			context.ExecuteCommandBuffer(shadowBuffer);
			shadowBuffer.Clear();
			//设置cascade剔除球,并且让他得到半径的平方
			cascadeCullingSpheres[i] = shadowSettings.splitData.cullingSphere = splitData.cullingSphere;
			cascadeCullingSpheres[i].w *= splitData.cullingSphere.w;
			
			context.DrawShadows(ref shadowSettings);
			CalculateWorldToShadowMatrix(
				ref viewMatrix, ref projectionMatrix,
				out worldToShadowCascadeMatrices[i]
			);
			tileMatrix.m03 = tileOffset.x * 0.5f;
			tileMatrix.m13 = tileOffset.y * 0.5f;
			worldToShadowCascadeMatrices[i] =
				tileMatrix * worldToShadowCascadeMatrices[i];
		}
		shadowBuffer.DisableScissorRect();
		shadowBuffer.SetGlobalTexture(cascadedShadowMapId, cascadedShadowMap);
		
		shadowBuffer.SetGlobalVectorArray(
			cascadeCullingSpheresId, cascadeCullingSpheres
		);
		shadowBuffer.SetGlobalMatrixArray(
			worldToShadowCascadeMatricesId, worldToShadowCascadeMatrices
		);
		
		float invShadowMapSize = 1f / shadowMapSize;
		shadowBuffer.SetGlobalVector(
			cascadedShadowMapSizeId, new Vector4(
				invShadowMapSize, invShadowMapSize, shadowMapSize, shadowMapSize
			)
		);
		shadowBuffer.SetGlobalFloat(
			cascadedShadoStrengthId, shadowLight.shadowStrength
		);
		
		bool hard = shadowLight.shadows == LightShadows.Hard;
		CoreUtils.SetKeyword(shadowBuffer, cascadedShadowsHardKeyword, hard);
		CoreUtils.SetKeyword(shadowBuffer, cascadedShadowsSoftKeyword, !hard);
		
		shadowBuffer.EndSample("Render Shadows");
		context.ExecuteCommandBuffer(shadowBuffer);
		shadowBuffer.Clear();
	}
	
	/// <summary>
	/// 配置光参数
	/// </summary>
	void ConfigureLights ()
	{
		mainLightExists = false;
		shadowTileCount = 0;
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
				visibleLightDirectionsOrPositions[i] = v;//负平行光向量的 w分量为0
				shadow = ConfigureShadows(i, light.light);//shadowTileCount=1;
				shadow.z = 1;//这里设置1表示为直射光阴影
				
				if (i == 0 && shadow.x > 0f && shadowCascades > 0) {
					mainLightExists = true;
					shadowTileCount -= 1;
				}
			}
			else //这里不是平行光了所以现在需要光衰减和光的位置，如点光源聚光灯
			{
				//光源=================
				//光源位置信息传递过去
				visibleLightDirectionsOrPositions[i] = light.localToWorld.GetColumn(3); //its local-to-world matrix.//w分量为1
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
					shadow = ConfigureShadows(i, shadowLight);
				}
			}
			
			visibleLightAttenuations[i] = attenuation;
			shadowData[i] = shadow;
		}

		
		
		if (mainLightExists || cull.visibleLights.Count>maxVisibleLights)
		{
			int[] lightIndices = cull.GetLightIndexMap();
			if (mainLightExists) {
				lightIndices[0] = -1;
			}
			for (int i = maxVisibleLights; i < cull.visibleLights.Count; i++)
			{   
				lightIndices[i] = -1;
				//Debug.Log(lightIndices.Length+"+"+i+"+"+cull.visibleLights.Count+"");
				//假设这里有30盏灯这里数值则为30+16～29+30把多余的灯光全部清空为-1而跳过
			}
			cull.SetLightIndexMap(lightIndices);
		}
	}
	/// <summary>
	/// 配置阴影参数
	/// </summary>
	/// <param name="lightIndex"></param>
	/// <param name="shadowLight"></param>
	/// <returns></returns>
	Vector4 ConfigureShadows (int lightIndex, Light shadowLight) {
		Vector4 shadow = Vector4.zero;
		Bounds shadowBounds;
		if (
			shadowLight.shadows != LightShadows.None &&
			cull.GetShadowCasterBounds(lightIndex, out shadowBounds)
		) {
			shadowTileCount += 1;
			shadow.x = shadowLight.shadowStrength;//阴影强度存储在向量的x分量中。
			shadow.y =
				shadowLight.shadows == LightShadows.Soft ? 1f : 0f;//1表示软阴影，0表示硬阴影。
		}
		return shadow;
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