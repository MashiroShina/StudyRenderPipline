using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using Conditional = System.Diagnostics.ConditionalAttribute;

public class MyPipeline : RenderPipeline
{
	//传递光参数======================================================
	private const int maxVisibleLights = 4;
	private static int visibleLightColorsId = Shader.PropertyToID("_VisibleLightColors");//所有光颜色
	private static int visibleLightDirectionsOrPositionsId = Shader.PropertyToID("_VisibleLightDirectionsOrPositions");//直射光
	static int visibleLightAttenuationsId = Shader.PropertyToID("_VisibleLightAttenuations");//点光源
	static int visibleLightSpotDirectionsId = Shader.PropertyToID("_VisibleLightSpotDirections");//聚光灯
	Vector4[] visibleLightColors = new Vector4[maxVisibleLights];
	Vector4[] visibleLightDirectionsOrPositions = new Vector4[maxVisibleLights];
	Vector4[] visibleLightAttenuations = new Vector4[maxVisibleLights];
	Vector4[] visibleLightSpotDirections = new Vector4[maxVisibleLights];
	//===============================================================
	
	DrawRendererFlags drawFlags;//设置渲染处理方式
	public MyPipeline(bool dynamicBatching,bool instancing)
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
	}

	CullResults cull;

	Material errorMaterial;

	CommandBuffer cameraBuffer = new CommandBuffer {
		name = "Render Camera"
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

		context.SetupCameraProperties(camera);//相当于mvp Setup camera specific global shader variables.

		CameraClearFlags clearFlags = camera.clearFlags;
		cameraBuffer.ClearRenderTarget(
			(clearFlags & CameraClearFlags.Depth) != 0,
			(clearFlags & CameraClearFlags.Color) != 0,
			camera.backgroundColor
		);
		
		if (cull.visibleLights.Count > 0) {
			ConfigureLights();
		}
		
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
			rendererConfiguration = RendererConfiguration.PerObjectLightIndices8//设置光源索引。
		};
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
	}
	/// <summary>
	/// 配置光参数
	/// </summary>
	void ConfigureLights ()
	{
		int i = 0;
		for (; i < cull.visibleLights.Count; i++) 
		{
			if (i==maxVisibleLights)
			{
				break;
			}
			
			VisibleLight light = cull.visibleLights[i];
			visibleLightColors[i] = light.finalColor;//颜色*强度 显示颜色
			Vector4 attenuation = Vector4.zero;
			attenuation.w = 1f;
			
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
				}
			}

			visibleLightAttenuations[i] = attenuation;
		}
		for (; i < maxVisibleLights; i++) {
			visibleLightColors[i] = Color.clear;
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