using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Permissions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using CSCore;
using CSCore.CoreAudioAPI;
using CSCore.SoundOut;
using Cloudtoid.Interprocess;
using EnumsNET;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NativeGraphics.NET;
using Renderite.Shared;
using Renderite.Unity;
using SharpDX;
using SharpDX.DXGI;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Scripting;
using UnityEngine.XR;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: TargetFramework(".NETStandard,Version=v2.0", FrameworkDisplayName = ".NET Standard 2.0")]
[assembly: AssemblyCompany("Renderite.Unity")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0+7cfb8fff7620d7adbe32cfac402064e6c656d3ff")]
[assembly: AssemblyProduct("Renderite.Unity")]
[assembly: AssemblyTitle("Renderite.Unity")]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
[assembly: AssemblyVersion("1.0.0.0")]
[module: UnverifiableCode]
[module: System.Runtime.CompilerServices.RefSafetyRules(11)]
namespace Microsoft.CodeAnalysis
{
	[CompilerGenerated]
	[Microsoft.CodeAnalysis.Embedded]
	internal sealed class EmbeddedAttribute : Attribute
	{
	}
}
namespace System.Runtime.CompilerServices
{
	[CompilerGenerated]
	[Microsoft.CodeAnalysis.Embedded]
	internal sealed class IsReadOnlyAttribute : Attribute
	{
	}
	[CompilerGenerated]
	[Microsoft.CodeAnalysis.Embedded]
	internal sealed class IsUnmanagedAttribute : Attribute
	{
	}
	[CompilerGenerated]
	[Microsoft.CodeAnalysis.Embedded]
	internal sealed class IsByRefLikeAttribute : Attribute
	{
	}
	[CompilerGenerated]
	[Microsoft.CodeAnalysis.Embedded]
	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Event | AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.GenericParameter, AllowMultiple = false, Inherited = false)]
	internal sealed class NullableAttribute : Attribute
	{
		public readonly byte[] NullableFlags;

		public NullableAttribute(byte P_0)
		{
			NullableFlags = new byte[1] { P_0 };
		}

		public NullableAttribute(byte[] P_0)
		{
			NullableFlags = P_0;
		}
	}
	[CompilerGenerated]
	[Microsoft.CodeAnalysis.Embedded]
	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Interface | AttributeTargets.Delegate, AllowMultiple = false, Inherited = false)]
	internal sealed class NullableContextAttribute : Attribute
	{
		public readonly byte Flag;

		public NullableContextAttribute(byte P_0)
		{
			Flag = P_0;
		}
	}
	[CompilerGenerated]
	[Microsoft.CodeAnalysis.Embedded]
	[AttributeUsage(AttributeTargets.Module, AllowMultiple = false, Inherited = false)]
	internal sealed class RefSafetyRulesAttribute : Attribute
	{
		public readonly int Version;

		public RefSafetyRulesAttribute(int P_0)
		{
			Version = P_0;
		}
	}
}
[ExecuteInEditMode]
public class CameraPortal : MonoBehaviour
{
	public enum Mode
	{
		Mirror,
		Portal
	}

	public Mode RenderMode;

	public bool DisablePixelLights;

	public bool DisableShadows;

	public float ClipPlaneOffset = 0.07f;

	public RenderTexture ReflectionTexture;

	public LayerMask ReflectLayers = -1;

	public Vector3 Normal = new Vector3(0f, 0f, 1f);

	public Matrix4x4 PortalTransform;

	public Vector3 PortalPlanePosition;

	public Vector3 PortalPlaneNormal;

	public float? OverrideFarClip;

	public CameraClearFlags? OverrideClearFlag;

	public Color ClearColor;

	private Dictionary<Camera, Camera> reflectionCameras = new Dictionary<Camera, Camera>();

	private static bool isRendering;

	private static CommandBuffer stereoRenderCommandBuffer;

	private HashSet<Camera> setupCameras = new HashSet<Camera>();

	public void OnWillRenderObject()
	{
		if (!base.enabled)
		{
			return;
		}
		Renderer component = GetComponent<Renderer>();
		if (component == null || component.sharedMaterial == null || !component.enabled || ReflectionTexture == null)
		{
			return;
		}
		Camera current = Camera.current;
		if (!current || isRendering)
		{
			return;
		}
		RenderingContext? currentRenderingContext = RenderContextHelper.CurrentRenderingContext;
		RenderContextHelper.BeginRenderContext((RenderMode == Mode.Mirror) ? RenderingContext.Mirror : RenderingContext.Portal);
		isRendering = true;
		float nearClipPlane = current.nearClipPlane;
		float farClipPlane = current.farClipPlane;
		if (OverrideFarClip.HasValue)
		{
			current.farClipPlane = OverrideFarClip.Value;
		}
		CreateObjects(current, out Camera reflectionCamera);
		_ = base.transform.position;
		base.transform.TransformDirection(Normal);
		int pixelLightCount = QualitySettings.pixelLightCount;
		ShadowQuality shadows = QualitySettings.shadows;
		if (DisablePixelLights)
		{
			QualitySettings.pixelLightCount = 0;
		}
		if (DisableShadows)
		{
			QualitySettings.shadows = ShadowQuality.Disable;
		}
		UpdateCameraModes(current, reflectionCamera);
		if (reflectionCamera.clearFlags != CameraClearFlags.Skybox)
		{
			RenderTexture.active = ReflectionTexture;
			GL.Clear(reflectionCamera.clearFlags != CameraClearFlags.Nothing, reflectionCamera.clearFlags == CameraClearFlags.Color, reflectionCamera.backgroundColor);
			RenderTexture.active = null;
			reflectionCamera.clearFlags = CameraClearFlags.Nothing;
		}
		if (current.stereoEnabled)
		{
			current.allowMSAA = false;
			if (current.stereoTargetEye == StereoTargetEyeMask.Both || current.stereoTargetEye == StereoTargetEyeMask.Left)
			{
				Vector3 camPos = current.transform.TransformPoint(new Vector3(-0.5f * current.stereoSeparation, 0f, 0f));
				Matrix4x4 stereoProjectionMatrix = current.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
				SetupCameraMatrix(reflectionCamera, camPos, current.transform.rotation, stereoProjectionMatrix, current);
				RenderReflection(reflectionCamera, new Rect(0f, 0f, 0.5f, 1f));
			}
			if (current.stereoTargetEye == StereoTargetEyeMask.Both || current.stereoTargetEye == StereoTargetEyeMask.Right)
			{
				Vector3 camPos2 = current.transform.TransformPoint(new Vector3(0.5f * current.stereoSeparation, 0f, 0f));
				Matrix4x4 stereoProjectionMatrix2 = current.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);
				SetupCameraMatrix(reflectionCamera, camPos2, current.transform.rotation, stereoProjectionMatrix2, current);
				RenderReflection(reflectionCamera, new Rect(0.5f, 0f, 0.5f, 1f));
			}
		}
		else
		{
			SetupCameraMatrix(reflectionCamera, current.transform.position, current.transform.rotation, current.nonJitteredProjectionMatrix, current);
			RenderReflection(reflectionCamera, new Rect(0f, 0f, 1f, 1f));
		}
		current.nearClipPlane = nearClipPlane;
		current.farClipPlane = farClipPlane;
		if (DisablePixelLights)
		{
			QualitySettings.pixelLightCount = pixelLightCount;
		}
		if (DisableShadows)
		{
			QualitySettings.shadows = shadows;
		}
		isRendering = false;
		RenderingManager.Instance?.Stats?.CameraPortalRendered();
		if (currentRenderingContext.HasValue)
		{
			RenderContextHelper.BeginRenderContext(currentRenderingContext.Value);
		}
	}

	private void RenderReflection(Camera reflectionCamera, Rect viewPort)
	{
		if (!(Mathf.Abs(reflectionCamera.projectionMatrix.determinant) <= 1E-12f) && !(Mathf.Abs(reflectionCamera.worldToCameraMatrix.determinant) <= 1E-12f))
		{
			reflectionCamera.targetTexture = ReflectionTexture;
			reflectionCamera.rect = viewPort;
			reflectionCamera.cullingMask &= ReflectLayers.value;
			bool invertCulling = GL.invertCulling;
			if (RenderMode == Mode.Mirror)
			{
				GL.invertCulling = !invertCulling;
			}
			reflectionCamera.Render();
			if (RenderMode == Mode.Mirror)
			{
				GL.invertCulling = invertCulling;
			}
		}
	}

	private void SetupCameraMatrix(Camera reflectionCamera, Vector3 camPos, Quaternion camRot, Matrix4x4 camProjMatrix, Camera sourceCam = null)
	{
		reflectionCamera.ResetWorldToCameraMatrix();
		reflectionCamera.transform.position = camPos;
		reflectionCamera.transform.rotation = camRot;
		reflectionCamera.projectionMatrix = camProjMatrix;
		Vector3 vector;
		Vector3 vector2;
		if (RenderMode == Mode.Mirror)
		{
			vector = base.transform.position;
			vector2 = base.transform.TransformDirection(Normal);
			float w = 0f - Vector3.Dot(vector2, vector) - ClipPlaneOffset;
			Vector4 plane = new Vector4(vector2.x, vector2.y, vector2.z, w);
			Matrix4x4 reflectionMat = Matrix4x4.zero;
			CalculateReflectionMatrix(ref reflectionMat, plane);
			reflectionCamera.worldToCameraMatrix *= reflectionMat;
		}
		else
		{
			reflectionCamera.worldToCameraMatrix *= PortalTransform;
			vector = PortalPlanePosition;
			vector2 = -PortalPlaneNormal;
		}
		Vector4 clipPlane = CameraSpacePlane(reflectionCamera, vector, vector2, 1f);
		Matrix4x4 projectionMatrix = reflectionCamera.CalculateObliqueMatrix(clipPlane);
		reflectionCamera.projectionMatrix = projectionMatrix;
		reflectionCamera.transform.position = reflectionCamera.cameraToWorldMatrix.GetColumn(3);
		reflectionCamera.transform.rotation = Quaternion.LookRotation(reflectionCamera.cameraToWorldMatrix.GetColumn(2), reflectionCamera.cameraToWorldMatrix.GetColumn(1));
	}

	private void SetupStereoSinglePass(Camera camera)
	{
		if (stereoRenderCommandBuffer == null)
		{
			stereoRenderCommandBuffer = new CommandBuffer();
			stereoRenderCommandBuffer.SetSinglePassStereo(SinglePassStereoMode.SideBySide);
			stereoRenderCommandBuffer.EnableShaderKeyword("UNITY_SINGLE_PASS_STEREO");
		}
		if (!setupCameras.Contains(camera))
		{
			setupCameras.Add(camera);
			for (int i = 0; i <= 24; i++)
			{
				camera.AddCommandBuffer((CameraEvent)i, stereoRenderCommandBuffer);
			}
		}
	}

	private void ClearStereoSinglePass(Camera camera)
	{
	}

	private void OnDisable()
	{
		foreach (KeyValuePair<Camera, Camera> reflectionCamera in reflectionCameras)
		{
			UnityEngine.Object.Destroy(reflectionCamera.Value.gameObject);
		}
		reflectionCameras.Clear();
	}

	private void UpdateCameraModes(Camera src, Camera dest)
	{
		if (dest == null)
		{
			return;
		}
		if (!OverrideClearFlag.HasValue)
		{
			dest.clearFlags = src.clearFlags;
			dest.backgroundColor = src.backgroundColor;
			if (src.clearFlags == CameraClearFlags.Skybox)
			{
				Skybox skybox = src.GetComponent(typeof(Skybox)) as Skybox;
				Skybox skybox2 = dest.GetComponent(typeof(Skybox)) as Skybox;
				if (!skybox || !skybox.material)
				{
					skybox2.enabled = false;
				}
				else
				{
					skybox2.enabled = true;
					skybox2.material = skybox.material;
				}
			}
		}
		else
		{
			dest.clearFlags = OverrideClearFlag.Value;
			dest.backgroundColor = ClearColor;
		}
		dest.farClipPlane = OverrideFarClip ?? src.farClipPlane;
		dest.nearClipPlane = src.nearClipPlane;
		dest.orthographic = src.orthographic;
		dest.fieldOfView = src.fieldOfView;
		dest.aspect = src.aspect;
		dest.orthographicSize = src.orthographicSize;
		dest.cullingMask = src.cullingMask;
	}

	private void CreateObjects(Camera currentCamera, out Camera reflectionCamera)
	{
		reflectionCamera = null;
		reflectionCameras.TryGetValue(currentCamera, out reflectionCamera);
		if (!reflectionCamera)
		{
			GameObject gameObject = new GameObject("Refl Camera id" + GetInstanceID() + " for " + currentCamera.GetInstanceID(), typeof(Camera), typeof(Skybox));
			reflectionCamera = gameObject.GetComponent<Camera>();
			reflectionCamera.enabled = false;
			reflectionCamera.transform.position = base.transform.position;
			reflectionCamera.transform.rotation = base.transform.rotation;
			reflectionCamera.stereoTargetEye = StereoTargetEyeMask.None;
			gameObject.hideFlags = HideFlags.HideAndDontSave;
			reflectionCameras[currentCamera] = reflectionCamera;
			RenderingManager.Instance?.CameraInitializer?.RegisterCamera(reflectionCamera);
		}
	}

	private Vector4 CameraSpacePlane(Camera cam, Vector3 pos, Vector3 normal, float sideSign)
	{
		Vector3 point = pos + normal * ClipPlaneOffset;
		Matrix4x4 worldToCameraMatrix = cam.worldToCameraMatrix;
		Vector3 lhs = worldToCameraMatrix.MultiplyPoint(point);
		Vector3 rhs = worldToCameraMatrix.MultiplyVector(normal).normalized * sideSign;
		return new Vector4(rhs.x, rhs.y, rhs.z, 0f - Vector3.Dot(lhs, rhs));
	}

	private static void CalculateReflectionMatrix(ref Matrix4x4 reflectionMat, Vector4 plane)
	{
		reflectionMat.m00 = 1f - 2f * plane[0] * plane[0];
		reflectionMat.m01 = -2f * plane[0] * plane[1];
		reflectionMat.m02 = -2f * plane[0] * plane[2];
		reflectionMat.m03 = -2f * plane[3] * plane[0];
		reflectionMat.m10 = -2f * plane[1] * plane[0];
		reflectionMat.m11 = 1f - 2f * plane[1] * plane[1];
		reflectionMat.m12 = -2f * plane[1] * plane[2];
		reflectionMat.m13 = -2f * plane[3] * plane[1];
		reflectionMat.m20 = -2f * plane[2] * plane[0];
		reflectionMat.m21 = -2f * plane[2] * plane[1];
		reflectionMat.m22 = 1f - 2f * plane[2] * plane[2];
		reflectionMat.m23 = -2f * plane[3] * plane[2];
		reflectionMat.m30 = 0f;
		reflectionMat.m31 = 0f;
		reflectionMat.m32 = 0f;
		reflectionMat.m33 = 1f;
	}
}
namespace Renderite.Unity
{
	public abstract class Asset
	{
		public int AssetId { get; private set; } = -1;

		public AssetIntegrator AssetIntegrator => RenderingManager.Instance.AssetIntegrator;

		public void AssignId(int assetId)
		{
			if (AssetId >= 0)
			{
				throw new InvalidOperationException("AssetId was already assigned");
			}
			AssetId = assetId;
		}
	}
	public class AssetIntegrator
	{
		private struct QueueAction
		{
			public readonly Action action;

			public readonly Action<object> actionWithData;

			public readonly IEnumerator coroutine;

			public readonly object data;

			public QueueAction(Action action)
			{
				this.action = action;
				actionWithData = null;
				coroutine = null;
				data = null;
			}

			public QueueAction(IEnumerator coroutine)
			{
				this.coroutine = coroutine;
				action = null;
				actionWithData = null;
				data = null;
			}

			public QueueAction(Action<object> actionWithData, object data)
			{
				this.actionWithData = actionWithData;
				this.data = data;
				action = null;
				coroutine = null;
			}
		}

		[CompilerGenerated]
		private static class <>O
		{
			public static RenderEventDelegate <0>__RenderThreadCallback;
		}

		private const int DELAYED_REMOVAL_UPDATES = 3;

		internal static Device _dx11device;

		private ConcurrentQueue<QueueAction> highpriorityQueue = new ConcurrentQueue<QueueAction>();

		private ConcurrentQueue<QueueAction> processingQueue = new ConcurrentQueue<QueueAction>();

		private ConcurrentQueue<QueueAction> renderThreadQueue = new ConcurrentQueue<QueueAction>();

		private ConcurrentQueue<QueueAction> particlesQueue = new ConcurrentQueue<QueueAction>();

		private ConcurrentQueue<Action> taskQueue = new ConcurrentQueue<Action>();

		private Queue<Action> delayedRemovals = new Queue<Action>();

		private int[] delayedRemovalCounts = new int[3];

		private int delayedRemovalBucketIndex;

		private Stopwatch stopwatch = new Stopwatch();

		private Stopwatch particlesStopwatch = new Stopwatch();

		private double maxMilliseconds;

		private Action<int> renderThreadCallback;

		private IntPtr renderThreadPointer;

		private Action tasksAvailable;

		public GraphicsDeviceType GraphicsDeviceType { get; private set; }

		public bool IsUsingLinearSpace { get; private set; }

		public static bool IsEditor { get; private set; }

		public static bool IsDebugBuild { get; private set; }

		public int HighPriorityTasks => highpriorityQueue.Count;

		public int NormalTasks => processingQueue.Count;

		public int RenderThreadTasks => renderThreadQueue.Count;

		public int ParticleTasks => particlesQueue.Count;

		public bool RenderThreadProcessingEnabled { get; private set; }

		[MonoPInvokeCallback(typeof(RenderEventDelegate))]
		private static void RenderThreadCallback()
		{
			try
			{
				if (!IsDebugBuild)
				{
					GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
				}
				AssetIntegrator assetIntegrator = RenderingManager.Instance.AssetIntegrator;
				assetIntegrator.ProcessRenderThreadQueue(assetIntegrator.maxMilliseconds);
			}
			catch (Exception ex)
			{
				UnityEngine.Debug.LogError("Exception in render thread queue processing:\n" + ex);
			}
			finally
			{
				if (!IsDebugBuild)
				{
					GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
				}
			}
		}

		public void Initialize(Action onTasksAvailable)
		{
			//IL_0076: Unknown result type (might be due to invalid IL or missing references)
			//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
			//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
			//IL_00c6: Expected O, but got Unknown
			IsUsingLinearSpace = QualitySettings.activeColorSpace == ColorSpace.Linear;
			GraphicsDeviceType = SystemInfo.graphicsDeviceType;
			IsEditor = Application.isEditor;
			IsDebugBuild = UnityEngine.Debug.isDebugBuild;
			UnityEngine.Debug.Log($"Graphics Device Type: {GraphicsDeviceType}");
			switch (GraphicsDeviceType)
			{
			case GraphicsDeviceType.Direct3D11:
			{
				Texture2D texture2D = new Texture2D(4, 4);
				_dx11device = ((DeviceChild)new Texture2D(texture2D.GetNativeTexturePtr())).Device;
				if ((bool)texture2D)
				{
					UnityEngine.Object.Destroy(texture2D);
				}
				RenderThreadProcessingEnabled = true;
				break;
			}
			case GraphicsDeviceType.OpenGLES2:
			case GraphicsDeviceType.OpenGLES3:
			case GraphicsDeviceType.OpenGLCore:
				RenderThreadProcessingEnabled = true;
				break;
			}
			if (RenderThreadProcessingEnabled)
			{
				object obj = <>O.<0>__RenderThreadCallback;
				if (obj == null)
				{
					RenderEventDelegate val = RenderThreadCallback;
					<>O.<0>__RenderThreadCallback = val;
					obj = (object)val;
				}
				Callback.SetUpdateCallback((RenderEventDelegate)obj);
				renderThreadPointer = Callback.GetRenderEventFunc();
			}
			tasksAvailable = onTasksAvailable;
		}

		public void EnqueueDelayedRemoval(Action removalAction)
		{
			delayedRemovals.Enqueue(removalAction);
			delayedRemovalCounts[delayedRemovalBucketIndex]++;
		}

		public void ProcessDelayedRemovals()
		{
			int num = (delayedRemovalBucketIndex + 2) % 3;
			int num2 = delayedRemovalCounts[num];
			for (int i = 0; i < num2; i++)
			{
				delayedRemovals.Dequeue()();
			}
			delayedRemovalCounts[num] = 0;
			delayedRemovalBucketIndex++;
			delayedRemovalBucketIndex %= 3;
		}

		public void EnqueueRenderThreadProcessing(IEnumerator coroutine)
		{
			if (!RenderThreadProcessingEnabled)
			{
				throw new NotSupportedException("Render Thread Processing is not enabled");
			}
			renderThreadQueue.Enqueue(new QueueAction(coroutine));
			tasksAvailable();
		}

		public void EnqueueRenderThreadProcessing(Action action)
		{
			if (!RenderThreadProcessingEnabled)
			{
				throw new NotSupportedException("Render Thread Processing is not enabled");
			}
			renderThreadQueue.Enqueue(new QueueAction(action));
			tasksAvailable();
		}

		public void EnqueueProcessing(IEnumerator coroutine, bool highPriority)
		{
			if (highPriority)
			{
				highpriorityQueue.Enqueue(new QueueAction(coroutine));
			}
			else
			{
				processingQueue.Enqueue(new QueueAction(coroutine));
			}
			tasksAvailable();
		}

		public void EnqueueProcessing(Action action, bool highPriority)
		{
			if (highPriority)
			{
				highpriorityQueue.Enqueue(new QueueAction(action));
			}
			else
			{
				processingQueue.Enqueue(new QueueAction(action));
			}
			tasksAvailable();
		}

		public void EnqueueProcessing(Action<object> action, object data, bool highPriority)
		{
			if (highPriority)
			{
				highpriorityQueue.Enqueue(new QueueAction(action, data));
			}
			else
			{
				processingQueue.Enqueue(new QueueAction(action, data));
			}
			tasksAvailable();
		}

		public void EnqueueParticleProcessing(Action action)
		{
			particlesQueue.Enqueue(new QueueAction(action));
			tasksAvailable();
		}

		public void EnqueueParticleProcessing(Action<object> action, object data)
		{
			particlesQueue.Enqueue(new QueueAction(action, data));
			tasksAvailable();
		}

		public void EnqueueTask(Action action)
		{
			taskQueue.Enqueue(action);
			tasksAvailable();
		}

		public bool Process()
		{
			bool flag = false;
			Action result;
			while (taskQueue.TryDequeue(out result))
			{
				try
				{
					result();
					flag = true;
				}
				catch (Exception ex)
				{
					UnityEngine.Debug.LogError("Exception running AssetIntegrator task:\n" + ex);
				}
			}
			if (flag)
			{
				return true;
			}
			if (ProcessHighPriorityQueueTask())
			{
				return true;
			}
			if (ProcessNormalQueueTask())
			{
				return true;
			}
			if (ProcessParticleQueueTask())
			{
				return true;
			}
			return false;
		}

		public void RunRenderThreadUploads(double maxMilliseconds)
		{
			if (RenderThreadProcessingEnabled && renderThreadQueue.Count != 0)
			{
				this.maxMilliseconds = maxMilliseconds;
				GL.IssuePluginEvent(renderThreadPointer, 0);
			}
		}

		public int ProcessRenderThreadQueue(double maxMilliseconds)
		{
			int num = 0;
			stopwatch.Restart();
			try
			{
				while (ProcessRenderThreadTask())
				{
					num++;
					if (!((double)stopwatch.ElapsedMilliseconds < maxMilliseconds))
					{
						break;
					}
				}
			}
			catch (Exception ex)
			{
				UnityEngine.Debug.LogError("Exception integrating asset: " + ex);
			}
			return num;
		}

		public bool ProcessHighPriorityQueueTask()
		{
			return ProcessQueueTask(highpriorityQueue);
		}

		public bool ProcessNormalQueueTask()
		{
			return ProcessQueueTask(processingQueue);
		}

		private bool ProcessQueueTask(ConcurrentQueue<QueueAction> queue)
		{
			if (!queue.TryPeek(out var result))
			{
				return false;
			}
			if (ProcessQueueAction(result))
			{
				queue.TryDequeue(out var _);
			}
			return true;
		}

		private bool ProcessRenderThreadTask()
		{
			if (!renderThreadQueue.TryPeek(out var result))
			{
				return false;
			}
			if (ProcessQueueAction(result))
			{
				renderThreadQueue.TryDequeue(out var _);
			}
			return true;
		}

		public bool ProcessParticleQueueTask()
		{
			if (!particlesQueue.TryPeek(out var result))
			{
				return false;
			}
			if (ProcessQueueAction(result))
			{
				particlesQueue.TryDequeue(out var _);
			}
			return true;
		}

		private bool ProcessQueueAction(QueueAction process)
		{
			if (process.action != null)
			{
				process.action();
				return true;
			}
			if (process.actionWithData != null)
			{
				process.actionWithData(process.data);
				return true;
			}
			return !process.coroutine.MoveNext();
		}
	}
	public class AssetManager<T> where T : Asset, new()
	{
		private Dictionary<int, T> _instances = new Dictionary<int, T>();

		public T GetAsset(int id)
		{
			if (id < 0)
			{
				return null;
			}
			lock (_instances)
			{
				if (!_instances.TryGetValue(id, out var value))
				{
					value = new T();
					value.AssignId(id);
					_instances.Add(id, value);
				}
				return value;
			}
		}

		public void RemoveAsset(T asset)
		{
			lock (_instances)
			{
				_instances.Remove(asset.AssetId);
			}
		}
	}
	public class GaussianSplatAsset : Asset
	{
		private const int SH_COEFFICIENT_COUNT = 16;

		private static Texture2D _emptyTexture;

		private GaussianVectorFormat positionsFormat;

		private GaussianRotationFormat rotationsFormat;

		private GaussianVectorFormat scalesFormat;

		private GaussianColorFormat colorsFormat;

		private GaussianSHFormat shFormat;

		private ComputeBuffer chunks;

		private int splatChunkCount;

		private int shIndexesOffset;

		private ComputeBuffer encodedPositions;

		private ComputeBuffer encodedRotations;

		private ComputeBuffer encodedScales;

		private ComputeBuffer encodedColors;

		private ComputeBuffer encodedSH;

		private ComputeBuffer rawRotations;

		private ComputeBuffer rawOpacities;

		private ComputeBuffer rawColorData;

		private Texture2D colorsTexture;

		public int SplatCount { get; private set; }

		public Bounds Bounds { get; private set; }

		public bool IsLoaded => encodedPositions != null;

		public void AssignDataBuffers(CommandBuffer cmd, ComputeShader compute, int kernelID)
		{
			cmd.SetComputeIntParam(compute, "_positionFormat", (int)positionsFormat);
			cmd.SetComputeBufferParam(compute, kernelID, "_encodedPositions", encodedPositions);
			cmd.SetComputeIntParam(compute, "_rotationFormat", (int)rotationsFormat);
			cmd.SetComputeBufferParam(compute, kernelID, "_rawRotations", rawRotations);
			cmd.SetComputeBufferParam(compute, kernelID, "_encodedRotations", encodedRotations);
			cmd.SetComputeIntParam(compute, "_scaleFormat", (int)scalesFormat);
			cmd.SetComputeBufferParam(compute, kernelID, "_encodedScales", encodedScales);
			cmd.SetComputeIntParam(compute, "_colorFormat", (int)colorsFormat);
			cmd.SetComputeIntParam(compute, "_shFormat", (int)shFormat);
			cmd.SetComputeBufferParam(compute, kernelID, "_rawOpacities", rawOpacities);
			cmd.SetComputeBufferParam(compute, kernelID, "_rawColorData", rawColorData);
			cmd.SetComputeBufferParam(compute, kernelID, "_encodedColors", encodedColors);
			cmd.SetComputeBufferParam(compute, kernelID, "_encodedSH", encodedSH);
			cmd.SetComputeIntParam(compute, "_SplatChunkCount", splatChunkCount);
			cmd.SetComputeIntParam(compute, "_shIndexesOffset", shIndexesOffset);
			cmd.SetComputeBufferParam(compute, kernelID, "_chunks", chunks);
			cmd.SetComputeTextureParam(compute, kernelID, "_SplatColor", colorsTexture ?? _emptyTexture);
		}

		public void HandleUpload(GaussianSplatUpload upload)
		{
			base.AssetIntegrator.EnqueueProcessing(Upload, upload, highPriority: false);
		}

		private unsafe void Upload(object untypedUpload)
		{
			GaussianSplatUpload gaussianSplatUpload = (GaussianSplatUpload)untypedUpload;
			GaussianSplatUploadRaw gaussianSplatUploadRaw = gaussianSplatUpload as GaussianSplatUploadRaw;
			GaussianSplatUploadEncoded gaussianSplatUploadEncoded = gaussianSplatUpload as GaussianSplatUploadEncoded;
			if (_emptyTexture == null)
			{
				_emptyTexture = new Texture2D(4, 4);
				_emptyTexture.Apply();
			}
			Bounds = gaussianSplatUpload.bounds.ToUnity();
			if (gaussianSplatUploadEncoded != null)
			{
				positionsFormat = gaussianSplatUploadEncoded.positionsFormat;
				rotationsFormat = gaussianSplatUploadEncoded.rotationsFormat;
				scalesFormat = gaussianSplatUploadEncoded.scalesFormat;
				colorsFormat = gaussianSplatUploadEncoded.colorsFormat;
				shFormat = gaussianSplatUploadEncoded.shFormat;
				splatChunkCount = gaussianSplatUploadEncoded.chunkCount;
				shIndexesOffset = gaussianSplatUploadEncoded.shIndexesOffset;
				colorsTexture = RenderingManager.Instance.Texture2Ds.GetAsset(gaussianSplatUploadEncoded.texture2DtextureAssetId)?.Texture;
			}
			else
			{
				positionsFormat = GaussianVectorFormat.Float32;
				rotationsFormat = (GaussianRotationFormat)(-1);
				scalesFormat = GaussianVectorFormat.Float32;
				colorsFormat = (GaussianColorFormat)(-1);
				shFormat = (GaussianSHFormat)(-1);
				splatChunkCount = 0;
				shIndexesOffset = 0;
				colorsTexture = null;
			}
			bool instanceChanged = false;
			if (gaussianSplatUpload.splatCount != SplatCount)
			{
				instanceChanged = true;
				DisposeBuffers();
				if (gaussianSplatUpload.splatCount > 0)
				{
					encodedPositions = new ComputeBuffer(MathHelper.AlignToNextMultiple(gaussianSplatUpload.positionsBuffer.length, 4) / 4, 4);
					encodedScales = new ComputeBuffer(MathHelper.AlignToNextMultiple(gaussianSplatUpload.scalesBuffer.length, 4) / 4, 4);
					if (gaussianSplatUploadRaw != null)
					{
						chunks = new ComputeBuffer(1, 64);
						encodedRotations = new ComputeBuffer(1, 4);
						encodedColors = new ComputeBuffer(1, 4);
						encodedSH = new ComputeBuffer(1, 4);
						rawRotations = new ComputeBuffer(gaussianSplatUpload.splatCount, sizeof(Quaternion));
						rawOpacities = new ComputeBuffer(gaussianSplatUpload.splatCount, 4);
						rawColorData = new ComputeBuffer(gaussianSplatUpload.splatCount, 192);
					}
					else
					{
						if (splatChunkCount > 0)
						{
							chunks = new ComputeBuffer(splatChunkCount, 64);
						}
						else
						{
							chunks = new ComputeBuffer(1, 64);
						}
						encodedRotations = new ComputeBuffer(MathHelper.AlignToNextMultiple(gaussianSplatUpload.rotationsBuffer.length, 4) / 4, 4);
						encodedSH = new ComputeBuffer(MathHelper.AlignToNextMultiple(gaussianSplatUploadEncoded.shBuffer.length, 4) / 4, 4);
						UnityEngine.Debug.Log($"SH Format: {shFormat}, SplatCount: {gaussianSplatUpload.splatCount}, TotalBytes: {gaussianSplatUploadEncoded.shBuffer.length}, " + $"Buffer Stride: {encodedSH.stride}, Buffer Count: {encodedSH.count}");
						if (colorsFormat != GaussianColorFormat.BC7)
						{
							encodedColors = new ComputeBuffer(MathHelper.AlignToNextMultiple(gaussianSplatUpload.colorsBuffer.length, 4) / 4, 4);
						}
						else
						{
							encodedColors = new ComputeBuffer(1, 4);
						}
						rawRotations = new ComputeBuffer(1, sizeof(Quaternion));
						rawOpacities = new ComputeBuffer(1, 4);
						rawColorData = new ComputeBuffer(1, 192);
					}
				}
			}
			SplatCount = gaussianSplatUpload.splatCount;
			if (SplatCount > 0)
			{
				SharedMemoryAccessor sharedMemory = RenderingManager.Instance.SharedMemory;
				Span<byte> span = sharedMemory.AccessData(gaussianSplatUpload.positionsBuffer);
				Span<byte> span2 = sharedMemory.AccessData(gaussianSplatUpload.rotationsBuffer);
				Span<byte> span3 = sharedMemory.AccessData(gaussianSplatUpload.scalesBuffer);
				Span<byte> span4 = sharedMemory.AccessData(gaussianSplatUpload.colorsBuffer);
				SetData(encodedPositions, MemoryMarshal.Cast<byte, uint>(span));
				SetData(encodedScales, MemoryMarshal.Cast<byte, uint>(span3));
				if (gaussianSplatUploadRaw != null)
				{
					Span<byte> span5 = sharedMemory.AccessData(gaussianSplatUploadRaw.alphasBuffer);
					SetData(rawRotations, MemoryMarshal.Cast<byte, uint>(span2));
					SetData(rawOpacities, MemoryMarshal.Cast<byte, uint>(span5));
					SetData(rawColorData, MemoryMarshal.Cast<byte, uint>(span4));
				}
				else
				{
					Span<byte> span6 = sharedMemory.AccessData(gaussianSplatUploadEncoded.shBuffer);
					SetData(encodedRotations, MemoryMarshal.Cast<byte, uint>(span2));
					SetData(encodedColors, MemoryMarshal.Cast<byte, uint>(span4));
					SetData(encodedSH, MemoryMarshal.Cast<byte, uint>(span6));
					if (splatChunkCount > 0)
					{
						Span<byte> data = sharedMemory.AccessData(gaussianSplatUploadEncoded.chunksBuffer);
						SetData(chunks, data);
					}
				}
			}
			GaussianSplatResult gaussianSplatResult = new GaussianSplatResult();
			gaussianSplatResult.assetId = base.AssetId;
			gaussianSplatResult.instanceChanged = instanceChanged;
			RenderingManager.Instance.SendAssetUpdate(gaussianSplatResult);
			if (gaussianSplatUploadRaw != null)
			{
				PackerMemoryPool.Instance.Return(gaussianSplatUploadRaw);
			}
			if (gaussianSplatUploadEncoded != null)
			{
				PackerMemoryPool.Instance.Return(gaussianSplatUploadEncoded);
			}
		}

		private unsafe static void SetData<T>(ComputeBuffer buffer, Span<T> data) where T : unmanaged
		{
			fixed (T* dataPointer = data)
			{
				NativeArray<T> data2 = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(dataPointer, data.Length, Allocator.None);
				buffer.SetData(data2);
			}
		}

		private void DisposeBuffers()
		{
			chunks?.Dispose();
			encodedPositions?.Dispose();
			encodedRotations?.Dispose();
			encodedScales?.Dispose();
			encodedColors?.Dispose();
			encodedSH?.Dispose();
			rawRotations?.Dispose();
			rawOpacities?.Dispose();
			rawColorData?.Dispose();
			chunks = null;
			encodedPositions = null;
			encodedRotations = null;
			encodedScales = null;
			encodedColors = null;
			encodedSH = null;
			rawRotations = null;
			rawOpacities = null;
			rawColorData = null;
		}

		public void Unload()
		{
			RenderingManager.Instance.GaussianSplats.RemoveAsset(this);
			base.AssetIntegrator.EnqueueProcessing(DisposeBuffers, highPriority: true);
		}
	}
	public class MaterialAsset : Asset
	{
		public Material Material { get; private set; }

		public bool SetShader(Shader shader)
		{
			shader = shader ?? RenderingManager.Instance.NullShader;
			if (Material == null)
			{
				Material = new Material(shader);
				return true;
			}
			Material.shader = shader;
			return false;
		}

		public void Destroy()
		{
			if (Material != null)
			{
				UnityEngine.Object.Destroy(Material);
			}
			Material = null;
		}
	}
	public class MaterialAssetManager
	{
		private const int UNLOAD_GROUP_GRANULARITY = 100;

		public readonly AssetManager<MaterialAsset> Materials;

		public readonly AssetManager<MaterialPropertyBlockAsset> PropertyBlocks;

		private List<float> _floatArray = new List<float>();

		private List<Vector4> _vectorArray = new List<Vector4>();

		private ConcurrentQueue<int> _materialsIdsToUnload = new ConcurrentQueue<int>();

		private ConcurrentQueue<int> _propertyBlockIdsToUnload = new ConcurrentQueue<int>();

		private int _unloadScheduled;

		private bool HasUnloadsToProcess
		{
			get
			{
				if (_materialsIdsToUnload.Count <= 0)
				{
					return _propertyBlockIdsToUnload.Count > 0;
				}
				return true;
			}
		}

		public MaterialAssetManager()
		{
			Materials = new AssetManager<MaterialAsset>();
			PropertyBlocks = new AssetManager<MaterialPropertyBlockAsset>();
		}

		public void Handle(MaterialsUpdateBatch batch)
		{
			RenderingManager.Instance.AssetIntegrator.EnqueueProcessing(ApplyUpdate, batch, highPriority: true);
		}

		public void Handle(UnloadMaterial material)
		{
			_materialsIdsToUnload.Enqueue(material.assetId);
			PackerMemoryPool.Instance.Return(material);
			TryScheduleUnload();
		}

		public void Handle(UnloadMaterialPropertyBlock propertyBlock)
		{
			_propertyBlockIdsToUnload.Enqueue(propertyBlock.assetId);
			PackerMemoryPool.Instance.Return(propertyBlock);
			TryScheduleUnload();
		}

		private void TryScheduleUnload()
		{
			if (Interlocked.Exchange(ref _unloadScheduled, 1) == 0)
			{
				RenderingManager.Instance.AssetIntegrator.EnqueueProcessing(UnloadBatch(), highPriority: false);
			}
		}

		private IEnumerator UnloadBatch()
		{
			int maxToProcess = _materialsIdsToUnload.Count + _propertyBlockIdsToUnload.Count;
			int processed = 0;
			int result;
			while (_materialsIdsToUnload.TryDequeue(out result))
			{
				MaterialAsset asset = Materials.GetAsset(result);
				asset.Destroy();
				Materials.RemoveAsset(asset);
				if (processed++ % 100 == 0)
				{
					yield return null;
				}
				if (processed > maxToProcess)
				{
					break;
				}
			}
			int result2;
			while (_propertyBlockIdsToUnload.TryDequeue(out result2))
			{
				MaterialPropertyBlockAsset asset2 = PropertyBlocks.GetAsset(result2);
				asset2.Free();
				PropertyBlocks.RemoveAsset(asset2);
				if (processed++ % 100 == 0)
				{
					yield return null;
				}
				if (processed > maxToProcess)
				{
					break;
				}
			}
			if (HasUnloadsToProcess)
			{
				RenderingManager.Instance.AssetIntegrator.EnqueueProcessing(UnloadBatch(), highPriority: false);
				yield break;
			}
			_unloadScheduled = 0;
			if (HasUnloadsToProcess)
			{
				TryScheduleUnload();
			}
		}

		private void ApplyUpdate(object untypedBatch)
		{
			MaterialsUpdateBatch materialsUpdateBatch = (MaterialsUpdateBatch)untypedBatch;
			Span<uint> data = RenderingManager.Instance.SharedMemory.AccessData(materialsUpdateBatch.instanceChangedBuffer);
			BitSpan instanceChangedBuffer = new BitSpan(data);
			MaterialUpdateReader reader = new MaterialUpdateReader(materialsUpdateBatch, instanceChangedBuffer);
			MaterialAsset materialAsset = null;
			MaterialPropertyBlockAsset materialPropertyBlockAsset = null;
			bool? flag = null;
			bool flag2 = false;
			int num = 0;
			try
			{
				while (reader.HasNextUpdate)
				{
					MaterialPropertyUpdate update = reader.ReadUpdate();
					if (update.updateType == MaterialPropertyUpdateType.SelectTarget)
					{
						if (num == materialsUpdateBatch.materialUpdateCount)
						{
							flag2 = true;
						}
						num++;
						if (flag.HasValue)
						{
							reader.WriteInstanceChanged(flag.Value);
						}
						flag = false;
						if (flag2)
						{
							materialPropertyBlockAsset = PropertyBlocks.GetAsset(update.propertyID);
							flag = materialPropertyBlockAsset.EnsureInstance();
							if (RenderingManager.IsDebug)
							{
								UnityEngine.Debug.Log($"Targetting MaterialPropertyBlock: {materialPropertyBlockAsset.AssetId}");
							}
						}
						else
						{
							materialAsset = Materials.GetAsset(update.propertyID);
							if (RenderingManager.IsDebug)
							{
								UnityEngine.Debug.Log($"Targetting Material: {materialAsset.AssetId}. IsAllocated: {materialAsset.Material != null}");
							}
						}
					}
					else if (flag2)
					{
						bool? flag3 = flag;
						flag = HandlePropertyBlockUpdate(ref reader, ref update, materialPropertyBlockAsset) | flag3;
					}
					else
					{
						bool? flag3 = flag;
						flag = HandleMaterialUpdate(ref reader, ref update, materialAsset) | flag3;
					}
				}
			}
			catch (Exception)
			{
				UnityEngine.Debug.LogError("Exception when applying material update.\n" + $"UpdatedMaterials: {num}, MaterialUpdateCount: {materialsUpdateBatch.materialUpdateCount}\n" + "ReadState:\n" + reader.ToString());
				UnityEngine.Debug.LogError("Material update diagnostic:\n" + GenerateMaterialUpdateDiagnostic(materialsUpdateBatch));
				throw;
			}
			if (flag.HasValue)
			{
				reader.WriteInstanceChanged(flag.Value);
			}
			MaterialsUpdateBatchResult materialsUpdateBatchResult = new MaterialsUpdateBatchResult();
			materialsUpdateBatchResult.updateBatchId = materialsUpdateBatch.updateBatchId;
			RenderingManager.Instance.SendMaterialUpdateResult(materialsUpdateBatchResult);
			PackerMemoryPool.Instance.Return(materialsUpdateBatch);
		}

		private string GenerateMaterialUpdateDiagnostic(MaterialsUpdateBatch batch)
		{
			StringBuilder stringBuilder = new StringBuilder();
			try
			{
				Span<uint> data = RenderingManager.Instance.SharedMemory.AccessData(batch.instanceChangedBuffer);
				BitSpan instanceChangedBuffer = new BitSpan(data);
				MaterialUpdateReader materialUpdateReader = new MaterialUpdateReader(batch, instanceChangedBuffer);
				int num = 0;
				bool flag = false;
				while (materialUpdateReader.HasNextUpdate)
				{
					MaterialPropertyUpdate materialPropertyUpdate = materialUpdateReader.ReadUpdate();
					if (materialPropertyUpdate.updateType == MaterialPropertyUpdateType.SelectTarget)
					{
						if (num == batch.materialUpdateCount)
						{
							flag = true;
						}
						stringBuilder.AppendLine($"SelectTarget. IsPropertyBlock: {flag}, AssetID: {materialPropertyUpdate.propertyID}");
						num++;
						continue;
					}
					stringBuilder.AppendLine(materialPropertyUpdate.ToString());
					switch (materialPropertyUpdate.updateType)
					{
					case MaterialPropertyUpdateType.SetFloat:
						stringBuilder.AppendLine("Float: " + materialUpdateReader.ReadFloat());
						break;
					case MaterialPropertyUpdateType.SetFloat4:
						stringBuilder.AppendLine("Float4: " + materialUpdateReader.ReadVector().ToString());
						break;
					case MaterialPropertyUpdateType.SetFloat4x4:
						stringBuilder.AppendLine("Float4x4: " + materialUpdateReader.ReadMatrix().ToString());
						break;
					case MaterialPropertyUpdateType.SetFloatArray:
						stringBuilder.AppendLine("Float array length: " + materialUpdateReader.PeekInt());
						stringBuilder.AppendLine("Float array: " + string.Join(", ", materialUpdateReader.AccessFloatArray().ToArray()));
						break;
					case MaterialPropertyUpdateType.SetFloat4Array:
						stringBuilder.AppendLine("Float4 array length: " + materialUpdateReader.PeekInt());
						stringBuilder.AppendLine("Float4 array: " + string.Join(", ", materialUpdateReader.AccessVectorArray().ToArray()));
						break;
					case MaterialPropertyUpdateType.SetTexture:
					{
						int packed = materialUpdateReader.ReadInt();
						stringBuilder.AppendLine("PackedTextureID: " + packed);
						IdPacker<TextureAssetType>.Unpack(packed, out var id, out var type);
						stringBuilder.AppendLine($"TextureAssetId: {id}, AssetType: {type}");
						break;
					}
					}
				}
			}
			catch (Exception ex)
			{
				stringBuilder.AppendLine("EXCEPTION: " + ex);
			}
			return stringBuilder.ToString();
		}

		private bool HandleMaterialUpdate(ref MaterialUpdateReader reader, ref MaterialPropertyUpdate update, MaterialAsset target)
		{
			if (update.updateType != MaterialPropertyUpdateType.SetShader && target.Material == null)
			{
				throw new InvalidOperationException($"Material {target.AssetId} is not allocated and the first operation is not SetShader.\n" + $"Operation: {update.updateType}");
			}
			switch (update.updateType)
			{
			case MaterialPropertyUpdateType.SetShader:
			{
				int propertyID2 = update.propertyID;
				Shader shader = ((propertyID2 >= 0) ? RenderingManager.Instance.Shaders.GetAsset(propertyID2).UnityShader : null);
				return target.SetShader(shader);
			}
			case MaterialPropertyUpdateType.SetRenderQueue:
				target.Material.renderQueue = update.propertyID;
				return false;
			case MaterialPropertyUpdateType.SetInstancing:
				target.Material.enableInstancing = update.propertyID > 0;
				return false;
			case MaterialPropertyUpdateType.SetRenderType:
			{
				Material material = target.Material;
				MaterialRenderType propertyID = (MaterialRenderType)update.propertyID;
				material.SetOverrideTag("RenderType", propertyID.ToString());
				return false;
			}
			case MaterialPropertyUpdateType.SetFloat:
				target.Material.SetFloat(update.propertyID, reader.ReadFloat());
				return false;
			case MaterialPropertyUpdateType.SetFloat4:
				target.Material.SetVector(update.propertyID, reader.ReadVector());
				return false;
			case MaterialPropertyUpdateType.SetFloat4x4:
				target.Material.SetMatrix(update.propertyID, reader.ReadMatrix());
				return false;
			case MaterialPropertyUpdateType.SetFloatArray:
			{
				Span<float> span2 = reader.AccessFloatArray();
				for (int j = 0; j < span2.Length; j++)
				{
					_floatArray.Add(span2[j]);
				}
				target.Material.SetFloatArray(update.propertyID, _floatArray);
				_floatArray.Clear();
				return false;
			}
			case MaterialPropertyUpdateType.SetFloat4Array:
			{
				Span<Vector4> span = reader.AccessVectorArray();
				for (int i = 0; i < span.Length; i++)
				{
					_vectorArray.Add(span[i]);
				}
				target.Material.SetVectorArray(update.propertyID, _vectorArray);
				_vectorArray.Clear();
				return false;
			}
			case MaterialPropertyUpdateType.SetTexture:
				target.Material.SetTexture(update.propertyID, TextureHelper.GetTexture(reader.ReadInt()));
				return false;
			default:
			{
				MaterialPropertyUpdate materialPropertyUpdate = update;
				throw new InvalidOperationException("Invalid update type: " + materialPropertyUpdate.ToString());
			}
			}
		}

		private bool HandlePropertyBlockUpdate(ref MaterialUpdateReader reader, ref MaterialPropertyUpdate update, MaterialPropertyBlockAsset target)
		{
			switch (update.updateType)
			{
			case MaterialPropertyUpdateType.SetShader:
			case MaterialPropertyUpdateType.SetRenderQueue:
			case MaterialPropertyUpdateType.SetInstancing:
			case MaterialPropertyUpdateType.SetRenderType:
				throw new InvalidOperationException("Invalid operation for material property block: " + update.updateType);
			case MaterialPropertyUpdateType.SetFloat:
				target.PropertyBlock.SetFloat(update.propertyID, reader.ReadFloat());
				return true;
			case MaterialPropertyUpdateType.SetFloat4:
				target.PropertyBlock.SetVector(update.propertyID, reader.ReadVector());
				return true;
			case MaterialPropertyUpdateType.SetFloat4x4:
				target.PropertyBlock.SetMatrix(update.propertyID, reader.ReadMatrix());
				return true;
			case MaterialPropertyUpdateType.SetFloatArray:
			{
				Span<float> span2 = reader.AccessFloatArray();
				for (int j = 0; j < span2.Length; j++)
				{
					_floatArray.Add(span2[j]);
				}
				target.PropertyBlock.SetFloatArray(update.propertyID, _floatArray);
				_floatArray.Clear();
				return true;
			}
			case MaterialPropertyUpdateType.SetFloat4Array:
			{
				Span<Vector4> span = reader.AccessVectorArray();
				for (int i = 0; i < span.Length; i++)
				{
					_vectorArray.Add(span[i]);
				}
				target.PropertyBlock.SetVectorArray(update.propertyID, _vectorArray);
				_vectorArray.Clear();
				return true;
			}
			case MaterialPropertyUpdateType.SetTexture:
				target.PropertyBlock.SetTexture(update.propertyID, TextureHelper.GetTexture(reader.ReadInt()) ?? Texture2D.whiteTexture);
				return true;
			default:
			{
				MaterialPropertyUpdate materialPropertyUpdate = update;
				throw new InvalidOperationException("Invalid update type: " + materialPropertyUpdate.ToString());
			}
			}
		}
	}
	public class MaterialPropertyBlockAsset : Asset
	{
		private static Stack<MaterialPropertyBlock> _blockPool = new Stack<MaterialPropertyBlock>();

		public MaterialPropertyBlock PropertyBlock { get; private set; }

		public bool EnsureInstance()
		{
			if (PropertyBlock != null)
			{
				return false;
			}
			if (_blockPool.Count == 0)
			{
				PropertyBlock = new MaterialPropertyBlock();
			}
			else
			{
				PropertyBlock = _blockPool.Pop();
			}
			return true;
		}

		public void Free()
		{
			if (PropertyBlock != null)
			{
				PropertyBlock.Clear();
				_blockPool.Push(PropertyBlock);
				PropertyBlock = null;
			}
		}
	}
	public ref struct MaterialUpdateReader
	{
		private MaterialsUpdateBatch batch;

		private int instanceChangedIndex;

		private BitSpan instanceChangedBuffer;

		private Span<MaterialPropertyUpdate> updateBuffer;

		private Span<int> intBuffer;

		private Span<float> floatBuffer;

		private Span<Vector4> vectorBuffer;

		private Span<Matrix4x4> matrixBuffer;

		private int updateBufferIndex;

		private int intBufferIndex;

		private int floatBufferIndex;

		private int vectorBufferIndex;

		private int matrixBufferIndex;

		private int updateIndex;

		private int intIndex;

		private int floatIndex;

		private int vectorIndex;

		private int matrixIndex;

		public bool HasNextUpdate
		{
			get
			{
				if (updateIndex == updateBuffer.Length)
				{
					if (updateBufferIndex == batch.materialUpdates.Count)
					{
						return false;
					}
					return true;
				}
				return updateBuffer[updateIndex].updateType != MaterialPropertyUpdateType.UpdateBatchEnd;
			}
		}

		public MaterialUpdateReader(MaterialsUpdateBatch batch, BitSpan instanceChangedBuffer)
		{
			this = default(MaterialUpdateReader);
			this.batch = batch;
			this.instanceChangedBuffer = instanceChangedBuffer;
		}

		public void WriteInstanceChanged(bool instanceChanged)
		{
			instanceChangedBuffer[instanceChangedIndex++] = instanceChanged;
		}

		public MaterialPropertyUpdate ReadUpdate()
		{
			return ReadValue(ref updateBufferIndex, ref updateIndex, ref updateBuffer, batch.materialUpdates);
		}

		public int PeekInt()
		{
			return ReadValue(ref intBufferIndex, ref intIndex, ref intBuffer, batch.intBuffers, advance: false);
		}

		public int ReadInt()
		{
			return ReadValue(ref intBufferIndex, ref intIndex, ref intBuffer, batch.intBuffers);
		}

		public float ReadFloat()
		{
			return ReadValue(ref floatBufferIndex, ref floatIndex, ref floatBuffer, batch.floatBuffers);
		}

		public Vector4 ReadVector()
		{
			return ReadValue(ref vectorBufferIndex, ref vectorIndex, ref vectorBuffer, batch.float4Buffers);
		}

		public Matrix4x4 ReadMatrix()
		{
			return ReadValue(ref matrixBufferIndex, ref matrixIndex, ref matrixBuffer, batch.matrixBuffers);
		}

		public Span<float> AccessFloatArray()
		{
			return AccessArray(ref floatBufferIndex, ref floatIndex, ref floatBuffer, batch.floatBuffers);
		}

		public Span<Vector4> AccessVectorArray()
		{
			return AccessArray(ref vectorBufferIndex, ref vectorIndex, ref vectorBuffer, batch.float4Buffers);
		}

		private T ReadValue<T, S>(ref int bufferIndex, ref int valueIndex, ref Span<T> buffer, List<SharedMemoryBufferDescriptor<S>> list, bool advance = true) where T : unmanaged where S : unmanaged
		{
			if (valueIndex == buffer.Length)
			{
				buffer = FetchNextBuffer<T, S>(ref bufferIndex, ref valueIndex, list);
			}
			T result = buffer[valueIndex];
			if (advance)
			{
				valueIndex++;
			}
			return result;
		}

		private Span<T> AccessArray<T, S>(ref int bufferIndex, ref int valueIndex, ref Span<T> buffer, List<SharedMemoryBufferDescriptor<S>> list) where T : unmanaged where S : unmanaged
		{
			int num = ReadInt();
			if (num + valueIndex > buffer.Length)
			{
				buffer = FetchNextBuffer<T, S>(ref bufferIndex, ref valueIndex, list);
			}
			Span<T> result = buffer.Slice(valueIndex, num);
			valueIndex += num;
			return result;
		}

		private Span<T> FetchNextBuffer<T, S>(ref int bufferIndex, ref int valueIndex, List<SharedMemoryBufferDescriptor<S>> list) where T : unmanaged where S : unmanaged
		{
			if (bufferIndex >= list.Count)
			{
				throw new InvalidOperationException($"Next buffer of type {typeof(T)} does not exist!");
			}
			SharedMemoryBufferDescriptor<S> sharedMemoryBufferDescriptor = list[bufferIndex++];
			valueIndex = 0;
			return RenderingManager.Instance.SharedMemory.AccessData(sharedMemoryBufferDescriptor.As<T>());
		}

		public override string ToString()
		{
			return $"InstanceChangedIndex: {instanceChangedIndex}\n" + $"UpdateBufferIndex: {updateBufferIndex}\n" + $"IntBufferIndex: {intBufferIndex}\n" + $"FloatBufferIndex: {floatBufferIndex}\n" + $"VectorBufferIndex: {vectorBufferIndex}\n" + $"MatrixBufferIndex: {matrixBufferIndex}\n" + "\n" + $"UpdateIndex: {updateIndex}\n" + $"IntIndex: {intIndex}\n" + $"FloatIndex: {floatIndex}\n" + $"VectorIndex: {vectorIndex}\n" + $"MatrixIndex: {matrixIndex}\n" + "\n" + $"Current UpdateBuffer.Length: {updateBuffer.Length}\n" + $"Current IntBuffer.Length {intBuffer.Length}\n" + $"Current FloatBuffer.Length {floatBuffer.Length}\n" + $"Current VectorBuffer.Length {vectorBuffer.Length}\n" + $"Current MatrixBuffer.Length {matrixBuffer.Length}\n" + "\n" + $"UpdateBatch:\n{batch}";
		}
	}
	public class MeshAsset : Asset
	{
		private static List<string> blendshapeNames = new List<string>();

		public const MeshUpdateFlags UPDATE_FLAGS = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontResetBoneBounds | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds;

		private Mesh mesh;

		private MeshBuffer meshBuffer;

		private MeshUploadData uploadData;

		private bool firstUploadCompleted;

		private IndexBufferFormat? lastIndexBufferFormat;

		public Mesh Mesh => mesh;

		public static string GetBlendshapeName(int index)
		{
			while (blendshapeNames.Count <= index)
			{
				blendshapeNames.Add(blendshapeNames.Count.ToString());
			}
			return blendshapeNames[index];
		}

		public void Handle(MeshUploadData uploadData)
		{
			if (this.uploadData != null)
			{
				throw new InvalidOperationException("Cannot handle upload data, because previous upload was not processed yet!");
			}
			meshBuffer = ExtractMeshBuffer(uploadData);
			this.uploadData = uploadData;
			base.AssetIntegrator.EnqueueProcessing(Upload(), uploadData.highPriority);
		}

		public void Handle(MeshUnload unload)
		{
			Unload();
			RenderingManager.Instance.Meshes.RemoveAsset(this);
			PackerMemoryPool.Instance.Return(unload);
		}

		private MeshBuffer ExtractMeshBuffer(MeshUploadData uploadData)
		{
			MeshBuffer meshBuffer = new MeshBuffer(uploadData);
			if (!uploadData.buffer.IsEmpty)
			{
				meshBuffer.Data = RenderingManager.Instance.SharedMemory.AccessSlice(uploadData.buffer);
			}
			return meshBuffer;
		}

		private void UpdateVertexLayout(MeshBuffer buffer, Mesh mesh)
		{
			UnityEngine.Rendering.VertexAttributeDescriptor[] array = new UnityEngine.Rendering.VertexAttributeDescriptor[buffer.VertexAttributeCount];
			for (int i = 0; i < buffer.VertexAttributeCount; i++)
			{
				Renderite.Shared.VertexAttributeDescriptor vertexAttributeDescriptor = buffer.VertexAttributes[i];
				array[i] = new UnityEngine.Rendering.VertexAttributeDescriptor(vertexAttributeDescriptor.attribute.ToUnity(), vertexAttributeDescriptor.format.ToUnity(), vertexAttributeDescriptor.dimensions);
			}
			mesh.SetVertexBufferParams(buffer.VertexCount, array);
		}

		private void UpdateIndexLayout(MeshBuffer buffer, Mesh mesh)
		{
			mesh.SetIndexBufferParams(buffer.IndexCount, buffer.IndexBufferFormat.ToUnity());
		}

		private void UpdateSubmeshLayout(MeshBuffer buffer, Mesh mesh)
		{
			if (mesh.subMeshCount != buffer.SubmeshCount)
			{
				SanitizeSubmeshes();
			}
			mesh.subMeshCount = buffer.SubmeshCount;
			for (int i = 0; i < buffer.SubmeshCount; i++)
			{
				SubmeshBufferDescriptor submeshBufferDescriptor = buffer.Submeshes[i];
				mesh.SetSubMesh(i, new SubMeshDescriptor
				{
					baseVertex = 0,
					bounds = submeshBufferDescriptor.bounds.ToUnity(),
					firstVertex = 0,
					indexStart = submeshBufferDescriptor.indexStart,
					indexCount = submeshBufferDescriptor.indexCount,
					topology = submeshBufferDescriptor.topology.ToUnity(),
					vertexCount = buffer.VertexCount
				}, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontResetBoneBounds | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds);
			}
		}

		private unsafe void UploadVertexBuffer(MeshBuffer buffer, Mesh mesh)
		{
			Span<byte> rawVertexBufferData = meshBuffer.GetRawVertexBufferData();
			fixed (byte* ptr = rawVertexBufferData)
			{
				void* dataPointer = ptr;
				NativeArray<byte> data = ((!RenderingManager.IsDebug) ? NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(dataPointer, rawVertexBufferData.Length, Allocator.None) : new NativeArray<byte>(rawVertexBufferData.ToArray(), Allocator.Persistent));
				mesh.SetVertexBufferData(data, 0, 0, rawVertexBufferData.Length, 0, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontResetBoneBounds | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds);
				data.Dispose();
			}
		}

		private unsafe void UploadIndexBuffer(MeshBuffer buffer, Mesh mesh)
		{
			UpdateIndexLayout(meshBuffer, mesh);
			try
			{
				Span<byte> rawIndexBufferData = meshBuffer.GetRawIndexBufferData();
				fixed (byte* ptr = rawIndexBufferData)
				{
					void* dataPointer = ptr;
					NativeArray<byte> data = ((!RenderingManager.IsDebug) ? NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(dataPointer, rawIndexBufferData.Length, Allocator.None) : new NativeArray<byte>(rawIndexBufferData.ToArray(), Allocator.Persistent));
					mesh.SetIndexBufferData(data, 0, 0, rawIndexBufferData.Length, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontResetBoneBounds | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds);
					data.Dispose();
				}
			}
			catch (Exception)
			{
				UnityEngine.Debug.LogError($"[{GetHashCode()}] Exception uploading index buffer. MeshBuffer: {buffer.IndexCount}, " + $"Submeshes: {buffer.SubmeshCount}, Mesh: {mesh.indexFormat}.\n" + string.Join("\n", buffer.Submeshes.Select((SubmeshBufferDescriptor s) => s.ToString())));
				throw;
			}
		}

		private unsafe void UploadBonesBuffer(MeshBuffer buffer, Mesh mesh)
		{
			if (buffer.BindPosesBufferLength == 0)
			{
				mesh.bindposes = null;
				return;
			}
			Span<Matrix4x4> bindPosesBuffer = buffer.GetBindPosesBuffer<Matrix4x4>();
			Matrix4x4[] array = mesh.bindposes;
			if (array?.Length != bindPosesBuffer.Length)
			{
				array = new Matrix4x4[bindPosesBuffer.Length];
			}
			bindPosesBuffer.CopyTo(array);
			mesh.bindposes = array;
			Span<byte> boneCountsBuffer = buffer.GetBoneCountsBuffer();
			Span<BoneWeight1> span = MemoryMarshal.Cast<Renderite.Shared.BoneWeight, BoneWeight1>(buffer.GetBoneWeightsBuffer());
			fixed (byte* ptr = boneCountsBuffer)
			{
				void* dataPointer = ptr;
				fixed (BoneWeight1* ptr2 = span)
				{
					void* dataPointer2 = ptr2;
					NativeArray<byte> bonesPerVertex;
					NativeArray<BoneWeight1> weights;
					if (RenderingManager.IsDebug)
					{
						bonesPerVertex = new NativeArray<byte>(boneCountsBuffer.ToArray(), Allocator.Persistent);
						weights = new NativeArray<BoneWeight1>(span.ToArray(), Allocator.Persistent);
					}
					else
					{
						bonesPerVertex = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(dataPointer, boneCountsBuffer.Length, Allocator.None);
						weights = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<BoneWeight1>(dataPointer2, span.Length, Allocator.None);
					}
					mesh.SetBoneWeights(bonesPerVertex, weights);
					bonesPerVertex.Dispose();
					weights.Dispose();
				}
			}
		}

		private void SanitizeSubmeshes()
		{
			for (int i = 0; i < mesh.subMeshCount; i++)
			{
				mesh.SetSubMesh(i, new SubMeshDescriptor
				{
					baseVertex = 0,
					bounds = new Bounds(Vector3.zero, Vector3.zero),
					firstVertex = 0,
					indexStart = 0,
					indexCount = 0,
					topology = MeshTopology.Triangles,
					vertexCount = 0
				}, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontResetBoneBounds | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds);
			}
		}

		private IEnumerator Upload()
		{
			if (meshBuffer == null)
			{
				yield break;
			}
			if (uploadData == null)
			{
				throw new InvalidOperationException("Cannot run Upload when uploadData is not assigned!");
			}
			MeshUploadHint uploadHint = uploadData.uploadHint;
			if (uploadHint[MeshUploadHint.Flag.Debug] || RenderingManager.IsDebug)
			{
				string text = $"Uploading Mesh {base.AssetId} (first: {!firstUploadCompleted}): {meshBuffer}\nHint: ";
				MeshUploadHint meshUploadHint = uploadHint;
				string text2 = meshUploadHint.ToString();
				RenderBoundingBox bounds = uploadData.bounds;
				UnityEngine.Debug.Log(text + text2 + "\nBounds: " + bounds.ToString());
			}
			if (mesh != null && !mesh.isReadable)
			{
				if ((bool)mesh)
				{
					UnityEngine.Object.Destroy(mesh);
				}
				mesh = null;
			}
			bool instanceChanged = false;
			if (mesh == null)
			{
				mesh = new Mesh();
				instanceChanged = true;
				if (uploadHint[MeshUploadHint.Flag.Dynamic])
				{
					mesh.MarkDynamic();
				}
				if (RenderingManager.IsDebug)
				{
					mesh.name = $"AssetId: {base.AssetId}";
				}
			}
			if (uploadHint[MeshUploadHint.Flag.VertexLayout])
			{
				UpdateVertexLayout(meshBuffer, mesh);
			}
			if (!firstUploadCompleted)
			{
				yield return null;
			}
			if (uploadHint.AnyVertexStreams)
			{
				UploadVertexBuffer(meshBuffer, mesh);
			}
			if (!firstUploadCompleted)
			{
				yield return null;
			}
			if (mesh.subMeshCount != meshBuffer.SubmeshCount)
			{
				SanitizeSubmeshes();
			}
			bool flag = lastIndexBufferFormat != meshBuffer.IndexBufferFormat;
			lastIndexBufferFormat = meshBuffer.IndexBufferFormat;
			if (flag)
			{
				SanitizeSubmeshes();
			}
			if (firstUploadCompleted && !flag)
			{
				if (uploadHint[MeshUploadHint.Flag.SubmeshLayout])
				{
					UpdateSubmeshLayout(meshBuffer, mesh);
				}
				if (uploadHint[MeshUploadHint.Flag.Geometry] || uploadHint[MeshUploadHint.Flag.SubmeshLayout])
				{
					UploadIndexBuffer(meshBuffer, mesh);
				}
			}
			else
			{
				if (uploadHint[MeshUploadHint.Flag.Geometry] || uploadHint[MeshUploadHint.Flag.SubmeshLayout])
				{
					UploadIndexBuffer(meshBuffer, mesh);
				}
				if (uploadHint[MeshUploadHint.Flag.SubmeshLayout])
				{
					UpdateSubmeshLayout(meshBuffer, mesh);
				}
			}
			if (uploadHint[MeshUploadHint.Flag.BoneWeights])
			{
				if (meshBuffer.BoneCount <= 0)
				{
					Matrix4x4[] bindposes = mesh.bindposes;
					if (((bindposes != null && bindposes.Length != 0) ? 1 : 0) <= (false ? 1 : 0))
					{
						goto IL_0383;
					}
				}
				if (!firstUploadCompleted)
				{
					yield return null;
				}
				UploadBonesBuffer(meshBuffer, mesh);
			}
			goto IL_0383;
			IL_0383:
			mesh.bounds = uploadData.bounds.ToUnity();
			int offset;
			if (uploadHint[MeshUploadHint.Flag.Blendshapes] && meshBuffer.BlendshapeBufferCount > 0)
			{
				yield return null;
				Matrix4x4[] bindposes2 = mesh.bindposes;
				if (bindposes2 == null || bindposes2.Length == 0)
				{
					Matrix4x4[] bindposes3 = new Matrix4x4[1] { Matrix4x4.identity };
					mesh.bindposes = bindposes3;
					NativeArray<byte> bonesPerVertex = new NativeArray<byte>(meshBuffer.VertexCount, Allocator.Temp);
					NativeArray<BoneWeight1> weights = new NativeArray<BoneWeight1>(meshBuffer.VertexCount, Allocator.Temp);
					for (int i = 0; i < meshBuffer.VertexCount; i++)
					{
						bonesPerVertex[i] = 1;
						weights[i] = new BoneWeight1
						{
							boneIndex = 0,
							weight = 0f
						};
					}
					mesh.SetBoneWeights(bonesPerVertex, weights);
					bonesPerVertex.Dispose();
					weights.Dispose();
				}
				if (mesh.blendShapeCount > 0)
				{
					mesh.ClearBlendShapes();
					yield return null;
				}
				Vector3[] norStaging = null;
				Vector3[] tanStaging = null;
				Vector3[] posStaging = new Vector3[meshBuffer.VertexCount];
				offset = 0;
				for (int j = 0; j < meshBuffer.BlendshapeBufferCount; j++)
				{
					BlendshapeBufferDescriptor blendshapeBufferDescriptor = meshBuffer.BlendshapeBuffers[j];
					string blendshapeName = GetBlendshapeName(blendshapeBufferDescriptor.blendshapeIndex);
					Span<Vector3> blendshapeBuffer = meshBuffer.GetBlendshapeBuffer<Vector3>();
					ExtractData(blendshapeBuffer, posStaging);
					bool flag2 = blendshapeBufferDescriptor.dataFlags.HasFlag(BlendshapeDataFlags.Normals);
					bool flag3 = blendshapeBufferDescriptor.dataFlags.HasFlag(BlendshapeDataFlags.Tangets);
					if (flag2)
					{
						if (norStaging == null)
						{
							norStaging = new Vector3[meshBuffer.VertexCount];
						}
						ExtractData(blendshapeBuffer, norStaging);
					}
					if (flag3)
					{
						if (tanStaging == null)
						{
							tanStaging = new Vector3[meshBuffer.VertexCount];
						}
						ExtractData(blendshapeBuffer, tanStaging);
					}
					mesh.AddBlendShapeFrame(blendshapeName, blendshapeBufferDescriptor.frameWeight, posStaging, flag2 ? norStaging : null, flag3 ? tanStaging : null);
					yield return null;
				}
			}
			meshBuffer = null;
			firstUploadCompleted = true;
			PackerMemoryPool.Instance.Return(uploadData);
			uploadData = null;
			MeshUploadResult meshUploadResult = new MeshUploadResult();
			meshUploadResult.assetId = base.AssetId;
			meshUploadResult.instanceChanged = instanceChanged;
			RenderingManager.Instance.SendAssetUpdate(meshUploadResult);
			void ExtractData(Span<Vector3> source, Vector3[] target)
			{
				source.Slice(offset, meshBuffer.VertexCount).CopyTo(target.AsSpan());
				offset += meshBuffer.VertexCount;
			}
		}

		public void Unload()
		{
			base.AssetIntegrator.EnqueueProcessing(Destroy, highPriority: true);
		}

		private void Destroy()
		{
			if (mesh != null)
			{
				UnityEngine.Object.Destroy(mesh);
			}
			mesh = null;
			meshBuffer = null;
		}
	}
	public class PointRenderBufferAsset : RenderBufferAssetBase<PointRenderBufferAsset, PointRenderBufferUpload, PointRenderBufferConsumed>
	{
		public void HandleUnload(PointRenderBufferUnload unload)
		{
			Unload();
			RenderingManager.Instance.PointRenderBuffers.RemoveAsset(this);
			PackerMemoryPool.Instance.Return(unload);
		}
	}
	public interface IRenderBufferAsset<A, U> where A : IRenderBufferAsset<A, U> where U : RenderBufferUpload
	{
		void RegisterListener(RenderBufferUpdateHandler<A, U> handler);

		void UnregisterListener(RenderBufferUpdateHandler<A, U> handler);

		void BufferConsumed();
	}
	public delegate void RenderBufferUpdateHandler<A, D>(A asset, D data);
	public abstract class RenderBufferAssetBase<A, U, C> : Asset, IRenderBufferAsset<A, U> where A : RenderBufferAssetBase<A, U, C> where U : RenderBufferUpload, new() where C : AssetCommand, new()
	{
		private int remainingListenersToUpdate;

		private HashSet<RenderBufferUpdateHandler<A, U>> bufferUpdateListeners = new HashSet<RenderBufferUpdateHandler<A, U>>();

		private U _lastData;

		public void HandleUpload(U data)
		{
			lock (bufferUpdateListeners)
			{
				if (remainingListenersToUpdate > 0)
				{
					throw new InvalidOperationException("There are still listeners handling previous buffer update, cannot update them again!");
				}
				_lastData = data;
				if (bufferUpdateListeners.Count == 0)
				{
					SendBuffersConsumed();
					return;
				}
				remainingListenersToUpdate = bufferUpdateListeners.Count;
				foreach (RenderBufferUpdateHandler<A, U> bufferUpdateListener in bufferUpdateListeners)
				{
					bufferUpdateListener((A)this, data);
				}
			}
		}

		public void BufferConsumed()
		{
			if (Interlocked.Decrement(ref remainingListenersToUpdate) == 0)
			{
				SendBuffersConsumed();
			}
		}

		private void SendBuffersConsumed()
		{
			PackerMemoryPool.Instance.Return(_lastData);
			_lastData = null;
			C command = new C
			{
				assetId = base.AssetId
			};
			RenderingManager.Instance.SendAssetUpdate(command);
		}

		protected void Unload()
		{
			bufferUpdateListeners = null;
		}

		public void RegisterListener(RenderBufferUpdateHandler<A, U> callback)
		{
			lock (bufferUpdateListeners)
			{
				if (!bufferUpdateListeners.Add(callback))
				{
					throw new InvalidOperationException("Listener is already registered");
				}
			}
		}

		public void UnregisterListener(RenderBufferUpdateHandler<A, U> callback)
		{
			lock (bufferUpdateListeners)
			{
				if (!bufferUpdateListeners.Remove(callback))
				{
					throw new InvalidOperationException("Listener is not registered");
				}
			}
		}
	}
	public class TrailsRenderBufferAsset : RenderBufferAssetBase<TrailsRenderBufferAsset, TrailRenderBufferUpload, TrailRenderBufferConsumed>
	{
		public void HandleUnload(TrailRenderBufferUnload unload)
		{
			Unload();
			RenderingManager.Instance.TrailsRenderBuffers.RemoveAsset(this);
			PackerMemoryPool.Instance.Return(unload);
		}
	}
	public class ShaderAsset : Asset
	{
		private AssetBundle _assetBundle;

		public Shader UnityShader { get; private set; }

		public void Handle(ShaderUpload uploadData)
		{
			LoadFromFile(uploadData.file);
			PackerMemoryPool.Instance.Return(uploadData);
		}

		public void Handle(ShaderUnload unload)
		{
			Unload();
			RenderingManager.Instance.Shaders.RemoveAsset(this);
			PackerMemoryPool.Instance.Return(unload);
		}

		private void LoadFromFile(string file)
		{
			base.AssetIntegrator.EnqueueProcessing(LoadShader(file), highPriority: true);
		}

		private IEnumerator LoadShader(string file)
		{
			UnloadImmediate();
			try
			{
				AssetBundleCreateRequest bundleRequest = AssetBundle.LoadFromFileAsync(file);
				((AsyncOperation)(object)bundleRequest).completed += delegate
				{
					try
					{
						_assetBundle = bundleRequest.assetBundle;
						if ((UnityEngine.Object)(object)_assetBundle == null)
						{
							UnityEngine.Debug.LogWarning($"Could not load shader asset bundle: {file}, exists: {File.Exists(file)}");
							SendLoaded();
						}
						else
						{
							AssetBundleRequest shaderRequest = _assetBundle.LoadAssetAsync<Shader>(_assetBundle.GetAllAssetNames()[0]);
							((AsyncOperation)(object)shaderRequest).completed += delegate
							{
								try
								{
									UnityShader = shaderRequest.asset as Shader;
									SendLoaded();
								}
								catch (Exception arg2)
								{
									UnityEngine.Debug.LogError($"Exception loading shader from the loaded bundle {file}\n{arg2}");
									SendLoaded();
								}
							};
						}
					}
					catch (Exception arg)
					{
						UnityEngine.Debug.LogError($"Exception processing loaded shader bundle for {file}\n{arg}");
						SendLoaded();
					}
				};
			}
			catch (Exception ex)
			{
				UnityEngine.Debug.LogError("Exception loading shader from file: " + file + "\n" + ex);
				throw;
			}
			yield break;
		}

		private void SendLoaded()
		{
			ShaderUploadResult shaderUploadResult = new ShaderUploadResult();
			shaderUploadResult.assetId = base.AssetId;
			shaderUploadResult.instanceChanged = true;
			RenderingManager.Instance.SendAssetUpdate(shaderUploadResult);
		}

		private void Unload()
		{
			base.AssetIntegrator.EnqueueProcessing(UnloadImmediate, highPriority: true);
		}

		private void UnloadImmediate()
		{
			if ((UnityEngine.Object)(object)_assetBundle != null)
			{
				_assetBundle.Unload(true);
				if ((bool)(UnityEngine.Object)(object)_assetBundle)
				{
					UnityEngine.Object.Destroy((UnityEngine.Object)(object)_assetBundle);
				}
			}
			if ((bool)UnityShader)
			{
				UnityEngine.Object.Destroy(UnityShader);
			}
			_assetBundle = null;
			UnityShader = null;
		}
	}
	public class DesktopTextureAsset : Asset
	{
		private IDisplayTextureSource _source;

		public Texture Texture => _source?.UnityTexture;

		public void Handle(SetDesktopTextureProperties properties)
		{
			base.AssetIntegrator.EnqueueProcessing(Update, properties, highPriority: false);
		}

		private void Update(object untyped)
		{
			SetDesktopTextureProperties setDesktopTextureProperties = (SetDesktopTextureProperties)untyped;
			IDisplayTextureSource displayTextureSource = RenderingManager.Instance.Display.TryGetDisplayTexture(setDesktopTextureProperties.displayIndex);
			if (displayTextureSource != _source)
			{
				FreeSource();
				if (displayTextureSource != null)
				{
					_source = displayTextureSource;
					_source.RegisterRequest(TextureUpdated);
				}
				TextureUpdated();
			}
			PackerMemoryPool.Instance.Return(setDesktopTextureProperties);
		}

		private void FreeSource()
		{
			IDisplayTextureSource source = _source;
			_source = null;
			if (source != null)
			{
				base.AssetIntegrator.EnqueueProcessing(delegate
				{
					source.UnregisterRequest(TextureUpdated);
				}, highPriority: true);
			}
		}

		private void TextureUpdated()
		{
			DesktopTexturePropertiesUpdate desktopTexturePropertiesUpdate = new DesktopTexturePropertiesUpdate();
			desktopTexturePropertiesUpdate.assetId = base.AssetId;
			desktopTexturePropertiesUpdate.size = new RenderVector2i(Texture?.width ?? 0, Texture?.height ?? 0);
			RenderingManager.Instance.SendAssetUpdate(desktopTexturePropertiesUpdate);
		}

		public void Unload()
		{
			RenderingManager.Instance.DesktopTextures.RemoveAsset(this);
			base.AssetIntegrator.EnqueueProcessing(FreeSource, highPriority: true);
		}
	}
	public static class DX11Helper
	{
		public static Format ToDX11(this Renderite.Shared.TextureFormat format, ColorProfile profile, bool usingLinearSpace)
		{
			//IL_0014: Unknown result type (might be due to invalid IL or missing references)
			Format? val = format.TryToDX11(profile, usingLinearSpace);
			if (val.HasValue)
			{
				return val.Value;
			}
			throw new NotSupportedException($"Cannot convert {format} with color profile {profile} to DX11");
		}

		public static Format? TryToDX11(this Renderite.Shared.TextureFormat format, ColorProfile profile, bool usingLinearSpace)
		{
			switch (format)
			{
			case Renderite.Shared.TextureFormat.Alpha8:
				return (Format)65;
			case Renderite.Shared.TextureFormat.R8:
				return (Format)61;
			case Renderite.Shared.TextureFormat.RGBA32:
				switch (profile)
				{
				case ColorProfile.Linear:
					return (Format)28;
				case ColorProfile.sRGB:
				case ColorProfile.sRGBAlpha:
					if (usingLinearSpace)
					{
						return (Format)29;
					}
					return (Format)28;
				default:
					return null;
				}
			case Renderite.Shared.TextureFormat.RGBAHalf:
				return (Format)10;
			case Renderite.Shared.TextureFormat.RGBAFloat:
				return (Format)2;
			case Renderite.Shared.TextureFormat.RHalf:
				return (Format)54;
			case Renderite.Shared.TextureFormat.RGHalf:
				return (Format)34;
			case Renderite.Shared.TextureFormat.RFloat:
				return (Format)41;
			case Renderite.Shared.TextureFormat.RGFloat:
				return (Format)16;
			case Renderite.Shared.TextureFormat.BC1:
				switch (profile)
				{
				case ColorProfile.Linear:
					return (Format)71;
				case ColorProfile.sRGB:
				case ColorProfile.sRGBAlpha:
					if (usingLinearSpace)
					{
						return (Format)72;
					}
					return (Format)71;
				default:
					return null;
				}
			case Renderite.Shared.TextureFormat.BC2:
				switch (profile)
				{
				case ColorProfile.Linear:
					return (Format)74;
				case ColorProfile.sRGB:
				case ColorProfile.sRGBAlpha:
					if (usingLinearSpace)
					{
						return (Format)75;
					}
					return (Format)74;
				default:
					return null;
				}
			case Renderite.Shared.TextureFormat.BC3:
				switch (profile)
				{
				case ColorProfile.Linear:
					return (Format)77;
				case ColorProfile.sRGB:
				case ColorProfile.sRGBAlpha:
					if (usingLinearSpace)
					{
						return (Format)78;
					}
					return (Format)77;
				default:
					return null;
				}
			case Renderite.Shared.TextureFormat.BC4:
				return (Format)80;
			case Renderite.Shared.TextureFormat.BC5:
				return (Format)83;
			case Renderite.Shared.TextureFormat.BC6H:
				return (Format)95;
			case Renderite.Shared.TextureFormat.BC7:
				switch (profile)
				{
				case ColorProfile.Linear:
					return (Format)98;
				case ColorProfile.sRGB:
				case ColorProfile.sRGBAlpha:
					if (usingLinearSpace)
					{
						return (Format)99;
					}
					return (Format)98;
				default:
					return null;
				}
			case Renderite.Shared.TextureFormat.BGR565:
				return (Format)85;
			case Renderite.Shared.TextureFormat.RGB24:
			case Renderite.Shared.TextureFormat.ARGB32:
			case Renderite.Shared.TextureFormat.BGRA32:
			case Renderite.Shared.TextureFormat.RGB565:
				return null;
			default:
				return null;
			}
		}
	}
	public interface IDisplayTextureSource
	{
		Texture UnityTexture { get; }

		void RegisterRequest(Action onTextureChanged);

		void UnregisterRequest(Action onTextureChanged);
	}
	public class RenderTextureAsset : Asset
	{
		public RenderTexture Texture { get; private set; }

		public void Handle(SetRenderTextureFormat format)
		{
			base.AssetIntegrator.EnqueueProcessing(ApplyUpdate, format, highPriority: false);
		}

		public void Handle(UnloadRenderTexture unload)
		{
			base.AssetIntegrator.EnqueueProcessing(Destroy, highPriority: false);
			RenderingManager.Instance.RenderTextures.RemoveAsset(this);
			PackerMemoryPool.Instance.Return(unload);
		}

		private void ApplyUpdate(object untypedFormat)
		{
			SetRenderTextureFormat setRenderTextureFormat = (SetRenderTextureFormat)untypedFormat;
			Destroy();
			int width = Mathf.Clamp(setRenderTextureFormat.size.x, 4, 8192);
			int height = Mathf.Clamp(setRenderTextureFormat.size.y, 4, 8192);
			int depth = Mathf.Max(setRenderTextureFormat.depth, 0);
			Texture = new RenderTexture(width, height, depth, RenderTextureFormat.ARGBHalf);
			Texture.Create();
			if (setRenderTextureFormat.filterMode == TextureFilterMode.Anisotropic)
			{
				Texture.filterMode = FilterMode.Trilinear;
				Texture.anisoLevel = setRenderTextureFormat.anisoLevel;
			}
			else
			{
				Texture.filterMode = setRenderTextureFormat.filterMode.ToUnity();
				Texture.anisoLevel = 0;
			}
			Texture.wrapModeU = setRenderTextureFormat.wrapU.ToUnity();
			Texture.wrapModeV = setRenderTextureFormat.wrapV.ToUnity();
			RenderTextureResult renderTextureResult = new RenderTextureResult();
			renderTextureResult.assetId = base.AssetId;
			renderTextureResult.instanceChanged = true;
			RenderingManager.Instance.SendAssetUpdate(renderTextureResult);
			PackerMemoryPool.Instance.Return(setRenderTextureFormat);
		}

		private void Destroy()
		{
			if (!(Texture == null))
			{
				UnityEngine.Object.Destroy(Texture);
			}
		}
	}
	public class Texture2DAsset : TextureAssetBase
	{
		private Renderite.Shared.TextureWrapMode _wrapU;

		private Renderite.Shared.TextureWrapMode _wrapV;

		public Texture2D Texture { get; private set; }

		protected override Texture UnityTexture => Texture;

		public void SetFormat(SetTexture2DFormat format)
		{
			TextureFormatData textureFormatData = new TextureFormatData();
			textureFormatData.type = TextureType.Texture2D;
			textureFormatData.width = format.width;
			textureFormatData.height = format.height;
			textureFormatData.depth = 1;
			textureFormatData.mips = format.mipmapCount;
			textureFormatData.format = format.format;
			textureFormatData.profile = format.profile;
			PackerMemoryPool.Instance.Return(format);
			SetTextureFormat(textureFormatData);
		}

		public void SetProperties(SetTexture2DProperties properties)
		{
			MarkTexturePropertiesDirty();
			_filterMode = properties.filterMode;
			_anisoLevel = properties.anisoLevel;
			_wrapU = properties.wrapU;
			_wrapV = properties.wrapV;
			_mipmapBias = properties.mipmapBias;
			if (properties.applyImmediatelly)
			{
				base.AssetIntegrator.EnqueueProcessing(base.UpdateTextureProperties, properties.highPriority);
			}
			PackerMemoryPool.Instance.Return(properties);
		}

		public void SetData(SetTexture2DData data)
		{
			TextureUploadData textureUploadData = new TextureUploadData();
			textureUploadData.type = TextureType.Texture2D;
			textureUploadData.data = RenderingManager.Instance.SharedMemory.AccessSlice(data.data);
			textureUploadData.startMip = data.startMipLevel;
			textureUploadData.hint2D = data.hint;
			textureUploadData.mipMapSizes = data.mipMapSizes;
			textureUploadData.flipY = data.flipY;
			textureUploadData.mipStarts = new List<List<int>>();
			textureUploadData.mipStarts.Add(data.mipStarts);
			SetTextureData(textureUploadData, data.highPriority);
		}

		protected override void DoAssignTextureProperties()
		{
			if (RenderingManager.IsDebug)
			{
				UnityEngine.Debug.Log($"Assigning Texture Properties for: {base.AssetId}, Texture: {Texture}");
			}
			if (_filterMode == TextureFilterMode.Anisotropic)
			{
				Texture.filterMode = FilterMode.Trilinear;
				Texture.anisoLevel = _anisoLevel;
			}
			else
			{
				Texture.filterMode = _filterMode.ToUnity();
				Texture.anisoLevel = 0;
			}
			Texture.wrapModeU = _wrapU.ToUnity();
			Texture.wrapModeV = _wrapV.ToUnity();
			Texture.mipMapBias = _mipmapBias;
		}

		protected override void DoGenerateUnityTextureFromDX11(TextureFormatData format)
		{
			if (_dx11Resource == null)
			{
				throw new InvalidOperationException($"DX11 resource is null on texture {base.AssetId}");
			}
			if (((DisposeBase)_dx11Resource).IsDisposed)
			{
				throw new InvalidOperationException($"DX11 resource was disposed on texture {base.AssetId}");
			}
			if (((CppObject)_dx11Resource).NativePointer == IntPtr.Zero)
			{
				throw new InvalidOperationException($"DX11 resource native pointer is zero on texture {base.AssetId}");
			}
			Texture = Texture2D.CreateExternalTexture(format.width, format.height, format.format.ToUnity(), format.mips > 1, linear: false, ((CppObject)_dx11Resource).NativePointer);
			if (Texture == null)
			{
				UnityEngine.Debug.LogWarning("Failed to create Unity texture from native.\n" + $"Size: {format.width} x {format.height}, Format: {format.format}, Mips: {format.mips}, Pointer: {((CppObject)_dx11Resource).NativePointer}");
			}
		}

		protected override void DoUploadTextureDataUnity(TextureUploadData data)
		{
			if (RenderingManager.IsDebug)
			{
				UnityEngine.Debug.Log($"Uploading Texture Data for: {base.AssetId}. Texture: {Texture}");
			}
			int num = Texture.width;
			int num2 = Texture.height;
			int num3 = 0;
			RenderVector2i renderVector2i = _format.BlockSize();
			for (int i = 0; i < data.startMip; i++)
			{
				int num4 = MathHelper.AlignSize(num, renderVector2i.x);
				int num5 = MathHelper.AlignSize(num2, renderVector2i.y);
				num3 += num4 * num5;
				num = Math.Max(1, num >> 1);
				num2 = Math.Max(1, num2 >> 1);
			}
			num3 = MathHelper.PixelsToBytes(num3, _format);
			NativeArray<byte> rawTextureData = Texture.GetRawTextureData<byte>();
			Span<byte> rawData = data.data.RawData;
			if (RenderingManager.IsDebug)
			{
				UnityEngine.Debug.Log($"Texture: {base.AssetId} ({num}x{num2}), Mips: {Texture.mipmapCount}, Offset: {num3}. Format: {_format}. RawData.Length: {rawData.Length}. TexData.Length: {rawTextureData.Length}");
			}
			for (int j = 0; j < rawData.Length; j++)
			{
				rawTextureData[j + num3] = rawData[j];
			}
			if (data.startMip == 0)
			{
				Texture.Apply(updateMipmaps: false, !data.hint2D.readable);
			}
		}

		protected override void DoSetTextureFormatUnity(TextureFormatData format, ref bool instanceChanged)
		{
			if (RenderingManager.IsDebug)
			{
				UnityEngine.Debug.Log($"Setting texture format for: {base.AssetId}, Texture: {Texture}");
			}
			UnityEngine.TextureFormat textureFormat = format.format.ToUnity();
			if (Texture == null || Texture.width != format.width || Texture.height != format.height || Texture.format != textureFormat || Texture.mipmapCount > 1 != format.mips > 1)
			{
				Destroy();
				Texture = new Texture2D(format.width, format.height, textureFormat, format.mips > 1);
				instanceChanged = true;
			}
		}

		protected override void DoDestroy()
		{
			if (RenderingManager.IsDebug)
			{
				UnityEngine.Debug.Log($"Destroying Texture: {base.AssetId}, Texture: {Texture}");
			}
			if (Texture != null)
			{
				UnityEngine.Object.Destroy(Texture);
				Texture = null;
			}
		}

		protected override void SendResult(TextureUpdateResultType type, bool instanceChanged)
		{
			SetTexture2DResult setTexture2DResult = new SetTexture2DResult();
			setTexture2DResult.assetId = base.AssetId;
			setTexture2DResult.type = type;
			setTexture2DResult.instanceChanged = instanceChanged;
			RenderingManager.Instance.SendAssetUpdate(setTexture2DResult);
		}

		protected override void RemoveFromManager()
		{
			RenderingManager.Instance.Texture2Ds.RemoveAsset(this);
		}
	}
	public class Texture3DAsset : TextureAssetBase
	{
		private Renderite.Shared.TextureWrapMode _wrapU;

		private Renderite.Shared.TextureWrapMode _wrapV;

		private Renderite.Shared.TextureWrapMode _wrapW;

		public Texture3D Texture { get; private set; }

		protected override Texture UnityTexture => Texture;

		public void SetFormat(SetTexture3DFormat format)
		{
			TextureFormatData textureFormatData = new TextureFormatData();
			textureFormatData.type = TextureType.Texture3D;
			textureFormatData.width = format.width;
			textureFormatData.height = format.height;
			textureFormatData.depth = format.depth;
			textureFormatData.mips = format.mipmapCount;
			textureFormatData.format = format.format;
			textureFormatData.profile = format.profile;
			SetTextureFormat(textureFormatData);
		}

		public void SetProperties(SetTexture3DProperties properties)
		{
			MarkTexturePropertiesDirty();
			_filterMode = properties.filterMode;
			_wrapU = properties.wrapU;
			_wrapV = properties.wrapV;
			_wrapW = properties.wrapW;
			if (properties.applyImmediatelly)
			{
				base.AssetIntegrator.EnqueueProcessing(base.UpdateTextureProperties, properties.highPriority);
			}
		}

		public void SetData(SetTexture3DData data)
		{
			TextureUploadData textureUploadData = new TextureUploadData();
			textureUploadData.type = TextureType.Texture3D;
			textureUploadData.data = RenderingManager.Instance.SharedMemory.AccessSlice(data.data);
			textureUploadData.startMip = 0;
			textureUploadData.hint3D = data.hint;
			SetTextureData(textureUploadData, data.highPriority);
		}

		protected override void DoAssignTextureProperties()
		{
			if (_filterMode == TextureFilterMode.Anisotropic)
			{
				Texture.filterMode = FilterMode.Trilinear;
				Texture.anisoLevel = _anisoLevel;
			}
			else
			{
				Texture.filterMode = _filterMode.ToUnity();
				Texture.anisoLevel = 0;
			}
			Texture.mipMapBias = _mipmapBias;
			Texture.wrapModeU = _wrapU.ToUnity();
			Texture.wrapModeV = _wrapV.ToUnity();
			Texture.wrapModeW = _wrapW.ToUnity();
		}

		protected override void DoGenerateUnityTextureFromDX11(TextureFormatData format)
		{
			throw new NotSupportedException();
		}

		protected unsafe override void DoUploadTextureDataUnity(TextureUploadData data)
		{
			Span<byte> rawData = data.data.RawData;
			fixed (byte* ptr = rawData)
			{
				void* dataPointer = ptr;
				NativeArray<byte> data2 = ((!RenderingManager.IsDebug) ? NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(dataPointer, rawData.Length, Allocator.None) : new NativeArray<byte>(rawData.ToArray(), Allocator.Persistent));
				Texture.SetPixelData(data2, data.startMip);
				data2.Dispose();
			}
			if (data.startMip == 0)
			{
				Texture.Apply(updateMipmaps: false, !data.hint3D.readable);
			}
		}

		protected override void DoDestroy()
		{
			if (Texture != null)
			{
				UnityEngine.Object.Destroy(Texture);
				Texture = null;
			}
		}

		protected override void DoSetTextureFormatUnity(TextureFormatData format, ref bool instanceChanged)
		{
			ColorProfile profile = format.profile;
			GraphicsFormat graphicsFormat = format.format.ToUnityExperimental(ref profile);
			if (profile != format.profile)
			{
				_targetProfile = profile;
			}
			if (Texture == null || Texture.width != format.width || Texture.height != format.height || Texture.depth != format.depth || Texture.graphicsFormat != graphicsFormat || Texture.mipmapCount > 1 != format.mips > 1 || profile != _targetProfile)
			{
				Destroy();
				_targetProfile = profile;
				Texture = new Texture3D(format.width, format.height, format.depth, graphicsFormat, TextureCreationFlags.None);
				instanceChanged = true;
			}
		}

		protected override void SendResult(TextureUpdateResultType type, bool instanceChanged)
		{
			SetTexture3DResult setTexture3DResult = new SetTexture3DResult();
			setTexture3DResult.assetId = base.AssetId;
			setTexture3DResult.type = type;
			setTexture3DResult.instanceChanged = instanceChanged;
			RenderingManager.Instance.SendAssetUpdate(setTexture3DResult);
		}

		protected override void RemoveFromManager()
		{
			RenderingManager.Instance.Texture3Ds.RemoveAsset(this);
		}
	}
	public abstract class TextureAssetBase : Asset
	{
		public const int TIMESLICE_RESOLUTION = 65536;

		protected Texture2D _dx11Tex;

		protected ShaderResourceView _dx11Resource;

		protected ColorProfile? _targetProfile;

		private int _totalMips;

		protected TextureFilterMode _filterMode;

		protected int _anisoLevel;

		protected Renderite.Shared.TextureFormat _format;

		protected float _mipmapBias;

		private int _lastLoadedMip;

		private bool _texturePropertiesDirty;

		private bool _destroyed;

		protected abstract Texture UnityTexture { get; }

		protected void CheckDestroyed()
		{
			if (_destroyed)
			{
				throw new InvalidOperationException($"Texture asset {base.AssetId} is destroyed.");
			}
		}

		protected void MarkTexturePropertiesDirty()
		{
			_texturePropertiesDirty = true;
		}

		protected abstract void SendResult(TextureUpdateResultType result, bool instanceChanged);

		protected void SetTextureFormat(TextureFormatData format)
		{
			CheckDestroyed();
			_format = format.format;
			_targetProfile = format.profile;
			if (format.type == TextureType.Texture3D)
			{
				SetFormatUnity();
			}
			else if (base.AssetIntegrator.GraphicsDeviceType == GraphicsDeviceType.Direct3D11 && !RenderingManager.IsDebug)
			{
				base.AssetIntegrator.EnqueueRenderThreadProcessing(SetTextureFormatDX11Native(format));
			}
			else
			{
				SetFormatUnity();
			}
			void SetFormatUnity()
			{
				base.AssetIntegrator.EnqueueProcessing(delegate
				{
					SetTextureFormatUnity(format);
				}, highPriority: true);
			}
		}

		protected void SetTextureData(TextureUploadData data, bool highPriority)
		{
			//IL_0061: Unknown result type (might be due to invalid IL or missing references)
			CheckDestroyed();
			if (this is Texture3DAsset)
			{
				EnqueueUnityUpload();
			}
			else if (base.AssetIntegrator.GraphicsDeviceType == GraphicsDeviceType.Direct3D11 && !Renderite.Unity.AssetIntegrator.IsDebugBuild)
			{
				_format.ToDX11(_targetProfile.Value, base.AssetIntegrator.IsUsingLinearSpace);
				base.AssetIntegrator.EnqueueRenderThreadProcessing(UploadTextureDataDX11Native(data));
			}
			else
			{
				EnqueueUnityUpload();
			}
			void EnqueueUnityUpload()
			{
				base.AssetIntegrator.EnqueueProcessing(delegate
				{
					UploadTextureDataUnity(data);
				}, highPriority);
			}
		}

		public void Unload()
		{
			base.AssetIntegrator.EnqueueProcessing(Destroy, highPriority: true);
			RemoveFromManager();
		}

		protected abstract void DoDestroy();

		protected abstract void RemoveFromManager();

		protected void Destroy()
		{
			DoDestroy();
			if (_dx11Resource != null)
			{
				ShaderResourceView resource = _dx11Resource;
				Texture2D tex = _dx11Tex;
				base.AssetIntegrator.EnqueueDelayedRemoval(delegate
				{
					base.AssetIntegrator.EnqueueRenderThreadProcessing(DestroyDX11(resource, tex));
				});
				_dx11Tex = null;
				_dx11Resource = null;
			}
			_targetProfile = null;
		}

		private IEnumerator DestroyDX11(ShaderResourceView resource, Texture2D tex)
		{
			if (resource != null)
			{
				((DisposeBase)resource).Dispose();
			}
			if (tex != null)
			{
				((DisposeBase)tex).Dispose();
			}
			yield break;
		}

		protected abstract void DoAssignTextureProperties();

		private void AssignTextureProperties()
		{
			CheckDestroyed();
			if (_texturePropertiesDirty)
			{
				_texturePropertiesDirty = false;
				DoAssignTextureProperties();
			}
		}

		protected void UpdateTextureProperties()
		{
			CheckDestroyed();
			AssignTextureProperties();
			SendResult(TextureUpdateResultType.PropertiesSet, instanceChanged: false);
		}

		protected abstract void DoSetTextureFormatUnity(TextureFormatData format, ref bool instanceChanged);

		private void SetTextureFormatUnity(TextureFormatData format)
		{
			CheckDestroyed();
			bool instanceChanged = false;
			DoSetTextureFormatUnity(format, ref instanceChanged);
			AssignTextureProperties();
			SendResult(TextureUpdateResultType.FormatSet, instanceChanged);
		}

		protected abstract void DoUploadTextureDataUnity(TextureUploadData data);

		private void UploadTextureDataUnity(TextureUploadData data)
		{
			CheckDestroyed();
			DoUploadTextureDataUnity(data);
			SendResult(TextureUpdateResultType.DataUpload, instanceChanged: false);
		}

		protected abstract void DoGenerateUnityTextureFromDX11(TextureFormatData format);

		private void GenerateUnityTextureFromDX11(TextureFormatData format)
		{
			CheckDestroyed();
			DoGenerateUnityTextureFromDX11(format);
			AssignTextureProperties();
			CompleteFormatUpdate(format, instanceChanged: true);
		}

		private IEnumerator SetTextureFormatDX11Native(TextureFormatData format)
		{
			CheckDestroyed();
			Format val = format.format.ToDX11(format.profile, base.AssetIntegrator.IsUsingLinearSpace);
			Texture2D dx11Tex = _dx11Tex;
			Texture2DDescription val2 = (Texture2DDescription)((dx11Tex != null) ? dx11Tex.Description : default(Texture2DDescription));
			bool flag = false;
			if (_dx11Tex == null || val2.Width != format.width || val2.Height != format.height || val2.ArraySize != format.ArraySize || val2.Format != val || val2.MipLevels != format.mips)
			{
				if (_dx11Tex != null)
				{
					Texture oldUnityTex = UnityTexture;
					Texture2D oldDX11tex = _dx11Tex;
					ShaderResourceView oldDX11res = _dx11Resource;
					format.oldCleanup = delegate
					{
						UnityEngine.Object.Destroy(oldUnityTex);
						base.AssetIntegrator.EnqueueDelayedRemoval(delegate
						{
							ShaderResourceView obj = oldDX11res;
							if (obj != null)
							{
								((DisposeBase)obj).Dispose();
							}
							Texture2D obj2 = oldDX11tex;
							if (obj2 != null)
							{
								((DisposeBase)obj2).Dispose();
							}
						});
					};
				}
				val2.Width = format.width;
				val2.Height = format.height;
				val2.MipLevels = format.mips;
				val2.ArraySize = format.ArraySize;
				val2.Format = val;
				val2.SampleDescription.Count = 1;
				val2.Usage = (ResourceUsage)0;
				val2.BindFlags = (BindFlags)8;
				val2.CpuAccessFlags = (CpuAccessFlags)0;
				val2.OptionFlags = (ResourceOptionFlags)((format.type == TextureType.Texture2D) ? 128 : 132);
				ShaderResourceViewDescription val3 = new ShaderResourceViewDescription
				{
					Format = val2.Format,
					Dimension = (ShaderResourceViewDimension)((format.type == TextureType.Texture2D) ? 4 : 9)
				};
				switch (format.type)
				{
				case TextureType.Texture2D:
					val3.Texture2D.MipLevels = format.mips;
					val3.Texture2D.MostDetailedMip = 0;
					break;
				case TextureType.Cubemap:
					val3.TextureCube.MipLevels = format.mips;
					val3.TextureCube.MostDetailedMip = 0;
					break;
				}
				try
				{
					_dx11Tex = new Texture2D(Renderite.Unity.AssetIntegrator._dx11device, val2);
					_dx11Resource = new ShaderResourceView(Renderite.Unity.AssetIntegrator._dx11device, (Resource)(object)_dx11Tex, val3);
					_totalMips = format.mips;
				}
				catch (Exception ex)
				{
					UnityEngine.Debug.LogError($"Exception creating texture: Width: {val2.Width}, Height: {val2.Height}, Mips: {val2.MipLevels}, format: {val}.");
					throw ex;
				}
				_lastLoadedMip = format.mips;
				flag = true;
			}
			if (flag)
			{
				base.AssetIntegrator.EnqueueProcessing(delegate
				{
					GenerateUnityTextureFromDX11(format);
				}, highPriority: true);
			}
			else if (_texturePropertiesDirty)
			{
				base.AssetIntegrator.EnqueueProcessing(delegate
				{
					AssignTextureProperties();
					CompleteFormatUpdate(format);
				}, highPriority: true);
			}
			else
			{
				CompleteFormatUpdate(format);
			}
			yield break;
		}

		private void CompleteFormatUpdate(TextureFormatData format, bool instanceChanged = false)
		{
			CheckDestroyed();
			format.oldCleanup?.Invoke();
			SendResult(TextureUpdateResultType.FormatSet, format.oldCleanup != null || instanceChanged);
		}

		private IEnumerator UploadTextureDataDX11Native(TextureUploadData data)
		{
			CheckDestroyed();
			int elements = data.type switch
			{
				TextureType.Texture2D => 1, 
				TextureType.Cubemap => 6, 
				TextureType.Texture3D => throw new NotSupportedException("Texture3D upload via DX11 isn't currently supported"), 
				_ => throw new ArgumentException("Invalid texture type"), 
			};
			TextureUploadHint hint2D = data.hint2D;
			RenderVector2i faceSize = data.FaceSize;
			Renderite.Shared.TextureFormat format = _format;
			int totalMipMaps = _totalMips;
			int width = hint2D.region?.width ?? faceSize.x;
			int height = hint2D.region?.height ?? faceSize.y;
			int startX = hint2D.region?.x ?? 0;
			int startY = hint2D.region?.y ?? 0;
			RenderVector2i blockSize = format.BlockSize();
			double bitsPerPixel = format.GetBitsPerPixel();
			if (width > 0 && height > 0)
			{
				for (int mip = 0; mip < data.MipMapCount; mip++)
				{
					for (int face = 0; face < elements; face++)
					{
						RenderVector2i levelSize = data.MipMapSize(mip);
						int targetMip = data.startMip + mip;
						width = Math.Min(width, levelSize.x - startX);
						height = Math.Min(height, levelSize.y - startY);
						int mipWidth = MathHelper.AlignSize(levelSize.x, blockSize.x);
						int mipHeight = MathHelper.AlignSize(levelSize.y, blockSize.y);
						width = MathHelper.AlignSize(width, blockSize.x);
						height = MathHelper.AlignSize(height, blockSize.y);
						int rowGranularity = 65536 / width;
						rowGranularity -= rowGranularity % 4;
						rowGranularity = Math.Max(4, rowGranularity);
						int row = 0;
						int rowPitch = (int)(MathHelper.BitsToBytes((double)mipWidth * bitsPerPixel) * (double)blockSize.y);
						while (row < height)
						{
							if (row > 0)
							{
								yield return null;
								CheckDestroyed();
							}
							ResourceRegion? val = new ResourceRegion(startX, startY + row, 0, startX + width, Math.Min(startY + row + rowGranularity, startY + height), 1);
							if (val.Value.Left == 0 && val.Value.Top == 0 && val.Value.Right == mipWidth && val.Value.Bottom == mipHeight)
							{
								val = null;
							}
							int num = startY + row;
							if (data.type == TextureType.Texture2D)
							{
								num = levelSize.y - num - 1;
							}
							int index = (int)MathHelper.BitsToBytes((double)data.PixelStart(startX, num, mip, face) * bitsPerPixel);
							Span<byte> rawData = data.data.RawData;
							Renderite.Unity.AssetIntegrator._dx11device.ImmediateContext.UpdateSubresource<byte>(ref rawData[index], (Resource)(object)_dx11Tex, targetMip + face * totalMipMaps, rowPitch, 0, val);
							row += rowGranularity;
							RenderingManager.Instance.Stats.TextureSliceUpdated();
						}
					}
					width >>= 1;
					height >>= 1;
					startX >>= 1;
					startY >>= 1;
					width = Math.Max(width, 1);
					height = Math.Max(height, 1);
				}
				_lastLoadedMip = Math.Min(_lastLoadedMip, data.startMip);
				((DeviceChild)_dx11Tex).Device.ImmediateContext.SetMinimumLod((Resource)(object)_dx11Tex, (float)_lastLoadedMip);
			}
			RenderingManager.Instance.Stats.TextureUpdated();
			SendResult(TextureUpdateResultType.DataUpload, instanceChanged: false);
		}
	}
	public class CubemapAsset : TextureAssetBase
	{
		public Cubemap Texture { get; private set; }

		protected override Texture UnityTexture => Texture;

		public void SetFormat(SetCubemapFormat format)
		{
			TextureFormatData textureFormatData = new TextureFormatData();
			textureFormatData.type = TextureType.Cubemap;
			textureFormatData.width = format.size;
			textureFormatData.height = format.size;
			textureFormatData.depth = 1;
			textureFormatData.mips = format.mipmapCount;
			textureFormatData.format = format.format;
			textureFormatData.profile = format.profile;
			SetTextureFormat(textureFormatData);
			PackerMemoryPool.Instance.Return(format);
		}

		public void SetProperties(SetCubemapProperties properties)
		{
			MarkTexturePropertiesDirty();
			_filterMode = properties.filterMode;
			_anisoLevel = properties.anisoLevel;
			_mipmapBias = properties.mipmapBias;
			if (properties.applyImmediatelly)
			{
				base.AssetIntegrator.EnqueueProcessing(base.UpdateTextureProperties, properties.highPriority);
			}
			PackerMemoryPool.Instance.Return(properties);
		}

		public void SetData(SetCubemapData data)
		{
			TextureUploadData textureUploadData = new TextureUploadData();
			textureUploadData.type = TextureType.Cubemap;
			textureUploadData.data = RenderingManager.Instance.SharedMemory.AccessSlice(data.data);
			textureUploadData.startMip = data.startMipLevel;
			textureUploadData.mipMapSizes = data.mipMapSizes;
			textureUploadData.mipStarts = data.mipStarts;
			textureUploadData.flipY = data.flipY;
			SetTextureData(textureUploadData, data.highPriority);
		}

		protected override void DoAssignTextureProperties()
		{
			if (_filterMode == TextureFilterMode.Anisotropic)
			{
				Texture.filterMode = FilterMode.Trilinear;
				Texture.anisoLevel = _anisoLevel;
			}
			else
			{
				Texture.filterMode = _filterMode.ToUnity();
				Texture.anisoLevel = 0;
			}
			Texture.mipMapBias = _mipmapBias;
		}

		protected override void DoGenerateUnityTextureFromDX11(TextureFormatData format)
		{
			Texture = Cubemap.CreateExternalTexture(format.width, format.format.ToUnity(), format.mips > 1, ((CppObject)_dx11Resource).NativePointer);
			if (Texture == null)
			{
				UnityEngine.Debug.LogWarning("Failed to create Unity texture from native.\n" + $"Size: {format.width} x {format.height}, Format: {format.format}, Mips: {format.mips}, Pointer: {((CppObject)_dx11Resource).NativePointer}");
			}
		}

		protected override void DoDestroy()
		{
			if (Texture != null)
			{
				UnityEngine.Object.Destroy(Texture);
				Texture = null;
			}
		}

		protected override void DoSetTextureFormatUnity(TextureFormatData format, ref bool instanceChanged)
		{
			if (RenderingManager.IsDebug)
			{
				UnityEngine.Debug.Log($"Setting cubemap format for: {base.AssetId}, Texture: {Texture}");
			}
			UnityEngine.TextureFormat textureFormat = format.format.ToUnity();
			if (Texture == null || Texture.width != format.width || Texture.format != textureFormat || Texture.mipmapCount > 1 != format.mips > 1)
			{
				Destroy();
				Texture = new Cubemap(format.width, textureFormat, format.mips > 1);
				instanceChanged = true;
			}
		}

		protected override void DoUploadTextureDataUnity(TextureUploadData data)
		{
			if (RenderingManager.IsDebug)
			{
				UnityEngine.Debug.Log($"Uploading Cubemap Data for: {base.AssetId}. Texture: {Texture}");
			}
			Span<byte> rawData = data.data.RawData;
			RenderVector2i blockSize = _format.BlockSize();
			for (int i = 0; i < 6; i++)
			{
				List<int> list = data.mipStarts[i];
				for (int j = 0; j < data.MipMapCount; j++)
				{
					RenderVector2i renderVector2i = MathHelper.AlignSize(data.MipMapSize(j), blockSize);
					int pixels = renderVector2i.x * renderVector2i.y;
					int num = MathHelper.PixelsToBytes(list[j], _format);
					int num2 = MathHelper.PixelsToBytes(pixels, _format);
					Span<byte> span = rawData.Slice(num, num2);
					if (RenderingManager.IsDebug)
					{
						UnityEngine.Debug.Log($"Uploading Cubemap {base.AssetId}, Face: {i}, Mip: {j}, Format: {_format}, Start: {num}, Length: {num2}, Size: {renderVector2i}");
					}
					Texture.SetPixelData(span.ToArray(), data.startMip + j, (CubemapFace)i);
				}
			}
			if (data.startMip == 0)
			{
				Texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
			}
		}

		protected override void SendResult(TextureUpdateResultType type, bool instanceChanged)
		{
			SetCubemapResult setCubemapResult = new SetCubemapResult();
			setCubemapResult.assetId = base.AssetId;
			setCubemapResult.type = type;
			setCubemapResult.instanceChanged = instanceChanged;
			RenderingManager.Instance.SendAssetUpdate(setCubemapResult);
		}

		protected override void RemoveFromManager()
		{
			RenderingManager.Instance.Cubemaps.RemoveAsset(this);
		}
	}
	public class TextureFormatData
	{
		public TextureType type;

		public int width;

		public int height;

		public int depth;

		public int mips;

		public Renderite.Shared.TextureFormat format;

		public ColorProfile profile;

		public Action oldCleanup;

		public int ArraySize => type switch
		{
			TextureType.Texture2D => 1, 
			TextureType.Cubemap => 6, 
			TextureType.Texture3D => depth, 
			_ => throw new Exception("Invalid texture type: " + type), 
		};
	}
	public static class TextureHelper
	{
		public static Texture GetTexture(int packedId)
		{
			if (packedId == -1)
			{
				return null;
			}
			IdPacker<TextureAssetType>.Unpack(packedId, out var id, out var type);
			return type switch
			{
				TextureAssetType.Texture2D => RenderingManager.Instance.Texture2Ds.GetAsset(id).Texture, 
				TextureAssetType.Texture3D => RenderingManager.Instance.Texture3Ds.GetAsset(id).Texture, 
				TextureAssetType.Cubemap => RenderingManager.Instance.Cubemaps.GetAsset(id).Texture, 
				TextureAssetType.RenderTexture => RenderingManager.Instance.RenderTextures.GetAsset(id).Texture, 
				TextureAssetType.VideoTexture => RenderingManager.Instance.VideoTextures.GetAsset(id).Texture, 
				TextureAssetType.Desktop => RenderingManager.Instance.DesktopTextures.GetAsset(id).Texture, 
				_ => throw new NotImplementedException($"Unsupported texture type: {type}"), 
			};
		}
	}
	public class TextureUploadData
	{
		public TextureType type;

		public SharedMemoryViewSlice<byte> data;

		public int startMip;

		public TextureUploadHint hint2D;

		public Texture3DUploadHint hint3D;

		public List<RenderVector2i> mipMapSizes;

		public List<List<int>> mipStarts;

		public bool flipY;

		public int MipMapCount => mipMapSizes.Count;

		public RenderVector2i FaceSize => mipMapSizes[0];

		public RenderVector2i MipMapSize(int mip)
		{
			return mipMapSizes[mip];
		}

		public int PixelStart(int x, int y, int mip, int face)
		{
			int num = mipStarts[face][mip];
			RenderVector2i renderVector2i = mipMapSizes[mip];
			if (flipY)
			{
				y = renderVector2i.y - y - 1;
			}
			return num + (x + y * renderVector2i.x);
		}
	}
	public interface IVideoPlaybackInstance
	{
		bool IsLoaded { get; }

		double Length { get; }

		Vector2Int Size { get; }

		bool HasAlpha { get; }

		Texture Texture { get; }

		IEnumerator Setup(VideoTextureAsset asset, string dataSource, int audioSystemSampleRate);

		List<VideoAudioTrack> GetTracks();

		void HandleUpdate(VideoTextureUpdate update);

		void HandleProperties(VideoTextureProperties properties);

		void StartAudio(VideoTextureStartAudioTrack audioTrack);
	}
	public class VideoPlaybackEngine
	{
		public string Name { get; private set; }

		public Func<GameObject, IVideoPlaybackInstance> Instantiate { get; private set; }

		public int InitializationAttempts { get; private set; }

		public VideoPlaybackEngine(string name, Func<GameObject, IVideoPlaybackInstance> instantiate, int initializationAttempts)
		{
			Name = name;
			Instantiate = instantiate;
			InitializationAttempts = initializationAttempts;
		}
	}
	public abstract class VideoPlaybackManager : MonoBehaviour
	{
		private List<VideoPlaybackEngine> _playbackEngines = new List<VideoPlaybackEngine>();

		public IReadOnlyList<VideoPlaybackEngine> AvailablePlaybackEngines => _playbackEngines;

		public VideoPlaybackEngine FindPlaybackEngine(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return null;
			}
			return _playbackEngines.FirstOrDefault((VideoPlaybackEngine e) => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
		}

		protected void RegisterPlaybackEngine(VideoPlaybackEngine engine)
		{
			_playbackEngines.Add(engine);
		}
	}
	public class VideoTextureAsset : Asset
	{
		private static DateTime _lastLoad;

		private IVideoPlaybackInstance _playbackInstance;

		private CancellationTokenSource _cancellationToken;

		private VideoTextureProperties _stagedProperties;

		private bool _unloaded;

		public Texture Texture => _playbackInstance?.Texture;

		private VideoPlaybackManager Manager => RenderingManager.Instance.VideoPlaybackManager;

		public void TextureChanged()
		{
			VideoTextureChanged videoTextureChanged = new VideoTextureChanged();
			videoTextureChanged.assetId = base.AssetId;
			RenderingManager.Instance.SendAssetUpdate(videoTextureChanged);
		}

		public void Handle(VideoTextureLoad load)
		{
			if (_unloaded)
			{
				throw new InvalidOperationException("Cannot load already unloaded video texture asset");
			}
			if (_cancellationToken != null)
			{
				throw new InvalidOperationException("This instance is already trying to load a video texture");
			}
			_cancellationToken = new CancellationTokenSource();
			base.AssetIntegrator.EnqueueTask(delegate
			{
				Manager.StartCoroutine(Load(load));
			});
		}

		public void Handle(VideoTextureUpdate update)
		{
			_playbackInstance?.HandleUpdate(update);
		}

		public void Handle(VideoTextureProperties properties)
		{
			if (_playbackInstance != null)
			{
				_playbackInstance.HandleProperties(properties);
			}
			else
			{
				_stagedProperties = properties;
			}
		}

		public void Handle(VideoTextureStartAudioTrack audioTrack)
		{
			if (_playbackInstance == null)
			{
				throw new InvalidOperationException("Audio can be started only after playback instance is initialized");
			}
			_playbackInstance.StartAudio(audioTrack);
		}

		public void Unload()
		{
			_unloaded = true;
			RenderingManager.Instance.VideoTextures.RemoveAsset(this);
			_cancellationToken?.Cancel();
			if (_playbackInstance != null)
			{
				UnityEngine.Object.Destroy((UnityEngine.Object)_playbackInstance);
			}
			_playbackInstance = null;
		}

		private IEnumerator Load(VideoTextureLoad load)
		{
			while ((DateTime.UtcNow - _lastLoad).TotalSeconds < 4.0)
			{
				yield return new WaitForEndOfFrame();
			}
			if (_cancellationToken.IsCancellationRequested)
			{
				yield break;
			}
			_lastLoad = DateTime.UtcNow;
			List<VideoPlaybackEngine> list = new List<VideoPlaybackEngine>();
			VideoPlaybackEngine videoPlaybackEngine = Manager.FindPlaybackEngine(load.overrideEngine);
			if (videoPlaybackEngine != null)
			{
				list.Add(videoPlaybackEngine);
			}
			else
			{
				list.AddRange(Manager.AvailablePlaybackEngines);
			}
			foreach (VideoPlaybackEngine engine in list)
			{
				int attemptsLeft = engine.InitializationAttempts;
				for (int attempt = 0; attempt < attemptsLeft; attempt++)
				{
					if (_cancellationToken.IsCancellationRequested)
					{
						yield break;
					}
					IVideoPlaybackInstance instance = engine.Instantiate(Manager.gameObject);
					yield return instance.Setup(this, load.source, load.audioSystemSampleRate);
					if (instance.IsLoaded && !_cancellationToken.IsCancellationRequested)
					{
						_playbackInstance = instance;
						if (_stagedProperties != null)
						{
							_playbackInstance.HandleProperties(_stagedProperties);
							_stagedProperties = null;
						}
						SendOnLoaded(engine.Name);
						yield break;
					}
					UnityEngine.Object.Destroy((UnityEngine.Object)instance);
				}
			}
		}

		private void SendOnLoaded(string playbackEngine)
		{
			VideoTextureReady videoTextureReady = new VideoTextureReady();
			videoTextureReady.assetId = base.AssetId;
			videoTextureReady.length = _playbackInstance.Length;
			videoTextureReady.size = _playbackInstance.Size.ToRender();
			videoTextureReady.hasAlpha = _playbackInstance.HasAlpha;
			videoTextureReady.audioTracks = _playbackInstance.GetTracks();
			videoTextureReady.playbackEngine = playbackEngine;
			videoTextureReady.instanceChanged = true;
			RenderingManager.Instance.SendAssetUpdate(videoTextureReady);
		}
	}
	public class FrameResultsManager
	{
		private List<ReflectionProbeChangeRenderResult> _finishedProbes = new List<ReflectionProbeChangeRenderResult>();

		private List<VideoTextureClockErrorState> _videoTextureClockErrors = new List<VideoTextureClockErrorState>();

		public void ProbeFinishedRendering(ReflectionProbeRenderable probe, int uniqueId)
		{
			_finishedProbes.Add(new ReflectionProbeChangeRenderResult
			{
				renderSpaceId = probe.Space.Id,
				renderProbeUniqueId = uniqueId,
				requireReset = probe.MarkedForReset
			});
		}

		public void UpdateVideoClockError(int assetId, float currentClockError)
		{
			_videoTextureClockErrors.Add(new VideoTextureClockErrorState
			{
				assetId = assetId,
				currentClockError = currentClockError
			});
		}

		public void CollectResults(FrameStartData data)
		{
			data.renderedReflectionProbes?.Clear();
			data.videoClockErrors?.Clear();
			if (_finishedProbes.Count > 0)
			{
				if (data.renderedReflectionProbes == null)
				{
					data.renderedReflectionProbes = new List<ReflectionProbeChangeRenderResult>();
				}
				data.renderedReflectionProbes.AddRange(_finishedProbes);
				_finishedProbes.Clear();
			}
			if (_videoTextureClockErrors.Count > 0)
			{
				if (data.videoClockErrors == null)
				{
					data.videoClockErrors = new List<VideoTextureClockErrorState>();
				}
				data.videoClockErrors.AddRange(_videoTextureClockErrors);
				_videoTextureClockErrors.Clear();
			}
		}
	}
	public static class Helper
	{
		public static Vector2 ToUnity(this RenderVector2 vector)
		{
			return new Vector2(vector.x, vector.y);
		}

		public static Vector3 ToUnity(this RenderVector3 vector)
		{
			return new Vector3(vector.x, vector.y, vector.z);
		}

		public static Vector4 ToUnity(this RenderVector4 vector)
		{
			return new Vector4(vector.x, vector.y, vector.z, vector.w);
		}

		public static Quaternion ToUnity(this RenderQuaternion quaternion)
		{
			return new Quaternion(quaternion.x, quaternion.y, quaternion.z, quaternion.w);
		}

		public static RenderVector2 ToRender(this Vector2 vector)
		{
			return new RenderVector2(vector.x, vector.y);
		}

		public static RenderVector3 ToRender(this Vector3 vector)
		{
			return new RenderVector3(vector.x, vector.y, vector.z);
		}

		public static RenderVector4 ToRender(this Vector4 vector)
		{
			return new RenderVector4(vector.x, vector.y, vector.z, vector.w);
		}

		public static RenderQuaternion ToRender(this Quaternion quaternion)
		{
			return new RenderQuaternion(quaternion.x, quaternion.y, quaternion.z, quaternion.w);
		}

		public static RenderVector2i ToRender(this Vector2Int vector)
		{
			return new RenderVector2i(vector.x, vector.y);
		}

		public static RenderVector3i ToRender(this Vector3Int vector)
		{
			return new RenderVector3i(vector.x, vector.y, vector.z);
		}

		public static Bounds ToUnity(this RenderBoundingBox bounds)
		{
			return new Bounds(bounds.center.ToUnity(), bounds.extents.ToUnity() * 2f);
		}

		public static RenderBoundingBox ToRender(this Bounds bounds)
		{
			return new RenderBoundingBox(bounds.center.ToRender(), bounds.extents.ToRender());
		}

		public static Matrix4x4 ToUnity(this RenderMatrix4x4 matrix)
		{
			return new Matrix4x4(new Vector4(matrix.m00, matrix.m10, matrix.m20, matrix.m30), new Vector4(matrix.m01, matrix.m11, matrix.m21, matrix.m31), new Vector4(matrix.m02, matrix.m12, matrix.m22, matrix.m32), new Vector4(matrix.m03, matrix.m13, matrix.m23, matrix.m33));
		}

		public static Rect ToUnity(this RenderRect rect)
		{
			return new Rect(rect.x, rect.y, rect.width, rect.height);
		}

		public static SphericalHarmonicsL2 ToUnity(this RenderSH2 sh)
		{
			SphericalHarmonicsL2 unity = default(SphericalHarmonicsL2);
			Assign(sh.sh0, 0, 0.2820948f);
			Assign(sh.sh1, 1, 0.48860252f);
			Assign(sh.sh2, 2, 0.48860252f);
			Assign(sh.sh3, 3, 0.48860252f);
			Assign(sh.sh4, 4, 1.0925485f);
			Assign(sh.sh5, 5, 1.0925485f);
			Assign(sh.sh7, 7, 1.0925485f);
			Assign(sh.sh6, 6, 0.31539157f);
			Assign(sh.sh8, 8, 0.54627424f);
			return unity;
			void Assign(RenderVector3 v, int index, float scale)
			{
				for (int i = 0; i < 3; i++)
				{
					unity[i, index] = v[i] * scale;
				}
			}
		}

		public static Vector2Int ToUnity(this RenderVector2i vector)
		{
			return new Vector2Int(vector.x, vector.y);
		}

		public static Vector3Int ToUnity(this RenderVector3i vector)
		{
			return new Vector3Int(vector.x, vector.y, vector.z);
		}

		public static VertexAttribute ToUnity(this VertexAttributeType attribute)
		{
			return attribute switch
			{
				VertexAttributeType.Position => VertexAttribute.Position, 
				VertexAttributeType.Normal => VertexAttribute.Normal, 
				VertexAttributeType.Tangent => VertexAttribute.Tangent, 
				VertexAttributeType.Color => VertexAttribute.Color, 
				VertexAttributeType.UV0 => VertexAttribute.TexCoord0, 
				VertexAttributeType.UV1 => VertexAttribute.TexCoord1, 
				VertexAttributeType.UV2 => VertexAttribute.TexCoord2, 
				VertexAttributeType.UV3 => VertexAttribute.TexCoord3, 
				VertexAttributeType.UV4 => VertexAttribute.TexCoord4, 
				VertexAttributeType.UV5 => VertexAttribute.TexCoord5, 
				VertexAttributeType.UV6 => VertexAttribute.TexCoord6, 
				VertexAttributeType.UV7 => VertexAttribute.TexCoord7, 
				VertexAttributeType.BoneWeights => VertexAttribute.BlendWeight, 
				VertexAttributeType.BoneIndicies => VertexAttribute.BlendIndices, 
				_ => throw new ArgumentOutOfRangeException("Invalid VertexAttributeType mode: " + attribute), 
			};
		}

		public static UnityEngine.Rendering.VertexAttributeFormat ToUnity(this Renderite.Shared.VertexAttributeFormat format)
		{
			return format switch
			{
				Renderite.Shared.VertexAttributeFormat.Float32 => UnityEngine.Rendering.VertexAttributeFormat.Float32, 
				Renderite.Shared.VertexAttributeFormat.Half16 => UnityEngine.Rendering.VertexAttributeFormat.Float16, 
				Renderite.Shared.VertexAttributeFormat.UNorm8 => UnityEngine.Rendering.VertexAttributeFormat.UNorm8, 
				Renderite.Shared.VertexAttributeFormat.UNorm16 => UnityEngine.Rendering.VertexAttributeFormat.UNorm16, 
				Renderite.Shared.VertexAttributeFormat.SInt8 => UnityEngine.Rendering.VertexAttributeFormat.SInt8, 
				Renderite.Shared.VertexAttributeFormat.SInt16 => UnityEngine.Rendering.VertexAttributeFormat.SInt16, 
				Renderite.Shared.VertexAttributeFormat.SInt32 => UnityEngine.Rendering.VertexAttributeFormat.SInt32, 
				Renderite.Shared.VertexAttributeFormat.UInt8 => UnityEngine.Rendering.VertexAttributeFormat.UInt8, 
				Renderite.Shared.VertexAttributeFormat.UInt16 => UnityEngine.Rendering.VertexAttributeFormat.UInt16, 
				Renderite.Shared.VertexAttributeFormat.UInt32 => UnityEngine.Rendering.VertexAttributeFormat.UInt32, 
				_ => throw new ArgumentOutOfRangeException("Invalid VertexAttributeFormat mode: " + format), 
			};
		}

		public static IndexFormat ToUnity(this IndexBufferFormat format)
		{
			return format switch
			{
				IndexBufferFormat.UInt16 => IndexFormat.UInt16, 
				IndexBufferFormat.UInt32 => IndexFormat.UInt32, 
				_ => throw new ArgumentOutOfRangeException("Invalid IndexBufferFormat mode: " + format), 
			};
		}

		public static MeshTopology ToUnity(this SubmeshTopology format)
		{
			return format switch
			{
				SubmeshTopology.Points => MeshTopology.Points, 
				SubmeshTopology.Triangles => MeshTopology.Triangles, 
				_ => throw new ArgumentOutOfRangeException("Invalid SubmeshTopology mode: " + format), 
			};
		}

		public static UnityEngine.TextureFormat ToUnity(this Renderite.Shared.TextureFormat format, bool throwOnError = true)
		{
			switch (format)
			{
			case Renderite.Shared.TextureFormat.Alpha8:
				return UnityEngine.TextureFormat.Alpha8;
			case Renderite.Shared.TextureFormat.R8:
				return UnityEngine.TextureFormat.R8;
			case Renderite.Shared.TextureFormat.RGB565:
			case Renderite.Shared.TextureFormat.BGR565:
				return UnityEngine.TextureFormat.RGB565;
			case Renderite.Shared.TextureFormat.ARGB32:
				return UnityEngine.TextureFormat.ARGB32;
			case Renderite.Shared.TextureFormat.RGB24:
				return UnityEngine.TextureFormat.RGB24;
			case Renderite.Shared.TextureFormat.RGBA32:
				return UnityEngine.TextureFormat.RGBA32;
			case Renderite.Shared.TextureFormat.BGRA32:
				return UnityEngine.TextureFormat.BGRA32;
			case Renderite.Shared.TextureFormat.RGBAHalf:
				return UnityEngine.TextureFormat.RGBAHalf;
			case Renderite.Shared.TextureFormat.RGBAFloat:
				return UnityEngine.TextureFormat.RGBAFloat;
			case Renderite.Shared.TextureFormat.RHalf:
				return UnityEngine.TextureFormat.RHalf;
			case Renderite.Shared.TextureFormat.RFloat:
				return UnityEngine.TextureFormat.RFloat;
			case Renderite.Shared.TextureFormat.RGHalf:
				return UnityEngine.TextureFormat.RGHalf;
			case Renderite.Shared.TextureFormat.RGFloat:
				return UnityEngine.TextureFormat.RGFloat;
			case Renderite.Shared.TextureFormat.BC1:
				return UnityEngine.TextureFormat.DXT1;
			case Renderite.Shared.TextureFormat.BC3:
				return UnityEngine.TextureFormat.DXT5;
			case Renderite.Shared.TextureFormat.BC4:
				return UnityEngine.TextureFormat.BC4;
			case Renderite.Shared.TextureFormat.BC5:
				return UnityEngine.TextureFormat.BC5;
			case Renderite.Shared.TextureFormat.BC6H:
				return UnityEngine.TextureFormat.BC6H;
			case Renderite.Shared.TextureFormat.BC7:
				return UnityEngine.TextureFormat.BC7;
			case Renderite.Shared.TextureFormat.ETC2_RGB:
				return UnityEngine.TextureFormat.ETC2_RGB;
			case Renderite.Shared.TextureFormat.ETC2_RGBA1:
				return UnityEngine.TextureFormat.ETC2_RGBA1;
			case Renderite.Shared.TextureFormat.ETC2_RGBA8:
				return UnityEngine.TextureFormat.ETC2_RGBA8;
			case Renderite.Shared.TextureFormat.ASTC_4x4:
				return UnityEngine.TextureFormat.ASTC_4x4;
			case Renderite.Shared.TextureFormat.ASTC_5x5:
				return UnityEngine.TextureFormat.ASTC_5x5;
			case Renderite.Shared.TextureFormat.ASTC_6x6:
				return UnityEngine.TextureFormat.ASTC_6x6;
			case Renderite.Shared.TextureFormat.ASTC_8x8:
				return UnityEngine.TextureFormat.ASTC_8x8;
			case Renderite.Shared.TextureFormat.ASTC_10x10:
				return UnityEngine.TextureFormat.ASTC_10x10;
			case Renderite.Shared.TextureFormat.ASTC_12x12:
				return UnityEngine.TextureFormat.ASTC_12x12;
			default:
				if (throwOnError)
				{
					throw new Exception($"Invalid texture format {format}");
				}
				return (UnityEngine.TextureFormat)(-1);
			}
		}

		public static GraphicsFormat ToUnityExperimental(this Renderite.Shared.TextureFormat format, ref ColorProfile profile)
		{
			switch (format)
			{
			case Renderite.Shared.TextureFormat.Alpha8:
				return (GraphicsFormat)54;
			case Renderite.Shared.TextureFormat.R8:
				if (profile == ColorProfile.Linear || !SystemInfo.IsFormatSupported(GraphicsFormat.R8_SRGB, FormatUsage.Sample))
				{
					profile = ColorProfile.Linear;
					return GraphicsFormat.R8_UNorm;
				}
				if (profile == ColorProfile.sRGB || profile == ColorProfile.sRGBAlpha)
				{
					return GraphicsFormat.R8_SRGB;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.RGB24:
				if (profile == ColorProfile.Linear || !SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8_SRGB, FormatUsage.Sample))
				{
					profile = ColorProfile.Linear;
					return GraphicsFormat.R8G8B8_UNorm;
				}
				if (profile == ColorProfile.sRGB || profile == ColorProfile.sRGBAlpha)
				{
					return GraphicsFormat.R8G8B8_SRGB;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.BC1:
				if (profile == ColorProfile.Linear)
				{
					return GraphicsFormat.RGB_DXT1_UNorm;
				}
				if (profile == ColorProfile.sRGB || profile == ColorProfile.sRGBAlpha)
				{
					return GraphicsFormat.RGB_DXT1_SRGB;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.BC2:
				if (profile == ColorProfile.Linear)
				{
					return GraphicsFormat.RGBA_DXT3_UNorm;
				}
				if (profile == ColorProfile.sRGB || profile == ColorProfile.sRGBAlpha)
				{
					return GraphicsFormat.RGBA_DXT3_SRGB;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.BC3:
				if (profile == ColorProfile.Linear)
				{
					return GraphicsFormat.RGBA_DXT5_UNorm;
				}
				if (profile == ColorProfile.sRGB || profile == ColorProfile.sRGBAlpha)
				{
					return GraphicsFormat.RGBA_DXT5_SRGB;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.BC4:
				if (profile == ColorProfile.Linear)
				{
					return GraphicsFormat.R_BC4_UNorm;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.BC5:
				if (profile == ColorProfile.Linear)
				{
					return GraphicsFormat.RG_BC5_UNorm;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.BC6H:
				if (profile == ColorProfile.Linear)
				{
					return GraphicsFormat.RGB_BC6H_SFloat;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.BC7:
				if (profile == ColorProfile.Linear)
				{
					return GraphicsFormat.RGBA_BC7_UNorm;
				}
				if (profile == ColorProfile.sRGB || profile == ColorProfile.sRGBAlpha)
				{
					return GraphicsFormat.RGBA_BC7_SRGB;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.RGB565:
				if (profile == ColorProfile.Linear)
				{
					return GraphicsFormat.R5G6B5_UNormPack16;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.RGBA32:
				if (profile == ColorProfile.Linear || !SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8A8_SRGB, FormatUsage.Sample))
				{
					profile = ColorProfile.Linear;
					return GraphicsFormat.R8G8B8A8_UNorm;
				}
				if (profile == ColorProfile.sRGB || profile == ColorProfile.sRGBAlpha)
				{
					return GraphicsFormat.R8G8B8A8_SRGB;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.BGRA32:
				if (profile == ColorProfile.Linear || !SystemInfo.IsFormatSupported(GraphicsFormat.B8G8R8A8_SRGB, FormatUsage.Sample))
				{
					profile = ColorProfile.Linear;
					return GraphicsFormat.B8G8R8A8_UNorm;
				}
				if (profile == ColorProfile.sRGB || profile == ColorProfile.sRGBAlpha)
				{
					return GraphicsFormat.B8G8R8A8_SRGB;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.RHalf:
				if (profile == ColorProfile.Linear)
				{
					return GraphicsFormat.R16_SFloat;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.RFloat:
				if (profile == ColorProfile.Linear)
				{
					return GraphicsFormat.R32_SFloat;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.RGHalf:
				if (profile == ColorProfile.Linear)
				{
					return GraphicsFormat.R16G16_SFloat;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.RGFloat:
				if (profile == ColorProfile.Linear)
				{
					return GraphicsFormat.R32G32_SFloat;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.RGBAHalf:
				if (profile == ColorProfile.Linear)
				{
					return GraphicsFormat.R16G16B16A16_SFloat;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			case Renderite.Shared.TextureFormat.RGBAFloat:
				if (profile == ColorProfile.Linear)
				{
					return GraphicsFormat.R32G32B32A32_SFloat;
				}
				throw new NotImplementedException($"Invalid profile for {format}: {profile}");
			default:
				throw new NotImplementedException("Invalid texture format: " + format);
			}
		}

		public static Renderite.Shared.TextureFormat ToEngine(this UnityEngine.TextureFormat format)
		{
			return format switch
			{
				UnityEngine.TextureFormat.Alpha8 => Renderite.Shared.TextureFormat.Alpha8, 
				UnityEngine.TextureFormat.ARGB32 => Renderite.Shared.TextureFormat.ARGB32, 
				UnityEngine.TextureFormat.RGB24 => Renderite.Shared.TextureFormat.RGB24, 
				UnityEngine.TextureFormat.RGBA32 => Renderite.Shared.TextureFormat.RGBA32, 
				UnityEngine.TextureFormat.RGBAHalf => Renderite.Shared.TextureFormat.RGBAHalf, 
				UnityEngine.TextureFormat.RGFloat => Renderite.Shared.TextureFormat.RGFloat, 
				_ => Renderite.Shared.TextureFormat.Unknown, 
			};
		}

		public static Renderite.Shared.TextureFormat ToEngine(this GraphicsFormat format)
		{
			switch (format)
			{
			case GraphicsFormat.R8G8B8_SRGB:
			case GraphicsFormat.R8G8B8_UNorm:
			case GraphicsFormat.R8G8B8_SNorm:
			case GraphicsFormat.R8G8B8_UInt:
			case GraphicsFormat.R8G8B8_SInt:
				return Renderite.Shared.TextureFormat.RGB24;
			case GraphicsFormat.R8G8B8A8_SRGB:
			case GraphicsFormat.R8G8B8A8_UNorm:
			case GraphicsFormat.R8G8B8A8_SNorm:
			case GraphicsFormat.R8G8B8A8_UInt:
			case GraphicsFormat.R8G8B8A8_SInt:
				return Renderite.Shared.TextureFormat.RGBA32;
			case GraphicsFormat.R16G16B16A16_SFloat:
				return Renderite.Shared.TextureFormat.RGBAHalf;
			case GraphicsFormat.R32G32B32A32_SFloat:
				return Renderite.Shared.TextureFormat.RGBAFloat;
			default:
				throw new NotSupportedException("Unsupposted Unity GraphicsFormat: " + format);
			}
		}

		public static ColorProfile ToEngineProfile(this GraphicsFormat format)
		{
			switch (format)
			{
			case GraphicsFormat.R8G8B8_UNorm:
			case GraphicsFormat.R8G8B8A8_UNorm:
			case GraphicsFormat.R8G8B8_SNorm:
			case GraphicsFormat.R8G8B8A8_SNorm:
			case GraphicsFormat.R8G8B8_UInt:
			case GraphicsFormat.R8G8B8A8_UInt:
			case GraphicsFormat.R8G8B8_SInt:
			case GraphicsFormat.R8G8B8A8_SInt:
			case GraphicsFormat.R16G16B16A16_SFloat:
			case GraphicsFormat.R32G32B32A32_SFloat:
				return ColorProfile.Linear;
			case GraphicsFormat.R8_SRGB:
			case GraphicsFormat.R8G8_SRGB:
			case GraphicsFormat.R8G8B8_SRGB:
			case GraphicsFormat.R8G8B8A8_SRGB:
				return ColorProfile.sRGB;
			default:
				throw new NotSupportedException("Unsupposted Unity GraphicsFormat: " + format);
			}
		}

		public static UnityEngine.TextureWrapMode ToUnity(this Renderite.Shared.TextureWrapMode wrap)
		{
			return wrap switch
			{
				Renderite.Shared.TextureWrapMode.Clamp => UnityEngine.TextureWrapMode.Clamp, 
				Renderite.Shared.TextureWrapMode.Mirror => UnityEngine.TextureWrapMode.Mirror, 
				Renderite.Shared.TextureWrapMode.MirrorOnce => UnityEngine.TextureWrapMode.MirrorOnce, 
				Renderite.Shared.TextureWrapMode.Repeat => UnityEngine.TextureWrapMode.Repeat, 
				_ => UnityEngine.TextureWrapMode.Repeat, 
			};
		}

		public static FilterMode ToUnity(this TextureFilterMode filterMode)
		{
			return filterMode switch
			{
				TextureFilterMode.Point => FilterMode.Point, 
				TextureFilterMode.Bilinear => FilterMode.Bilinear, 
				TextureFilterMode.Trilinear => FilterMode.Trilinear, 
				_ => throw new Exception("Invalid filter mode: " + filterMode), 
			};
		}

		public static UnityEngine.LightType ToUnity(this Renderite.Shared.LightType lightType)
		{
			return lightType switch
			{
				Renderite.Shared.LightType.Point => UnityEngine.LightType.Point, 
				Renderite.Shared.LightType.Spot => UnityEngine.LightType.Spot, 
				Renderite.Shared.LightType.Directional => UnityEngine.LightType.Directional, 
				_ => throw new ArgumentOutOfRangeException("Invalid LightType: " + lightType), 
			};
		}

		public static LightShadows ToUnity(this ShadowType shadowType)
		{
			return shadowType switch
			{
				ShadowType.None => LightShadows.None, 
				ShadowType.Soft => LightShadows.Soft, 
				ShadowType.Hard => LightShadows.Hard, 
				_ => throw new ArgumentOutOfRangeException("Invalid ShadowType: " + shadowType), 
			};
		}

		public static ShadowCastingMode ToUnity(this ShadowCastMode mode)
		{
			return mode switch
			{
				ShadowCastMode.Off => ShadowCastingMode.Off, 
				ShadowCastMode.On => ShadowCastingMode.On, 
				ShadowCastMode.ShadowOnly => ShadowCastingMode.ShadowsOnly, 
				ShadowCastMode.DoubleSided => ShadowCastingMode.TwoSided, 
				_ => throw new Exception("Invalid shadow cast mode"), 
			};
		}

		public static ShadowCastMode ToEngine(this ShadowCastingMode mode)
		{
			return mode switch
			{
				ShadowCastingMode.Off => ShadowCastMode.Off, 
				ShadowCastingMode.On => ShadowCastMode.On, 
				ShadowCastingMode.ShadowsOnly => ShadowCastMode.ShadowOnly, 
				ShadowCastingMode.TwoSided => ShadowCastMode.DoubleSided, 
				_ => throw new Exception("Invalid shadow cast mode"), 
			};
		}

		public static MotionVectorGenerationMode ToUnity(this MotionVectorMode mode)
		{
			return mode switch
			{
				MotionVectorMode.Camera => MotionVectorGenerationMode.Camera, 
				MotionVectorMode.NoMotion => MotionVectorGenerationMode.ForceNoMotion, 
				MotionVectorMode.Object => MotionVectorGenerationMode.Object, 
				_ => throw new Exception("Invalid MotionVectorMode: " + mode), 
			};
		}

		public static CameraClearFlags ToUnity(this CameraClearMode mode)
		{
			return mode switch
			{
				CameraClearMode.Depth => CameraClearFlags.Depth, 
				CameraClearMode.Nothing => CameraClearFlags.Nothing, 
				CameraClearMode.Skybox => CameraClearFlags.Skybox, 
				CameraClearMode.Color => CameraClearFlags.Color, 
				_ => throw new Exception("Invalid camera clear mode: " + mode), 
			};
		}

		public static UnityEngine.Rendering.ReflectionProbeTimeSlicingMode ToUnity(this Renderite.Shared.ReflectionProbeTimeSlicingMode mode)
		{
			return mode switch
			{
				Renderite.Shared.ReflectionProbeTimeSlicingMode.AllFacesAtOnce => UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.AllFacesAtOnce, 
				Renderite.Shared.ReflectionProbeTimeSlicingMode.IndividualFaces => UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.IndividualFaces, 
				_ => UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.NoTimeSlicing, 
			};
		}

		public static ReflectionProbeClearFlags ToUnity(this ReflectionProbeClear mode)
		{
			return mode switch
			{
				ReflectionProbeClear.Skybox => ReflectionProbeClearFlags.Skybox, 
				ReflectionProbeClear.Color => ReflectionProbeClearFlags.SolidColor, 
				_ => ReflectionProbeClearFlags.SolidColor, 
			};
		}

		public static ParticleSystemTrailTextureMode ToUnity(this TrailTextureMode textureMode)
		{
			return (ParticleSystemTrailTextureMode)(textureMode switch
			{
				TrailTextureMode.Stretch => 0, 
				TrailTextureMode.Tile => 1, 
				TrailTextureMode.DistributePerSegment => 2, 
				TrailTextureMode.RepeatPerSegment => 3, 
				_ => throw new ArgumentOutOfRangeException("Invalid texture mode: " + textureMode), 
			});
		}

		public static ShadowResolution ToUnity(this ShadowResolutionMode resolution)
		{
			return resolution switch
			{
				ShadowResolutionMode.Low => ShadowResolution.Low, 
				ShadowResolutionMode.Medium => ShadowResolution.Medium, 
				ShadowResolutionMode.High => ShadowResolution.High, 
				ShadowResolutionMode.Ultra => ShadowResolution.VeryHigh, 
				_ => throw new ArgumentOutOfRangeException("Invalid shadow resolution mode: " + resolution), 
			};
		}

		public static int ToUnity(this ShadowCascadeMode shadowCascades)
		{
			return shadowCascades switch
			{
				ShadowCascadeMode.None => 1, 
				ShadowCascadeMode.TwoCascades => 2, 
				ShadowCascadeMode.FourCascades => 4, 
				_ => throw new ArgumentOutOfRangeException("Invalid shadow cascades: " + shadowCascades), 
			};
		}

		public static SkinWeights ToUnity(this SkinWeightMode mode)
		{
			return mode switch
			{
				SkinWeightMode.OneBone => SkinWeights.OneBone, 
				SkinWeightMode.TwoBones => SkinWeights.TwoBones, 
				SkinWeightMode.FourBones => SkinWeights.FourBones, 
				SkinWeightMode.Unlimited => SkinWeights.Unlimited, 
				_ => throw new ArgumentOutOfRangeException("Invalid skin weight mode: " + mode), 
			};
		}

		public static string ToMillisecondTimeString(this DateTime datetime)
		{
			return datetime.ToLongTimeString() + "." + datetime.Millisecond.ToString("D3");
		}

		public static string ToDebugString(this SphericalHarmonicsL2 sh)
		{
			StringBuilder stringBuilder = new StringBuilder();
			for (int i = 0; i < 9; i++)
			{
				stringBuilder.AppendLine($"SH{i} - [{sh[0, i]}; {sh[1, i]}; {sh[2, i]}]");
			}
			return stringBuilder.ToString();
		}
	}
	public abstract class EngineInitProgress : MonoBehaviour
	{
		public abstract void InitStarted();

		public abstract void ApplySplashScreenOverride(RendererSplashScreenOverride splashScreen);

		public abstract void UpdateProgress(RendererInitProgressUpdate update);

		public abstract void InitCompleted();
	}
	public abstract class DisplayInput : MonoBehaviour
	{
		public abstract IDisplayTextureSource TryGetDisplayTexture(int index);

		public void UpdateState(InputState state)
		{
			if (state.displays == null)
			{
				state.displays = new List<DisplayState>();
			}
			UpdateState(state.displays);
		}

		protected abstract void UpdateState(List<DisplayState> states);
	}
	public class WindowIconTools
	{
		private class IconCache
		{
			private IntPtr bitmap = IntPtr.Zero;

			private IntPtr icon = IntPtr.Zero;

			public unsafe IntPtr Update(Span<byte> bgra, int width, int height, bool topRowFirst = false)
			{
				IntPtr intPtr = CreateBitmap(width, height, 1u, 32u, IntPtr.Zero);
				IntPtr dC = GetDC(IntPtr.Zero);
				BITMAPINFOHEADER lpbmi = default(BITMAPINFOHEADER);
				lpbmi.Init();
				lpbmi.biWidth = width;
				lpbmi.biHeight = (topRowFirst ? (-height) : height);
				lpbmi.biPlanes = 1;
				lpbmi.biBitCount = 32;
				lpbmi.biCompression = BitmapCompressionMode.BI_RGB;
				fixed (byte* ptr = bgra)
				{
					void* value = ptr;
					SetDIBits(dC, intPtr, 0u, (uint)height, new IntPtr(value), ref lpbmi, 0u);
				}
				ReleaseDC(IntPtr.Zero, dC);
				ICONINFO piconinfo = new ICONINFO
				{
					IsIcon = true,
					ColorBitmap = intPtr,
					MaskBitmap = intPtr
				};
				IntPtr intPtr2 = CreateIconIndirect(ref piconinfo);
				if (intPtr2 == IntPtr.Zero)
				{
					DeleteObject(intPtr);
					return IntPtr.Zero;
				}
				if (bitmap != IntPtr.Zero)
				{
					DeleteObject(bitmap);
				}
				bitmap = intPtr;
				if (icon != IntPtr.Zero)
				{
					DestroyIcon(icon);
				}
				icon = intPtr2;
				return intPtr2;
			}
		}

		private struct BITMAPINFOHEADER
		{
			public uint biSize;

			public int biWidth;

			public int biHeight;

			public ushort biPlanes;

			public ushort biBitCount;

			public BitmapCompressionMode biCompression;

			public uint biSizeImage;

			public int biXPelsPerMeter;

			public int biYPelsPerMeter;

			public uint biClrUsed;

			public uint biClrImportant;

			public void Init()
			{
				biSize = (uint)Marshal.SizeOf(this);
			}
		}

		private enum BitmapCompressionMode : uint
		{
			BI_RGB,
			BI_RLE8,
			BI_RLE4,
			BI_BITFIELDS,
			BI_JPEG,
			BI_PNG
		}

		private struct ICONINFO
		{
			public bool IsIcon;

			public int xHotspot;

			public int yHotspot;

			public IntPtr MaskBitmap;

			public IntPtr ColorBitmap;
		}

		[ComImport]
		[Guid("c43dc798-95d1-4bea-9030-bb99e2983a1a")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface ITaskbarList4
		{
			[PreserveSig]
			void HrInit();

			[PreserveSig]
			void AddTab(IntPtr hwnd);

			[PreserveSig]
			void DeleteTab(IntPtr hwnd);

			[PreserveSig]
			void ActivateTab(IntPtr hwnd);

			[PreserveSig]
			void SetActiveAlt(IntPtr hwnd);

			[PreserveSig]
			void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

			[PreserveSig]
			void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);

			[PreserveSig]
			void SetProgressState(IntPtr hwnd, TaskbarProgressBarState tbpFlags);

			[PreserveSig]
			void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);

			[PreserveSig]
			void UnregisterTab(IntPtr hwndTab);

			[PreserveSig]
			void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);

			[PreserveSig]
			void SetTabActive(IntPtr hwndTab, IntPtr hwndInsertBefore, uint dwReserved);

			[PreserveSig]
			int ThumbBarAddButtons(IntPtr hwnd, uint cButtons, IntPtr pButtons);

			[PreserveSig]
			int ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, IntPtr pButtons);

			[PreserveSig]
			void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);

			[PreserveSig]
			void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);

			[PreserveSig]
			void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);

			[PreserveSig]
			void SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
		}

		[ComImport]
		[Guid("56fdf344-fd6d-11d0-958a-006097c9a090")]
		[ClassInterface(ClassInterfaceType.None)]
		private class CTaskbarList
		{
		}

		private static IntPtr[] _SetIcon_baseIcon = new IntPtr[2];

		private static bool[] _SetIcon_hasBaseIcon = new bool[2];

		private static IconCache[] _SetIcon_cache = new IconCache[2]
		{
			new IconCache(),
			new IconCache()
		};

		private static IconCache _SetOverlayIcon_cache = new IconCache();

		private static object _initLock = new object();

		private static ITaskbarList4 _taskbarList;

		private static bool _taskbarListReady = false;

		private static ITaskbarList4 taskbarList
		{
			get
			{
				if (!_taskbarListReady)
				{
					lock (_initLock)
					{
						if (!_taskbarListReady)
						{
							try
							{
								_taskbarList = (ITaskbarList4)new CTaskbarList();
								_taskbarList.HrInit();
							}
							catch (Exception)
							{
								UnityEngine.Debug.LogError("ITaskbarList4 init failed! Go to Build Settings > Player Settings > Standalone > Other Settings, and set Api Compatibility Level to 4.x");
							}
							_taskbarListReady = true;
						}
					}
				}
				return _taskbarList;
			}
		}

		public static bool SetIcon(Span<byte> bgra, int width, int height, WindowIconKind kind, bool topRowFirst = false)
		{
			IntPtr mainWindowHandle = WindowsNativeHelper.MainWindowHandle;
			if (mainWindowHandle == IntPtr.Zero)
			{
				return false;
			}
			IntPtr intPtr;
			if (!bgra.IsEmpty)
			{
				intPtr = _SetIcon_cache[(int)kind].Update(bgra, width, height, topRowFirst);
				if (intPtr == IntPtr.Zero)
				{
					return false;
				}
				if (!_SetIcon_hasBaseIcon[(int)kind])
				{
					_SetIcon_hasBaseIcon[(int)kind] = true;
					_SetIcon_baseIcon[(int)kind] = SendMessage(mainWindowHandle, 127, (int)kind, IntPtr.Zero);
				}
			}
			else
			{
				if (!_SetIcon_hasBaseIcon[(int)kind])
				{
					return true;
				}
				intPtr = _SetIcon_baseIcon[(int)kind];
			}
			SendMessage(mainWindowHandle, 128, (int)kind, intPtr);
			return true;
		}

		public static bool SetOverlayIcon(Span<byte> bgra, int width, int height, string description = "")
		{
			IntPtr mainWindowHandle = WindowsNativeHelper.MainWindowHandle;
			if (mainWindowHandle == IntPtr.Zero)
			{
				return false;
			}
			if (!bgra.IsEmpty)
			{
				IntPtr intPtr = _SetOverlayIcon_cache.Update(bgra, width, height);
				if (intPtr == IntPtr.Zero)
				{
					return false;
				}
				taskbarList.SetOverlayIcon(mainWindowHandle, intPtr, description);
			}
			else
			{
				taskbarList.SetOverlayIcon(mainWindowHandle, IntPtr.Zero, description);
			}
			return true;
		}

		public static bool SetProgress(TaskbarProgressBarState state, ulong completed, ulong total)
		{
			IntPtr mainWindowHandle = WindowsNativeHelper.MainWindowHandle;
			if (mainWindowHandle == IntPtr.Zero)
			{
				return false;
			}
			ITaskbarList4 taskbarList = WindowIconTools.taskbarList;
			taskbarList.SetProgressState(mainWindowHandle, state);
			taskbarList.SetProgressValue(mainWindowHandle, completed, total);
			return true;
		}

		public static bool SetProgressState(TaskbarProgressBarState state)
		{
			IntPtr mainWindowHandle = WindowsNativeHelper.MainWindowHandle;
			if (mainWindowHandle == IntPtr.Zero)
			{
				return false;
			}
			taskbarList.SetProgressState(mainWindowHandle, state);
			return true;
		}

		public static bool SetProgressValue(ulong completed, ulong total)
		{
			IntPtr mainWindowHandle = WindowsNativeHelper.MainWindowHandle;
			if (mainWindowHandle == IntPtr.Zero)
			{
				return false;
			}
			taskbarList.SetProgressValue(mainWindowHandle, completed, total);
			return true;
		}

		[DllImport("gdi32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool DeleteObject([In] IntPtr hObject);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool DestroyIcon(IntPtr hIcon);

		[DllImport("gdi32.dll")]
		private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, IntPtr lpvBits);

		[DllImport("gdi32.dll")]
		private static extern int SetDIBits(IntPtr hDC, IntPtr hBitmap, uint start, uint clines, IntPtr lpvBits, ref BITMAPINFOHEADER lpbmi, uint colorUse);

		[DllImport("user32.dll")]
		private static extern IntPtr GetDC(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern bool ReleaseDC(IntPtr hWnd, IntPtr hDC);

		[DllImport("user32.dll")]
		private static extern IntPtr CreateIconIndirect([In] ref ICONINFO piconinfo);

		[DllImport("user32.dll", CharSet = CharSet.Auto)]
		public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, IntPtr lParam);

		[DllImport("user32.dll")]
		private static extern IntPtr GetActiveWindow();
	}
	public enum WindowIconKind
	{
		Small,
		Big
	}
	public enum TaskbarProgressBarState
	{
		NoProgress = 0,
		Indeterminate = 1,
		Normal = 2,
		Error = 4,
		Paused = 8
	}
	public enum DibColors
	{
		DIB_RGB_COLORS,
		DIB_PAL_COLORS,
		DIB_PAL_INDICES
	}
	public static class WindowsNativeHelper
	{
		private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

		private const uint GW_OWNER = 4u;

		private static IntPtr _mainWindowHandle = IntPtr.Zero;

		private const int GWL_STYLE = -16;

		private const int WS_CHILD = 1073741824;

		private const int WS_POPUP = int.MinValue;

		public static IntPtr MainWindowHandle
		{
			get
			{
				if (_mainWindowHandle == IntPtr.Zero)
				{
					_mainWindowHandle = GetSelfMainWindowHandle();
				}
				return _mainWindowHandle;
			}
		}

		public static bool ApplicationIsActivated()
		{
			IntPtr foregroundWindow = GetForegroundWindow();
			if (foregroundWindow == IntPtr.Zero)
			{
				return false;
			}
			int id = Process.GetCurrentProcess().Id;
			GetWindowThreadProcessId(foregroundWindow, out var processId);
			return processId == id;
		}

		public unsafe static IntPtr GetSelfMainWindowHandle()
		{
			IntPtr zero = IntPtr.Zero;
			EnumWindows(MainWindowPredicate, (IntPtr)(&zero));
			return zero;
		}

		[MonoPInvokeCallback(typeof(EnumWindowsProc))]
		private unsafe static bool MainWindowPredicate(IntPtr hWnd, IntPtr lParam)
		{
			IntPtr* ptr = (IntPtr*)(void*)lParam;
			int id = Process.GetCurrentProcess().Id;
			GetWindowThreadProcessId(hWnd, out var processId);
			if (processId == id && GetWindow(hWnd, 4u) == IntPtr.Zero && IsWindowVisible(hWnd))
			{
				*ptr = hWnd;
				return false;
			}
			return true;
		}

		public static bool SetWindowTitle(string title)
		{
			IntPtr selfMainWindowHandle = GetSelfMainWindowHandle();
			if (selfMainWindowHandle == IntPtr.Zero)
			{
				return false;
			}
			return SetWindowText(selfMainWindowHandle, title);
		}

		public static bool ParentWindowUnderMain(IntPtr window)
		{
			IntPtr selfMainWindowHandle = GetSelfMainWindowHandle();
			if (selfMainWindowHandle == IntPtr.Zero)
			{
				return false;
			}
			int windowLong = GetWindowLong(window, -16);
			windowLong &= 0x7FFFFFFF;
			windowLong |= 0x40000000;
			SetWindowLong(window, -16, windowLong);
			return SetParent(window, selfMainWindowHandle) != IntPtr.Zero;
		}

		[DllImport("user32.dll")]
		private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

		[DllImport("user32.dll")]
		private static extern bool IsWindowVisible(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

		[DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
		private static extern IntPtr GetForegroundWindow();

		[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		private static extern int GetWindowThreadProcessId(IntPtr handle, out int processId);

		[DllImport("user32.dll")]
		private static extern bool SetWindowText(IntPtr handle, string title);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
	}
	public abstract class InputDriver : MonoBehaviour
	{
		public virtual void Initialize(InputManager manager)
		{
		}

		public abstract void UpdateState(InputState state);
	}
	public interface IOutputDriver
	{
		void HandleOutputState(OutputState state);
	}
	public class InputManager
	{
		private MouseInput mouse;

		private KeyboardInput keyboard;

		private WindowInput window;

		private DisplayInput display;

		private List<InputDriver> drivers;

		private List<IOutputDriver> outputDrivers = new List<IOutputDriver>();

		public InputState State { get; private set; } = new InputState();

		public event Action<bool> OnVR_ActiveChanged;

		public InputManager(MouseInput mouse, KeyboardInput keyboard, WindowInput window, DisplayInput display, List<InputDriver> drivers)
		{
			this.mouse = mouse;
			this.keyboard = keyboard;
			this.window = window;
			this.display = display;
			this.drivers = drivers;
			foreach (InputDriver driver in drivers)
			{
				driver.Initialize(this);
				if (driver is IOutputDriver item)
				{
					outputDrivers.Add(item);
				}
			}
		}

		public void RegisterDriver(InputDriver driver)
		{
			drivers.Add(driver);
			driver.Initialize(this);
			if (driver is IOutputDriver item)
			{
				outputDrivers.Add(item);
			}
		}

		public void UpdateStateDecoupled()
		{
			display.UpdateState(State);
		}

		public void UpdateState()
		{
			mouse.UpdateState(State);
			keyboard.UpdateState(State);
			window.UpdateState(State);
			display.UpdateState(State);
			foreach (InputDriver driver in drivers)
			{
				driver.UpdateState(State);
			}
		}

		public void HandleOutputState(OutputState state)
		{
			mouse.HandleStateUpdate(state);
			keyboard.HandleOutputState(state);
			foreach (IOutputDriver outputDriver in outputDrivers)
			{
				outputDriver.HandleOutputState(state);
			}
		}

		public void VR_ActiveChanged(bool vrActive)
		{
			this.OnVR_ActiveChanged?.Invoke(vrActive);
		}
	}
	public abstract class KeyboardInput : MonoBehaviour
	{
		public void UpdateState(InputState state)
		{
			if (state.keyboard == null)
			{
				state.keyboard = new KeyboardState();
			}
			UpdateState(state.keyboard);
		}

		protected abstract void UpdateState(KeyboardState state);

		public abstract void HandleOutputState(OutputState output);
	}
	public abstract class MouseInput : MonoBehaviour
	{
		public void UpdateState(InputState state)
		{
			if (state.mouse == null)
			{
				state.mouse = new MouseState();
			}
			UpdateState(state.mouse);
		}

		public abstract void HandleStateUpdate(OutputState state);

		protected abstract void UpdateState(MouseState state);
	}
	public abstract class WindowInput : MonoBehaviour
	{
		private bool _resolutionChanged;

		public bool IsFocused { get; private set; }

		public void FlagResolutionChanged()
		{
			_resolutionChanged = true;
		}

		public void UpdateState(InputState state)
		{
			if (state.window == null)
			{
				state.window = new WindowState();
			}
			UpdateState(state.window);
		}

		public virtual void UpdateState(WindowState state)
		{
			state.windowResolution = new RenderVector2i(Screen.width, Screen.height);
			state.isFullscreen = Screen.fullScreen;
			IsFocused = WindowsNativeHelper.ApplicationIsActivated();
			state.isWindowFocused = IsFocused;
			state.resolutionSettingsApplied = _resolutionChanged;
			_resolutionChanged = false;
		}
	}
	[StructLayout(LayoutKind.Explicit, Size = 48)]
	public struct UnityLightData
	{
		[FieldOffset(0)]
		public Vector3 point;

		[FieldOffset(12)]
		public Quaternion orientation;

		[FieldOffset(28)]
		public Vector3 color;

		[FieldOffset(40)]
		public float intensity;

		[FieldOffset(44)]
		public float range;

		[FieldOffset(48)]
		public float angle;
	}
	[StructLayout(LayoutKind.Explicit, Size = 40)]
	public struct UnityRenderTransform
	{
		[FieldOffset(0)]
		public Vector3 position;

		[FieldOffset(12)]
		public Vector3 scale;

		[FieldOffset(24)]
		public Quaternion rotation;

		public override string ToString()
		{
			return $"Position: {position}, Rotation: {rotation}, Scale: {scale}";
		}
	}
	public struct UnitySkinnedMeshBoundsUpdate
	{
		public int renderableIndex;

		public Bounds localBounds;
	}
	[StructLayout(LayoutKind.Explicit, Size = 44)]
	public struct UnityTransformPoseUpdate
	{
		[FieldOffset(0)]
		public int transformId;

		[FieldOffset(4)]
		public UnityRenderTransform pose;
	}
	public class SharedMemoryAccessor
	{
		private Dictionary<int, SharedMemoryView> _views = new Dictionary<int, SharedMemoryView>();

		public string Prefix { get; private set; }

		public SharedMemoryAccessor(string prefix)
		{
			Prefix = prefix;
		}

		public Span<T> AccessData<T>(SharedMemoryBufferDescriptor<T> descriptor) where T : unmanaged
		{
			try
			{
				return MemoryMarshal.Cast<byte, T>(GetMemoryView(descriptor).RawData.Slice(descriptor.offset, descriptor.length));
			}
			catch (ArgumentOutOfRangeException)
			{
				UnityEngine.Debug.LogError("Out of range exception. " + $"Offset: {descriptor.offset}, Length: {descriptor.length}, BufferCapacity: {descriptor.bufferCapacity}, BufferId: {descriptor.bufferId}");
				throw;
			}
		}

		public UnmanagedSpan<T> AccessDataUnmanaged<T>(SharedMemoryBufferDescriptor<T> descriptor) where T : unmanaged
		{
			return GetMemoryView(descriptor).UnmanagedRawData.Slice(descriptor.offset, descriptor.length).As<T>();
		}

		public SharedMemoryViewSlice<T> AccessSlice<T>(SharedMemoryBufferDescriptor<T> descriptor) where T : unmanaged
		{
			return new SharedMemoryViewSlice<T>(GetMemoryView(descriptor), descriptor.offset, descriptor.length);
		}

		private SharedMemoryView GetMemoryView<T>(SharedMemoryBufferDescriptor<T> descriptor) where T : unmanaged
		{
			lock (_views)
			{
				if (!_views.TryGetValue(descriptor.bufferId, out SharedMemoryView value))
				{
					value = new SharedMemoryView(this, descriptor.bufferId, descriptor.bufferCapacity);
					_views.Add(descriptor.bufferId, value);
				}
				return value;
			}
		}

		public void ReleaseView(int bufferId)
		{
			lock (_views)
			{
				if (_views.TryGetValue(bufferId, out SharedMemoryView value))
				{
					value.Dispose();
					_views.Remove(bufferId);
				}
			}
		}
	}
	public class SharedMemoryView : IDisposable
	{
		private MemoryView view;

		private UnmanagedMemoryManager<byte> memory;

		private long capacity;

		public SharedMemoryAccessor Accessor { get; private set; }

		public int BufferId { get; private set; }

		public Span<byte> RawData => view.Data;

		public Memory<byte> Memory => memory.Memory;

		public unsafe UnmanagedSpan<byte> UnmanagedRawData => new UnmanagedSpan<byte>(view.Pointer, (int)capacity);

		public unsafe SharedMemoryView(SharedMemoryAccessor accessor, int bufferId, long capacity)
		{
			//IL_002c: Unknown result type (might be due to invalid IL or missing references)
			//IL_003b: Expected O, but got Unknown
			//IL_0036: Unknown result type (might be due to invalid IL or missing references)
			//IL_0040: Expected O, but got Unknown
			Accessor = accessor;
			BufferId = bufferId;
			this.capacity = capacity;
			string text = Renderite.Shared.Helper.ComposeMemoryViewName(accessor.Prefix, bufferId);
			view = new MemoryView(new MemoryViewOptions(text, capacity, false), (ILoggerFactory)NullLoggerFactory.Instance);
			memory = new UnmanagedMemoryManager<byte>(view.Pointer, (int)capacity);
		}

		public void Dispose()
		{
			view.Dispose();
			view = null;
		}
	}
	public class SharedMemoryViewSlice<T> : BackingMemoryBuffer where T : unmanaged
	{
		private int _sizeBytes;

		public SharedMemoryView SharedView { get; private set; }

		public int OffsetBytes { get; private set; }

		public override int SizeBytes => _sizeBytes;

		public override Span<byte> RawData => SharedView.RawData.Slice(OffsetBytes, SizeBytes);

		public Span<T> Data => MemoryMarshal.Cast<byte, T>(RawData);

		public override Memory<byte> Memory => SharedView.Memory.Slice(OffsetBytes, SizeBytes);

		public SharedMemoryViewSlice(SharedMemoryView view, int offset, int size)
		{
			SharedView = view;
			OffsetBytes = offset;
			_sizeBytes = size;
		}

		protected override void ActuallyDispose()
		{
			SharedView = null;
		}
	}
	[AttributeUsage(AttributeTargets.Method)]
	public class MonoPInvokeCallbackAttribute : Attribute
	{
		public MonoPInvokeCallbackAttribute(Type type)
		{
		}
	}
	public class PerformanceStats
	{
		private Stopwatch _framerateUpdate = new Stopwatch();

		private int _framerateCounter;

		private float _fps;

		public int RenderedFramesSinceLast { get; set; }

		public float FrameBeginToSubmitTime { get; set; }

		public float FrameProcessedToNextBeginTime { get; set; }

		public float IntegrationProcessingTime { get; set; }

		public float ExtraParticleProcessingTime { get; set; }

		public int ProcessedAssetIntegratorTasks { get; set; }

		public int ProcessingHandleWaits { get; set; }

		public int IntegrationHighPriorityTasks { get; set; }

		public int IntegrationTasks { get; set; }

		public int IntegrationRenderTasks { get; set; }

		public int IntegrationParticleTasks { get; set; }

		public float FrameUpdateHandleTime { get; set; }

		public int RenderedCameras { get; private set; }

		public int RenderedCameraPortals { get; private set; }

		public int UpdatedTextures { get; private set; }

		public int TextureSliceUploads { get; private set; }

		public int UploadedParticles { get; private set; }

		public void CameraRendered()
		{
			RenderedCameras++;
		}

		public void CameraPortalRendered()
		{
			RenderedCameraPortals++;
		}

		public void TextureUpdated()
		{
			UpdatedTextures++;
		}

		public void TextureSliceUpdated()
		{
			TextureSliceUploads++;
		}

		public void ParticlesUploaded(int count)
		{
			UploadedParticles += count;
		}

		public void Update()
		{
			if (!_framerateUpdate.IsRunning)
			{
				_framerateUpdate.Restart();
				return;
			}
			_framerateCounter++;
			double totalMilliseconds = _framerateUpdate.Elapsed.TotalMilliseconds;
			if (totalMilliseconds >= 500.0)
			{
				_fps = (float)((double)_framerateCounter / (totalMilliseconds * 0.0010000000474974513));
				_framerateCounter = 0;
				_framerateUpdate.Restart();
			}
		}

		public void UpdateStats(FrameStartData data)
		{
			if (data.performance == null)
			{
				data.performance = new PerformanceState();
			}
			data.performance.renderedFramesSinceLast = RenderedFramesSinceLast;
			data.performance.frameBeginToSubmitTime = FrameBeginToSubmitTime;
			data.performance.frameProcessedToNextBeginTime = FrameProcessedToNextBeginTime;
			data.performance.integrationProcessingTime = IntegrationProcessingTime;
			data.performance.extraParticleProcessingTime = ExtraParticleProcessingTime;
			data.performance.processedAssetIntegratorTasks = ProcessedAssetIntegratorTasks;
			data.performance.integrationHighPriorityTasks = IntegrationHighPriorityTasks;
			data.performance.integrationTasks = IntegrationTasks;
			data.performance.integrationRenderTasks = IntegrationRenderTasks;
			data.performance.integrationParticleTasks = IntegrationParticleTasks;
			data.performance.processingHandleWaits = ProcessingHandleWaits;
			data.performance.frameUpdateHandleTime = FrameUpdateHandleTime;
			UpdateRenderStats(data.performance);
			UpdateFrameRate(data.performance);
		}

		private void UpdateRenderStats(PerformanceState state)
		{
			state.renderedCameras = RenderedCameras;
			state.renderedCameraPortals = RenderedCameraPortals;
			state.updatedTextures = UpdatedTextures;
			state.textureSliceUploads = TextureSliceUploads;
			RenderedCameras = 0;
			RenderedCameraPortals = 0;
			UpdatedTextures = 0;
			TextureSliceUploads = 0;
		}

		private void UpdateFrameRate(PerformanceState state)
		{
			float num = default(float);
			if (XRStats.TryGetGPUTimeLastFrame(ref num))
			{
				state.renderTime = num * 0.001f;
			}
			else
			{
				state.renderTime = -1f;
			}
			state.immediateFPS = 1f / Time.unscaledDeltaTime;
			state.fps = _fps;
		}
	}
	public class BillboardBufferRendererManager : RenderableStateChangeManager<BillboardRenderBufferRenderer, BillboardRenderBufferUpdate, BillboardRenderBufferState, EmptyUpdateData>
	{
		public BillboardBufferRendererManager(RenderSpace space)
			: base(space)
		{
		}

		protected override void ApplyState(ref BillboardRenderBufferState update, BillboardRenderBufferRenderer handler, ref EmptyUpdateData updateData, BillboardRenderBufferUpdate batch)
		{
			handler.ApplyState(ref update);
		}

		protected override int GetRenderableIndex(ref BillboardRenderBufferState state)
		{
			return state.renderableIndex;
		}

		protected override EmptyUpdateData InitUpdateData(BillboardRenderBufferUpdate batch)
		{
			return default(EmptyUpdateData);
		}
	}
	public class BlitToDisplayManager : RenderableStateChangeManager<BlitToDisplayRenderable, BlitToDisplayRenderablesUpdate, BlitToDisplayState, EmptyUpdateData>
	{
		public BlitToDisplayManager(RenderSpace space)
			: base(space)
		{
		}

		protected override void ApplyState(ref BlitToDisplayState update, BlitToDisplayRenderable handler, ref EmptyUpdateData updateData, BlitToDisplayRenderablesUpdate batch)
		{
			TextureDisplayBlitter blitter = handler.Blitter;
			blitter.Texture = TextureHelper.GetTexture(update.textureId);
			blitter.DisplayIndex = update.displayIndex;
			blitter.Color = update.backgroundColor.ToUnity();
			blitter.FlipHorizontally = update.flipHorizontally;
			blitter.FlipVertically = update.flipVertically;
		}

		protected override int GetRenderableIndex(ref BlitToDisplayState state)
		{
			return state.renderableIndex;
		}

		protected override EmptyUpdateData InitUpdateData(BlitToDisplayRenderablesUpdate batch)
		{
			return default(EmptyUpdateData);
		}
	}
	public class CameraManager : RenderableStateChangeManager<CameraRenderable, CameraRenderablesUpdate, CameraState, CameraManager.UpdateState>
	{
		public struct UpdateState
		{
			public int transformIndex;

			public UnmanagedSpan<int> transformIds;

			public int ReadTransformId()
			{
				return transformIds[transformIndex++];
			}
		}

		private static int _layerMask;

		private static int _privateLayerMask;

		public CameraManager(RenderSpace space)
			: base(space)
		{
			if (_layerMask == 0)
			{
				_layerMask = RenderHelper.PUBLIC_RENDER_MASK;
			}
			if (_privateLayerMask == 0)
			{
				_privateLayerMask = RenderHelper.PRIVATE_RENDER_MASK;
			}
		}

		protected override int GetRenderableIndex(ref CameraState state)
		{
			return state.renderableIndex;
		}

		protected override void ApplyState(ref CameraState update, CameraRenderable cameraHandler, ref UpdateState updateData, CameraRenderablesUpdate batch)
		{
			Camera camera = cameraHandler.Camera;
			CameraController helper = cameraHandler.Helper;
			camera.orthographic = update.projection == CameraProjection.Orthographic;
			camera.fieldOfView = update.fieldOfView;
			camera.orthographicSize = update.orthographicSize;
			helper.OrthographicSize = update.orthographicSize;
			helper.UseTransformScale = update.useTransformScale;
			helper.NearClip = update.nearClip;
			helper.FarClip = update.farClip;
			camera.clearFlags = update.clearMode.ToUnity();
			camera.backgroundColor = update.backgroundColor.ToUnity();
			camera.rect = update.viewport.ToUnity();
			camera.depth = update.depth;
			camera.renderingPath = (update.forwardOnly ? RenderingPath.Forward : RenderingPath.UsePlayerSettings);
			helper.RenderShadows = update.renderShadows;
			if (update.postprocessing != cameraHandler.PostprocessingSetup || update.screenSpaceReflections != cameraHandler.ScreenspaceReflectionsSetup || update.motionBlur != cameraHandler.MotionBlurSetup)
			{
				camera.targetTexture = null;
				cameraHandler.PostprocessingSetup = update.postprocessing;
				cameraHandler.ScreenspaceReflectionsSetup = update.screenSpaceReflections;
				cameraHandler.MotionBlurSetup = update.motionBlur;
				RenderingManager.Instance.CameraInitializer.SetupPostprocessing(camera, new CameraSettings
				{
					IsPrimary = false,
					IsSingleCapture = false,
					IsVR = false,
					SetupPostProcessing = update.postprocessing,
					ScreenSpaceReflection = update.screenSpaceReflections,
					MotionBlur = update.motionBlur
				});
			}
			if (update.renderTextureAssetId < 0)
			{
				helper.Texture = null;
			}
			else
			{
				helper.Texture = RenderingManager.Instance.RenderTextures.GetAsset(update.renderTextureAssetId).Texture;
			}
			helper.DoubleBuffer = update.doubleBuffered && !update.postprocessing;
			helper.SelectiveRender.Clear();
			for (int i = 0; i < update.selectiveRenderCount; i++)
			{
				Transform transform = base.Space.Transforms[updateData.ReadTransformId()];
				helper.SelectiveRender.Add(transform.gameObject);
			}
			helper.ExcludeRender.Clear();
			for (int j = 0; j < update.excludeRenderCount; j++)
			{
				Transform transform2 = base.Space.Transforms[updateData.ReadTransformId()];
				helper.ExcludeRender.Add(transform2.gameObject);
			}
			if (helper.SelectiveRender.Count > 0)
			{
				camera.cullingMask = 1 << LayerMask.NameToLayer("Temp");
			}
			else
			{
				camera.cullingMask = (update.renderPrivateUI ? _privateLayerMask : _layerMask);
			}
			camera.targetTexture = helper.Texture;
			camera.enabled = camera.targetTexture != null && update.enabled;
		}

		protected override UpdateState InitUpdateData(CameraRenderablesUpdate batch)
		{
			return new UpdateState
			{
				transformIds = RenderingManager.Instance.SharedMemory.AccessDataUnmanaged(batch.transformIds),
				transformIndex = 0
			};
		}
	}
	public class CameraPortalManager : RenderableStateChangeManager<CameraPortalRenderable, CameraPortalsRenderablesUpdate, CameraPortalState, EmptyUpdateData>
	{
		public static int LayerMask { get; private set; }

		public CameraPortalManager(RenderSpace space)
			: base(space)
		{
			LayerMask = ~UnityEngine.LayerMask.GetMask("Private", "Overlay");
		}

		protected override EmptyUpdateData InitUpdateData(CameraPortalsRenderablesUpdate batch)
		{
			return default(EmptyUpdateData);
		}

		protected override int GetRenderableIndex(ref CameraPortalState state)
		{
			return state.renderableIndex;
		}

		protected override void ApplyState(ref CameraPortalState update, CameraPortalRenderable handler, ref EmptyUpdateData updateData, CameraPortalsRenderablesUpdate batch)
		{
			GameObject gameObject = ((update.meshRendererIndex >= 0) ? base.Space.Meshes[update.meshRendererIndex] : null)?.Renderer?.gameObject;
			GameObject gameObject2 = null;
			if (handler.CurrentPortal != null)
			{
				gameObject2 = handler.CurrentPortal.gameObject;
			}
			if ((object)gameObject != gameObject2)
			{
				handler.CleanupInstance();
				if (gameObject != null)
				{
					handler.SetupInstanceOn(gameObject);
				}
			}
			CameraPortal currentPortal = handler.CurrentPortal;
			if (currentPortal != null)
			{
				currentPortal.Normal = update.planeNormal.ToUnity();
				currentPortal.ReflectLayers = RenderHelper.PUBLIC_RENDER_MASK;
				currentPortal.ClipPlaneOffset = update.planeOffset;
				if (update.renderTextureId < 0)
				{
					currentPortal.ReflectionTexture = null;
				}
				else
				{
					currentPortal.ReflectionTexture = RenderingManager.Instance.RenderTextures.GetAsset(update.renderTextureId).Texture;
				}
				currentPortal.OverrideClearFlag = update.overrideClearFlag?.ToUnity();
				currentPortal.OverrideFarClip = update.overrideFarClip;
				currentPortal.DisablePixelLights = update.disablePerPixelLights;
				currentPortal.DisableShadows = update.disableShadows;
				currentPortal.RenderMode = (update.portalMode ? CameraPortal.Mode.Portal : CameraPortal.Mode.Mirror);
				if (update.portalMode)
				{
					currentPortal.PortalTransform = update.portalTransform.ToUnity();
					currentPortal.PortalPlanePosition = update.portalPlanePosition.ToUnity();
					currentPortal.PortalPlaneNormal = update.portalPlaneNormal.ToUnity();
				}
			}
		}
	}
	public class GaussianSplatRenderableManager : RenderableStateChangeManager<GaussianSplatRenderable, GaussianSplatRenderablesUpdate, GaussianSplatRendererState, EmptyUpdateData>
	{
		public GaussianSplatRenderableManager(RenderSpace space)
			: base(space)
		{
		}

		protected override void ApplyState(ref GaussianSplatRendererState update, GaussianSplatRenderable handler, ref EmptyUpdateData updateData, GaussianSplatRenderablesUpdate batch)
		{
			handler.ApplyState(ref update);
		}

		protected override int GetRenderableIndex(ref GaussianSplatRendererState state)
		{
			return state.renderableIndex;
		}

		protected override EmptyUpdateData InitUpdateData(GaussianSplatRenderablesUpdate batch)
		{
			return default(EmptyUpdateData);
		}
	}
	public class LayerManager(RenderSpace space) : RenderableManager<LayerRenderable, LayerUpdate>(space)
	{
		private List<LayerRenderable> newLayers = new List<LayerRenderable>();

		protected override LayerRenderable AllocateRenderable(Transform rootTransform, bool isInUse)
		{
			LayerRenderable layerRenderable = new LayerRenderable();
			layerRenderable.Setup(base.Space, rootTransform, !isInUse);
			newLayers.Add(layerRenderable);
			return layerRenderable;
		}

		protected override void ApplyUpdate(LayerUpdate updateBatch)
		{
			Span<LayerType> span = RenderingManager.Instance.SharedMemory.AccessData(updateBatch.layerAssignments);
			for (int i = 0; i < newLayers.Count; i++)
			{
				newLayers[i].AssignLayer(span[i]);
			}
			newLayers.Clear();
		}
	}
	public class LightManager : RenderableStateChangeManager<LightRenderable, LightRenderablesUpdate, LightState, EmptyUpdateData>
	{
		public LightManager(RenderSpace space)
			: base(space)
		{
		}

		protected override EmptyUpdateData InitUpdateData(LightRenderablesUpdate batch)
		{
			return default(EmptyUpdateData);
		}

		protected override int GetRenderableIndex(ref LightState state)
		{
			return state.renderableIndex;
		}

		protected override void ApplyState(ref LightState update, LightRenderable lightHandler, ref EmptyUpdateData updateData, LightRenderablesUpdate batch)
		{
			Light light = lightHandler.Light;
			light.type = update.type.ToUnity();
			light.intensity = update.intensity;
			light.range = update.range;
			light.spotAngle = update.spotAngle;
			light.color = update.color.ToUnity();
			light.shadows = update.shadowType.ToUnity();
			light.shadowStrength = update.shadowStrength;
			light.shadowNearPlane = update.shadowNearPlane;
			light.shadowCustomResolution = update.shadowMapResolutionOverride;
			light.shadowBias = update.shadowBias;
			light.shadowNormalBias = update.shadowNormalBias;
			if (lightHandler.LastCookieAssetId != update.cookieTextureAssetId)
			{
				light.cookie = TextureHelper.GetTexture(update.cookieTextureAssetId);
				lightHandler.LastCookieAssetId = update.cookieTextureAssetId;
			}
		}
	}
	public class LightsBufferRendererManager : RenderableStateChangeManager<LightsBufferRenderer, LightsBufferRendererUpdate, LightsBufferRendererState, EmptyUpdateData>
	{
		public LightsBufferRendererManager(RenderSpace space)
			: base(space)
		{
		}

		protected override void ApplyState(ref LightsBufferRendererState update, LightsBufferRenderer handler, ref EmptyUpdateData updateData, LightsBufferRendererUpdate batch)
		{
			handler.ApplyState(ref update);
		}

		protected override int GetRenderableIndex(ref LightsBufferRendererState state)
		{
			return state.renderableIndex;
		}

		protected override EmptyUpdateData InitUpdateData(LightsBufferRendererUpdate batch)
		{
			return default(EmptyUpdateData);
		}
	}
	public class LODGroupRenderableManager : RenderableStateChangeManager<LODGroupRenderable, LODGroupRenderablesUpdate, LODGroupState, LODGroupRenderableManager.UpdateState>
	{
		public struct UpdateState
		{
			public UnmanagedSpan<LODState> lodStates;

			public UnmanagedSpan<int> rendererIds;
		}

		public LODGroupRenderableManager(RenderSpace space)
			: base(space)
		{
		}

		protected override void ApplyState(ref LODGroupState update, LODGroupRenderable handler, ref UpdateState updateData, LODGroupRenderablesUpdate batch)
		{
			handler.ApplyState(ref update, ref updateData.lodStates, ref updateData.rendererIds);
		}

		protected override int GetRenderableIndex(ref LODGroupState state)
		{
			return state.renderableIndex;
		}

		protected override UpdateState InitUpdateData(LODGroupRenderablesUpdate batch)
		{
			return new UpdateState
			{
				lodStates = RenderingManager.Instance.SharedMemory.AccessDataUnmanaged(batch.lodStates),
				rendererIds = RenderingManager.Instance.SharedMemory.AccessDataUnmanaged(batch.packedMeshRendererIds)
			};
		}
	}
	public class MeshBufferRendererManager : RenderableStateChangeManager<MeshRenderBufferRenderer, MeshRenderBufferUpdate, MeshRenderBufferState, EmptyUpdateData>
	{
		public MeshBufferRendererManager(RenderSpace space)
			: base(space)
		{
		}

		protected override void ApplyState(ref MeshRenderBufferState update, MeshRenderBufferRenderer handler, ref EmptyUpdateData updateData, MeshRenderBufferUpdate batch)
		{
			handler.ApplyState(ref update);
		}

		protected override int GetRenderableIndex(ref MeshRenderBufferState state)
		{
			return state.renderableIndex;
		}

		protected override EmptyUpdateData InitUpdateData(MeshRenderBufferUpdate batch)
		{
			return default(EmptyUpdateData);
		}
	}
	public interface IMeshRenderable
	{
		Renderer Renderer { get; }

		Mesh SharedMesh { set; }

		int LastPropertyBlockCount { get; set; }
	}
	public class MeshRendererManager : MeshRendererManager<MeshRenderable, MeshRenderablesUpdate>
	{
		public MeshRendererManager(RenderSpace space)
			: base(space)
		{
		}

		protected override MeshRenderable AllocateRenderable(Transform rootTransform, bool isInUse)
		{
			MeshRenderable meshRenderable = new MeshRenderable();
			meshRenderable.Setup(base.Space, rootTransform, !isInUse);
			return meshRenderable;
		}
	}
	public abstract class MeshRendererManager<TRenderable, TUpdate> : RenderableManager<TRenderable, TUpdate> where TRenderable : Renderable, IMeshRenderable where TUpdate : MeshRenderablesUpdate
	{
		public int LastPropertyBlockCount { get; set; }

		public MeshRendererManager(RenderSpace space)
			: base(space)
		{
		}

		protected override void ApplyUpdate(TUpdate updateBatch)
		{
			AssetManager<MeshAsset> meshes = RenderingManager.Instance.Meshes;
			AssetManager<MaterialAsset> materials = RenderingManager.Instance.Materials.Materials;
			AssetManager<MaterialPropertyBlockAsset> propertyBlocks = RenderingManager.Instance.Materials.PropertyBlocks;
			if (updateBatch.meshStates.IsEmpty)
			{
				return;
			}
			Span<MeshRendererState> span = RenderingManager.Instance.SharedMemory.AccessData(updateBatch.meshStates);
			Span<int> span2 = RenderingManager.Instance.SharedMemory.AccessData(updateBatch.meshMaterialsAndPropertyBlocks);
			int num = 0;
			for (int i = 0; i < span.Length; i++)
			{
				ref MeshRendererState reference = ref span[i];
				if (reference.renderableIndex < 0)
				{
					break;
				}
				TRenderable val = base[reference.renderableIndex];
				if (reference.meshAssetId < 0)
				{
					val.SharedMesh = null;
				}
				else
				{
					val.SharedMesh = meshes.GetAsset(reference.meshAssetId).Mesh;
				}
				val.Renderer.shadowCastingMode = reference.shadowCastMode.ToUnity();
				val.Renderer.motionVectorGenerationMode = reference.motionVectorMode.ToUnity();
				val.Renderer.sortingOrder = reference.sortingOrder;
				if (reference.materialCount < 0)
				{
					continue;
				}
				Material[] array = new Material[reference.materialCount];
				for (int j = 0; j < array.Length; j++)
				{
					int id = span2[num++];
					array[j] = materials.GetAsset(id)?.Material ?? RenderingManager.Instance.NullMaterial;
				}
				val.Renderer.sharedMaterials = array;
				if (reference.materialPropertyBlockCount >= 0)
				{
					for (int k = 0; k < reference.materialPropertyBlockCount; k++)
					{
						int id2 = span2[num++];
						MaterialPropertyBlock properties = propertyBlocks.GetAsset(id2)?.PropertyBlock;
						val.Renderer.SetPropertyBlock(properties, k);
					}
					val.LastPropertyBlockCount = Math.Min(val.LastPropertyBlockCount, reference.materialCount);
					for (int l = reference.materialPropertyBlockCount; l < val.LastPropertyBlockCount; l++)
					{
						val.Renderer.SetPropertyBlock(null, l);
					}
					val.LastPropertyBlockCount = reference.materialPropertyBlockCount;
				}
			}
		}
	}
	public class ReflectionProbeManager : RenderableStateChangeManager<ReflectionProbeRenderable, ReflectionProbeRenderablesUpdate, ReflectionProbeState, EmptyUpdateData>
	{
		public ReflectionProbeManager(RenderSpace space)
			: base(space)
		{
		}

		public void HandleRenderTasks(List<ReflectionProbeRenderTask> tasks)
		{
			foreach (ReflectionProbeRenderTask task in tasks)
			{
				base[task.renderableIndex].RenderToTexture(task);
			}
		}

		protected override void ApplyState(ref ReflectionProbeState update, ReflectionProbeRenderable probeHandler, ref EmptyUpdateData updateData, ReflectionProbeRenderablesUpdate batch)
		{
			AssetManager<CubemapAsset> cubemaps = RenderingManager.Instance.Cubemaps;
			probeHandler.ApplyState(ref update, cubemaps);
		}

		protected override void ApplyUpdate(ReflectionProbeRenderablesUpdate updateBatch)
		{
			base.ApplyUpdate(updateBatch);
			if (updateBatch.changedProbesToRender.IsEmpty)
			{
				return;
			}
			Span<ReflectionProbeChangeRenderTask> span = RenderingManager.Instance.SharedMemory.AccessData(updateBatch.changedProbesToRender);
			for (int i = 0; i < span.Length; i++)
			{
				ref ReflectionProbeChangeRenderTask reference = ref span[i];
				if (reference.renderableIndex >= 0)
				{
					base[reference.renderableIndex].StartRender(reference.uniqueId);
					continue;
				}
				break;
			}
		}

		protected override int GetRenderableIndex(ref ReflectionProbeState state)
		{
			return state.renderableIndex;
		}

		protected override EmptyUpdateData InitUpdateData(ReflectionProbeRenderablesUpdate batch)
		{
			return default(EmptyUpdateData);
		}
	}
	public class ReflectionProbeSH2Manager : RenderableManager<ReflectionProbeSH2Renderable, ReflectionProbeSH2Tasks>
	{
		public ReflectionProbeSH2Manager(RenderSpace space)
			: base(space)
		{
		}

		protected override ReflectionProbeSH2Renderable AllocateRenderable(Transform rootTransform, bool isInUse)
		{
			ReflectionProbeSH2Renderable reflectionProbeSH2Renderable = new ReflectionProbeSH2Renderable();
			reflectionProbeSH2Renderable.Setup(base.Space, rootTransform, !isInUse);
			return reflectionProbeSH2Renderable;
		}

		protected override void ApplyUpdate(ReflectionProbeSH2Tasks updateBatch)
		{
			if (updateBatch.tasks.IsEmpty)
			{
				return;
			}
			Span<ReflectionProbeSH2Task> span = RenderingManager.Instance.SharedMemory.AccessData(updateBatch.tasks);
			ReflectionProbeManager reflectionProbes = base.Space.ReflectionProbes;
			for (int i = 0; i < span.Length; i++)
			{
				ref ReflectionProbeSH2Task reference = ref span[i];
				if (reference.renderableIndex < 0)
				{
					break;
				}
				ReflectionProbeSH2Renderable reflectionProbeSH2Renderable = base[reference.renderableIndex];
				if (reference.reflectionProbeRenderableIndex < 0)
				{
					reference.result = ComputeResult.Failed;
					continue;
				}
				ReflectionProbeRenderable reflectionProbeRenderable = reflectionProbes[reference.reflectionProbeRenderableIndex];
				if (RenderingManager.IsDebug)
				{
					UnityEngine.Debug.Log($"[{RenderingManager.Instance.LastFrameIndex}] Computing SH2 for {reference.renderableIndex}. ProbeIndex: {reference.reflectionProbeRenderableIndex}.");
				}
				reference.result = reflectionProbeSH2Renderable.Compute(reflectionProbeRenderable.Probe, out reference.resultData);
				if (RenderingManager.IsDebug)
				{
					UnityEngine.Debug.Log($"RESULT [{RenderingManager.Instance.LastFrameIndex}] - for computing SH2 for {reference.renderableIndex}. ProbeIndex: {reference.reflectionProbeRenderableIndex} - {reference.result}\n{reference.resultData}");
				}
			}
		}
	}
	public abstract class RenderableManager<TRenderable, TUpdate> where TRenderable : Renderable where TUpdate : RenderablesUpdate
	{
		private List<TRenderable> renderables = new List<TRenderable>();

		public RenderSpace Space { get; private set; }

		public int RenderableCount => renderables.Count;

		public TRenderable this[int renderableIndex] => renderables[renderableIndex];

		public RenderableManager(RenderSpace space)
		{
			Space = space;
		}

		public void HandleUpdate(TUpdate update)
		{
			if (!update.removals.IsEmpty)
			{
				Span<int> span = RenderingManager.Instance.SharedMemory.AccessData(update.removals);
				for (int i = 0; i < span.Length; i++)
				{
					int num = span[i];
					if (num < 0)
					{
						break;
					}
					TRenderable val = renderables[num];
					val.Index = -1;
					val.Remove();
					renderables[num] = renderables[renderables.Count - 1];
					renderables[num].Index = num;
					renderables.RemoveAt(renderables.Count - 1);
				}
			}
			if (!update.additions.IsEmpty)
			{
				Span<int> span2 = RenderingManager.Instance.SharedMemory.AccessData(update.additions);
				for (int j = 0; j < span2.Length; j++)
				{
					int num2 = span2[j];
					if (num2 < 0)
					{
						break;
					}
					TransformManager.TransformData transformData = Space.Transforms.GetTransformData(num2);
					if (!transformData.inUse)
					{
						Space.Transforms.MarkInUse(num2);
					}
					if (transformData.transform == null)
					{
						throw new Exception($"TransformId: {num2} is null! InUse: {transformData.inUse}. Renderable type handler: {GetType().FullName}");
					}
					TRenderable val2 = AllocateRenderable(transformData.transform, transformData.inUse);
					val2.Index = renderables.Count;
					renderables.Add(val2);
				}
			}
			ApplyUpdate(update);
		}

		protected abstract void ApplyUpdate(TUpdate updateBatch);

		protected abstract TRenderable AllocateRenderable(Transform rootTransform, bool isInUse);
	}
	[StructLayout(LayoutKind.Sequential, Size = 1)]
	public struct EmptyUpdateData
	{
	}
	public abstract class RenderableStateChangeManager<TRenderable, TUpdate, TState, TUpdateData> : RenderableManager<TRenderable, TUpdate> where TRenderable : Renderable, new() where TUpdate : RenderablesStateUpdate<TState> where TState : unmanaged where TUpdateData : unmanaged
	{
		protected RenderableStateChangeManager(RenderSpace space)
			: base(space)
		{
		}

		protected override TRenderable AllocateRenderable(Transform rootTransform, bool isInUse)
		{
			TRenderable val = new TRenderable();
			val.Setup(base.Space, rootTransform, !isInUse);
			return val;
		}

		protected override void ApplyUpdate(TUpdate updateBatch)
		{
			if (updateBatch.states.IsEmpty)
			{
				return;
			}
			TUpdateData updateData = InitUpdateData(updateBatch);
			Span<TState> span = RenderingManager.Instance.SharedMemory.AccessData(updateBatch.states);
			for (int i = 0; i < span.Length; i++)
			{
				ref TState reference = ref span[i];
				int renderableIndex = GetRenderableIndex(ref reference);
				if (renderableIndex >= 0)
				{
					TRenderable val = base[renderableIndex];
					if (val == null)
					{
						throw new Exception($"Renderable at index {renderableIndex} is null! Update batch states length: {updateBatch.states.length}. " + "Manager: " + GetType().FullName);
					}
					ApplyState(ref reference, val, ref updateData, updateBatch);
					continue;
				}
				break;
			}
		}

		protected abstract TUpdateData InitUpdateData(TUpdate batch);

		protected abstract int GetRenderableIndex(ref TState state);

		protected abstract void ApplyState(ref TState update, TRenderable handler, ref TUpdateData updateData, TUpdate batch);
	}
	public struct RenderMaterialOverrideUpdateData
	{
		public UnmanagedSpan<MaterialOverrideState> materialStates;
	}
	public class RenderMaterialOverrideManager : RenderableStateChangeManager<RenderMaterialOverrideRenderable, RenderMaterialOverridesUpdate, RenderMaterialOverrideState, RenderMaterialOverrideUpdateData>, IDisposable
	{
		public RenderMaterialOverrideManager(RenderSpace space)
			: base(space)
		{
		}

		protected override void ApplyState(ref RenderMaterialOverrideState update, RenderMaterialOverrideRenderable handler, ref RenderMaterialOverrideUpdateData updateData, RenderMaterialOverridesUpdate batch)
		{
			handler.ApplyState(ref update, updateData.materialStates);
			if (update.materrialOverrideCount > 0)
			{
				updateData.materialStates = updateData.materialStates.Slice(update.materrialOverrideCount);
			}
		}

		protected override int GetRenderableIndex(ref RenderMaterialOverrideState state)
		{
			return state.renderableIndex;
		}

		protected override RenderMaterialOverrideUpdateData InitUpdateData(RenderMaterialOverridesUpdate batch)
		{
			return new RenderMaterialOverrideUpdateData
			{
				materialStates = RenderingManager.Instance.SharedMemory.AccessDataUnmanaged(batch.materialOverrideStates)
			};
		}

		public void Dispose()
		{
			for (int i = 0; i < base.RenderableCount; i++)
			{
				base[i].Remove(removingSpace: true);
			}
		}
	}
	public struct RenderTransformOverrideUpdateData
	{
		public UnmanagedSpan<int> skinnedMeshIndexes;
	}
	public class RenderTransformOverrideManager : RenderableStateChangeManager<RenderTransformOverrideRenderable, RenderTransformOverridesUpdate, RenderTransformOverrideState, RenderTransformOverrideUpdateData>, IDisposable
	{
		public RenderTransformOverrideManager(RenderSpace space)
			: base(space)
		{
		}

		protected override void ApplyState(ref RenderTransformOverrideState update, RenderTransformOverrideRenderable handler, ref RenderTransformOverrideUpdateData updateData, RenderTransformOverridesUpdate batch)
		{
			handler.ApplyState(ref update, updateData.skinnedMeshIndexes);
			if (update.skinnedMeshRendererCount > 0)
			{
				updateData.skinnedMeshIndexes = updateData.skinnedMeshIndexes.Slice(update.skinnedMeshRendererCount);
			}
		}

		protected override int GetRenderableIndex(ref RenderTransformOverrideState state)
		{
			return state.renderableIndex;
		}

		protected override RenderTransformOverrideUpdateData InitUpdateData(RenderTransformOverridesUpdate batch)
		{
			return new RenderTransformOverrideUpdateData
			{
				skinnedMeshIndexes = RenderingManager.Instance.SharedMemory.AccessDataUnmanaged(batch.skinnedMeshRenderersIndexes)
			};
		}

		public void Dispose()
		{
			for (int i = 0; i < base.RenderableCount; i++)
			{
				base[i].Remove(removingSpace: true);
			}
		}
	}
	public class SkinnedMeshRendererManager : MeshRendererManager<SkinnedMeshRenderable, SkinnedMeshRenderablesUpdate>
	{
		public SkinnedMeshRendererManager(RenderSpace space)
			: base(space)
		{
		}

		protected override SkinnedMeshRenderable AllocateRenderable(Transform rootTransform, bool isInUse)
		{
			SkinnedMeshRenderable skinnedMeshRenderable = new SkinnedMeshRenderable();
			skinnedMeshRenderable.Setup(base.Space, rootTransform, !isInUse);
			return skinnedMeshRenderable;
		}

		protected override void ApplyUpdate(SkinnedMeshRenderablesUpdate updateBatch)
		{
			base.ApplyUpdate(updateBatch);
			if (RenderingManager.IsDebug)
			{
				UnityEngine.Debug.Log($"Skinned mesh update. Bounds: {updateBatch.boundsUpdates.length}, " + $"Bones: {updateBatch.boneAssignments.length}, Blendshape: {updateBatch.blendshapeUpdates.length}");
			}
			if (!updateBatch.boundsUpdates.IsEmpty)
			{
				Span<UnitySkinnedMeshBoundsUpdate> span = RenderingManager.Instance.SharedMemory.AccessData(updateBatch.boundsUpdates.As<UnitySkinnedMeshBoundsUpdate>());
				if (RenderingManager.IsDebug)
				{
					UnityEngine.Debug.Log("Actual update count: " + span.Length);
				}
				for (int i = 0; i < span.Length; i++)
				{
					ref UnitySkinnedMeshBoundsUpdate reference = ref span[i];
					if (reference.renderableIndex < 0)
					{
						break;
					}
					SkinnedMeshRenderable skinnedMeshRenderable = base[reference.renderableIndex];
					skinnedMeshRenderable.Renderer.updateWhenOffscreen = false;
					skinnedMeshRenderable.Renderer.localBounds = reference.localBounds;
					if (RenderingManager.IsDebug)
					{
						UnityEngine.Debug.Log($"[{i}] ({reference.renderableIndex} - {skinnedMeshRenderable.Transform.name} - {reference.localBounds}");
					}
				}
			}
			if (!updateBatch.realtimeBoundsUpdates.IsEmpty)
			{
				Span<SkinnedMeshRealtimeBoundsUpdate> span2 = RenderingManager.Instance.SharedMemory.AccessData(updateBatch.realtimeBoundsUpdates);
				for (int j = 0; j < span2.Length; j++)
				{
					ref SkinnedMeshRealtimeBoundsUpdate reference2 = ref span2[j];
					if (reference2.renderableIndex < 0)
					{
						break;
					}
					SkinnedMeshRenderable skinnedMeshRenderable2 = base[reference2.renderableIndex];
					skinnedMeshRenderable2.Renderer.updateWhenOffscreen = true;
					reference2.computedGlobalBounds = skinnedMeshRenderable2.Renderer.bounds.ToRender();
				}
			}
			TransformManager transforms;
			if (!updateBatch.boneAssignments.IsEmpty)
			{
				Span<BoneAssignment> span3 = RenderingManager.Instance.SharedMemory.AccessData(updateBatch.boneAssignments);
				Span<int> span4 = RenderingManager.Instance.SharedMemory.AccessData(updateBatch.boneTransformIndexes);
				transforms = base.Space.Transforms;
				int num = 0;
				for (int k = 0; k < span3.Length; k++)
				{
					ref BoneAssignment reference3 = ref span3[k];
					if (reference3.renderableIndex < 0)
					{
						break;
					}
					SkinnedMeshRenderable skinnedMeshRenderable3 = base[reference3.renderableIndex];
					Transform[] array = new Transform[reference3.boneCount];
					for (int l = 0; l < reference3.boneCount; l++)
					{
						array[l] = GetBone(span4[num++]);
					}
					skinnedMeshRenderable3.Renderer.bones = array;
					skinnedMeshRenderable3.Renderer.rootBone = GetBone(reference3.rootBoneTransformId);
					if (RenderingManager.IsDebug)
					{
						UnityEngine.Debug.Log($"Assigning bones to {reference3.renderableIndex} - {skinnedMeshRenderable3.Transform.name}: {array.Length}. Mesh bone count: {skinnedMeshRenderable3.Renderer.sharedMesh?.bindposes.Length}." + "\nBones: " + string.Join(", ", array.Select((Transform b) => b?.name)));
					}
				}
			}
			if (updateBatch.blendshapeUpdateBatches.IsEmpty)
			{
				return;
			}
			Span<BlendshapeUpdateBatch> span5 = RenderingManager.Instance.SharedMemory.AccessData(updateBatch.blendshapeUpdateBatches);
			Span<BlendshapeUpdate> span6 = RenderingManager.Instance.SharedMemory.AccessData(updateBatch.blendshapeUpdates);
			int num2 = 0;
			for (int num3 = 0; num3 < span5.Length; num3++)
			{
				ref BlendshapeUpdateBatch reference4 = ref span5[num3];
				if (reference4.renderableIndex >= 0)
				{
					SkinnedMeshRenderable skinnedMeshRenderable4 = base[reference4.renderableIndex];
					for (int num4 = 0; num4 < reference4.blendshapeUpdateCount; num4++)
					{
						BlendshapeUpdate blendshapeUpdate = span6[num2++];
						skinnedMeshRenderable4.Renderer.SetBlendShapeWeight(blendshapeUpdate.blendshapeIndex, blendshapeUpdate.weight);
					}
					continue;
				}
				break;
			}
			Transform GetBone(int index)
			{
				if (index < 0)
				{
					return null;
				}
				return transforms[index];
			}
		}
	}
	public class TrailsBufferRendererManager : RenderableStateChangeManager<TrailsRenderBufferRenderer, TrailsRendererUpdate, TrailsRendererState, EmptyUpdateData>
	{
		public TrailsBufferRendererManager(RenderSpace space)
			: base(space)
		{
		}

		protected override void ApplyState(ref TrailsRendererState update, TrailsRenderBufferRenderer handler, ref EmptyUpdateData updateData, TrailsRendererUpdate batch)
		{
			handler.ApplyState(ref update);
		}

		protected override int GetRenderableIndex(ref TrailsRendererState state)
		{
			return state.renderableIndex;
		}

		protected override EmptyUpdateData InitUpdateData(TrailsRendererUpdate batch)
		{
			return default(EmptyUpdateData);
		}
	}
	public class BlitToDisplayRenderable : Renderable
	{
		public TextureDisplayBlitter Blitter { get; private set; }

		protected override void Cleanup()
		{
			Blitter?.Deinitialize();
			UnityEngine.Object.Destroy(Blitter);
		}

		protected override void Setup(Transform root)
		{
			Blitter = root.gameObject.AddComponent<TextureDisplayBlitter>();
		}
	}
	public class CameraPortalRenderable : Renderable
	{
		public CameraPortal CurrentPortal { get; private set; }

		protected override void Cleanup()
		{
			CleanupInstance();
		}

		protected override void Setup(Transform root)
		{
		}

		public void SetupInstanceOn(GameObject gameObject)
		{
			if (CurrentPortal != null)
			{
				throw new InvalidOperationException("Instance has already been setup. Clean it up first.");
			}
			CurrentPortal = gameObject.AddComponent<CameraPortal>();
			CurrentPortal.ReflectLayers = CameraPortalManager.LayerMask;
		}

		public void CleanupInstance()
		{
			if (!(CurrentPortal == null))
			{
				UnityEngine.Object.Destroy(CurrentPortal);
				CurrentPortal = null;
			}
		}
	}
	public class CameraRenderable : Renderable
	{
		public bool PostprocessingSetup;

		public bool ScreenspaceReflectionsSetup;

		public bool MotionBlurSetup;

		public Camera Camera { get; private set; }

		public CameraController Helper { get; private set; }

		protected override void Cleanup()
		{
			RenderingManager.Instance.CameraInitializer.CleanupCamera(Camera);
			if (PostprocessingSetup)
			{
				RenderingManager.Instance.CameraInitializer.RemovePostProcessing(Camera);
				PostprocessingSetup = false;
			}
			UnityEngine.Object.Destroy(Helper);
			UnityEngine.Object.Destroy(Camera);
			Camera = null;
			Helper = null;
		}

		protected override void Setup(Transform root)
		{
			GameObject gameObject = root.gameObject;
			Camera = gameObject.AddComponent<Camera>();
			Camera.allowHDR = true;
			Camera.stereoTargetEye = StereoTargetEyeMask.None;
			RenderingManager.Instance.CameraInitializer.RegisterCamera(Camera);
			Helper = gameObject.AddComponent<CameraController>();
			Helper.Camera = Camera;
		}
	}
	public class GaussianSplatRenderable : Renderable
	{
		private GaussianSplatRenderer renderer;

		protected override void Setup(Transform root)
		{
			renderer = root.gameObject.AddComponent<GaussianSplatRenderer>();
		}

		protected override void Cleanup()
		{
			UnityEngine.Object.Destroy(renderer);
		}

		public void ApplyState(ref GaussianSplatRendererState state)
		{
			renderer.Asset = RenderingManager.Instance.GaussianSplats.GetAsset(state.gaussianSplatAssetId);
			renderer.SplatScale = state.sizeScale;
			renderer.OpacityScale = state.opacityScale;
			renderer.SHOrder = Math.Min(state.maxSHOrder, 3);
			renderer.SHOnly = state.sphericalHamornicsOnly;
		}
	}
	public class LayerRenderable : Renderable
	{
		private OverlayRootPositioner _positioner;

		public override bool DirectOnly => true;

		protected override void Cleanup()
		{
			if (base.Transform != null)
			{
				base.Transform.tag = "Untagged";
				if (base.Transform.parent != null)
				{
					SetLayerRecursively(base.Transform, base.Transform.parent.gameObject.layer);
				}
			}
			if (_positioner != null)
			{
				UnityEngine.Object.Destroy(_positioner);
				_positioner = null;
			}
		}

		protected override void Setup(Transform root)
		{
			base.Transform.tag = "FORCE_LAYER";
			if (RenderingManager.IsDebug)
			{
				UnityEngine.Debug.Log("Forcing layer on " + base.Transform.name);
			}
		}

		public void AssignLayer(LayerType type)
		{
			SetLayerRecursively(base.Transform, GetLayer(type));
			if (type == LayerType.Overlay)
			{
				_positioner = base.ActualTransform.gameObject.AddComponent<OverlayRootPositioner>();
			}
		}

		public static int GetLayer(LayerType type)
		{
			return type switch
			{
				LayerType.Overlay => LayerMask.NameToLayer("Overlay"), 
				LayerType.Hidden => LayerMask.NameToLayer("Hidden"), 
				_ => throw new InvalidOperationException("Invalid layer type: " + type), 
			};
		}

		public static void SetLayerRecursively(Transform root, int layer, bool isStart = true)
		{
			if (!(root.tag == "FORCE_LAYER") || isStart)
			{
				root.gameObject.layer = layer;
				for (int i = 0; i < root.childCount; i++)
				{
					SetLayerRecursively(root.GetChild(i), layer, isStart: false);
				}
			}
		}
	}
	public class LightRenderable : Renderable
	{
		public int? LastCookieAssetId;

		public Light Light { get; private set; }

		protected override void Cleanup()
		{
			UnityEngine.Object.Destroy(Light);
			Light = null;
		}

		protected override void Setup(Transform root)
		{
			GameObject gameObject = root.gameObject;
			Light = gameObject.AddComponent<Light>();
		}
	}
	public class LightsBufferRenderer : Renderable
	{
		private List<Light> _lights = new List<Light>();

		private object _dataLock = new object();

		private bool _submissionScheduled;

		private int _dataLength;

		private UnityLightData[] _bufferedData;

		private UnityEngine.LightType type;

		private LightShadows shadows;

		private float shadowStrength;

		private float shadowNearPlane;

		private int shadowCustomResolution;

		private float shadowBias;

		private float shadowNormalBias;

		private Texture cookie;

		public int GlobalUniqueId { get; private set; } = -1;

		protected override void Cleanup()
		{
			RenderingManager.Instance.Unregister(this);
			GlobalUniqueId = -2;
			foreach (Light light in _lights)
			{
				UnityEngine.Object.Destroy(light.gameObject);
			}
			_lights.Clear();
		}

		protected override void Setup(Transform root)
		{
		}

		public void HandleSubmission(LightsBufferRendererSubmission submission)
		{
			Span<UnityLightData> span = RenderingManager.Instance.SharedMemory.AccessData(submission.lights.As<UnityLightData>());
			span = span.Slice(0, submission.lightsCount);
			lock (_dataLock)
			{
				UnityLightData[] bufferedData = _bufferedData;
				if (((bufferedData != null) ? bufferedData.Length : 0) < span.Length)
				{
					_bufferedData = new UnityLightData[span.Length];
				}
				span.CopyTo(_bufferedData);
				_dataLength = span.Length;
				if (!_submissionScheduled)
				{
					RenderingManager.Instance.AssetIntegrator.EnqueueParticleProcessing(SubmitLightsData);
					_submissionScheduled = true;
				}
			}
			LightsBufferRendererConsumed lightsBufferRendererConsumed = new LightsBufferRendererConsumed();
			lightsBufferRendererConsumed.globalUniqueId = submission.lightsBufferUniqueId;
			RenderingManager.Instance.SendBufferConsumed(lightsBufferRendererConsumed);
			PackerMemoryPool.Instance.Return(submission);
		}

		private void SubmitLightsData()
		{
			lock (_dataLock)
			{
				if (_bufferedData != null)
				{
					SubmitLightsBuffer(_bufferedData.AsSpan(0, _dataLength));
				}
				_submissionScheduled = false;
			}
		}

		private void SubmitLightsBuffer(Span<UnityLightData> lights)
		{
			if (RenderingManager.IsDebug)
			{
				UnityEngine.Debug.Log($"Submitting lights buffer: {lights.Length}");
			}
			Vector3 lossyScale = base.ActualTransform.lossyScale;
			float num = (lossyScale.x + lossyScale.y + lossyScale.z) * (1f / 3f);
			Transform actualTransform = base.ActualTransform;
			for (int i = 0; i < lights.Length; i++)
			{
				Light light;
				if (_lights.Count <= i)
				{
					GameObject gameObject = new GameObject(RenderingManager.IsDebug ? $"LightBufferLight {GlobalUniqueId}" : "");
					gameObject.transform.SetParent(actualTransform, worldPositionStays: false);
					gameObject.layer = actualTransform.gameObject.layer;
					light = gameObject.AddComponent<Light>();
					AssignLightProperties(light);
					_lights.Add(light);
				}
				else
				{
					light = _lights[i];
				}
				ref UnityLightData reference = ref lights[i];
				light.transform.localPosition = reference.point;
				light.transform.localRotation = reference.orientation;
				light.intensity = Mathf.Clamp(MathHelper.FilterInvalid(reference.intensity), -1024f, 1024f);
				Vector3 color = reference.color;
				float r = Mathf.Clamp(MathHelper.FilterInvalid(color.x), -64f, 64f);
				float g = Mathf.Clamp(MathHelper.FilterInvalid(color.y), -64f, 64f);
				float b = Mathf.Clamp(MathHelper.FilterInvalid(color.z), -64f, 64f);
				light.color = new Color(r, g, b);
				light.range = MathHelper.FilterInvalid(reference.range * num);
				light.spotAngle = Mathf.Clamp(MathHelper.FilterInvalid(reference.angle), 0f, 180f);
			}
			while (_lights.Count > lights.Length)
			{
				int index = _lights.Count - 1;
				UnityEngine.Object.Destroy(_lights[index].gameObject);
				_lights.RemoveAt(index);
			}
		}

		public void ApplyState(ref LightsBufferRendererState state)
		{
			if (GlobalUniqueId < 0)
			{
				GlobalUniqueId = state.globalUniqueId;
				RenderingManager.Instance.Register(this);
			}
			else if (GlobalUniqueId != state.globalUniqueId)
			{
				throw new InvalidOperationException("GlobalUniqueID cannot be changed after being assigned");
			}
			type = state.lightType.ToUnity();
			shadows = state.shadowType.ToUnity();
			shadowStrength = state.shadowStrength;
			shadowNearPlane = state.shadowNearPlane;
			shadowCustomResolution = state.shadowMapResolution;
			shadowBias = state.shadowBias;
			shadowNormalBias = state.shadowNormalBias;
			cookie = TextureHelper.GetTexture(state.cookieTextureAssetId);
			AssignAllLightProperties();
		}

		private void AssignAllLightProperties()
		{
			foreach (Light light in _lights)
			{
				AssignLightProperties(light);
			}
		}

		private void AssignLightProperties(Light light)
		{
			light.type = type;
			light.shadows = shadows;
			light.shadowStrength = shadowStrength;
			light.shadowNearPlane = shadowNearPlane;
			light.shadowCustomResolution = shadowCustomResolution;
			light.shadowBias = shadowBias;
			light.shadowNormalBias = shadowNormalBias;
			light.cookie = cookie;
		}
	}
	public class LODGroupRenderable : Renderable
	{
		private LODGroup lodGroup;

		public void ApplyState(ref LODGroupState state, ref UnmanagedSpan<LODState> lodStates, ref UnmanagedSpan<int> rendererIds)
		{
			LOD[] array = new LOD[state.lodCount];
			int num = 0;
			for (int i = 0; i < array.Length; i++)
			{
				ref LOD reference = ref array[i];
				LODState lODState = lodStates[i];
				reference.screenRelativeTransitionHeight = lODState.screenRelativeTransitionHeight;
				reference.fadeTransitionWidth = lODState.fadeTransitionWidth;
				Renderer[] array2 = new Renderer[lODState.rendererCount];
				for (int j = 0; j < array2.Length; j++)
				{
					array2[j] = MeshRendererHelper.GetMeshRenderable(rendererIds[num++], base.Space)?.Renderer;
				}
				reference.renderers = array2;
			}
			lodGroup.SetLODs(array);
			lodGroup.RecalculateBounds();
			if (state.lodCount > 0)
			{
				lodStates = lodStates.Slice(state.lodCount);
			}
			if (num > 0)
			{
				rendererIds = rendererIds.Slice(num);
			}
		}

		protected override void Setup(Transform root)
		{
			lodGroup = root.gameObject.AddComponent<LODGroup>();
		}

		protected override void Cleanup()
		{
			UnityEngine.Object.Destroy(lodGroup);
		}
	}
	public class MeshRenderable : Renderable, IMeshRenderable
	{
		public MeshRenderer Renderer { get; private set; }

		public MeshFilter Filter { get; private set; }

		public Mesh SharedMesh
		{
			set
			{
				Filter.sharedMesh = value;
			}
		}

		public int LastPropertyBlockCount { get; set; }

		Renderer IMeshRenderable.Renderer => Renderer;

		protected override void Cleanup()
		{
			UnityEngine.Object.Destroy(Renderer);
			UnityEngine.Object.Destroy(Filter);
			Renderer = null;
			Filter = null;
		}

		protected override void Setup(Transform root)
		{
			GameObject gameObject = root.gameObject;
			Filter = gameObject.AddComponent<MeshFilter>();
			Renderer = gameObject.AddComponent<MeshRenderer>();
			Renderer.sharedMaterial = RenderingManager.Instance.NullMaterial;
		}
	}
	public static class MeshRendererHelper
	{
		public static IMeshRenderable GetMeshRenderable(int packedId, RenderSpace space)
		{
			if (packedId == -1)
			{
				return null;
			}
			IdPacker<MeshRendererType>.Unpack(packedId, out var id, out var type);
			return type switch
			{
				MeshRendererType.SkinnedMeshRenderer => space.SkinnedMeshes[id], 
				MeshRendererType.MeshRenderer => space.Meshes[id], 
				_ => throw new NotImplementedException($"Unsupported mesh renderer type: {type}"), 
			};
		}
	}
	public class ReflectionProbeRenderable : Renderable
	{
		private GameObject root;

		private int? _currentRenderIndex;

		private bool _resetProbe;

		private bool _renderAgain;

		private int _renderAgainUniqueId;

		private bool? _lastBaked;

		public ReflectionProbe Probe { get; private set; }

		public bool MarkedForReset => _resetProbe;

		protected override void Cleanup()
		{
			if (!(Probe == null))
			{
				UnityEngine.Object.DestroyImmediate(Probe);
				Probe = null;
			}
		}

		internal void ApplyState(ref ReflectionProbeState update, AssetManager<CubemapAsset> cubemaps)
		{
			bool flag = update.type == Renderite.Shared.ReflectionProbeType.Baked;
			if (_lastBaked.HasValue && flag != _lastBaked)
			{
				MarkProbeForReset();
			}
			_lastBaked = flag;
			EnsureValidProbe();
			switch (update.type)
			{
			case Renderite.Shared.ReflectionProbeType.Baked:
				Probe.mode = ReflectionProbeMode.Custom;
				if (update.cubemapAssetId < 0)
				{
					Probe.customBakedTexture = null;
				}
				else
				{
					Probe.customBakedTexture = cubemaps.GetAsset(update.cubemapAssetId).Texture;
				}
				Probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
				break;
			case Renderite.Shared.ReflectionProbeType.Realtime:
				Probe.mode = ReflectionProbeMode.Realtime;
				Probe.refreshMode = ReflectionProbeRefreshMode.EveryFrame;
				Probe.customBakedTexture = null;
				break;
			case Renderite.Shared.ReflectionProbeType.OnChanges:
				Probe.mode = ReflectionProbeMode.Realtime;
				Probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
				Probe.customBakedTexture = null;
				break;
			}
			Probe.importance = update.importance;
			Probe.intensity = update.intensity;
			Probe.blendDistance = update.blendDistance;
			Probe.boxProjection = update.useBoxProjection;
			Probe.size = update.boxSize.ToUnity();
			Probe.timeSlicingMode = update.timeSlicingMode.ToUnity();
			Probe.resolution = update.resolution;
			Probe.hdr = update.HDR;
			Probe.clearFlags = update.clearFlags.ToUnity();
			Probe.backgroundColor = update.backgroundColor.ToUnity();
			Probe.nearClipPlane = update.nearClip;
			Probe.farClipPlane = update.farClip;
			Probe.cullingMask = ((!update.skyboxOnly) ? RenderHelper.PUBLIC_RENDER_MASK : 0);
		}

		protected override void Setup(Transform root)
		{
			this.root = root.gameObject;
			AttachProbe();
		}

		public void MarkProbeForReset()
		{
			_resetProbe = true;
		}

		public void EnsureValidProbe()
		{
			if (_resetProbe)
			{
				Cleanup();
				AttachProbe();
				_resetProbe = false;
			}
		}

		private void AttachProbe()
		{
			Probe = root.AddComponent<ReflectionProbe>();
			Probe.cullingMask = RenderHelper.PUBLIC_RENDER_MASK;
		}

		public void StartRender(int uniqueId)
		{
			if (_currentRenderIndex.HasValue)
			{
				_renderAgain = true;
				_renderAgainUniqueId = uniqueId;
			}
			else
			{
				_currentRenderIndex = Probe.RenderProbe();
				RenderingManager.Instance.StartCoroutine(HandleRenderResult(uniqueId));
			}
		}

		private IEnumerator HandleRenderResult(int uniqueId)
		{
			bool renderingFinished = false;
			do
			{
				yield return new WaitForEndOfFrame();
				try
				{
					ReflectionProbe probe = Probe;
					if (!(probe == null))
					{
						renderingFinished = probe.IsFinishedRendering(_currentRenderIndex.Value);
					}
				}
				catch (Exception ex)
				{
					UnityEngine.Debug.LogError("Exception when checking state of ReflectionProbe render. " + $"RenderIndex: {_currentRenderIndex}, RenderAgain: {_renderAgain}, Reset: {_resetProbe}, Probe: {Probe}:\n" + ex);
					break;
				}
			}
			while (!renderingFinished && Probe != null && Probe.enabled && Probe.gameObject.activeInHierarchy && Probe.refreshMode == ReflectionProbeRefreshMode.ViaScripting);
			if (Probe != null && (!renderingFinished || !Probe.enabled || !Probe.gameObject.activeInHierarchy))
			{
				MarkProbeForReset();
			}
			_currentRenderIndex = null;
			if (Probe != null)
			{
				RenderingManager.Instance.Results.ProbeFinishedRendering(this, uniqueId);
			}
			if (renderingFinished && _renderAgain)
			{
				_renderAgain = false;
				StartRender(_renderAgainUniqueId);
			}
		}

		public void RenderToTexture(ReflectionProbeRenderTask task)
		{
			List<GameObject> excludeObjects = null;
			if ((task.excludeTransformIds?.Count ?? 0) > 0)
			{
				excludeObjects = new List<GameObject>();
				RenderSpace space = base.Space;
				for (int i = 0; i < task.excludeTransformIds.Count; i++)
				{
					excludeObjects.Add(space.Transforms[task.excludeTransformIds[i]].gameObject);
				}
			}
			RenderingManager.Instance.AssetIntegrator.EnqueueTask(delegate
			{
				ReflectionProbeRenderer reflectionProbeRenderer = base.ActualTransform.gameObject.AddComponent<ReflectionProbeRenderer>();
				reflectionProbeRenderer.probe = Probe;
				reflectionProbeRenderer.task = task;
				reflectionProbeRenderer.renderable = this;
				RenderTextureDescriptor desc = new RenderTextureDescriptor(task.size, task.size, task.hdr ? GraphicsFormat.R16G16B16A16_SFloat : GraphicsFormat.R8G8B8A8_UNorm, 24, -1)
				{
					useMipMap = true,
					dimension = TextureDimension.Cube,
					autoGenerateMips = false
				};
				RenderTexture targetTexture = (reflectionProbeRenderer.texture = RenderTexture.GetTemporary(desc));
				if (excludeObjects != null)
				{
					reflectionProbeRenderer.previousLayers = new Dictionary<GameObject, int>();
					RenderHelper.SetHiearchyLayer(excludeObjects, LayerMask.NameToLayer("Temp"), reflectionProbeRenderer.previousLayers);
				}
				Probe.timeSlicingMode = UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.NoTimeSlicing;
				reflectionProbeRenderer.renderId = Probe.RenderProbe(targetTexture);
			});
		}
	}
	public class ReflectionProbeSH2Renderable : Renderable
	{
		private static Vector4[] output = new Vector4[9];

		private RenderTexture _convertTexture;

		protected override void Cleanup()
		{
			if (_convertTexture != null)
			{
				UnityEngine.Object.Destroy(_convertTexture);
				_convertTexture = null;
			}
		}

		protected override void Setup(Transform root)
		{
		}

		public ComputeResult Compute(ReflectionProbe reflectionProbe, out RenderSH2 sh2)
		{
			ComputeResult num = SH2Calculator.ComputeFromProbe(reflectionProbe, output, ref _convertTexture);
			if (num == ComputeResult.Computed)
			{
				sh2 = new RenderSH2(ToVector(output[0]), ToVector(output[1]), ToVector(output[2]), ToVector(output[3]), ToVector(output[4]), ToVector(output[5]), ToVector(output[6]), ToVector(output[7]), ToVector(output[8]));
				return num;
			}
			sh2 = default(RenderSH2);
			return num;
			static RenderVector3 ToVector(Vector4 vector)
			{
				return new RenderVector3(vector.x, vector.y, vector.z);
			}
		}
	}
	public abstract class Renderable
	{
		public bool IsDirect => SubTransform != null;

		public int Index { get; internal set; }

		public virtual bool DirectOnly => false;

		public RenderSpace Space { get; private set; }

		public Transform Transform { get; private set; }

		public Transform SubTransform { get; private set; }

		public Transform ActualTransform => SubTransform ?? Transform;

		public void Setup(RenderSpace space, Transform transform, bool direct)
		{
			Space = space;
			Transform = transform;
			if (!direct && !DirectOnly)
			{
				GameObject gameObject = new GameObject("");
				gameObject.transform.SetParent(transform, worldPositionStays: false);
				gameObject.layer = transform.gameObject.layer;
				SubTransform = gameObject.transform;
			}
			Setup(SubTransform ?? Transform);
		}

		public void Remove(bool removingSpace = false)
		{
			Cleanup();
			if (!removingSpace && SubTransform != null)
			{
				UnityEngine.Object.Destroy(SubTransform.gameObject);
				SubTransform = null;
			}
		}

		protected abstract void Setup(Transform root);

		protected abstract void Cleanup();
	}
	public class BillboardRenderBufferRenderer : ParticleBasedPointRenderBufferRenderer<BillboardRenderBufferState>
	{
		private RotationMode rotationMode;

		protected override RotationMode RotationHandling => rotationMode;

		protected override PointRenderBufferAsset ExtractBuffer(ref BillboardRenderBufferState state)
		{
			return RenderingManager.Instance.PointRenderBuffers.GetAsset(state.pointRenderBufferAssetId);
		}

		protected override void ApplyState(ParticleSystem system, ParticleSystemRenderer renderer, ref BillboardRenderBufferState state)
		{
			((Renderer)(object)renderer).sharedMaterial = RenderingManager.Instance.Materials.Materials.GetAsset(state.materialAssetId)?.Material;
			((Renderer)(object)renderer).motionVectorGenerationMode = state.motionVectorMode.ToUnity();
			renderer.minParticleSize = state.minBillboardScreenSize;
			renderer.maxParticleSize = state.maxBillboardScreenSize;
			renderer.allowRoll = state.alignment != BillboardAlignment.Facing;
			switch (state.alignment)
			{
			default:
				renderer.alignment = (ParticleSystemRenderSpace)0;
				renderer.renderMode = (ParticleSystemRenderMode)0;
				rotationMode = RotationMode.EulerAngles;
				break;
			case BillboardAlignment.Facing:
				renderer.alignment = (ParticleSystemRenderSpace)3;
				renderer.renderMode = (ParticleSystemRenderMode)0;
				rotationMode = RotationMode.EulerAngles;
				break;
			case BillboardAlignment.Local:
				renderer.alignment = (ParticleSystemRenderSpace)4;
				renderer.renderMode = (ParticleSystemRenderMode)0;
				rotationMode = RotationMode.VelocityAndRotationForward;
				break;
			case BillboardAlignment.Global:
				renderer.alignment = (ParticleSystemRenderSpace)4;
				renderer.renderMode = (ParticleSystemRenderMode)0;
				rotationMode = RotationMode.VelocityAndRotationForward;
				break;
			case BillboardAlignment.Direction:
				renderer.alignment = (ParticleSystemRenderSpace)0;
				renderer.renderMode = (ParticleSystemRenderMode)1;
				rotationMode = RotationMode.VelocityOnly;
				break;
			}
		}
	}
	public class MeshRenderBufferRenderer : ParticleBasedPointRenderBufferRenderer<MeshRenderBufferState>
	{
		private RotationMode rotationMode;

		protected override RotationMode RotationHandling => rotationMode;

		protected override void ParticleSystemAllocated(ParticleSystem system, ParticleSystemRenderer renderer)
		{
			base.ParticleSystemAllocated(system, renderer);
			renderer.renderMode = (ParticleSystemRenderMode)4;
		}

		protected override void ApplyState(ParticleSystem system, ParticleSystemRenderer renderer, ref MeshRenderBufferState state)
		{
			((Renderer)(object)renderer).sharedMaterial = RenderingManager.Instance.Materials.Materials.GetAsset(state.materialAssetId)?.Material;
			renderer.mesh = RenderingManager.Instance.Meshes.GetAsset(state.meshAssetId)?.Mesh;
			renderer.allowRoll = state.alignment != MeshAlignment.Facing;
			switch (state.alignment)
			{
			default:
				renderer.alignment = (ParticleSystemRenderSpace)0;
				rotationMode = RotationMode.EulerAngles;
				break;
			case MeshAlignment.Facing:
				renderer.alignment = (ParticleSystemRenderSpace)3;
				rotationMode = RotationMode.EulerAngles;
				break;
			case MeshAlignment.Local:
				renderer.alignment = (ParticleSystemRenderSpace)2;
				rotationMode = RotationMode.EulerAngles;
				break;
			case MeshAlignment.Global:
				renderer.alignment = (ParticleSystemRenderSpace)2;
				rotationMode = RotationMode.EulerAngles;
				break;
			}
		}

		protected override PointRenderBufferAsset ExtractBuffer(ref MeshRenderBufferState state)
		{
			return RenderingManager.Instance.PointRenderBuffers.GetAsset(state.pointRenderBufferAssetId);
		}
	}
	public class PointRenderBufferData : IPoolable
	{
		private bool _hasFrameIndexes;

		private Vector3[] _positions;

		private Quaternion[] _rotations;

		private Vector3[] _scales;

		private Color[] _colors;

		private ushort[] _frameIndexes;

		public int Count { get; set; }

		public Span<Vector3> Positions
		{
			get
			{
				if (_positions != null)
				{
					return _positions.AsSpan().Slice(0, Count);
				}
				return default(Span<Vector3>);
			}
		}

		public Span<Quaternion> Rotations
		{
			get
			{
				if (_rotations != null)
				{
					return _rotations.AsSpan().Slice(0, Count);
				}
				return default(Span<Quaternion>);
			}
		}

		public Span<Vector3> Scales
		{
			get
			{
				if (_scales != null)
				{
					return _scales.AsSpan().Slice(0, Count);
				}
				return default(Span<Vector3>);
			}
		}

		public Span<Color> Colors
		{
			get
			{
				if (_colors != null)
				{
					return _colors.AsSpan().Slice(0, Count);
				}
				return default(Span<Color>);
			}
		}

		public Vector2Int FrameGridSize { get; set; }

		public Span<ushort> FrameIndexes
		{
			get
			{
				Span<ushort> result;
				if (_frameIndexes == null || _frameIndexes.Length < Count || !_hasFrameIndexes)
				{
					result = default(Span<ushort>);
					return result;
				}
				result = _frameIndexes.AsSpan();
				return result.Slice(0, Count);
			}
		}

		public void CopyFrom(PointRenderBufferUpload data)
		{
			Span<byte> span = RenderingManager.Instance.SharedMemory.AccessData(data.buffer);
			Count = data.count;
			FrameGridSize = data.frameGridSize.ToUnity();
			_hasFrameIndexes = data.frameIndexesOffset >= 0;
			Span<Vector3> source = MemoryMarshal.Cast<byte, Vector3>(span.Slice(data.positionsOffset)).Slice(0, Count);
			Span<Quaternion> source2 = MemoryMarshal.Cast<byte, Quaternion>(span.Slice(data.rotationsOffset)).Slice(0, Count);
			Span<Vector3> source3 = MemoryMarshal.Cast<byte, Vector3>(span.Slice(data.sizesOffset)).Slice(0, Count);
			Span<Color> source4 = MemoryMarshal.Cast<byte, Color>(span.Slice(data.colorsOffset)).Slice(0, Count);
			Span<ushort> source5 = ((!_hasFrameIndexes) ? default(Span<ushort>) : MemoryMarshal.Cast<byte, ushort>(span.Slice(data.frameIndexesOffset)).Slice(0, Count));
			Copy(ref _positions, source);
			Copy(ref _rotations, source2);
			Copy(ref _scales, source3);
			Copy(ref _colors, source4);
			Copy(ref _frameIndexes, source5);
		}

		private void Copy<T>(ref T[] array, Span<T> source)
		{
			if (!source.IsEmpty)
			{
				if (array == null || array.Length < Count)
				{
					array = new T[Count];
				}
				source.Slice(0, Count).CopyTo(array);
			}
		}

		public void Clean()
		{
			Count = 0;
			FrameGridSize = default(Vector2Int);
		}
	}
	public abstract class ParticleBasedPointRenderBufferRenderer<TState> : ParticleBasedRenderBufferRenderer<PointRenderBufferAsset, PointRenderBufferData, PointRenderBufferUpload, TState> where TState : unmanaged
	{
		protected enum RotationMode
		{
			None,
			EulerAngles,
			VelocityAndRotationForward,
			VelocityAndRotationUp,
			VelocityOnly
		}

		private const int CONVERT_GROUP_SIZE = 4096;

		private TextureSheetAnimationModule textureSheet;

		protected abstract RotationMode RotationHandling { get; }

		protected override void ParticleSystemAllocated(ParticleSystem system, ParticleSystemRenderer renderer)
		{
			//IL_0002: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Unknown result type (might be due to invalid IL or missing references)
			textureSheet = system.textureSheetAnimation;
		}

		protected override void OnSubmitBufferData(BufferSubmission data)
		{
			if (data.gridSize == Vector2Int.one)
			{
				if (((TextureSheetAnimationModule)(ref textureSheet)).enabled)
				{
					((TextureSheetAnimationModule)(ref textureSheet)).enabled = false;
				}
				return;
			}
			if (!((TextureSheetAnimationModule)(ref textureSheet)).enabled)
			{
				((TextureSheetAnimationModule)(ref textureSheet)).enabled = true;
			}
			if (((TextureSheetAnimationModule)(ref textureSheet)).numTilesX != data.gridSize.x)
			{
				((TextureSheetAnimationModule)(ref textureSheet)).numTilesX = data.gridSize.x;
			}
			if (((TextureSheetAnimationModule)(ref textureSheet)).numTilesY != data.gridSize.y)
			{
				((TextureSheetAnimationModule)(ref textureSheet)).numTilesY = data.gridSize.y;
			}
		}

		protected override PointRenderBufferData ExtractData(PointRenderBufferAsset buffer, PointRenderBufferUpload uploadData)
		{
			PointRenderBufferData pointRenderBufferData = MemoryPool.Borrow<PointRenderBufferData>();
			pointRenderBufferData.CopyFrom(uploadData);
			return pointRenderBufferData;
		}

		protected override void AssignFrame(ref Particle particle, ushort frame, int frameCount)
		{
			int num = frameCount - 1;
			((Particle)(ref particle)).startLifetime = num;
			((Particle)(ref particle)).remainingLifetime = Mathf.Max(num - frame, 0.5f);
		}

		protected override BufferSubmission GenerateSubmissionData(PointRenderBufferData data)
		{
			int length = data.Count;
			Vector2Int frameGridSize = data.FrameGridSize;
			Span<Vector3> positions = data.Positions;
			Span<Quaternion> rotations = data.Rotations;
			Span<Vector3> scales = data.Scales;
			Span<Color> colors = data.Colors;
			Span<ushort> frameIndexes = data.FrameIndexes;
			bool hasFrames = !frameIndexes.IsEmpty;
			int frameCount = frameGridSize.x * frameGridSize.y;
			if (positions.Length != length || rotations.Length != length || scales.Length != length || colors.Length != length || (!frameIndexes.IsEmpty && frameIndexes.Length != length))
			{
				return null;
			}
			ParticleBasedRenderBufferRenderer<PointRenderBufferAsset, PointRenderBufferData, PointRenderBufferUpload, TState>.BufferSubmission bufferSubmission = MemoryPool.Borrow<ParticleBasedRenderBufferRenderer<PointRenderBufferAsset, PointRenderBufferData, PointRenderBufferUpload, TState>.BufferSubmission>();
			bufferSubmission.buffer = new NativeArray<Particle>(length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			NativeArray<Particle> targetData = bufferSubmission.buffer;
			RotationMode rotationMode = RotationHandling;
			int toExclusive = MathHelper.CeilToInt((double)length / 4096.0);
			Parallel.For(0, toExclusive, delegate(int g)
			{
				//IL_005a: Unknown result type (might be due to invalid IL or missing references)
				//IL_01c3: Unknown result type (might be due to invalid IL or missing references)
				int num = g * 4096;
				int num2 = Mathf.Min(num + 4096, length);
				Span<Vector3> positions2 = data.Positions;
				Span<Quaternion> rotations2 = data.Rotations;
				Span<Vector3> scales2 = data.Scales;
				Span<Color> colors2 = data.Colors;
				Span<ushort> frameIndexes2 = data.FrameIndexes;
				Particle particle = default(Particle);
				for (int i = num; i < num2; i++)
				{
					((Particle)(ref particle)).position = positions2[i];
					((Particle)(ref particle)).startSize3D = scales2[i];
					((Particle)(ref particle)).startColor = colors2[i];
					Vector3 velocity;
					float angle;
					switch (rotationMode)
					{
					case RotationMode.EulerAngles:
						((Particle)(ref particle)).rotation3D = Quaternion.LookRotation(rotations2[i] * Vector3.forward, rotations2[i] * Vector3.up).eulerAngles;
						break;
					case RotationMode.VelocityOnly:
						((Particle)(ref particle)).velocity = rotations2[i] * Vector3.forward;
						break;
					case RotationMode.VelocityAndRotationForward:
						ComputeVelocityOrientation(in rotations2[i], Vector3.forward, out velocity, out angle);
						((Particle)(ref particle)).velocity = velocity;
						((Particle)(ref particle)).rotation = angle;
						break;
					case RotationMode.VelocityAndRotationUp:
						ComputeVelocityOrientation(in rotations2[i], Vector3.up, out velocity, out angle);
						((Particle)(ref particle)).velocity = velocity;
						((Particle)(ref particle)).rotation = 0f - angle;
						break;
					}
					if (hasFrames)
					{
						AssignFrame(ref particle, frameIndexes2[i], frameCount);
					}
					targetData[i] = particle;
				}
			});
			bufferSubmission.gridSize = (hasFrames ? frameGridSize : Vector2Int.one);
			return bufferSubmission;
		}

		private static void ComputeVelocityOrientation(in Quaternion rotation, in Vector3 targetUp, out Vector3 velocity, out float angle)
		{
			Vector3 vector = rotation * Vector3.forward;
			if (Vector3.Dot(vector, Vector3.forward) >= 0.999999f)
			{
				vector = new Vector3(vector.x + 1E-05f, vector.y, vector.z).normalized;
			}
			Quaternion quaternion = Quaternion.LookRotation(vector, targetUp);
			Vector3 vector2 = rotation * Vector3.up;
			Vector3 vector3 = quaternion * Vector3.up;
			Vector3 rhs = Vector3.Cross(vector2, vector3);
			angle = Vector3.Angle(vector2, vector3);
			float f = Vector3.Dot(vector, rhs);
			if (float.IsNaN(f))
			{
				f = 0f;
			}
			float num = Mathf.Sign(f);
			if (num != 0f)
			{
				angle *= num;
			}
			velocity = vector;
		}
	}
	public abstract class ParticleBasedRenderBufferRenderer<TAsset, TSubmission, TUpdate, TState> : Renderable where TAsset : class, IRenderBufferAsset<TAsset, TUpdate> where TSubmission : class, IPoolable, new() where TUpdate : RenderBufferUpload where TState : unmanaged
	{
		private class QueuedBuffer
		{
			public TAsset buffer;

			public TUpdate update;

			public QueuedBuffer(TAsset buffer, TUpdate update)
			{
				this.buffer = buffer;
				this.update = update;
			}
		}

		protected class BufferSubmission : IPoolable
		{
			public NativeArray<Particle> buffer;

			public Vector2Int gridSize;

			public int ribbonCount;

			public void Clean()
			{
				buffer.Dispose();
				buffer = default(NativeArray<Particle>);
				gridSize = Vector2Int.zero;
				ribbonCount = 0;
			}
		}

		private readonly struct BufferUpdate(Action<TAsset, TUpdate> submit, TAsset buffer, TUpdate update)
		{
			public readonly Action<TAsset, TUpdate> submit = submit;

			public readonly TAsset buffer = buffer;

			public readonly TUpdate update = update;
		}

		private const int MAX_SCHEDULED_SUBMISSIONS = 2;

		private ParticleSystem particleSystem;

		private ParticleSystemRenderer particleRenderer;

		private TAsset _registeredRenderBuffer;

		private bool _lastSubmissionEmpty;

		private static ActionBlock<BufferUpdate> updateProcessor;

		private Action<TAsset, TUpdate> _submitMethod;

		private int _scheduledSubmissions;

		private QueuedBuffer _queuedBuffer;

		static ParticleBasedRenderBufferRenderer()
		{
			updateProcessor = new ActionBlock<BufferUpdate>(delegate(BufferUpdate u)
			{
				try
				{
					u.submit(u.buffer, u.update);
				}
				catch (Exception arg)
				{
					UnityEngine.Debug.LogError($"Exception converting buffer data:\n{arg}");
				}
			}, new ExecutionDataflowBlockOptions
			{
				EnsureOrdered = false,
				MaxDegreeOfParallelism = -1
			});
		}

		protected abstract BufferSubmission GenerateSubmissionData(TSubmission updatingBuffer);

		private void ConvertBufferData(TAsset updatingBuffer, TUpdate uploadData)
		{
			if ((UnityEngine.Object)(object)particleSystem == null || updatingBuffer != _registeredRenderBuffer)
			{
				updatingBuffer.BufferConsumed();
				return;
			}
			TSubmission instance = ExtractData(updatingBuffer, uploadData);
			updatingBuffer.BufferConsumed();
			BufferSubmission bufferSubmission = GenerateSubmissionData(instance);
			MemoryPool.Return(ref instance);
			if (bufferSubmission != null)
			{
				Interlocked.Increment(ref _scheduledSubmissions);
				RenderingManager.Instance.AssetIntegrator.EnqueueParticleProcessing(SubmitBufferData, bufferSubmission);
			}
		}

		private void SubmitBufferData(object data)
		{
			ProcessQueuedBuffer();
			BufferSubmission instance = (BufferSubmission)data;
			if ((UnityEngine.Object)(object)particleSystem != null)
			{
				OnSubmitBufferData(instance);
				particleSystem.SetParticles(instance.buffer);
				RenderingManager.Instance.Stats.ParticlesUploaded(instance.buffer.Length);
			}
			MemoryPool.Return(ref instance);
			if (Interlocked.Decrement(ref _scheduledSubmissions) < 2)
			{
				ProcessQueuedBuffer();
			}
		}

		protected override void Setup(Transform root)
		{
			_submitMethod = ConvertBufferData;
		}

		public void ApplyState(ref TState state)
		{
			//IL_003b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0040: Unknown result type (might be due to invalid IL or missing references)
			if ((UnityEngine.Object)(object)particleSystem == null)
			{
				GameObject gameObject = base.ActualTransform.gameObject;
				particleSystem = gameObject.AddComponent<ParticleSystem>();
				particleRenderer = gameObject.GetComponent<ParticleSystemRenderer>();
				MainModule main = particleSystem.main;
				((MainModule)(ref main)).playOnAwake = false;
				((MainModule)(ref main)).scalingMode = (ParticleSystemScalingMode)0;
				((MainModule)(ref main)).simulationSpace = (ParticleSystemSimulationSpace)0;
				particleRenderer.lengthScale = 1f;
				particleRenderer.velocityScale = 0f;
				((Renderer)(object)particleRenderer).reflectionProbeUsage = ReflectionProbeUsage.BlendProbesAndSkybox;
				ParticleSystemAllocated(particleSystem, particleRenderer);
				particleSystem.Pause();
			}
			ApplyState(particleSystem, particleRenderer, ref state);
			TAsset val = ExtractBuffer(ref state);
			if (val == null)
			{
				particleSystem.Clear();
				UnregisterRenderBuffer();
			}
			else if (_registeredRenderBuffer != val)
			{
				UnregisterRenderBuffer();
				_registeredRenderBuffer = val;
				_registeredRenderBuffer.RegisterListener(OnRenderBufferUpdate);
			}
		}

		private void OnRenderBufferUpdate(TAsset buffer, TUpdate data)
		{
			if (buffer != _registeredRenderBuffer || (data.IsEmpty && _lastSubmissionEmpty))
			{
				buffer.BufferConsumed();
				return;
			}
			_lastSubmissionEmpty = data.IsEmpty;
			GetHashCode();
			if (_scheduledSubmissions >= 2)
			{
				if (_queuedBuffer != null)
				{
					throw new InvalidOperationException("There's already a queued buffer when render buffer update is called");
				}
				_queuedBuffer = new QueuedBuffer(buffer, data);
				if (_scheduledSubmissions < 2)
				{
					ProcessQueuedBuffer();
				}
			}
			else
			{
				updateProcessor.Post(new BufferUpdate(_submitMethod, buffer, data));
			}
		}

		private void ProcessQueuedBuffer()
		{
			QueuedBuffer queuedBuffer = Interlocked.Exchange(ref _queuedBuffer, null);
			if (queuedBuffer != null)
			{
				updateProcessor.Post(new BufferUpdate(_submitMethod, queuedBuffer.buffer, queuedBuffer.update));
			}
		}

		private void UnregisterRenderBuffer()
		{
			if (_registeredRenderBuffer != null)
			{
				_registeredRenderBuffer.UnregisterListener(OnRenderBufferUpdate);
				_registeredRenderBuffer = null;
			}
		}

		protected override void Cleanup()
		{
			if ((UnityEngine.Object)(object)particleSystem != null)
			{
				UnityEngine.Object.Destroy((UnityEngine.Object)(object)particleSystem);
			}
			particleSystem = null;
		}

		protected abstract TAsset ExtractBuffer(ref TState state);

		protected abstract TSubmission ExtractData(TAsset buffer, TUpdate data);

		protected abstract void ParticleSystemAllocated(ParticleSystem system, ParticleSystemRenderer renderer);

		protected abstract void ApplyState(ParticleSystem system, ParticleSystemRenderer renderer, ref TState state);

		protected abstract void OnSubmitBufferData(BufferSubmission data);

		protected abstract void AssignFrame(ref Particle particle, ushort frame, int frameCount);
	}
	public class TrailsRenderBufferData : IPoolable
	{
		private int _trailsCount;

		private int _positionsCount;

		private int _colorsCount;

		private int _sizesCount;

		private TrailOffset[] _trails;

		private Vector3[] _positions;

		private Color[] _colors;

		private float[] _sizes;

		public Span<TrailOffset> Trails
		{
			get
			{
				if (_trails != null)
				{
					return _trails.AsSpan().Slice(0, _trailsCount);
				}
				return default(Span<TrailOffset>);
			}
		}

		public Span<Vector3> TrailPositions
		{
			get
			{
				if (_positions != null)
				{
					return _positions.AsSpan().Slice(0, _positionsCount);
				}
				return default(Span<Vector3>);
			}
		}

		public Span<Color> TrailColors
		{
			get
			{
				if (_colors != null)
				{
					return _colors.AsSpan().Slice(0, _colorsCount);
				}
				return default(Span<Color>);
			}
		}

		public Span<float> TrailSizes
		{
			get
			{
				if (_sizes != null)
				{
					return _sizes.AsSpan().Slice(0, _sizesCount);
				}
				return default(Span<float>);
			}
		}

		public void CopyFrom(TrailRenderBufferUpload data)
		{
			Span<byte> span = RenderingManager.Instance.SharedMemory.AccessData(data.buffer);
			Span<TrailOffset> source = MemoryMarshal.Cast<byte, TrailOffset>(span.Slice(data.trailsOffset)).Slice(0, data.trailsCount);
			Span<Vector3> source2 = MemoryMarshal.Cast<byte, Vector3>(span.Slice(data.positionsOffset)).Slice(0, data.trailPointCount);
			Span<Color> source3 = MemoryMarshal.Cast<byte, Color>(span.Slice(data.colorsOffset)).Slice(0, data.trailPointCount);
			Span<float> source4 = MemoryMarshal.Cast<byte, float>(span.Slice(data.sizesOffset)).Slice(0, data.trailPointCount);
			Copy(ref _trails, ref _trailsCount, source);
			Copy(ref _positions, ref _positionsCount, source2);
			Copy(ref _colors, ref _colorsCount, source3);
			Copy(ref _sizes, ref _sizesCount, source4);
		}

		private void Copy<T>(ref T[] array, ref int count, Span<T> source)
		{
			count = source.Length;
			if (!source.IsEmpty)
			{
				if (array == null || array.Length < count)
				{
					array = new T[count];
				}
				source.CopyTo(array);
			}
		}

		public void Clean()
		{
			_trailsCount = 0;
			_positionsCount = 0;
			_colorsCount = 0;
			_sizesCount = 0;
		}
	}
	public class TrailsRenderBufferRenderer : ParticleBasedRenderBufferRenderer<TrailsRenderBufferAsset, TrailsRenderBufferData, TrailRenderBufferUpload, TrailsRendererState>
	{
		private const int CONVERT_GROUP_SIZE = 4096;

		private TrailModule trailModule;

		protected override TrailsRenderBufferAsset ExtractBuffer(ref TrailsRendererState state)
		{
			return RenderingManager.Instance.TrailsRenderBuffers.GetAsset(state.trailsRenderBufferAssetId);
		}

		protected override void AssignFrame(ref Particle particle, ushort frame, int frameCount)
		{
		}

		protected override TrailsRenderBufferData ExtractData(TrailsRenderBufferAsset buffer, TrailRenderBufferUpload uploadData)
		{
			TrailsRenderBufferData trailsRenderBufferData = MemoryPool.Borrow<TrailsRenderBufferData>();
			trailsRenderBufferData.CopyFrom(uploadData);
			return trailsRenderBufferData;
		}

		protected override BufferSubmission GenerateSubmissionData(TrailsRenderBufferData data)
		{
			Span<TrailOffset> trails = data.Trails;
			ParticleBasedRenderBufferRenderer<TrailsRenderBufferAsset, TrailsRenderBufferData, TrailRenderBufferUpload, TrailsRendererState>.BufferSubmission bufferSubmission = MemoryPool.Borrow<ParticleBasedRenderBufferRenderer<TrailsRenderBufferAsset, TrailsRenderBufferData, TrailRenderBufferUpload, TrailsRendererState>.BufferSubmission>();
			int trailCount = trails.Length;
			int maxTrailLength = 0;
			Span<TrailOffset> span = trails;
			for (int i = 0; i < span.Length; i++)
			{
				TrailOffset trailOffset = span[i];
				maxTrailLength = Mathf.Max(maxTrailLength, trailOffset.count);
			}
			maxTrailLength += 2;
			int num = maxTrailLength * trailCount;
			int trailsPerGroup = Mathf.Max(1, MathHelper.RoundToInt((double)maxTrailLength / 4096.0));
			int toExclusive = MathHelper.CeilToInt(trailCount / trailsPerGroup);
			bufferSubmission.buffer = new NativeArray<Particle>(num, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			NativeArray<Particle> targetData = bufferSubmission.buffer;
			int maxLifetime = num + 1;
			Parallel.For(0, toExclusive, delegate(int g)
			{
				//IL_0002: Unknown result type (might be due to invalid IL or missing references)
				//IL_010d: Unknown result type (might be due to invalid IL or missing references)
				//IL_0147: Unknown result type (might be due to invalid IL or missing references)
				//IL_014c: Unknown result type (might be due to invalid IL or missing references)
				//IL_01bb: Unknown result type (might be due to invalid IL or missing references)
				//IL_0210: Unknown result type (might be due to invalid IL or missing references)
				//IL_0215: Unknown result type (might be due to invalid IL or missing references)
				//IL_022d: Unknown result type (might be due to invalid IL or missing references)
				//IL_0232: Unknown result type (might be due to invalid IL or missing references)
				//IL_0273: Unknown result type (might be due to invalid IL or missing references)
				//IL_0283: Unknown result type (might be due to invalid IL or missing references)
				//IL_0288: Unknown result type (might be due to invalid IL or missing references)
				//IL_02a0: Unknown result type (might be due to invalid IL or missing references)
				//IL_02a5: Unknown result type (might be due to invalid IL or missing references)
				//IL_02e6: Unknown result type (might be due to invalid IL or missing references)
				Particle value = default(Particle);
				Span<TrailOffset> trails2 = data.Trails;
				Span<Vector3> trailPositions = data.TrailPositions;
				Span<Color> trailColors = data.TrailColors;
				Span<float> trailSizes = data.TrailSizes;
				int num2 = g * trailsPerGroup;
				int num3 = Mathf.Min(num2 + trailsPerGroup, trailCount);
				int trailIndex;
				for (trailIndex = num2; trailIndex < num3; trailIndex++)
				{
					int num4 = 0;
					ref TrailOffset reference = ref trails2[trailIndex];
					int idx = num4++;
					int idx2 = num4;
					for (int j = 0; j < reference.count; j++)
					{
						int index = reference.GetIndex(j);
						int num5 = TrailIndexToUnity(num4);
						((Particle)(ref value)).position = trailPositions[index];
						((Particle)(ref value)).startColor = trailColors[index];
						((Particle)(ref value)).startSize = trailSizes[index];
						((Particle)(ref value)).startLifetime = maxLifetime;
						((Particle)(ref value)).remainingLifetime = maxLifetime - num5;
						targetData[num5] = value;
						num4++;
					}
					int idx3 = num4 - 1;
					int index2 = TrailIndexToUnity(idx3);
					Particle val = targetData[index2];
					Vector3 position = ((Particle)(ref val)).position;
					for (int k = reference.count; k < maxTrailLength - 2; k++)
					{
						int num6 = TrailIndexToUnity(num4);
						((Particle)(ref value)).position = position;
						((Particle)(ref value)).startColor = default(Color32);
						((Particle)(ref value)).startSize = 0f;
						((Particle)(ref value)).startLifetime = maxLifetime;
						((Particle)(ref value)).remainingLifetime = maxLifetime - num6;
						targetData[num6] = value;
						num4++;
					}
					int idx4 = num4;
					int index3 = TrailIndexToUnity(idx2);
					int num7 = TrailIndexToUnity(idx);
					int num8 = TrailIndexToUnity(idx4);
					val = targetData[index3];
					((Particle)(ref value)).position = ((Particle)(ref val)).position;
					val = targetData[index3];
					((Particle)(ref value)).startColor = ((Particle)(ref val)).startColor;
					((Particle)(ref value)).startSize = 0f;
					((Particle)(ref value)).startLifetime = maxLifetime;
					((Particle)(ref value)).remainingLifetime = maxLifetime - num7;
					targetData[num7] = value;
					val = targetData[index2];
					((Particle)(ref value)).position = ((Particle)(ref val)).position;
					val = targetData[index2];
					((Particle)(ref value)).startColor = ((Particle)(ref val)).startColor;
					((Particle)(ref value)).startSize = 0f;
					((Particle)(ref value)).startLifetime = maxLifetime;
					((Particle)(ref value)).remainingLifetime = maxLifetime - num8;
					targetData[num8] = value;
				}
			});
			bufferSubmission.gridSize = Vector2Int.one;
			bufferSubmission.ribbonCount = trailCount;
			return bufferSubmission;
			int TrailIndexToUnity(int idx)
			{
				return P_1.trailIndex + idx * trailCount;
			}
		}

		protected override void ParticleSystemAllocated(ParticleSystem system, ParticleSystemRenderer renderer)
		{
			//IL_0009: Unknown result type (might be due to invalid IL or missing references)
			//IL_000e: Unknown result type (might be due to invalid IL or missing references)
			renderer.renderMode = (ParticleSystemRenderMode)5;
			trailModule = system.trails;
			((TrailModule)(ref trailModule)).enabled = true;
			((TrailModule)(ref trailModule)).mode = (ParticleSystemTrailMode)1;
			((TrailModule)(ref trailModule)).ribbonCount = 1;
			((TrailModule)(ref trailModule)).textureMode = (ParticleSystemTrailTextureMode)0;
		}

		protected override void OnSubmitBufferData(BufferSubmission data)
		{
			((TrailModule)(ref trailModule)).ribbonCount = data.ribbonCount;
		}

		protected override void ApplyState(ParticleSystem system, ParticleSystemRenderer renderer, ref TrailsRendererState state)
		{
			//IL_0049: Unknown result type (might be due to invalid IL or missing references)
			renderer.trailMaterial = RenderingManager.Instance.Materials.Materials.GetAsset(state.materialAssetId)?.Material;
			((Renderer)(object)renderer).motionVectorGenerationMode = state.motionVectorMode.ToUnity();
			((TrailModule)(ref trailModule)).textureMode = state.textureMode.ToUnity();
			((TrailModule)(ref trailModule)).generateLightingData = state.generateLightingData;
		}
	}
	public abstract class RenderContextOverride : Renderable
	{
		private RenderingContextHandler _handler;

		private bool _isOverriden;

		private RenderingContext? _overridenContext;

		protected RenderingContext? _registeredContext { get; private set; }

		protected abstract void Override();

		protected abstract void Restore();

		protected void BeginUpdateSetup(RenderingContext targetContext)
		{
			if (_registeredContext != targetContext)
			{
				if (_isOverriden)
				{
					RunRestore();
				}
				UnregisterHandler();
				RenderContextHelper.RegisterRenderContextEvents(targetContext, _handler);
				_registeredContext = targetContext;
			}
		}

		protected void FinishUpdateSetup()
		{
			if (_isOverriden)
			{
				throw new InvalidOperationException("RenderTransform is overriden while being updated. " + $"Current: {RenderContextHelper.CurrentRenderingContext}, Overriden: {_overridenContext}");
			}
			if (_registeredContext.HasValue && RenderContextHelper.CurrentRenderingContext == _registeredContext)
			{
				RunOverride();
			}
		}

		protected override void Setup(Transform root)
		{
			_handler = HandleRenderingContextSwitch;
		}

		protected override void Cleanup()
		{
			UnregisterHandler();
		}

		private void HandleRenderingContextSwitch(RenderingContextStage stage)
		{
			switch (stage)
			{
			case RenderingContextStage.Begin:
				RunOverride();
				break;
			case RenderingContextStage.End:
				if (_isOverriden)
				{
					RunRestore();
				}
				break;
			}
		}

		protected void RunOverride()
		{
			if (_isOverriden)
			{
				throw new Exception("RenderTransform is already overriden!");
			}
			_isOverriden = true;
			_overridenContext = RenderContextHelper.CurrentRenderingContext;
			Override();
		}

		protected void RunRestore()
		{
			if (!_isOverriden)
			{
				throw new Exception("RenderTransform is not overriden");
			}
			Restore();
			_isOverriden = false;
			_overridenContext = null;
		}

		private void UnregisterHandler()
		{
			if (_registeredContext.HasValue)
			{
				RenderContextHelper.UnregisterRenderContextEvents(_registeredContext.Value, _handler);
				_registeredContext = null;
			}
		}
	}
	public class RenderMaterialOverrideRenderable : RenderContextOverride
	{
		private class MaterialOverride
		{
			public int index;

			public Material original;

			public Material replacement;
		}

		private IMeshRenderable targetMesh;

		private List<MaterialOverride> overrides = new List<MaterialOverride>();

		protected override void Override()
		{
			Renderer renderer = targetMesh?.Renderer;
			if (renderer == null)
			{
				return;
			}
			Material[] sharedMaterials = renderer.sharedMaterials;
			foreach (MaterialOverride @override in overrides)
			{
				if (@override.index >= 0 && @override.index < sharedMaterials.Length)
				{
					@override.original = sharedMaterials[@override.index];
					sharedMaterials[@override.index] = @override.replacement;
				}
			}
			renderer.sharedMaterials = sharedMaterials;
		}

		protected override void Restore()
		{
			Renderer renderer = targetMesh?.Renderer;
			if (renderer == null)
			{
				return;
			}
			Material[] sharedMaterials = renderer.sharedMaterials;
			foreach (MaterialOverride @override in overrides)
			{
				if (@override.index >= 0 && @override.index < sharedMaterials.Length)
				{
					sharedMaterials[@override.index] = @override.original;
					@override.original = null;
				}
			}
			renderer.sharedMaterials = sharedMaterials;
		}

		public void ApplyState(ref RenderMaterialOverrideState state, UnmanagedSpan<MaterialOverrideState> materialOverrides)
		{
			BeginUpdateSetup(state.context);
			IMeshRenderable meshRenderable = MeshRendererHelper.GetMeshRenderable(state.packedMeshRendererIndex, base.Space);
			UpdateSetup(meshRenderable, materialOverrides.Slice(0, state.materrialOverrideCount));
			FinishUpdateSetup();
		}

		protected void UpdateSetup(IMeshRenderable renderer, UnmanagedSpan<MaterialOverrideState> newOverrides)
		{
			targetMesh = renderer;
			while (overrides.Count > newOverrides.Length)
			{
				overrides.RemoveAt(overrides.Count - 1);
			}
			while (newOverrides.Length > overrides.Count)
			{
				overrides.Add(new MaterialOverride());
			}
			AssetManager<MaterialAsset> materials = RenderingManager.Instance.Materials.Materials;
			for (int i = 0; i < newOverrides.Length; i++)
			{
				MaterialOverride materialOverride = overrides[i];
				MaterialOverrideState materialOverrideState = newOverrides[i];
				materialOverride.index = materialOverrideState.materialSlotIndex;
				materialOverride.replacement = materials.GetAsset(materialOverrideState.materialAssetId)?.Material ?? RenderingManager.Instance.NullMaterial;
			}
		}
	}
	public class RenderTransformOverrideRenderable : RenderContextOverride
	{
		private static HashSet<SkinnedMeshRenderable> _existing = new HashSet<SkinnedMeshRenderable>();

		private Vector3? _targetPosition;

		private Quaternion? _targetRotation;

		private Vector3? _targetScale;

		private Vector3? _originalPosition;

		private Quaternion? _originalRotation;

		private Vector3? _originalScale;

		private HashSet<SkinnedMeshRenderable> _registeredSkinnedRenderables;

		private bool renderersDirty;

		private List<SkinnedMeshRenderable> _skinnedMeshesToRegister;

		public void ApplyState(ref RenderTransformOverrideState state, UnmanagedSpan<int> skinnedMeshRenderers)
		{
			BeginUpdateSetup(state.context);
			if (state.skinnedMeshRendererCount >= 0)
			{
				if (_skinnedMeshesToRegister == null && state.skinnedMeshRendererCount > 0)
				{
					_skinnedMeshesToRegister = new List<SkinnedMeshRenderable>();
				}
				_skinnedMeshesToRegister?.Clear();
				for (int i = 0; i < state.skinnedMeshRendererCount; i++)
				{
					int num = skinnedMeshRenderers[i];
					if (num >= 0)
					{
						SkinnedMeshRenderable item = base.Space.SkinnedMeshes[num];
						_skinnedMeshesToRegister.Add(item);
					}
				}
				renderersDirty = true;
			}
			if (!base._registeredContext.HasValue)
			{
				ClearRecalcRequests();
				renderersDirty = true;
			}
			_targetPosition = state.PositionOverride?.ToUnity();
			_targetRotation = state.RotationOverride?.ToUnity();
			_targetScale = state.ScaleOverride?.ToUnity();
			FinishUpdateSetup();
		}

		protected override void Override()
		{
			if (renderersDirty)
			{
				if (_registeredSkinnedRenderables != null)
				{
					foreach (SkinnedMeshRenderable registeredSkinnedRenderable in _registeredSkinnedRenderables)
					{
						_existing.Add(registeredSkinnedRenderable);
					}
				}
				if (_skinnedMeshesToRegister != null)
				{
					foreach (SkinnedMeshRenderable item in _skinnedMeshesToRegister)
					{
						if (item != null && !_existing.Remove(item))
						{
							if (_registeredSkinnedRenderables == null)
							{
								_registeredSkinnedRenderables = new HashSet<SkinnedMeshRenderable>();
							}
							item.RequestForceRecalcPerRender(this);
							_registeredSkinnedRenderables.Add(item);
						}
					}
				}
				foreach (SkinnedMeshRenderable item2 in _existing)
				{
					item2.RemoveRequestForceRecalcPerRender(this);
					_registeredSkinnedRenderables.Remove(item2);
				}
				_existing.Clear();
				renderersDirty = false;
			}
			Transform transform = base.Transform;
			if (_targetPosition.HasValue)
			{
				_originalPosition = transform.localPosition;
				transform.localPosition = _targetPosition.Value;
			}
			else
			{
				_originalPosition = null;
			}
			if (_targetRotation.HasValue)
			{
				_originalRotation = transform.localRotation;
				transform.localRotation = _targetRotation.Value;
			}
			else
			{
				_originalRotation = null;
			}
			if (_targetScale.HasValue)
			{
				_originalScale = transform.localScale;
				transform.localScale = _targetScale.Value;
			}
			else
			{
				_originalScale = null;
			}
		}

		protected override void Restore()
		{
			Transform transform = base.Transform;
			if (_originalPosition.HasValue)
			{
				transform.localPosition = _originalPosition.Value;
			}
			if (_originalRotation.HasValue)
			{
				transform.localRotation = _originalRotation.Value;
			}
			if (_originalScale.HasValue)
			{
				transform.localScale = _originalScale.Value;
			}
		}

		protected override void Cleanup()
		{
			base.Cleanup();
			ClearRecalcRequests();
		}

		private void ClearRecalcRequests()
		{
			if (_registeredSkinnedRenderables == null)
			{
				return;
			}
			foreach (SkinnedMeshRenderable registeredSkinnedRenderable in _registeredSkinnedRenderables)
			{
				registeredSkinnedRenderable.RemoveRequestForceRecalcPerRender(this);
			}
			_registeredSkinnedRenderables.Clear();
		}
	}
	public class SkinnedMeshRenderable : Renderable, IMeshRenderable
	{
		private HashSet<Renderable> _forceRecalcRequests;

		public SkinnedMeshRenderer Renderer { get; private set; }

		public Mesh SharedMesh
		{
			set
			{
				Renderer.sharedMesh = value;
				int valueOrDefault = (value?.bindposes?.Length).GetValueOrDefault();
				Transform[] bones = Renderer.bones;
				if (valueOrDefault != ((bones != null) ? bones.Length : 0))
				{
					Renderer.bones = new Transform[valueOrDefault];
				}
			}
		}

		public int LastPropertyBlockCount { get; set; }

		Renderer IMeshRenderable.Renderer => Renderer;

		protected override void Cleanup()
		{
			UnityEngine.Object.Destroy(Renderer);
			Renderer = null;
		}

		protected override void Setup(Transform root)
		{
			GameObject gameObject = root.gameObject;
			Renderer = gameObject.AddComponent<SkinnedMeshRenderer>();
			Renderer.sharedMaterial = RenderingManager.Instance.NullMaterial;
		}

		public void RequestForceRecalcPerRender(Renderable requester)
		{
			if (!(Renderer == null))
			{
				if (_forceRecalcRequests == null)
				{
					_forceRecalcRequests = new HashSet<Renderable>();
				}
				if (_forceRecalcRequests.Count == 0)
				{
					Renderer.forceMatrixRecalculationPerRender = true;
				}
				_forceRecalcRequests.Add(requester);
			}
		}

		public void RemoveRequestForceRecalcPerRender(Renderable requester)
		{
			_forceRecalcRequests.Remove(requester);
			if (!(Renderer == null) && _forceRecalcRequests.Count == 0)
			{
				Renderer.forceMatrixRecalculationPerRender = false;
			}
		}
	}
	internal class SilenceSource : IWaveSource, IReadableAudioSource<byte>, IAudioSource, IDisposable
	{
		public bool CanSeek => false;

		public WaveFormat WaveFormat { get; private set; }

		public long Position
		{
			get
			{
				throw new NotSupportedException();
			}
			set
			{
				throw new NotSupportedException();
			}
		}

		public long Length
		{
			get
			{
				throw new NotSupportedException();
			}
		}

		public void Dispose()
		{
		}

		public int Read(byte[] buffer, int offset, int count)
		{
			Array.Clear(buffer, offset, count);
			return count;
		}

		public SilenceSource(WaveFormat format)
		{
			WaveFormat = format;
		}
	}
	public class RenderingManager : MonoBehaviour
	{
		public const float MAX_PROCESSING_MS = 2f;

		public const float MAX_PARTICLE_PROCESSING_MS = 4f;

		public bool DebugFramePacing;

		public string EditorQueueName;

		public long EditorQueueCapacity;

		public EngineInitProgress Progress;

		public Camera OverlayCamera;

		public CameraInitializer CameraInitializer;

		public KeyboardInput Keyboard;

		public MouseInput Mouse;

		public WindowInput Window;

		public DisplayInput Display;

		public List<InputDriver> InputDrivers;

		public VideoPlaybackManager VideoPlaybackManager;

		public Action<PostProcessingConfig> PostProcessingUpdated;

		private FrameSubmitData _dataWithRenderTasks;

		private Dictionary<int, RenderSpace> _renderSpaces = new Dictionary<int, RenderSpace>();

		private Dictionary<int, LightsBufferRenderer> _lightBuffers = new Dictionary<int, LightsBufferRenderer>();

		private List<int> _spacesToRemove = new List<int>();

		private MessagingManager _primaryMessagingManager;

		private MessagingManager _backgroundMessagingManager;

		private ManualResetEventSlim _processingReady;

		private volatile FrameSubmitData _frameData;

		private PostProcessingConfig _postProcessing;

		private QualityConfig _quality;

		private ResolutionConfig _resolution;

		private FrameStartData _frameStart;

		private int? _maxBackgroundFPS;

		private int? _maxForegroundFPS;

		private bool _useVSync;

		private bool _initReceived;

		private RendererInitData _initData;

		private int _mainProcessId;

		private bool _initFinalized;

		private volatile bool _fatalError;

		private HeadOutput _vrOutput;

		private HeadOutput _screenOutput;

		private bool _shutdown;

		private bool? _lastVRactive;

		private bool _lastFrameDataProcessed = true;

		private bool _lockStepActivated;

		private bool _decoupleActive;

		private int _recoupleFrames;

		private float _decoupleActivationInterval = 1f / 15f;

		private int _recoupleFrameCount = 10;

		private float _decoupledMaxAssetProcessingTime = 0.002f;

		private Stopwatch processingStopwatch = new Stopwatch();

		private Stopwatch readyToFrameStopwatch = new Stopwatch();

		private Stopwatch processedFrameToNextBegin = new Stopwatch();

		public Shader NullShader { get; private set; }

		public Shader InvisibleShader { get; private set; }

		public Material NullMaterial { get; private set; }

		public Material InvisibleMaterial { get; private set; }

		public static bool IsDebug { get; private set; }

		public static RenderingManager Instance { get; private set; }

		public Process? MainProcess { get; private set; }

		private bool HasMainProcessExited
		{
			get
			{
				if (Renderite.Shared.Helper.IsWine || MainProcess == null)
				{
					return !Directory.Exists($"/proc/{_mainProcessId}");
				}
				return MainProcess.HasExited;
			}
		}

		public HeadOutput VROutput => _vrOutput;

		public HeadOutput ScreenOutput => _screenOutput;

		public bool RendererDecoupled
		{
			get
			{
				if (_lockStepActivated)
				{
					return _decoupleActive;
				}
				return true;
			}
		}

		public int LastFrameIndex { get; private set; } = -1;

		public float NearClip { get; private set; } = 0.01f;

		public float FarClip { get; private set; } = 1024f;

		public float DesktopFOV { get; private set; } = 75f;

		public SharedMemoryAccessor SharedMemory { get; private set; }

		public AssetIntegrator AssetIntegrator { get; private set; }

		public PerformanceStats Stats { get; private set; }

		public FrameResultsManager Results { get; set; }

		public AssetManager<MeshAsset> Meshes { get; private set; }

		public AssetManager<ShaderAsset> Shaders { get; private set; }

		public AssetManager<Texture2DAsset> Texture2Ds { get; private set; }

		public AssetManager<Texture3DAsset> Texture3Ds { get; private set; }

		public AssetManager<CubemapAsset> Cubemaps { get; private set; }

		public AssetManager<RenderTextureAsset> RenderTextures { get; private set; }

		public AssetManager<VideoTextureAsset> VideoTextures { get; private set; }

		public AssetManager<DesktopTextureAsset> DesktopTextures { get; private set; }

		public MaterialAssetManager Materials { get; private set; }

		public AssetManager<PointRenderBufferAsset> PointRenderBuffers { get; private set; }

		public AssetManager<TrailsRenderBufferAsset> TrailsRenderBuffers { get; private set; }

		public AssetManager<GaussianSplatAsset> GaussianSplats { get; private set; }

		public InputManager Input { get; private set; }

		public Guid UniqueSessionId { get; private set; }

		private void Awake()
		{
			if (Instance != null)
			{
				throw new InvalidOperationException("Only one RenderingManager can exist");
			}
			IsDebug = Application.isEditor;
			Instance = this;
			if (!GetConnectionParameters(out string queueName, out long queueCapacity))
			{
				UnityEngine.Debug.LogWarning("Could not get queue parameters to connect to");
				Application.Quit(1);
				return;
			}
			try
			{
				UnityEngine.Debug.Log($"Connecting to queue {queueName} (capacity: {queueCapacity}");
				_primaryMessagingManager = new MessagingManager(PackerMemoryPool.Instance);
				_primaryMessagingManager.CommandHandler = HandleRenderCommand;
				_primaryMessagingManager.FailureHandler = HandleMessagingFailure;
				_primaryMessagingManager.WarningHandler = delegate(string str)
				{
					UnityEngine.Debug.LogWarning(str);
				};
				_primaryMessagingManager.Connect(queueName + "Primary", isAuthority: false, queueCapacity);
				_backgroundMessagingManager = new MessagingManager(PackerMemoryPool.Instance);
				_backgroundMessagingManager.CommandHandler = HandleRenderCommand;
				_backgroundMessagingManager.FailureHandler = HandleMessagingFailure;
				_backgroundMessagingManager.WarningHandler = delegate(string str)
				{
					UnityEngine.Debug.LogWarning(str);
				};
				_backgroundMessagingManager.Connect(queueName + "Background", isAuthority: false, queueCapacity);
				Application.targetFrameRate = -1;
				Application.wantsToQuit += OnAppWantsToQuit;
				QualitySettings.vSyncCount = 0;
				NullShader = Shader.Find("BuiltIn/Null");
				InvisibleShader = Shader.Find("BuiltIn/Invisible");
				NullMaterial = new Material(NullShader);
				InvisibleMaterial = new Material(InvisibleShader);
				CameraRenderer.Initialize();
				UnityEngine.Debug.Log("Connected to queue");
				StartCoroutine(RenderTaskProcessor());
			}
			catch (Exception ex)
			{
				UnityEngine.Debug.LogError("Failed to connect to the controller:\n" + ex);
				Application.Quit(2);
			}
		}

		private FrameStartData BeginFrame()
		{
			if (_frameStart == null)
			{
				_frameStart = new FrameStartData();
			}
			_frameStart.lastFrameIndex = LastFrameIndex;
			Stats.UpdateStats(_frameStart);
			Results.CollectResults(_frameStart);
			Input.UpdateState();
			_frameStart.inputs = Input.State;
			return _frameStart;
		}

		private void Update()
		{
			if (_shutdown)
			{
				UnityEngine.Debug.Log("Shutting down");
				Application.Quit();
				return;
			}
			try
			{
				HandleUpdate();
			}
			catch (Exception arg)
			{
				_fatalError = true;
				UnityEngine.Debug.LogError($"FATAL Exception handling update!\n{arg}");
				Application.Quit(4);
			}
		}

		private void RunAssetIntegration()
		{
			if (_lastFrameDataProcessed)
			{
				Stats.RenderedFramesSinceLast = 0;
				Stats.IntegrationProcessingTime = 0f;
				Stats.ExtraParticleProcessingTime = 0f;
				Stats.ProcessedAssetIntegratorTasks = 0;
				Stats.ProcessingHandleWaits = 0;
				Stats.IntegrationHighPriorityTasks = 0;
				Stats.IntegrationTasks = 0;
				Stats.IntegrationRenderTasks = 0;
				Stats.IntegrationParticleTasks = 0;
			}
			processingStopwatch.Restart();
			int num = 0;
			int num2 = 0;
			AssetIntegrator.ProcessDelayedRemovals();
			AssetIntegrator.RunRenderThreadUploads(2.0);
			bool flag = true;
			while (_frameData == null)
			{
				double totalSeconds = processingStopwatch.Elapsed.TotalSeconds;
				if (RendererDecoupled && totalSeconds >= (double)_decoupledMaxAssetProcessingTime && !flag)
				{
					break;
				}
				if (!RendererDecoupled && totalSeconds >= (double)_decoupleActivationInterval)
				{
					_decoupleActive = true;
					_recoupleFrames = 0;
				}
				flag = false;
				if (AssetIntegrator.Process())
				{
					num++;
					continue;
				}
				if (RendererDecoupled)
				{
					break;
				}
				num2++;
				if (_frameData == null && _processingReady.Wait(Mathf.CeilToInt(Mathf.Clamp(MathHelper.FilterInvalid(_decoupleActivationInterval) * 1000f, 0f, 1000f))))
				{
					_processingReady.Reset();
				}
				if (!_fatalError)
				{
					continue;
				}
				return;
			}
			processingStopwatch.Stop();
			Stats.IntegrationProcessingTime += (float)processingStopwatch.Elapsed.TotalSeconds;
			processingStopwatch.Restart();
			while (AssetIntegrator.ProcessParticleQueueTask() && !(4.0 - processingStopwatch.Elapsed.TotalMilliseconds <= 0.0))
			{
			}
			processingStopwatch.Stop();
			Stats.RenderedFramesSinceLast++;
			Stats.ExtraParticleProcessingTime += (float)processingStopwatch.Elapsed.TotalSeconds;
			Stats.ProcessedAssetIntegratorTasks += num;
			Stats.ProcessingHandleWaits += num2;
			Stats.IntegrationHighPriorityTasks += AssetIntegrator.HighPriorityTasks;
			Stats.IntegrationTasks += AssetIntegrator.NormalTasks;
			Stats.IntegrationRenderTasks += AssetIntegrator.RenderThreadTasks;
			Stats.IntegrationParticleTasks += AssetIntegrator.ParticleTasks;
			Stats.FrameBeginToSubmitTime = (float)readyToFrameStopwatch.Elapsed.TotalSeconds;
			Stats.FrameProcessedToNextBeginTime = (float)processedFrameToNextBegin.Elapsed.TotalSeconds;
			if (_frameData == null || !_decoupleActive)
			{
				return;
			}
			if (Stats.FrameBeginToSubmitTime >= _decoupleActivationInterval)
			{
				_recoupleFrames = 0;
				return;
			}
			_recoupleFrames++;
			if (_recoupleFrames >= _recoupleFrameCount)
			{
				_decoupleActive = false;
			}
		}

		private void ProcessConfigUpdates()
		{
			if (_postProcessing != null)
			{
				PostProcessingUpdated?.Invoke(_postProcessing);
				PackerMemoryPool.Instance.Return(_postProcessing);
				_postProcessing = null;
			}
			if (_quality != null)
			{
				ApplyQualityConfig(_quality);
				PackerMemoryPool.Instance.Return(_quality);
				_quality = null;
			}
			if (_resolution != null)
			{
				ApplyResolutionConfig(_resolution);
				PackerMemoryPool.Instance.Return(_resolution);
				_resolution = null;
			}
			UpdateDesktopRendering();
		}

		private bool TryProcessFrame()
		{
			FrameSubmitData frameSubmitData = Interlocked.Exchange(ref _frameData, null);
			if (frameSubmitData != null)
			{
				if (!ProcessFrameData(frameSubmitData))
				{
					return false;
				}
				processedFrameToNextBegin.Restart();
				return true;
			}
			return false;
		}

		private void HandleUpdate()
		{
			if (_initData != null)
			{
				StartCoroutine(HandleInit(_initData));
				_initData = null;
			}
			else if (_initFinalized)
			{
				if (DebugFramePacing)
				{
					UnityEngine.Debug.Log($"{DateTime.Now.ToMillisecondTimeString()} SENDING BEGIN FRAME {LastFrameIndex}");
				}
				Stats.Update();
				if (_lastFrameDataProcessed)
				{
					processedFrameToNextBegin.Stop();
					readyToFrameStopwatch.Restart();
					_primaryMessagingManager.SendCommand(BeginFrame());
				}
				else
				{
					Input.UpdateStateDecoupled();
				}
				RunAssetIntegration();
				if (!_fatalError)
				{
					ProcessConfigUpdates();
					_lastFrameDataProcessed = TryProcessFrame();
				}
			}
		}

		private bool ProcessFrameData(FrameSubmitData data)
		{
			try
			{
				processingStopwatch.Restart();
				if (DebugFramePacing)
				{
					UnityEngine.Debug.Log($"{DateTime.Now.ToMillisecondTimeString()} PROCESSING FRAME {data.frameIndex}");
				}
				HandleFrameUpdate(data);
				processingStopwatch.Stop();
				Stats.FrameUpdateHandleTime = (float)processingStopwatch.Elapsed.TotalSeconds;
				if (DebugFramePacing)
				{
					UnityEngine.Debug.Log($"{DateTime.Now.ToMillisecondTimeString()} PROCESSED FRAME {data.frameIndex}");
				}
				if (data.renderTasks == null)
				{
					PackerMemoryPool.Instance.Return(data);
				}
				else
				{
					if (_dataWithRenderTasks != null)
					{
						throw new Exception("There's an unprocessed data with render tasks");
					}
					_dataWithRenderTasks = data;
				}
			}
			catch (Exception arg)
			{
				_fatalError = true;
				UnityEngine.Debug.LogError($"Exception handling frame update!\n{arg}" + "\nFrameData: " + data);
				Application.Quit(4);
				return false;
			}
			return true;
		}

		private IEnumerator RenderTaskProcessor()
		{
			while (this != null)
			{
				yield return new WaitForEndOfFrame();
				if (_dataWithRenderTasks != null)
				{
					ProcessRenderTasks(_dataWithRenderTasks.renderTasks);
					PackerMemoryPool.Instance.Return(_dataWithRenderTasks);
					_dataWithRenderTasks = null;
				}
			}
		}

		private HeadOutput UpdateVR_Active(bool vrActive)
		{
			HeadOutput headOutput = (vrActive ? _vrOutput : _screenOutput);
			HeadOutput headOutput2 = (vrActive ? _screenOutput : _vrOutput);
			if (headOutput2 != null && headOutput2.gameObject.activeSelf)
			{
				headOutput2.gameObject.SetActive(value: false);
			}
			if (!headOutput.gameObject.activeSelf)
			{
				headOutput.gameObject.SetActive(value: true);
			}
			if (vrActive != _lastVRactive)
			{
				_lastVRactive = vrActive;
				Input.VR_ActiveChanged(vrActive);
				UpdateQualitySettings(vrActive);
			}
			return headOutput;
		}

		private void UpdateQualitySettings(bool vrActive)
		{
			if (_lastVRactive.Value)
			{
				QualitySettings.lodBias = 3.8f;
				QualitySettings.maxQueuedFrames = 0;
			}
			else
			{
				QualitySettings.lodBias = 2f;
				QualitySettings.maxQueuedFrames = 2;
			}
		}

		private void ApplyQualityConfig(QualityConfig config)
		{
			QualitySettings.pixelLightCount = config.perPixelLights;
			QualitySettings.shadowResolution = config.shadowResolution.ToUnity();
			QualitySettings.shadowCascades = config.shadowCascades.ToUnity();
			QualitySettings.shadowDistance = config.shadowDistance;
			QualitySettings.skinWeights = config.skinWeightMode.ToUnity();
		}

		private void ApplyResolutionConfig(ResolutionConfig config)
		{
			if (config.resolution.x != Screen.width || config.resolution.y != Screen.height)
			{
				Screen.SetResolution(config.resolution.x, config.resolution.y, config.fullscreen);
			}
			else
			{
				Screen.fullScreen = config.fullscreen;
			}
			Window.FlagResolutionChanged();
		}

		private void UpdateDesktopRendering()
		{
			if (!(_vrOutput != null))
			{
				int? num = (Window.IsFocused ? _maxForegroundFPS : _maxBackgroundFPS);
				if (num.HasValue)
				{
					Application.targetFrameRate = Math.Max(5, num.Value);
					QualitySettings.vSyncCount = 0;
				}
				else
				{
					Application.targetFrameRate = -1;
					QualitySettings.vSyncCount = (_useVSync ? 1 : 0);
				}
			}
		}

		private void HandleRenderCommand(RendererCommand command, int messageSize)
		{
			if (IsDebug)
			{
				UnityEngine.Debug.Log("Received command: " + command?.ToString() + " - Size: " + messageSize);
			}
			if (command is KeepAlive)
			{
				return;
			}
			if (!_initReceived)
			{
				if (!(command is RendererInitData initData))
				{
					throw new InvalidOperationException("RendererInitData must be the first message");
				}
				_initData = initData;
				_initReceived = true;
			}
			else if (command is RendererShutdown)
			{
				_shutdown = true;
				_processingReady?.Set();
			}
			else if (!(command is SetWindowIcon icon))
			{
				if (!(command is FreeSharedMemoryView freeSharedMemoryView))
				{
					if (!(command is RendererParentWindow rendererParentWindow))
					{
						if (!(command is SetTaskbarProgress progress))
						{
							if (!(command is MeshUploadData meshUploadData))
							{
								if (!(command is MeshUnload meshUnload))
								{
									if (!(command is ShaderUpload shaderUpload))
									{
										if (!(command is ShaderUnload shaderUnload))
										{
											if (!(command is MaterialPropertyIdRequest request))
											{
												if (!(command is MaterialsUpdateBatch batch))
												{
													if (!(command is UnloadMaterial material))
													{
														if (!(command is UnloadMaterialPropertyBlock propertyBlock))
														{
															if (!(command is SetTexture2DFormat setTexture2DFormat))
															{
																if (!(command is SetTexture2DProperties setTexture2DProperties))
																{
																	if (!(command is SetTexture2DData setTexture2DData))
																	{
																		if (!(command is UnloadTexture2D unloadTexture2D))
																		{
																			if (!(command is SetDesktopTextureProperties setDesktopTextureProperties))
																			{
																				if (!(command is UnloadDesktopTexture unloadDesktopTexture))
																				{
																					if (!(command is SetTexture3DFormat setTexture3DFormat))
																					{
																						if (!(command is SetTexture3DProperties setTexture3DProperties))
																						{
																							if (!(command is SetTexture3DData setTexture3DData))
																							{
																								if (!(command is UnloadTexture3D unloadTexture3D))
																								{
																									if (!(command is SetCubemapFormat setCubemapFormat))
																									{
																										if (!(command is SetCubemapProperties setCubemapProperties))
																										{
																											if (!(command is SetCubemapData setCubemapData))
																											{
																												if (!(command is UnloadCubemap unloadCubemap))
																												{
																													if (!(command is SetRenderTextureFormat setRenderTextureFormat))
																													{
																														if (!(command is UnloadRenderTexture unloadRenderTexture))
																														{
																															if (!(command is VideoTextureLoad videoTextureLoad))
																															{
																																if (!(command is VideoTextureUpdate videoTextureUpdate))
																																{
																																	if (!(command is VideoTextureProperties videoTextureProperties))
																																	{
																																		if (!(command is VideoTextureStartAudioTrack videoTextureStartAudioTrack))
																																		{
																																			if (!(command is UnloadVideoTexture unloadVideoTexture))
																																			{
																																				if (!(command is PointRenderBufferUpload pointRenderBufferUpload))
																																				{
																																					if (!(command is PointRenderBufferUnload pointRenderBufferUnload))
																																					{
																																						if (!(command is TrailRenderBufferUpload trailRenderBufferUpload))
																																						{
																																							if (!(command is TrailRenderBufferUnload trailRenderBufferUnload))
																																							{
																																								if (!(command is GaussianSplatUpload gaussianSplatUpload))
																																								{
																																									if (!(command is UnloadGaussianSplat unloadGaussianSplat))
																																									{
																																										if (command is LightsBufferRendererSubmission lightsBufferRendererSubmission)
																																										{
																																											LightsBufferRenderer lightsBufferRenderer = TryGetLightsBuffer(lightsBufferRendererSubmission.lightsBufferUniqueId);
																																											if (lightsBufferRenderer != null)
																																											{
																																												lightsBufferRenderer.HandleSubmission(lightsBufferRendererSubmission);
																																												return;
																																											}
																																											LightsBufferRendererConsumed lightsBufferRendererConsumed = new LightsBufferRendererConsumed();
																																											lightsBufferRendererConsumed.globalUniqueId = lightsBufferRendererSubmission.lightsBufferUniqueId;
																																											SendBufferConsumed(lightsBufferRendererConsumed);
																																											PackerMemoryPool.Instance.Return(lightsBufferRendererSubmission);
																																										}
																																										else if (!_initFinalized)
																																										{
																																											if (!(command is RendererInitProgressUpdate update))
																																											{
																																												if (!(command is RendererInitFinalizeData initFinalize))
																																												{
																																													throw new InvalidOperationException("Invalid message type while waiting for init to finalize: " + command.GetType());
																																												}
																																												HandleInitFinalize(initFinalize);
																																											}
																																											else
																																											{
																																												Progress.UpdateProgress(update);
																																											}
																																										}
																																										else if (!(command is RendererEngineReady engineReady))
																																										{
																																											if (!(command is FrameSubmitData frameSubmitData))
																																											{
																																												if (!(command is PostProcessingConfig postProcessing))
																																												{
																																													if (!(command is QualityConfig quality))
																																													{
																																														if (!(command is ResolutionConfig resolution))
																																														{
																																															if (!(command is DesktopConfig desktopConfig))
																																															{
																																																if (!(command is RenderDecouplingConfig renderDecouplingConfig))
																																																{
																																																	if (!(command is GaussianSplatConfig config))
																																																	{
																																																		throw new InvalidOperationException("Invalid message type: " + command.GetType());
																																																	}
																																																	GaussianSplatRendererManager.ApplyConfig(config);
																																																}
																																																else
																																																{
																																																	_decoupleActivationInterval = renderDecouplingConfig.decoupleActivateInterval;
																																																	_decoupledMaxAssetProcessingTime = renderDecouplingConfig.decoupledMaxAssetProcessingTime;
																																																	_recoupleFrames = renderDecouplingConfig.recoupleFrameCount;
																																																}
																																															}
																																															else
																																															{
																																																_maxBackgroundFPS = desktopConfig.maximumBackgroundFramerate;
																																																_maxForegroundFPS = desktopConfig.maximumForegroundFramerate;
																																																_useVSync = desktopConfig.vSync;
																																																PackerMemoryPool.Instance.Return(desktopConfig);
																																															}
																																														}
																																														else
																																														{
																																															_resolution = resolution;
																																														}
																																													}
																																													else
																																													{
																																														_quality = quality;
																																													}
																																												}
																																												else
																																												{
																																													_postProcessing = postProcessing;
																																												}
																																											}
																																											else
																																											{
																																												readyToFrameStopwatch.Stop();
																																												_frameData = frameSubmitData;
																																												if (DebugFramePacing)
																																												{
																																													UnityEngine.Debug.Log($"{DateTime.Now.ToMillisecondTimeString()} FRAME SUBMISSION RECEIVE: {frameSubmitData.frameIndex}");
																																												}
																																												_processingReady.Set();
																																											}
																																										}
																																										else
																																										{
																																											HandleEngineReady(engineReady);
																																										}
																																									}
																																									else
																																									{
																																										GaussianSplats.GetAsset(unloadGaussianSplat.assetId).Unload();
																																										PackerMemoryPool.Instance.Return(unloadGaussianSplat);
																																									}
																																								}
																																								else
																																								{
																																									GaussianSplats.GetAsset(gaussianSplatUpload.assetId).HandleUpload(gaussianSplatUpload);
																																								}
																																							}
																																							else
																																							{
																																								TrailsRenderBuffers.GetAsset(trailRenderBufferUnload.assetId).HandleUnload(trailRenderBufferUnload);
																																							}
																																						}
																																						else
																																						{
																																							TrailsRenderBuffers.GetAsset(trailRenderBufferUpload.assetId).HandleUpload(trailRenderBufferUpload);
																																						}
																																					}
																																					else
																																					{
																																						PointRenderBuffers.GetAsset(pointRenderBufferUnload.assetId).HandleUnload(pointRenderBufferUnload);
																																					}
																																				}
																																				else
																																				{
																																					PointRenderBuffers.GetAsset(pointRenderBufferUpload.assetId).HandleUpload(pointRenderBufferUpload);
																																				}
																																			}
																																			else
																																			{
																																				VideoTextures.GetAsset(unloadVideoTexture.assetId).Unload();
																																				PackerMemoryPool.Instance.Return(unloadVideoTexture);
																																			}
																																		}
																																		else
																																		{
																																			VideoTextures.GetAsset(videoTextureStartAudioTrack.assetId).Handle(videoTextureStartAudioTrack);
																																		}
																																	}
																																	else
																																	{
																																		VideoTextures.GetAsset(videoTextureProperties.assetId).Handle(videoTextureProperties);
																																	}
																																}
																																else
																																{
																																	VideoTextures.GetAsset(videoTextureUpdate.assetId).Handle(videoTextureUpdate);
																																}
																															}
																															else
																															{
																																VideoTextures.GetAsset(videoTextureLoad.assetId).Handle(videoTextureLoad);
																															}
																														}
																														else
																														{
																															RenderTextures.GetAsset(unloadRenderTexture.assetId).Handle(unloadRenderTexture);
																														}
																													}
																													else
																													{
																														RenderTextures.GetAsset(setRenderTextureFormat.assetId).Handle(setRenderTextureFormat);
																													}
																												}
																												else
																												{
																													Cubemaps.GetAsset(unloadCubemap.assetId).Unload();
																													PackerMemoryPool.Instance.Return(unloadCubemap);
																												}
																											}
																											else
																											{
																												Cubemaps.GetAsset(setCubemapData.assetId).SetData(setCubemapData);
																											}
																										}
																										else
																										{
																											Cubemaps.GetAsset(setCubemapProperties.assetId).SetProperties(setCubemapProperties);
																										}
																									}
																									else
																									{
																										Cubemaps.GetAsset(setCubemapFormat.assetId).SetFormat(setCubemapFormat);
																									}
																								}
																								else
																								{
																									Texture3Ds.GetAsset(unloadTexture3D.assetId).Unload();
																									PackerMemoryPool.Instance.Return(unloadTexture3D);
																								}
																							}
																							else
																							{
																								Texture3Ds.GetAsset(setTexture3DData.assetId).SetData(setTexture3DData);
																							}
																						}
																						else
																						{
																							Texture3Ds.GetAsset(setTexture3DProperties.assetId).SetProperties(setTexture3DProperties);
																						}
																					}
																					else
																					{
																						Texture3Ds.GetAsset(setTexture3DFormat.assetId).SetFormat(setTexture3DFormat);
																					}
																				}
																				else
																				{
																					DesktopTextures.GetAsset(unloadDesktopTexture.assetId).Unload();
																					PackerMemoryPool.Instance.Return(unloadDesktopTexture);
																				}
																			}
																			else
																			{
																				DesktopTextures.GetAsset(setDesktopTextureProperties.assetId).Handle(setDesktopTextureProperties);
																			}
																		}
																		else
																		{
																			Texture2Ds.GetAsset(unloadTexture2D.assetId).Unload();
																			PackerMemoryPool.Instance.Return(unloadTexture2D);
																		}
																	}
																	else
																	{
																		Texture2Ds.GetAsset(setTexture2DData.assetId).SetData(setTexture2DData);
																	}
																}
																else
																{
																	Texture2Ds.GetAsset(setTexture2DProperties.assetId).SetProperties(setTexture2DProperties);
																}
															}
															else
															{
																Texture2Ds.GetAsset(setTexture2DFormat.assetId).SetFormat(setTexture2DFormat);
															}
														}
														else
														{
															Materials.Handle(propertyBlock);
														}
													}
													else
													{
														Materials.Handle(material);
													}
												}
												else
												{
													Materials.Handle(batch);
												}
											}
											else
											{
												HandleMaterialPropertyRequest(request);
											}
										}
										else
										{
											Shaders.GetAsset(shaderUnload.assetId).Handle(shaderUnload);
										}
									}
									else
									{
										Shaders.GetAsset(shaderUpload.assetId).Handle(shaderUpload);
									}
								}
								else
								{
									Meshes.GetAsset(meshUnload.assetId).Handle(meshUnload);
								}
							}
							else
							{
								Meshes.GetAsset(meshUploadData.assetId).Handle(meshUploadData);
							}
						}
						else
						{
							HandleTaskbarProgress(progress);
						}
					}
					else
					{
						bool flag = WindowsNativeHelper.ParentWindowUnderMain(new IntPtr(rendererParentWindow.windowHandle));
						UnityEngine.Debug.Log($"Parenting window: 0x{rendererParentWindow.windowHandle:X} - success: {flag}");
					}
				}
				else
				{
					SharedMemory.ReleaseView(freeSharedMemoryView.bufferId);
					PackerMemoryPool.Instance.Return(freeSharedMemoryView);
				}
			}
			else
			{
				HandleSetIcon(icon);
			}
		}

		private void HandleMaterialPropertyRequest(MaterialPropertyIdRequest request)
		{
			MaterialPropertyIdResult materialPropertyIdResult = new MaterialPropertyIdResult();
			materialPropertyIdResult.requestId = request.requestId;
			for (int i = 0; i < request.propertyNames.Count; i++)
			{
				materialPropertyIdResult.propertyIDs.Add(Shader.PropertyToID(request.propertyNames[i]));
			}
			_backgroundMessagingManager.SendCommand(materialPropertyIdResult);
		}

		private void HandleMessagingFailure(Exception ex)
		{
			_fatalError = true;
			UnityEngine.Debug.LogError("Exception in messaging system:\n" + ex);
			Application.Quit(3);
			_processingReady.Set();
		}

		private IEnumerator HandleInit(RendererInitData initData)
		{
			UniqueSessionId = initData.uniqueSessionId;
			UnityEngine.Debug.Log("UniqueSessionId: " + UniqueSessionId);
			if (!Renderite.Shared.Helper.IsWine)
			{
				WasapiOut val = new WasapiOut(false, (AudioClientShareMode)0, 100, initData.uniqueSessionId, true);
				WaveFormat format = new WaveFormat(val.Device.DeviceFormat.SampleRate, 32, val.Device.DeviceFormat.Channels, (AudioEncoding)3);
				val.Initialize((IWaveSource)(object)new SilenceSource(format));
				UnityEngine.Debug.Log("Initialized dummy WASAPI session");
			}
			if (initData.windowTitle != null)
			{
				if (WindowsNativeHelper.SetWindowTitle(initData.windowTitle))
				{
					UnityEngine.Debug.Log("Set window title to " + initData.windowTitle);
				}
				else
				{
					UnityEngine.Debug.LogWarning("Failed to set window title");
				}
			}
			else
			{
				UnityEngine.Debug.Log("No window title was provided");
			}
			_processingReady = new ManualResetEventSlim(initialState: false);
			SharedMemory = new SharedMemoryAccessor(initData.sharedMemoryPrefix);
			_mainProcessId = initData.mainProcessId;
			if (!Renderite.Shared.Helper.IsWine)
			{
				MainProcess = Process.GetProcessById(_mainProcessId);
			}
			Task.Run((Func<Task?>)MainProcessWatchDog);
			DebugFramePacing = initData.debugFramePacing;
			AssetIntegrator = new AssetIntegrator();
			AssetIntegrator.Initialize(delegate
			{
				_processingReady.Set();
			});
			Results = new FrameResultsManager();
			Stats = new PerformanceStats();
			Input = new InputManager(Mouse, Keyboard, Window, Display, InputDrivers);
			Texture2Ds = new AssetManager<Texture2DAsset>();
			Texture3Ds = new AssetManager<Texture3DAsset>();
			Cubemaps = new AssetManager<CubemapAsset>();
			RenderTextures = new AssetManager<RenderTextureAsset>();
			VideoTextures = new AssetManager<VideoTextureAsset>();
			DesktopTextures = new AssetManager<DesktopTextureAsset>();
			Meshes = new AssetManager<MeshAsset>();
			Shaders = new AssetManager<ShaderAsset>();
			Materials = new MaterialAssetManager();
			PointRenderBuffers = new AssetManager<PointRenderBufferAsset>();
			TrailsRenderBuffers = new AssetManager<TrailsRenderBufferAsset>();
			GaussianSplats = new AssetManager<GaussianSplatAsset>();
			if (initData.setWindowIcon != null)
			{
				UnityEngine.Debug.Log("Setting renderer icon");
				HandleSetIcon(initData.setWindowIcon);
			}
			if (initData.splashScreenOverride != null)
			{
				UnityEngine.Debug.Log("Applying splash screen override");
				Progress.ApplySplashScreenOverride(initData.splashScreenOverride);
			}
			Progress.InitStarted();
			if (initData.outputDevice == HeadOutputDevice.Autodetect)
			{
				yield return AutodetectOutputDevice(initData);
			}
			yield return LoadOutputDevice(initData.outputDevice);
			HeadOutputDevice actualOutputDevice = InitializeHeadOutputs(initData.outputDevice);
			RendererInitResult rendererInitResult = new RendererInitResult();
			rendererInitResult.rendererIdentifier = "Renderite.Renderer.Unity " + Application.version + " (" + Application.unityVersion + ")";
			rendererInitResult.mainWindowHandlePtr = WindowsNativeHelper.MainWindowHandle.ToInt64();
			rendererInitResult.actualOutputDevice = actualOutputDevice;
			rendererInitResult.stereoRenderingMode = ((object)XRSettings.stereoRenderingMode/*cast due to .constrained prefix*/).ToString();
			rendererInitResult.maxTextureSize = SystemInfo.maxTextureSize;
			GraphicsDeviceType graphicsDeviceType = SystemInfo.graphicsDeviceType;
			if (graphicsDeviceType == GraphicsDeviceType.Direct3D11 || graphicsDeviceType == GraphicsDeviceType.OpenGLCore)
			{
				rendererInitResult.isGPUTexturePOTByteAligned = true;
			}
			rendererInitResult.supportedTextureFormats = new List<Renderite.Shared.TextureFormat>();
			foreach (Renderite.Shared.TextureFormat value in Enums.GetValues<Renderite.Shared.TextureFormat>((EnumMemberSelection)0))
			{
				if (graphicsDeviceType == GraphicsDeviceType.Direct3D11)
				{
					if (!value.TryToDX11(ColorProfile.Linear, usingLinearSpace: true).HasValue)
					{
						continue;
					}
				}
				else
				{
					UnityEngine.TextureFormat textureFormat = value.ToUnity(throwOnError: false);
					if (textureFormat < (UnityEngine.TextureFormat)0 || !SystemInfo.SupportsTextureFormat(textureFormat))
					{
						continue;
					}
				}
				rendererInitResult.supportedTextureFormats.Add(value);
			}
			_primaryMessagingManager.SendCommand(rendererInitResult);
		}

		private void HandleInitFinalize(RendererInitFinalizeData initFinalize)
		{
			_initFinalized = true;
		}

		private void HandleEngineReady(RendererEngineReady engineReady)
		{
			Progress.InitCompleted();
			_lockStepActivated = true;
		}

		private void HandleFrameUpdate(FrameSubmitData submitData)
		{
			if (submitData.debugLog)
			{
				UnityEngine.Debug.Log("DEBUG LOG: " + submitData.ToString());
			}
			LastFrameIndex = submitData.frameIndex;
			NearClip = submitData.nearClip;
			FarClip = submitData.farClip;
			DesktopFOV = submitData.desktopFOV;
			RenderSpace renderSpace = null;
			foreach (RenderSpaceUpdate renderSpace2 in submitData.renderSpaces)
			{
				if (!_renderSpaces.TryGetValue(renderSpace2.id, out RenderSpace value))
				{
					value = new GameObject($"RenderSpace: {renderSpace2.id}").AddComponent<RenderSpace>();
					value.Initialize(renderSpace2.id);
					_renderSpaces.Add(renderSpace2.id, value);
				}
				value.HandleUpdate(renderSpace2);
				if (renderSpace2.isActive && !renderSpace2.isOverlay)
				{
					if (renderSpace != null)
					{
						throw new InvalidOperationException($"Trying to set multiple active render spaces. Exiting active: {renderSpace}, second active: {value}");
					}
					renderSpace = value;
				}
			}
			HeadOutput headOutput = UpdateVR_Active(submitData.vrActive);
			if (renderSpace != null)
			{
				headOutput.UpdatePositioning(renderSpace);
			}
			foreach (KeyValuePair<int, RenderSpace> renderSpace3 in _renderSpaces)
			{
				if (renderSpace3.Value.IsActive && renderSpace3.Value.IsOverlay)
				{
					renderSpace3.Value.UpdateOverlayPositioning(headOutput.transform);
				}
			}
			foreach (KeyValuePair<int, RenderSpace> renderSpace4 in _renderSpaces)
			{
				if (renderSpace4.Value.WasUpdated)
				{
					renderSpace4.Value.ClearUpdated();
				}
				else
				{
					_spacesToRemove.Add(renderSpace4.Key);
				}
			}
			foreach (int item in _spacesToRemove)
			{
				_renderSpaces[item].Remove();
				_renderSpaces.Remove(item);
			}
			_spacesToRemove.Clear();
			if (submitData.outputState != null)
			{
				Input.HandleOutputState(submitData.outputState);
			}
		}

		private void ProcessRenderTasks(List<CameraRenderTask> renderTasks)
		{
			RenderingContext? currentRenderingContext = RenderContextHelper.CurrentRenderingContext;
			RenderContextHelper.BeginRenderContext(RenderingContext.RenderToAsset);
			foreach (CameraRenderTask renderTask in renderTasks)
			{
				CameraRenderer.Render(renderTask);
			}
			if (currentRenderingContext.HasValue)
			{
				RenderContextHelper.BeginRenderContext(currentRenderingContext.Value);
			}
			else
			{
				RenderContextHelper.EndCurrentRenderContext();
			}
		}

		private bool GetConnectionParameters(out string queueName, out long queueCapacity)
		{
			if (Application.isEditor)
			{
				queueName = EditorQueueName;
				queueCapacity = EditorQueueCapacity;
				return true;
			}
			string[] commandLineArgs = Environment.GetCommandLineArgs();
			queueName = null;
			queueCapacity = -1L;
			if (commandLineArgs == null || commandLineArgs.Length == 0)
			{
				return false;
			}
			for (int i = 0; i < commandLineArgs.Length; i++)
			{
				string text = commandLineArgs[i];
				int num = i + 1;
				if (num >= commandLineArgs.Length)
				{
					return false;
				}
				if (text.EndsWith("QueueName", StringComparison.InvariantCultureIgnoreCase))
				{
					if (queueName != null)
					{
						return false;
					}
					queueName = commandLineArgs[num];
					i++;
				}
				else if (text.EndsWith("QueueCapacity", StringComparison.InvariantCultureIgnoreCase))
				{
					if (queueCapacity > 0)
					{
						return false;
					}
					if (!long.TryParse(commandLineArgs[num], out queueCapacity))
					{
						return false;
					}
					i++;
				}
				if (queueName != null && queueCapacity > 0)
				{
					return true;
				}
			}
			return false;
		}

		private void ForceCrash()
		{
			Process.GetCurrentProcess().Kill();
		}

		private bool OnAppWantsToQuit()
		{
			UnityEngine.Debug.Log($"AppWantsToQuit. InitStarted: {_initReceived}, InitFinalized: {_initFinalized}, FatalError: {_fatalError}, Shutdown: {_shutdown}");
			UnityEngine.Debug.Log("=================================================================== LOG END ===================================================================");
			if (!_initFinalized)
			{
				return true;
			}
			if (_fatalError || _shutdown)
			{
				if (!Application.isEditor)
				{
					ForceCrash();
				}
				return true;
			}
			_primaryMessagingManager.SendCommand(new RendererShutdownRequest());
			return false;
		}

		public RenderSpace TryGetRenderSpace(int renderSpaceId)
		{
			if (_renderSpaces.TryGetValue(renderSpaceId, out RenderSpace value))
			{
				return value;
			}
			return null;
		}

		public void Register(LightsBufferRenderer renderer)
		{
			if (renderer.GlobalUniqueId < 0)
			{
				throw new ArgumentException("Renderer doesn't have assigned global unique ID");
			}
			lock (_lightBuffers)
			{
				_lightBuffers.Add(renderer.GlobalUniqueId, renderer);
			}
		}

		public void Unregister(LightsBufferRenderer renderer)
		{
			if (renderer.GlobalUniqueId < 0)
			{
				throw new ArgumentException("Renderer doesn't have assigned global unique ID");
			}
			lock (_lightBuffers)
			{
				_lightBuffers.Remove(renderer.GlobalUniqueId);
			}
		}

		public LightsBufferRenderer TryGetLightsBuffer(int uniqueId)
		{
			lock (_lightBuffers)
			{
				if (_lightBuffers.TryGetValue(uniqueId, out LightsBufferRenderer value))
				{
					return value;
				}
				return null;
			}
		}

		public void SendReflectionProbeRenderResult(ReflectionProbeRenderResult result)
		{
			if (result == null)
			{
				throw new ArgumentNullException("result");
			}
			if (result.renderTaskId < 0)
			{
				throw new ArgumentException("renderTaskId was not set");
			}
			_backgroundMessagingManager.SendCommand(result);
		}

		public void SendAssetUpdate(AssetCommand command)
		{
			if (command == null)
			{
				throw new ArgumentNullException("command");
			}
			_backgroundMessagingManager.SendCommand(command);
		}

		public void SendMaterialUpdateResult(MaterialsUpdateBatchResult result)
		{
			if (result == null)
			{
				throw new ArgumentNullException("result");
			}
			if (result.updateBatchId < 0)
			{
				throw new ArgumentException("UpdateBatchId was not initialized");
			}
			_backgroundMessagingManager.SendCommand(result);
		}

		public void SendBufferConsumed(LightsBufferRendererConsumed consumed)
		{
			if (consumed == null)
			{
				throw new ArgumentNullException("consumed");
			}
			_backgroundMessagingManager.SendCommand(consumed);
		}

		private IEnumerator AutodetectOutputDevice(RendererInitData initData)
		{
			List<string> list = new List<string>();
			list.Add("oculus");
			list.Add("openvr");
			list.Add("none");
			if (Process.GetProcessesByName("vrcompositor").Length != 0 && Process.GetProcessesByName("vrmonitor").Length != 0)
			{
				UnityEngine.Debug.Log("Detected SteamVR running, skipping Oculus Runtime initialization.");
				list.Remove("oculus");
			}
			XRSettings.LoadDeviceByName(list.ToArray());
			yield return null;
			XRSettings.enabled = true;
			if (Application.platform == RuntimePlatform.Android)
			{
				if (XRDevice.isPresent)
				{
					initData.outputDevice = HeadOutputDevice.OculusQuest;
				}
				else
				{
					initData.outputDevice = HeadOutputDevice.Screen;
				}
			}
			else if (XRDevice.isPresent)
			{
				if (XRSettings.loadedDeviceName.ToLower().Contains("oculus"))
				{
					initData.outputDevice = HeadOutputDevice.Oculus;
				}
				else
				{
					initData.outputDevice = HeadOutputDevice.SteamVR;
				}
			}
			else
			{
				initData.outputDevice = HeadOutputDevice.Screen;
			}
			UnityEngine.Debug.Log("Autodetected device: " + initData.outputDevice);
		}

		private IEnumerator LoadOutputDevice(HeadOutputDevice device)
		{
			UnityEngine.Debug.Log("Loading output device: " + device);
			switch (device)
			{
			case HeadOutputDevice.Oculus:
				yield return LoadDevice("oculus");
				break;
			case HeadOutputDevice.SteamVR:
			case HeadOutputDevice.WindowsMR:
				yield return LoadDevice("openvr");
				break;
			}
		}

		private IEnumerator LoadDevice(string newDevice)
		{
			UnityEngine.Debug.Log("Loading XR runtime: " + newDevice);
			if (string.Compare(XRSettings.loadedDeviceName, newDevice, ignoreCase: true) != 0)
			{
				XRSettings.LoadDeviceByName(newDevice);
				yield return null;
				XRSettings.enabled = true;
			}
		}

		private HeadOutputDevice InitializeHeadOutputs(HeadOutputDevice device)
		{
			if (device.IsScreenViewSupported())
			{
				HeadOutputDevice device2 = ((device != HeadOutputDevice.Screen360) ? HeadOutputDevice.Screen : device);
				_screenOutput = HeadOutput.GetHeadObject(device2);
				RegisterInputDrivers(_screenOutput.gameObject);
			}
			else
			{
				UnityEngine.Object.Destroy(OverlayCamera.gameObject);
			}
			if (device.IsVR())
			{
				_vrOutput = HeadOutput.GetHeadObject(device);
			}
			if (_vrOutput != null && _screenOutput != null)
			{
				IDriverHeadDevice componentInChildren = _vrOutput.GetComponentInChildren<IDriverHeadDevice>();
				if (componentInChildren != null)
				{
					device = componentInChildren.Device;
					RegisterInputDrivers(_vrOutput.gameObject);
				}
				if (_screenOutput != null)
				{
					_screenOutput.gameObject.SetActive(value: false);
				}
			}
			return device;
		}

		private void RegisterInputDrivers(GameObject root)
		{
			InputDriver[] componentsInChildren = root.GetComponentsInChildren<InputDriver>();
			foreach (InputDriver driver in componentsInChildren)
			{
				Input.RegisterDriver(driver);
			}
		}

		private void HandleSetIcon(SetWindowIcon icon)
		{
			int num = icon.size.x * icon.size.y * 4;
			if (icon.iconData.length != num)
			{
				throw new ArgumentException($"Indicated icon size is {icon.size}, expected {num} bytes for icon data, got: {icon.iconData.length}");
			}
			Span<byte> bgra = SharedMemory.AccessData(icon.iconData);
			bool flag;
			if (!icon.isOverlay)
			{
				flag = WindowIconTools.SetIcon(bgra, icon.size.x, icon.size.y, WindowIconKind.Small);
				flag &= WindowIconTools.SetIcon(bgra, icon.size.x, icon.size.y, WindowIconKind.Big);
			}
			else
			{
				flag = WindowIconTools.SetOverlayIcon(bgra, icon.size.x, icon.size.y, icon.overlayDescription ?? "");
			}
			SetWindowIconResult setWindowIconResult = new SetWindowIconResult();
			setWindowIconResult.success = flag;
			setWindowIconResult.requestId = icon.requestId;
			_backgroundMessagingManager.SendCommand(setWindowIconResult);
		}

		private void HandleTaskbarProgress(SetTaskbarProgress progress)
		{
			WindowIconTools.SetProgress(progress.mode switch
			{
				TaskbarProgressBarMode.None => TaskbarProgressBarState.NoProgress, 
				TaskbarProgressBarMode.Normal => TaskbarProgressBarState.Normal, 
				TaskbarProgressBarMode.Indeterminate => TaskbarProgressBarState.Indeterminate, 
				TaskbarProgressBarMode.Paused => TaskbarProgressBarState.Paused, 
				TaskbarProgressBarMode.Error => TaskbarProgressBarState.Error, 
				_ => throw new ArgumentException($"Invalid mode: {progress.mode}"), 
			}, progress.completed, progress.total);
			PackerMemoryPool.Instance.Return(progress);
		}

		private async Task MainProcessWatchDog()
		{
			while (!_shutdown)
			{
				await Task.Delay(TimeSpan.FromSeconds(5.0));
				if (HasMainProcessExited && !_shutdown)
				{
					UnityEngine.Debug.Log("Main process has exited. Shutting down");
					ForceCrash();
					break;
				}
			}
		}
	}
	public class Camera360 : MonoBehaviour
	{
		private RenderTexture tex;

		public int CubemapSize = -1;

		public Material projectionMaterial;

		public Camera Camera { get; private set; }

		public Camera DisplayCamera { get; private set; }

		public int TotalOutputPixels
		{
			get
			{
				if (RenderTexture.active == null)
				{
					return Screen.width * Screen.height;
				}
				return RenderTexture.active.width * RenderTexture.active.height;
			}
		}

		private void Awake()
		{
			Camera = GetComponent<Camera>();
			if (Camera == null)
			{
				Camera = base.gameObject.AddComponent<Camera>();
			}
			Camera.enabled = false;
			Camera.stereoTargetEye = StereoTargetEyeMask.None;
			GameObject gameObject = new GameObject("Display");
			gameObject.transform.SetParent(base.transform, worldPositionStays: false);
			gameObject.AddComponent<Camera360Display>().SetCamera(this);
			DisplayCamera = gameObject.AddComponent<Camera>();
			DisplayCamera.clearFlags = CameraClearFlags.Depth;
			DisplayCamera.cullingMask = 0;
			DisplayCamera.stereoTargetEye = StereoTargetEyeMask.None;
		}

		public void Render(RenderTexture tex)
		{
			RenderTexture targetTexture = DisplayCamera.targetTexture;
			DisplayCamera.targetTexture = tex;
			DisplayCamera.Render();
			DisplayCamera.targetTexture = targetTexture;
		}

		public void RenderCubemap()
		{
			Quaternion rotation = base.transform.rotation;
			int num = ((CubemapSize == -1) ? Mathf.NextPowerOfTwo((int)Mathf.Sqrt((float)TotalOutputPixels / 6f)) : Mathf.NextPowerOfTwo(CubemapSize));
			if (tex == null || tex.width != num)
			{
				if (tex != null)
				{
					UnityEngine.Object.Destroy(tex);
				}
				tex = new RenderTexture(num, num, 0);
				tex.dimension = TextureDimension.Cube;
				tex.Create();
				projectionMaterial.EnableKeyword("FLIP");
			}
			projectionMaterial.SetTexture("_Cube", tex);
			projectionMaterial.SetMatrix("_Rotation", Matrix4x4.TRS(Vector3.zero, rotation, Vector3.one));
			RenderTexture temporary = RenderTexture.GetTemporary(num, num, 24, tex.format);
			RenderTexture active = RenderTexture.active;
			Camera.fieldOfView = 90f;
			Camera.targetTexture = temporary;
			Camera.transform.eulerAngles = new Vector3(0f, -90f, 0f);
			Camera.Render();
			Graphics.CopyTexture(temporary, 0, 0, tex, 0, 0);
			Camera.transform.eulerAngles = new Vector3(0f, 90f, 0f);
			Camera.Render();
			Graphics.CopyTexture(temporary, 0, 0, tex, 1, 0);
			Camera.transform.eulerAngles = new Vector3(90f, 180f, 0f);
			Camera.Render();
			Graphics.CopyTexture(temporary, 0, 0, tex, 2, 0);
			Camera.transform.eulerAngles = new Vector3(-90f, 180f, 0f);
			Camera.Render();
			Graphics.CopyTexture(temporary, 0, 0, tex, 3, 0);
			Camera.transform.eulerAngles = new Vector3(0f, 180f, 0f);
			Camera.Render();
			Graphics.CopyTexture(temporary, 0, 0, tex, 4, 0);
			Camera.transform.eulerAngles = new Vector3(0f, 0f, 0f);
			Camera.Render();
			Graphics.CopyTexture(temporary, 0, 0, tex, 5, 0);
			RenderTexture.active = active;
			RenderTexture.ReleaseTemporary(temporary);
			Camera.transform.rotation = rotation;
		}
	}
	public class Camera360Display : MonoBehaviour
	{
		private Camera360 camera360;

		public void SetCamera(Camera360 camera360)
		{
			this.camera360 = camera360;
		}

		private void OnRenderImage(RenderTexture src, RenderTexture dest)
		{
			camera360.RenderCubemap();
			Graphics.Blit(null, dest, camera360.projectionMaterial);
		}
	}
	public class CameraController : MonoBehaviour
	{
		public float NearClip;

		public float FarClip;

		public bool DoubleBuffer;

		public bool UseTransformScale;

		public float OrthographicSize;

		public int RenderToDisplay;

		public bool RenderShadows;

		public Camera Camera;

		public RenderTexture Texture;

		public List<GameObject> SelectiveRender = new List<GameObject>();

		public List<GameObject> ExcludeRender = new List<GameObject>();

		private RenderTexture _prevTexture;

		private Rect? _prevRect;

		private ShadowQuality? _prevShadowQuality;

		private Dictionary<GameObject, int> _previousLayers;

		private RenderingContext? _prevContext;

		public void OnPreCull()
		{
			try
			{
				if (!RenderShadows)
				{
					_prevShadowQuality = QualitySettings.shadows;
					QualitySettings.shadows = ShadowQuality.Disable;
				}
				if (SelectiveRender.Count > 0)
				{
					int layer = LayerMask.NameToLayer("Temp");
					if (_previousLayers == null)
					{
						_previousLayers = new Dictionary<GameObject, int>();
					}
					RenderHelper.SetHiearchyLayer(SelectiveRender, layer, _previousLayers);
					RenderHelper.RestoreHiearachyLayer(ExcludeRender, _previousLayers);
				}
				else if (ExcludeRender.Count > 0)
				{
					int layer2 = LayerMask.NameToLayer("Temp");
					if (_previousLayers == null)
					{
						_previousLayers = new Dictionary<GameObject, int>();
					}
					RenderHelper.SetHiearchyLayer(ExcludeRender, layer2, _previousLayers);
				}
				_prevContext = RenderContextHelper.CurrentRenderingContext;
				RenderContextHelper.BeginRenderContext(RenderingContext.Camera);
			}
			catch (Exception ex)
			{
				UnityEngine.Debug.LogError("Exception in Camera OnPreCull\n" + ex);
			}
		}

		public void OnPreRender()
		{
			try
			{
				float num;
				if (UseTransformScale)
				{
					Vector3 lossyScale = Camera.transform.lossyScale;
					num = (lossyScale.x + lossyScale.y + lossyScale.z) * (1f / 3f);
				}
				else
				{
					num = 1f;
				}
				if (float.IsNaN(num))
				{
					num = 0f;
				}
				num = Mathf.Clamp(num, 1E-05f, 1000000f);
				float value = OrthographicSize * num;
				float value2 = NearClip * num;
				float value3 = FarClip * num;
				value = Mathf.Clamp(value, 1E-06f, 1000000f);
				value2 = Mathf.Clamp(value2, 0.0001f, 1000000f);
				value3 = Mathf.Clamp(value3, Mathf.Max(0.0001f, value2 + 0.0001f), 1000000f);
				Camera.orthographicSize = value;
				Camera.nearClipPlane = value2;
				Camera.farClipPlane = value3;
				if (Texture != null)
				{
					Camera.targetTexture = Texture;
				}
				if (DoubleBuffer && !(Camera.targetTexture == null))
				{
					RenderTextureDescriptor descriptor = Camera.targetTexture.descriptor;
					if (Camera.rect != new Rect(0f, 0f, 1f, 1f))
					{
						descriptor.height = (int)(Camera.rect.height * (float)descriptor.height);
						descriptor.width = (int)(Camera.rect.width * (float)descriptor.width);
						_prevRect = Camera.rect;
						Camera.rect = new Rect(0f, 0f, 1f, 1f);
					}
					RenderTexture temporary = RenderTexture.GetTemporary(descriptor);
					_prevTexture = Camera.targetTexture;
					Camera.targetTexture = temporary;
				}
			}
			catch (Exception ex)
			{
				UnityEngine.Debug.LogError("Exception in Camera OnPreRender\n" + ex);
			}
		}

		public void OnPostRender()
		{
			try
			{
				RenderingManager.Instance.Stats.CameraRendered();
				if (_prevShadowQuality.HasValue)
				{
					QualitySettings.shadows = _prevShadowQuality.Value;
					_prevShadowQuality = null;
				}
				if (_previousLayers != null && _previousLayers.Count > 0)
				{
					RenderHelper.RestoreLayers(_previousLayers);
					_previousLayers.Clear();
				}
				if (_prevContext.HasValue)
				{
					RenderContextHelper.BeginRenderContext(_prevContext.Value);
				}
				if (DoubleBuffer && !(Camera.targetTexture == null))
				{
					if (_prevRect.HasValue)
					{
						Graphics.CopyTexture(Camera.targetTexture, 0, 0, 0, 0, Camera.targetTexture.width, Camera.targetTexture.height, _prevTexture, 0, 0, (int)(_prevRect.Value.x * (float)_prevTexture.width), (int)(_prevRect.Value.y * (float)_prevTexture.height));
						Camera.rect = _prevRect.Value;
						_prevRect = null;
					}
					else
					{
						Graphics.CopyTexture(Camera.targetTexture, _prevTexture);
					}
					RenderTexture targetTexture = Camera.targetTexture;
					Camera.targetTexture = _prevTexture;
					_prevTexture = null;
					RenderTexture.ReleaseTemporary(targetTexture);
				}
			}
			catch (Exception ex)
			{
				UnityEngine.Debug.LogError("Exception in Camera OnPreCull\n" + ex);
			}
		}
	}
	public abstract class CameraInitializer : MonoBehaviour
	{
		public void RegisterCamera(Camera camera)
		{
			camera.gameObject.AddComponent<ShaderCameraProperties>();
		}

		public void CleanupCamera(Camera camera)
		{
			UnityEngine.Object.Destroy(camera.gameObject.GetComponent<ShaderCameraProperties>());
		}

		public void SetupCamera(Camera camera, CameraSettings settings)
		{
			camera.backgroundColor = Color.black;
			camera.cullingMask = ~LayerMask.GetMask("Hidden", "Overlay");
			if (settings.SetupPostProcessing)
			{
				camera.allowHDR = true;
				SetupPostprocessing(camera, settings);
			}
		}

		public abstract void SetupPostprocessing(Camera camera, CameraSettings settings);

		public abstract void RemovePostProcessing(Camera camera);
	}
	public class CameraRenderer
	{
		private static bool _initialized;

		private static Camera360 camera360;

		private static Camera camera;

		private static int _privateLayerMask;

		private static int _hiddenLayerMask;

		private static Dictionary<GameObject, int> previousLayers = new Dictionary<GameObject, int>();

		private static List<GameObject> renderObjects = new List<GameObject>();

		private static List<GameObject> excludeObjects = new List<GameObject>();

		public static void Initialize()
		{
			if (_initialized)
			{
				throw new InvalidOperationException("CameraRenderer is already initialized");
			}
			_initialized = true;
			GameObject gameObject = new GameObject("CaptureCam");
			GameObject gameObject2 = new GameObject("CaptureCam360");
			gameObject.tag = "CaptureCamera";
			gameObject2.tag = "CaptureCamera";
			camera = gameObject.AddComponent<Camera>();
			gameObject.AddComponent<ShaderCameraProperties>();
			camera.stereoTargetEye = StereoTargetEyeMask.None;
			camera.enabled = false;
			camera.nearClipPlane = 0.05f;
			CameraSettings settings = new CameraSettings
			{
				IsSingleCapture = true,
				SetupPostProcessing = true
			};
			RenderingManager.Instance.CameraInitializer.SetupCamera(camera, settings);
			camera360 = gameObject2.AddComponent<Camera360>();
			camera360.DisplayCamera.enabled = false;
			camera360.Camera.nearClipPlane = 0.05f;
			camera360.projectionMaterial = Resources.Load<Material>("EquirectangularProjection");
			RenderingManager.Instance.CameraInitializer.SetupCamera(camera360.Camera, settings);
			camera360.Camera.gameObject.AddComponent<ShaderCameraProperties>();
			_privateLayerMask = ~LayerMask.GetMask("Private");
			_hiddenLayerMask = ~LayerMask.GetMask("Hidden", "Overlay");
		}

		public unsafe static void Render(CameraRenderTask task)
		{
			Span<byte> destination = RenderingManager.Instance.SharedMemory.AccessData(task.resultData);
			RenderSpace renderSpace = RenderingManager.Instance.TryGetRenderSpace(task.renderSpaceId);
			if (renderSpace == null || !renderSpace.IsActive)
			{
				destination.Clear();
				return;
			}
			Texture2D texture2D = new Texture2D(task.parameters.resolution.x, task.parameters.resolution.y, task.parameters.textureFormat.ToUnity(), mipChain: false);
			RenderTexture temporary = RenderTexture.GetTemporary(task.parameters.resolution.x, task.parameters.resolution.y, 24, RenderTextureFormat.ARGB32);
			RenderTexture active = RenderTexture.active;
			int num = LayerMask.NameToLayer("Temp");
			int num2 = 1 << num;
			if (task.excludeRenderList != null)
			{
				foreach (int excludeRender in task.excludeRenderList)
				{
					GameObject gameObject = renderSpace.Transforms[excludeRender].gameObject;
					excludeObjects.Add(gameObject);
				}
			}
			if (task.onlyRenderList != null)
			{
				foreach (int onlyRender in task.onlyRenderList)
				{
					GameObject gameObject2 = renderSpace.Transforms[onlyRender].gameObject;
					renderObjects.Add(gameObject2);
				}
			}
			if (renderObjects.Count > 0)
			{
				RenderHelper.SetHiearchyLayer(renderObjects, num, previousLayers);
				RenderHelper.RestoreHiearachyLayer(excludeObjects, previousLayers);
			}
			else if (excludeObjects.Count > 0)
			{
				RenderHelper.SetHiearchyLayer(excludeObjects, num, previousLayers);
			}
			CameraSettings settings = new CameraSettings
			{
				IsPrimary = false,
				IsSingleCapture = true,
				IsVR = false,
				MotionBlur = false,
				SetupPostProcessing = task.parameters.postProcessing,
				ScreenSpaceReflection = task.parameters.screenSpaceReflections
			};
			if (task.parameters.fov >= 180f)
			{
				if (renderObjects.Count > 0)
				{
					camera360.Camera.cullingMask = num2 & _hiddenLayerMask;
				}
				else
				{
					camera360.Camera.cullingMask = ~num2 & _hiddenLayerMask;
					if (!task.parameters.renderPrivateUI)
					{
						camera360.Camera.cullingMask &= _privateLayerMask;
					}
				}
				RenderingManager.Instance.CameraInitializer.SetupPostprocessing(camera360.Camera, settings);
				camera360.transform.position = task.position.ToUnity();
				camera360.transform.rotation = task.rotation.ToUnity();
				camera360.Camera.clearFlags = task.parameters.clearMode.ToUnity();
				camera360.Camera.backgroundColor = task.parameters.clearColor.ToUnity();
				camera360.Camera.nearClipPlane = task.parameters.nearClip;
				camera360.Camera.farClipPlane = task.parameters.farClip;
				camera360.Render(temporary);
			}
			else
			{
				if (renderObjects.Count > 0)
				{
					camera.cullingMask = num2 & _hiddenLayerMask;
				}
				else
				{
					camera.cullingMask = ~num2 & _hiddenLayerMask;
					if (!task.parameters.renderPrivateUI)
					{
						camera.cullingMask &= _privateLayerMask;
					}
				}
				RenderingManager.Instance.CameraInitializer.SetupPostprocessing(camera, settings);
				camera.transform.position = task.position.ToUnity();
				camera.transform.rotation = task.rotation.ToUnity();
				camera.clearFlags = task.parameters.clearMode.ToUnity();
				camera.backgroundColor = task.parameters.clearColor.ToUnity();
				camera.nearClipPlane = task.parameters.nearClip;
				camera.farClipPlane = task.parameters.farClip;
				camera.targetTexture = temporary;
				camera.fieldOfView = task.parameters.fov;
				camera.orthographicSize = task.parameters.orthographicSize;
				camera.orthographic = task.parameters.projection == CameraProjection.Orthographic;
				camera.Render();
			}
			if (renderObjects.Count > 0)
			{
				RenderHelper.RestoreHiearachyLayer(renderObjects, previousLayers);
			}
			else if (excludeObjects.Count > 0)
			{
				RenderHelper.RestoreHiearachyLayer(excludeObjects, previousLayers);
			}
			previousLayers.Clear();
			renderObjects.Clear();
			excludeObjects.Clear();
			RenderTexture.active = temporary;
			texture2D.ReadPixels(new Rect(0f, 0f, task.parameters.resolution.x, task.parameters.resolution.y), 0, 0, recalculateMipMaps: false);
			RenderTexture.active = active;
			RenderTexture.ReleaseTemporary(temporary);
			if (RenderingManager.IsDebug)
			{
				texture2D.GetRawTextureData().CopyTo(destination);
			}
			else
			{
				using NativeArray<byte> nativeArray = texture2D.GetRawTextureData<byte>();
				new Span<byte>(nativeArray.GetUnsafeReadOnlyPtr(), nativeArray.Length).CopyTo(destination);
			}
			UnityEngine.Object.Destroy(texture2D);
		}
	}
	public class CameraSettings
	{
		public bool SetupPostProcessing;

		public bool MotionBlur;

		public bool ScreenSpaceReflection;

		public bool IsPrimary;

		public bool IsVR;

		public bool IsSingleCapture;
	}
	public class BufferSorter : IDisposable
	{
		private class Kernels
		{
			public int Sort { get; private set; }

			public int PadBuffer { get; private set; }

			public int OverwriteAndTruncate { get; private set; }

			public int CopyBuffer { get; private set; }

			public Kernels(ComputeShader cs)
			{
				Sort = cs.FindKernel("BitonicSort");
				PadBuffer = cs.FindKernel("PadBuffer");
				OverwriteAndTruncate = cs.FindKernel("OverwriteAndTruncate");
				CopyBuffer = cs.FindKernel("CopyBuffer");
			}
		}

		private static class Properties
		{
			public static int Block { get; private set; } = Shader.PropertyToID("_Block");

			public static int Dimension { get; private set; } = Shader.PropertyToID("_Dimension");

			public static int Count { get; private set; } = Shader.PropertyToID("_Count");

			public static int Reverse { get; private set; } = Shader.PropertyToID("_Reverse");

			public static int NextPowerOfTwo { get; private set; } = Shader.PropertyToID("_NextPowerOfTwo");

			public static int KeysBuffer { get; private set; } = Shader.PropertyToID("_Keys");

			public static int ValuesBuffer { get; private set; } = Shader.PropertyToID("_Values");

			public static int ExternalValuesBuffer { get; private set; } = Shader.PropertyToID("_ExternalValues");

			public static int ExternalKeysBuffer { get; private set; } = Shader.PropertyToID("_ExternalKeys");

			public static int FromBuffer { get; private set; } = Shader.PropertyToID("_From");

			public static int ToBuffer { get; private set; } = Shader.PropertyToID("_To");
		}

		private static class Util
		{
			public const int GROUP_SIZE = 256;

			public const int MAX_DIM_GROUPS = 1024;

			public const int MAX_DIM_THREADS = 262144;

			public static void CalculateWorkSize(int length, out int x, out int y, out int z)
			{
				if (length <= 262144)
				{
					x = (length - 1) / 256 + 1;
					y = (z = 1);
				}
				else
				{
					x = 1024;
					y = (length - 1) / 262144 + 1;
					z = 1;
				}
			}
		}

		private int _currentDim = -1;

		private int _currentBlock = -1;

		private readonly Kernels m_kernels;

		private readonly ComputeShader m_computeShader;

		private ComputeBuffer m_keysBuffer;

		private ComputeBuffer m_valuesBuffer;

		private ComputeBuffer m_paddingBuffer;

		private readonly int[] m_paddingInput = new int[2];

		public int OriginalCount { get; private set; }

		public int PaddedCount { get; private set; }

		public bool IsSortRunning => _currentDim >= 0;

		public BufferSorter(ComputeShader computeShader, int length)
		{
			m_computeShader = computeShader;
			m_kernels = new Kernels(m_computeShader);
			OriginalCount = length;
			PaddedCount = Mathf.NextPowerOfTwo(OriginalCount);
			m_paddingBuffer = new ComputeBuffer(2, 4);
			m_keysBuffer = new ComputeBuffer(PaddedCount, 4);
			m_valuesBuffer = new ComputeBuffer(PaddedCount, 4);
			m_valuesBuffer.SetCounterValue(0u);
		}

		~BufferSorter()
		{
			Dispose();
		}

		public void Dispose()
		{
			m_keysBuffer?.Dispose();
			m_valuesBuffer?.Dispose();
			m_paddingBuffer?.Dispose();
		}

		public bool RunSortChunk(CommandBuffer cmd, ComputeBuffer values, ComputeBuffer keys, ref long? availableSortOps, bool reverse = false)
		{
			if (!IsSortRunning)
			{
				InitSort(cmd, values, keys, reverse);
				if (availableSortOps.HasValue)
				{
					availableSortOps -= PaddedCount;
				}
			}
			if (availableSortOps.HasValue && availableSortOps <= 0)
			{
				return false;
			}
			cmd.SetComputeIntParam(m_computeShader, Properties.Count, PaddedCount);
			Util.CalculateWorkSize(PaddedCount, out var x, out var y, out var z);
			while (!availableSortOps.HasValue || availableSortOps > 0)
			{
				if (availableSortOps.HasValue)
				{
					availableSortOps -= PaddedCount;
				}
				if (PerformSortStep(cmd, x, y, z))
				{
					CopyResults(cmd, keys);
					_currentDim = -1;
					_currentBlock = -1;
					return true;
				}
			}
			return false;
		}

		private bool PerformSortStep(CommandBuffer cmd, int x, int y, int z)
		{
			cmd.SetComputeIntParam(m_computeShader, Properties.Dimension, _currentDim);
			cmd.SetComputeIntParam(m_computeShader, Properties.Block, _currentBlock);
			cmd.SetComputeBufferParam(m_computeShader, m_kernels.Sort, Properties.KeysBuffer, m_keysBuffer);
			cmd.SetComputeBufferParam(m_computeShader, m_kernels.Sort, Properties.ValuesBuffer, m_valuesBuffer);
			cmd.DispatchCompute(m_computeShader, m_kernels.Sort, x, y, z);
			_currentBlock >>= 1;
			if (_currentBlock > 0)
			{
				return false;
			}
			_currentDim <<= 1;
			if (_currentDim > PaddedCount)
			{
				return true;
			}
			InitBlock();
			return false;
		}

		private void InitSort(CommandBuffer cmd, ComputeBuffer values, ComputeBuffer keys, bool reverse = false)
		{
			cmd.SetComputeIntParam(m_computeShader, Properties.Count, OriginalCount);
			cmd.SetComputeIntParam(m_computeShader, Properties.NextPowerOfTwo, PaddedCount);
			cmd.SetComputeIntParam(m_computeShader, Properties.Reverse, reverse ? 1 : 0);
			cmd.SetComputeBufferParam(m_computeShader, m_kernels.PadBuffer, Properties.ExternalValuesBuffer, values);
			cmd.SetComputeBufferParam(m_computeShader, m_kernels.PadBuffer, Properties.ValuesBuffer, m_valuesBuffer);
			cmd.SetComputeBufferParam(m_computeShader, m_kernels.PadBuffer, Properties.KeysBuffer, m_keysBuffer);
			cmd.DispatchCompute(m_computeShader, m_kernels.PadBuffer, Mathf.CeilToInt((float)PaddedCount / 256f), 1, 1);
			_currentDim = 2;
			InitBlock();
		}

		private void CopyResults(CommandBuffer cmd, ComputeBuffer keys)
		{
			cmd.SetComputeBufferParam(m_computeShader, m_kernels.OverwriteAndTruncate, Properties.KeysBuffer, m_keysBuffer);
			cmd.SetComputeBufferParam(m_computeShader, m_kernels.OverwriteAndTruncate, Properties.ExternalKeysBuffer, keys);
			cmd.DispatchCompute(m_computeShader, m_kernels.OverwriteAndTruncate, Mathf.CeilToInt((float)OriginalCount / 256f), 1, 1);
		}

		private void InitBlock()
		{
			_currentBlock = _currentDim >> 1;
		}
	}
	internal struct SplatViewData
	{
		private Vector4 pos;

		private Vector2 axis1;

		private Vector2 axis2;

		private uint color_a;

		private uint color_b;
	}
	public class GaussianSplatRenderer : MonoBehaviour
	{
		private class CameraSortData : IDisposable
		{
			public BufferSorter sorter;

			public ComputeBuffer orderBuffer;

			public int lastFullSortFrame;

			public void Dispose()
			{
				sorter.Dispose();
				orderBuffer.Dispose();
			}
		}

		private GaussianSplatAsset asset;

		public float SplatScale = 1f;

		[Range(0f, 3f)]
		public int SHOrder = 3;

		public float OpacityScale = 1f;

		public bool SHOnly;

		private int lastSplatCount;

		private ComputeBuffer splatViewData;

		private ComputeBuffer distancesBuffer;

		private Dictionary<Camera, CameraSortData> sortData;

		public GaussianSplatAsset Asset
		{
			get
			{
				return asset;
			}
			set
			{
				asset = value;
				if (lastSplatCount != SplatCount)
				{
					InitBuffers();
					lastSplatCount = SplatCount;
				}
			}
		}

		public bool IsAssetReady
		{
			get
			{
				if (Asset != null)
				{
					return Asset.IsLoaded;
				}
				return false;
			}
		}

		public bool IsValidToRender
		{
			get
			{
				if (IsAssetReady)
				{
					return SplatCount == lastSplatCount;
				}
				return false;
			}
		}

		public int SplatCount => Asset?.SplatCount ?? 0;

		public ComputeBuffer SplatViewData => splatViewData;

		public ComputeBuffer DistanceBuffer => distancesBuffer;

		private void OnDestroy()
		{
			Cleanup();
		}

		private unsafe void InitBuffers()
		{
			Cleanup();
			if (IsAssetReady)
			{
				sortData = new Dictionary<Camera, CameraSortData>();
				splatViewData = new ComputeBuffer(SplatCount * 2, sizeof(SplatViewData));
				distancesBuffer = new ComputeBuffer(SplatCount, 4);
				GaussianSplatRendererManager.RegisterRenderer(this);
			}
		}

		public void AssignDataBuffers(CommandBuffer cmd, ComputeShader compute, int kernelID)
		{
			Asset.AssignDataBuffers(cmd, compute, kernelID);
		}

		public int GetLastFullSortFrame(Camera camera)
		{
			return GetCameraSortData(camera).lastFullSortFrame;
		}

		public ComputeBuffer GetOrderBuffer(Camera camera, out bool initSort)
		{
			CameraSortData cameraSortData = GetCameraSortData(camera);
			initSort = !cameraSortData.sorter.IsSortRunning;
			return cameraSortData.orderBuffer;
		}

		private CameraSortData GetCameraSortData(Camera camera)
		{
			if (!sortData.TryGetValue(camera, out CameraSortData value))
			{
				value = new CameraSortData();
				value.sorter = GaussianSplatRendererManager.AllocateSorter(SplatCount);
				value.orderBuffer = new ComputeBuffer(SplatCount, 4);
				sortData.Add(camera, value);
			}
			return value;
		}

		public void CameraRemoved(Camera camera)
		{
			if (sortData.TryGetValue(camera, out CameraSortData value))
			{
				value.Dispose();
				sortData.Remove(camera);
			}
		}

		public void RunSortChunk(CommandBuffer cmd, Camera camera, ref long? availableSortOps)
		{
			CameraSortData cameraSortData = GetCameraSortData(camera);
			if (cameraSortData.sorter.RunSortChunk(cmd, distancesBuffer, cameraSortData.orderBuffer, ref availableSortOps, reverse: true))
			{
				cameraSortData.lastFullSortFrame = Time.frameCount;
			}
		}

		private unsafe static void SetData<T>(ComputeBuffer buffer, Span<T> data) where T : unmanaged
		{
			fixed (T* dataPointer = data)
			{
				NativeArray<T> data2 = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(dataPointer, data.Length, Allocator.None);
				buffer.SetData(data2);
			}
		}

		private void Cleanup()
		{
			GaussianSplatRendererManager.UnregisterRenderer(this);
			lastSplatCount = 0;
			splatViewData?.Dispose();
			distancesBuffer?.Dispose();
			if (sortData != null)
			{
				foreach (KeyValuePair<Camera, CameraSortData> sortDatum in sortData)
				{
					sortDatum.Value.Dispose();
				}
			}
			splatViewData = null;
			distancesBuffer = null;
			sortData = null;
		}
	}
	internal class DestroyProxy : MonoBehaviour
	{
		public Action DestroyCallback;

		private void OnDestroy()
		{
			DestroyCallback?.Invoke();
		}
	}
	public static class GaussianSplatRendererManager
	{
		private struct SplatRendererDist : IComparable<SplatRendererDist>
		{
			public GaussianSplatRenderer renderer;

			public float distance;

			public int CompareTo(SplatRendererDist other)
			{
				return distance.CompareTo(other.distance);
			}
		}

		private struct SplatRendererSort : IComparable<SplatRendererSort>
		{
			public GaussianSplatRenderer renderer;

			public int lastFullSortFrame;

			public int CompareTo(SplatRendererSort other)
			{
				return lastFullSortFrame.CompareTo(other.lastFullSortFrame);
			}
		}

		private class CameraData
		{
			public CommandBuffer Command;
		}

		public const int GROUP_SIZE = 1024;

		public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");

		private static bool _dataInitialized;

		private static HashSet<GaussianSplatRenderer> _renderers = new HashSet<GaussianSplatRenderer>();

		private static Dictionary<Camera, CameraData> _registeredCameras = new Dictionary<Camera, CameraData>();

		private static Material _renderMaterial;

		private static Material _compositeMaterial;

		private static MaterialPropertyBlock _renderPropertyBlock;

		private static ComputeShader _renderCompute;

		private static ComputeShader _sortCompute;

		private static int _calcDistances;

		private static int _calcViewDataMono;

		private static int _calcViewDataStereo;

		private static float sortMegaOperationsPerCamera = 1f;

		private static List<SplatRendererDist> toRender = new List<SplatRendererDist>();

		private static List<SplatRendererSort> toSort = new List<SplatRendererSort>();

		private static int ComputeThreadGroups(int count)
		{
			return MathHelper.CeilToInt((double)count / 1024.0);
		}

		public static void RegisterRenderer(GaussianSplatRenderer renderer)
		{
			if (!_renderers.Add(renderer))
			{
				throw new InvalidOperationException("Renderer already registered");
			}
			if (_renderers.Count == 1)
			{
				Initialize();
			}
		}

		public static BufferSorter AllocateSorter(int splatCount)
		{
			return new BufferSorter(_sortCompute, splatCount);
		}

		public static void UnregisterRenderer(GaussianSplatRenderer renderer)
		{
			if (_renderers.Remove(renderer) && _renderers.Count == 0)
			{
				Deinitialize();
			}
		}

		private static void Initialize()
		{
			if (!_dataInitialized)
			{
				InitializeData();
			}
			Camera.onPreCull = (Camera.CameraCallback)Delegate.Combine(Camera.onPreCull, new Camera.CameraCallback(OnPreCullCamera));
		}

		private static void Deinitialize()
		{
			Camera.onPreCull = (Camera.CameraCallback)Delegate.Remove(Camera.onPreCull, new Camera.CameraCallback(OnPreCullCamera));
			foreach (Camera item in _registeredCameras.Keys.ToList())
			{
				CameraRemoved(item);
			}
		}

		private static void InitializeData()
		{
			_renderMaterial = Resources.Load<Material>("GaussianSplatting/Render");
			_compositeMaterial = Resources.Load<Material>("GaussianSplatting/Composite");
			_renderCompute = Resources.Load<ComputeShader>("GaussianSplatting/RenderCompute");
			_sortCompute = Resources.Load<ComputeShader>("GaussianSplatting/SortCompute");
			_renderPropertyBlock = new MaterialPropertyBlock();
			_calcDistances = _renderCompute.FindKernel("CSCalcDistances");
			_calcViewDataMono = _renderCompute.FindKernel("CSCalcViewDataMono");
			_calcViewDataStereo = _renderCompute.FindKernel("CSCalcViewDataStereo");
			_dataInitialized = true;
		}

		public static void ApplyConfig(GaussianSplatConfig config)
		{
			sortMegaOperationsPerCamera = config.sortingMegaOperationsPerCamera;
		}

		private static void CameraRemoved(Camera camera)
		{
			if (!_registeredCameras.TryGetValue(camera, out CameraData value))
			{
				return;
			}
			camera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, value.Command);
			value.Command.Dispose();
			_registeredCameras.Remove(camera);
			foreach (GaussianSplatRenderer renderer in _renderers)
			{
				renderer.CameraRemoved(camera);
			}
		}

		private static CameraData GetCameraData(Camera cam)
		{
			if (_registeredCameras.TryGetValue(cam, out CameraData value))
			{
				return value;
			}
			DestroyProxy destroyProxy = cam.gameObject.AddComponent<DestroyProxy>();
			destroyProxy.DestroyCallback = (Action)Delegate.Combine(destroyProxy.DestroyCallback, (Action)delegate
			{
				CameraRemoved(cam);
			});
			value = new CameraData();
			value.Command = new CommandBuffer
			{
				name = "GaussianSplats - " + cam.name
			};
			cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, value.Command);
			_registeredCameras.Add(cam, value);
			return value;
		}

		private static void OnPreCullCamera(Camera cam)
		{
			if (cam.cameraType == CameraType.Preview)
			{
				return;
			}
			CameraData cameraData = GetCameraData(cam);
			CommandBuffer command = cameraData.Command;
			command.Clear();
			toRender.Clear();
			CollectAndSortRenderersForCamera(cam, cameraData, toRender);
			if (toRender.Count > 0)
			{
				int num = cam.pixelWidth;
				int pixelHeight = cam.pixelHeight;
				if (cam.stereoEnabled)
				{
					num *= 2;
				}
				command.GetTemporaryRT(GaussianSplatRT, num, pixelHeight, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
				command.SetRenderTarget(GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
				command.ClearRenderTarget(clearDepth: false, clearColor: true, new Color(0f, 0f, 0f, 0f), 0f);
				toSort.Clear();
				foreach (SplatRendererDist item in toRender)
				{
					toSort.Add(new SplatRendererSort
					{
						renderer = item.renderer,
						lastFullSortFrame = item.renderer.GetLastFullSortFrame(cam)
					});
				}
				toSort.Sort();
				long? availableSortOps = null;
				if (cam.tag != "CaptureCamera")
				{
					availableSortOps = MathHelper.RoundToLong(1048576f * sortMegaOperationsPerCamera);
				}
				foreach (SplatRendererSort item2 in toSort)
				{
					EnqueueSort(cam, cameraData, item2.renderer, ref availableSortOps);
				}
				toSort.Clear();
				foreach (SplatRendererDist item3 in toRender)
				{
					EnqueueRender(cam, cameraData, item3.renderer);
				}
				command.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
				command.DrawProcedural(Matrix4x4.identity, _compositeMaterial, 0, MeshTopology.Triangles, 3, 1);
				command.ReleaseTemporaryRT(GaussianSplatRT);
			}
			toRender.Clear();
		}

		private static void CollectAndSortRenderersForCamera(Camera camera, CameraData data, List<SplatRendererDist> toRender)
		{
			foreach (GaussianSplatRenderer renderer in _renderers)
			{
				if (renderer.IsValidToRender && renderer.enabled && renderer.gameObject.activeInHierarchy)
				{
					int num = 1 << renderer.gameObject.layer;
					if ((camera.cullingMask & num) != 0)
					{
						toRender.Add(new SplatRendererDist
						{
							renderer = renderer,
							distance = camera.transform.InverseTransformPoint(renderer.transform.position).z
						});
					}
				}
			}
			toRender.Sort();
		}

		private static void SetStereoCameraParams(CommandBuffer cmd, Camera camera, Camera.StereoscopicEye eye, Matrix4x4 matrixM)
		{
			Matrix4x4 stereoViewMatrix = camera.GetStereoViewMatrix(eye);
			Matrix4x4 gPUProjectionMatrix = GL.GetGPUProjectionMatrix(camera.GetStereoProjectionMatrix(eye), renderIntoTexture: true);
			Matrix4x4 val = gPUProjectionMatrix * stereoViewMatrix;
			Matrix4x4 val2 = stereoViewMatrix * matrixM;
			Matrix4x4 inverse = stereoViewMatrix.inverse;
			Vector3 vector = new Vector3(inverse[0, 3], inverse[1, 3], inverse[2, 3]);
			string text = ((eye == Camera.StereoscopicEye.Left) ? "_L" : "_R");
			cmd.SetComputeMatrixParam(_renderCompute, "_MatrixVP" + text, val);
			cmd.SetComputeMatrixParam(_renderCompute, "_MatrixMV" + text, val2);
			cmd.SetComputeMatrixParam(_renderCompute, "_MatrixP" + text, gPUProjectionMatrix);
			cmd.SetComputeVectorParam(_renderCompute, "_VecWorldSpaceCameraPos" + text, vector);
		}

		private static void EnqueueSort(Camera camera, CameraData data, GaussianSplatRenderer renderer, ref long? availableSortOps)
		{
			CommandBuffer command = data.Command;
			renderer.GetOrderBuffer(camera, out var initSort);
			if (initSort)
			{
				SetCameraParams(camera, data, renderer, out var _);
				renderer.AssignDataBuffers(command, _renderCompute, _calcDistances);
				command.SetComputeBufferParam(_renderCompute, _calcDistances, "_SplatSortDistances", renderer.DistanceBuffer);
				command.DispatchCompute(_renderCompute, _calcDistances, ComputeThreadGroups(renderer.SplatCount), 1, 1);
			}
			renderer.RunSortChunk(command, camera, ref availableSortOps);
		}

		private static void SetCameraParams(Camera camera, CameraData data, GaussianSplatRenderer renderer, out Matrix4x4 matrixM)
		{
			CommandBuffer command = data.Command;
			command.SetComputeIntParam(_renderCompute, "_SplatCount", renderer.SplatCount);
			command.SetComputeIntParam(_renderCompute, "_SHOrder", renderer.SHOrder);
			command.SetComputeFloatParam(_renderCompute, "_SplatScale", renderer.SplatScale);
			command.SetComputeFloatParam(_renderCompute, "_SplatOpacityScale", renderer.OpacityScale);
			command.SetComputeIntParam(_renderCompute, "_SHOnly", renderer.SHOnly ? 1 : 0);
			Matrix4x4 worldToCameraMatrix = camera.worldToCameraMatrix;
			Matrix4x4 gPUProjectionMatrix = GL.GetGPUProjectionMatrix(camera.projectionMatrix, renderIntoTexture: true);
			matrixM = renderer.transform.localToWorldMatrix;
			Matrix4x4 worldToLocalMatrix = renderer.transform.worldToLocalMatrix;
			Matrix4x4 val = gPUProjectionMatrix * worldToCameraMatrix;
			Matrix4x4 val2 = worldToCameraMatrix * matrixM;
			command.SetComputeVectorParam(_renderCompute, "_VecScreenParams", new Vector4(camera.pixelWidth, camera.pixelHeight));
			command.SetComputeMatrixParam(_renderCompute, "_MatrixObjectToWorld", matrixM);
			command.SetComputeMatrixParam(_renderCompute, "_MatrixWorldToObject", worldToLocalMatrix);
			command.SetComputeMatrixParam(_renderCompute, "_MatrixVP", val);
			command.SetComputeMatrixParam(_renderCompute, "_MatrixMV", val2);
			command.SetComputeMatrixParam(_renderCompute, "_MatrixP", gPUProjectionMatrix);
			command.SetComputeVectorParam(_renderCompute, "_VecWorldSpaceCameraPos", camera.transform.position);
			if (camera.stereoEnabled)
			{
				SetStereoCameraParams(command, camera, Camera.StereoscopicEye.Left, matrixM);
				SetStereoCameraParams(command, camera, Camera.StereoscopicEye.Right, matrixM);
			}
		}

		private static void EnqueueRender(Camera camera, CameraData data, GaussianSplatRenderer renderer)
		{
			CommandBuffer command = data.Command;
			SetCameraParams(camera, data, renderer, out var matrixM);
			int num = (camera.stereoEnabled ? _calcViewDataStereo : _calcViewDataMono);
			renderer.AssignDataBuffers(command, _renderCompute, num);
			command.SetComputeBufferParam(_renderCompute, num, "_SplatViewData", renderer.SplatViewData);
			command.DispatchCompute(_renderCompute, num, ComputeThreadGroups(renderer.SplatCount), 1, 1);
			bool initSort;
			ComputeBuffer orderBuffer = renderer.GetOrderBuffer(camera, out initSort);
			_renderPropertyBlock.SetBuffer("_SplatViewData", renderer.SplatViewData);
			_renderPropertyBlock.SetBuffer("_OrderBuffer", orderBuffer);
			_renderPropertyBlock.SetInt("_SplatCount", renderer.SplatCount);
			command.DrawProcedural(matrixM, _renderMaterial, 0, MeshTopology.Triangles, 6, renderer.SplatCount, _renderPropertyBlock);
		}
	}
	public class HeadOutput : MonoBehaviour
	{
		public enum HeadOutputType
		{
			VR,
			Screen,
			Screen360,
			Static
		}

		public const float INITIAL_HEIGHT = 1.75f;

		public HeadOutputType Type;

		public bool AllowMotionBlur;

		public bool AllowScreenSpaceReflection;

		public List<Camera> cameras;

		private bool _isUserView;

		private bool _overrideView;

		private Vector3 _viewPos = Vector3.up * 1.75f;

		private Quaternion _viewRot = Quaternion.identity;

		private Vector3 _viewScl = Vector3.one;

		private Vector3 _rootScl = Vector3.one;

		public Transform CameraRoot => cameras[0].transform;

		public float NearClipPlane
		{
			get
			{
				return cameras[0].nearClipPlane;
			}
			set
			{
				foreach (Camera camera in cameras)
				{
					camera.nearClipPlane = value;
				}
			}
		}

		public float FarClipPlane
		{
			get
			{
				return cameras[0].farClipPlane;
			}
			set
			{
				foreach (Camera camera in cameras)
				{
					camera.farClipPlane = value;
				}
			}
		}

		private void Awake()
		{
			_overrideView = Type != HeadOutputType.VR;
			if (_overrideView)
			{
				UpdateOverridenView();
			}
			CameraSettings settings = new CameraSettings
			{
				IsPrimary = true,
				IsVR = (Type == HeadOutputType.VR),
				MotionBlur = AllowMotionBlur,
				ScreenSpaceReflection = AllowScreenSpaceReflection,
				SetupPostProcessing = (Application.platform != RuntimePlatform.Android)
			};
			Camera.onPreCull = (Camera.CameraCallback)Delegate.Combine(Camera.onPreCull, new Camera.CameraCallback(OnPreCull));
			Camera.onPostRender = (Camera.CameraCallback)Delegate.Combine(Camera.onPostRender, new Camera.CameraCallback(OnPostRender));
			foreach (Camera camera in cameras)
			{
				if (Type == HeadOutputType.VR)
				{
					CommandBuffer commandBuffer = new CommandBuffer();
					commandBuffer.name = "ClearRenderTarget";
					commandBuffer.ClearRenderTarget(clearDepth: false, clearColor: true, Color.black);
					camera.AddCommandBuffer(CameraEvent.BeforeGBuffer, commandBuffer);
				}
				RenderingManager.Instance.CameraInitializer.SetupCamera(camera, settings);
			}
		}

		private static Vector3 FilterScale(in Vector3 scale)
		{
			if (!((double)Mathf.Min(scale.x, scale.y, scale.z) <= 1E-08))
			{
				return scale;
			}
			return Vector3.one;
		}

		private static Vector3 Mul(in Vector3 a, in Vector3 b)
		{
			return new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);
		}

		private static Vector3 Div(in Vector3 a, in Vector3 b)
		{
			return new Vector3(a.x / b.x, a.y / b.y, a.z / b.z);
		}

		private void OnPreCull(Camera camera)
		{
			if (!(camera != cameras[0]))
			{
				RenderContextHelper.BeginRenderContext((!_isUserView) ? RenderingContext.ExternalView : RenderingContext.UserView);
				UpdateOverridenView();
			}
		}

		private void OnPostRender(Camera camera)
		{
			if (!(camera != cameras[0]))
			{
				RenderContextHelper.EndCurrentRenderContext();
			}
		}

		private void UpdateOverridenView()
		{
			Transform cameraRoot = CameraRoot;
			if (_overrideView)
			{
				switch (Type)
				{
				case HeadOutputType.Screen:
					cameraRoot.position = _viewPos;
					cameraRoot.rotation = _viewRot;
					cameraRoot.localScale = FilterScale(Div(in _viewScl, in _rootScl));
					break;
				case HeadOutputType.Screen360:
					cameraRoot.position = _viewPos;
					cameraRoot.localScale = FilterScale(Div(in _viewScl, in _rootScl));
					break;
				case HeadOutputType.VR:
				{
					Vector3 b = CameraRoot.lossyScale;
					base.transform.localScale = Mul(FilterScale(Div(in _viewScl, in b)), base.transform.localScale);
					Quaternion rotation = CameraRoot.rotation;
					base.transform.localRotation = _viewRot * Quaternion.Inverse(rotation) * base.transform.localRotation;
					Vector3 position = CameraRoot.position;
					base.transform.localPosition += _viewPos - position;
					break;
				}
				}
			}
		}

		public void UpdatePositioning(RenderSpace renderSpace)
		{
			Vector3 rootPosition = renderSpace.RootPosition;
			Quaternion rootRotation = renderSpace.RootRotation;
			Vector3 vector = FilterScale(renderSpace.RootScale);
			base.transform.position = rootPosition;
			base.transform.rotation = rootRotation;
			base.transform.localScale = vector;
			float nearClip = RenderingManager.Instance.NearClip;
			float farClip = RenderingManager.Instance.FarClip;
			nearClip = Mathf.Max((Type == HeadOutputType.Screen360) ? 0.25f : 0.001f, nearClip);
			farClip = Mathf.Max(0.5f, farClip);
			NearClipPlane = nearClip * vector.x;
			FarClipPlane = farClip;
			if (Type == HeadOutputType.Screen || Type == HeadOutputType.Static)
			{
				foreach (Camera camera in cameras)
				{
					camera.fieldOfView = RenderingManager.Instance.DesktopFOV;
				}
			}
			if (renderSpace.OverrideViewPosition)
			{
				_overrideView = true;
				_viewPos = renderSpace.OverridenViewPosition;
				_viewRot = renderSpace.OverridenViewRotation;
				_viewScl = FilterScale(renderSpace.OverridenViewScale);
				_rootScl = vector;
				UpdateOverridenView();
			}
			else
			{
				_overrideView = false;
			}
			_isUserView = !renderSpace.ViewPositionIsExternal;
		}

		public static HeadOutput GetHeadObject(HeadOutputDevice device)
		{
			string text;
			switch (device)
			{
			case HeadOutputDevice.Screen:
				text = "Screen";
				break;
			case HeadOutputDevice.Screen360:
				text = "Screen360";
				break;
			case HeadOutputDevice.Oculus:
			case HeadOutputDevice.OculusQuest:
				text = "Oculus";
				break;
			case HeadOutputDevice.SteamVR:
			case HeadOutputDevice.WindowsMR:
				text = "SteamVR";
				break;
			default:
				text = device.ToString();
				break;
			}
			UnityEngine.Debug.Log("DeviceName: " + text);
			return UnityEngine.Object.Instantiate(Resources.Load<GameObject>("HeadRenderers/" + device)).GetComponentInChildren<HeadOutput>();
		}
	}
	public interface IDriverHeadDevice
	{
		HeadOutputDevice Device { get; }
	}
	public class OverlayRootPositioner : MonoBehaviour
	{
		private void LateUpdate()
		{
			base.transform.position = Vector3.zero;
			base.transform.rotation = Quaternion.identity;
			base.transform.localScale = 1f / base.transform.parent.lossyScale.x * Vector3.one;
		}
	}
	public class ReflectionProbeRenderer : MonoBehaviour
	{
		public ReflectionProbe probe;

		public ReflectionProbeRenderable renderable;

		public ReflectionProbeRenderTask task;

		public RenderTexture texture;

		public Dictionary<GameObject, int> previousLayers;

		public int renderId;

		public UnityEngine.Rendering.ReflectionProbeTimeSlicingMode previousTimeSlicingMode;

		private bool finishDone;

		private bool finishRunning;

		private List<Texture2D> readMips;

		private unsafe void FinishRender()
		{
			if (probe != null)
			{
				probe.timeSlicingMode = previousTimeSlicingMode;
			}
			RenderTextureDescriptor descriptor = texture.descriptor;
			descriptor.dimension = TextureDimension.Tex2D;
			_ = descriptor.width;
			List<RenderTexture> list = new List<RenderTexture>();
			readMips = new List<Texture2D>();
			int miplevels = task.mipOrigins[0].Count;
			for (int i = 0; i < miplevels; i++)
			{
				list.Add(RenderTexture.GetTemporary(descriptor));
				for (int j = 0; j < 6; j++)
				{
					readMips.Add(new Texture2D(descriptor.width, descriptor.height, descriptor.graphicsFormat, TextureCreationFlags.None));
				}
				descriptor.useMipMap = false;
				descriptor.width /= 2;
				descriptor.height /= 2;
			}
			List<NativeArray<byte>> faceData = new List<NativeArray<byte>>();
			for (int k = 0; k < 6; k++)
			{
				Graphics.CopyTexture(texture, k, list[0], 0);
				for (int l = 0; l < miplevels; l++)
				{
					if (l > 0)
					{
						Graphics.CopyTexture(list[0], 0, l, list[l], 0, 0);
					}
					Texture2D texture2D = GetReadMip(k, l);
					RenderTexture active = RenderTexture.active;
					RenderTexture.active = list[l];
					texture2D.ReadPixels(new Rect(0f, 0f, texture2D.width, texture2D.height), 0, 0, recalculateMipMaps: false);
					RenderTexture.active = active;
					faceData.Add(texture2D.GetRawTextureData<byte>());
				}
			}
			foreach (RenderTexture item in list)
			{
				RenderTexture.ReleaseTemporary(item);
			}
			Task.Run(delegate
			{
				try
				{
					Span<byte> span = RenderingManager.Instance.SharedMemory.AccessData(task.resultData);
					int num = 0;
					for (int m = 0; m < 6; m++)
					{
						List<int> list2 = task.mipOrigins[m];
						for (int n = 0; n < miplevels; n++)
						{
							int start = list2[n];
							NativeArray<byte> nativeArray = faceData[num++];
							Span<byte> destination = span.Slice(start, nativeArray.Length);
							new Span<byte>(nativeArray.GetUnsafeReadOnlyPtr(), nativeArray.Length).CopyTo(destination);
							nativeArray.Dispose();
						}
					}
					SendResult(success: true);
				}
				catch (Exception arg)
				{
					UnityEngine.Debug.LogError($"Exception converting reflection probe render data for task ID {task.renderTaskId}:\n{arg}");
					SendResult(success: false);
				}
				finally
				{
					finishDone = true;
				}
			});
			Texture2D GetReadMip(int face, int mip)
			{
				return readMips[face + mip * 6];
			}
		}

		private void LateUpdate()
		{
			if (finishDone)
			{
				Cleanup();
			}
			else if (!finishRunning && probe.IsFinishedRendering(renderId))
			{
				try
				{
					finishRunning = true;
					FinishRender();
				}
				catch (Exception arg)
				{
					UnityEngine.Debug.LogError($"Exception finishing reflection probe render task ID {task.renderTaskId}:\n{arg}");
					SendResult(success: false);
					Cleanup();
				}
			}
		}

		private void SendResult(bool success)
		{
			ReflectionProbeRenderResult result = new ReflectionProbeRenderResult
			{
				renderTaskId = task.renderTaskId,
				success = success
			};
			RenderingManager.Instance.SendReflectionProbeRenderResult(result);
			PackerMemoryPool.Instance.Return(task);
		}

		private void OnDestroy()
		{
			Cleanup();
		}

		private void Cleanup()
		{
			if (texture != null)
			{
				RenderTexture.ReleaseTemporary(texture);
			}
			if (previousLayers != null)
			{
				RenderHelper.RestoreLayers(previousLayers);
				previousLayers = null;
			}
			if (readMips != null)
			{
				foreach (Texture2D readMip in readMips)
				{
					UnityEngine.Object.Destroy(readMip);
				}
				readMips = null;
			}
			probe = null;
			renderable = null;
			task = null;
			texture = null;
			UnityEngine.Object.Destroy(this);
		}
	}
	public enum RenderingContextStage
	{
		Begin,
		End
	}
	public delegate void RenderingContextHandler(RenderingContextStage stage);
	public static class RenderContextHelper
	{
		private static RenderingContext? _currentContext;

		private static Dictionary<RenderingContext, HashSet<RenderingContextHandler>> renderingContexts = new Dictionary<RenderingContext, HashSet<RenderingContextHandler>>();

		public static RenderingContext? CurrentRenderingContext => _currentContext;

		public static void BeginRenderContext(RenderingContext context)
		{
			if (context == _currentContext)
			{
				return;
			}
			EndCurrentRenderContext();
			_currentContext = context;
			if (!renderingContexts.TryGetValue(context, out HashSet<RenderingContextHandler> value))
			{
				return;
			}
			foreach (RenderingContextHandler item in value)
			{
				item(RenderingContextStage.Begin);
			}
		}

		public static void EndCurrentRenderContext()
		{
			if (!_currentContext.HasValue)
			{
				return;
			}
			if (renderingContexts.TryGetValue(_currentContext.Value, out HashSet<RenderingContextHandler> value))
			{
				foreach (RenderingContextHandler item in value)
				{
					item(RenderingContextStage.End);
				}
			}
			_currentContext = null;
		}

		public static void RegisterRenderContextEvents(RenderingContext context, RenderingContextHandler handler)
		{
			if (!renderingContexts.TryGetValue(context, out HashSet<RenderingContextHandler> value))
			{
				value = new HashSet<RenderingContextHandler>();
				renderingContexts.Add(context, value);
			}
			if (!value.Add(handler))
			{
				throw new InvalidOperationException("Handler already registered");
			}
		}

		public static void UnregisterRenderContextEvents(RenderingContext context, RenderingContextHandler handler)
		{
			if (!renderingContexts[context].Remove(handler))
			{
				throw new InvalidOperationException("Handler not registered");
			}
		}
	}
	public static class RenderHelper
	{
		public const string PRIVATE_LAYER = "Private";

		public const string TEMP_LAYER = "Temp";

		public const string HIDDEN_LAYER = "Hidden";

		public const string OVERLAY_LAYER = "Overlay";

		public const string CAPTURE_CAMERA_TAG = "CaptureCamera";

		public static int PUBLIC_RENDER_MASK => ~LayerMask.GetMask("Private", "Temp", "Overlay", "Hidden");

		public static int PRIVATE_RENDER_MASK => ~LayerMask.GetMask("Temp", "Overlay", "Hidden");

		public static void SetHiearchyLayer(List<GameObject> gameObjects, int layer, Dictionary<GameObject, int> previous)
		{
			if (gameObjects == null)
			{
				return;
			}
			foreach (GameObject gameObject in gameObjects)
			{
				if (gameObject != null)
				{
					SetHiearchyLayer(gameObject, layer, previous);
				}
			}
		}

		public static void RestoreHiearachyLayer(List<GameObject> gameObjects, Dictionary<GameObject, int> previous)
		{
			if (gameObjects == null)
			{
				return;
			}
			foreach (GameObject gameObject in gameObjects)
			{
				if (gameObject != null)
				{
					RestoreHiearachyLayer(gameObject, previous);
				}
			}
		}

		public static void SetHiearchyLayer(GameObject root, int layer, Dictionary<GameObject, int> previous)
		{
			if (!previous.ContainsKey(root) && root.layer != layer)
			{
				previous.Add(root, root.layer);
				root.layer = layer;
				for (int i = 0; i < root.transform.childCount; i++)
				{
					SetHiearchyLayer(root.transform.GetChild(i).gameObject, layer, previous);
				}
			}
		}

		public static void RestoreHiearachyLayer(GameObject root, Dictionary<GameObject, int> previous)
		{
			if (previous.TryGetValue(root, out var value))
			{
				if (root.layer == value)
				{
					return;
				}
				root.layer = value;
			}
			for (int i = 0; i < root.transform.childCount; i++)
			{
				RestoreHiearachyLayer(root.transform.GetChild(i).gameObject, previous);
			}
		}

		public static void RestoreLayers(Dictionary<GameObject, int> previous)
		{
			foreach (KeyValuePair<GameObject, int> previou in previous)
			{
				previou.Key.layer = previou.Value;
			}
		}
	}
	public static class SH2Calculator
	{
		private static ComputeShader _compute;

		private static int _ReduceKernel;

		private static int[] _SHkernels = new int[9];

		private static ComputeBuffer[] _buffers = new ComputeBuffer[2];

		public static ComputeResult ComputeFromProbe(ReflectionProbe unityProbe, Vector4[] output, ref RenderTexture convertTexture)
		{
			if (unityProbe == null)
			{
				return ComputeResult.Failed;
			}
			if (unityProbe.customBakedTexture == null)
			{
				RenderTexture realtimeTexture = unityProbe.realtimeTexture;
				if (realtimeTexture == null)
				{
					return ComputeResult.Postpone;
				}
				if (!GPU_Project_Uniform_9Coeff(realtimeTexture, output, ref convertTexture))
				{
					return ComputeResult.Failed;
				}
				return ComputeResult.Computed;
			}
			Cubemap cubemap = unityProbe.customBakedTexture as Cubemap;
			if (cubemap != null)
			{
				if (!GPU_Project_Uniform_9Coeff(cubemap, output, ref convertTexture))
				{
					return ComputeResult.Failed;
				}
				return ComputeResult.Computed;
			}
			return ComputeResult.Failed;
		}

		public static bool GPU_Project_Uniform_9Coeff(RenderTexture input, Vector4[] output, ref RenderTexture currentTexture)
		{
			RenderTextureDescriptor desc = new RenderTextureDescriptor
			{
				autoGenerateMips = false,
				bindMS = false,
				colorFormat = input.format,
				depthBufferBits = 0,
				dimension = TextureDimension.Tex2DArray,
				enableRandomWrite = false,
				height = input.height,
				width = input.width,
				msaaSamples = 1,
				sRGB = true,
				useMipMap = false,
				volumeDepth = 6
			};
			if (currentTexture == null || currentTexture.descriptor.colorFormat != desc.colorFormat || currentTexture.descriptor.height != desc.height || currentTexture.descriptor.width != desc.width)
			{
				if (currentTexture != null)
				{
					UnityEngine.Object.Destroy(currentTexture);
				}
				currentTexture = new RenderTexture(desc);
				currentTexture.Create();
			}
			for (int i = 0; i < 6; i++)
			{
				Graphics.CopyTexture(input, i, 0, currentTexture, i, 0);
			}
			return Render_GPU_Project_Uniform_9Coeff(currentTexture, output);
		}

		public static bool GPU_Project_Uniform_9Coeff(Cubemap input, Vector4[] output, ref RenderTexture currentTexture)
		{
			RenderTextureFormat? renderTextureFormat = ConvertRenderFormat(input.format);
			if (!renderTextureFormat.HasValue)
			{
				return false;
			}
			RenderTextureDescriptor desc = new RenderTextureDescriptor
			{
				autoGenerateMips = false,
				bindMS = false,
				colorFormat = renderTextureFormat.Value,
				depthBufferBits = 0,
				dimension = TextureDimension.Tex2DArray,
				enableRandomWrite = false,
				height = input.height,
				width = input.width,
				msaaSamples = 1,
				sRGB = true,
				useMipMap = false,
				volumeDepth = 6
			};
			if (currentTexture == null || currentTexture.descriptor.colorFormat != desc.colorFormat || currentTexture.descriptor.height != desc.height || currentTexture.descriptor.width != desc.width)
			{
				if (currentTexture != null)
				{
					UnityEngine.Object.Destroy(currentTexture);
				}
				currentTexture = new RenderTexture(desc);
				currentTexture.Create();
			}
			for (int i = 0; i < 6; i++)
			{
				Graphics.CopyTexture(input, i, 0, currentTexture, i, 0);
			}
			return Render_GPU_Project_Uniform_9Coeff(currentTexture, output);
		}

		private static RenderTextureFormat? ConvertRenderFormat(UnityEngine.TextureFormat input_format)
		{
			return input_format switch
			{
				UnityEngine.TextureFormat.RGBA32 => RenderTextureFormat.ARGB32, 
				UnityEngine.TextureFormat.RGBAHalf => RenderTextureFormat.ARGBHalf, 
				UnityEngine.TextureFormat.RGBAFloat => RenderTextureFormat.ARGBFloat, 
				_ => null, 
			};
		}

		private static bool Render_GPU_Project_Uniform_9Coeff(RenderTexture input, Vector4[] output)
		{
			if (_compute == null)
			{
				_compute = Resources.Load<ComputeShader>("SphericalHarmonics/SH_Reduce_Uniform");
				_ReduceKernel = _compute.FindKernel("Reduce");
				for (int i = 0; i < 9; i++)
				{
					_SHkernels[i] = _compute.FindKernel("sh_" + i);
				}
			}
			int num = Mathf.CeilToInt((float)input.width / 8f);
			ComputeBuffer computeBuffer = new ComputeBuffer(9, 16);
			ComputeBuffer computeBuffer2 = new ComputeBuffer(num * num * 6, 16);
			ComputeBuffer computeBuffer3 = new ComputeBuffer(num * num * 6, 16);
			for (int j = 0; j < 9; j++)
			{
				num = Mathf.CeilToInt((float)input.width / 8f);
				int kernelIndex = _SHkernels[j];
				_compute.SetInt("coeff", j);
				_compute.SetTexture(kernelIndex, "input_data", input);
				_compute.SetBuffer(kernelIndex, "output_buffer", computeBuffer2);
				_compute.SetBuffer(kernelIndex, "coefficients", computeBuffer);
				_compute.SetInt("ceiled_size", num);
				_compute.SetInt("input_size", input.width);
				_compute.SetInt("row_size", num);
				_compute.SetInt("face_size", num * num);
				_compute.Dispatch(kernelIndex, num, num, 1);
				kernelIndex = _ReduceKernel;
				int num2 = 0;
				_buffers[0] = computeBuffer2;
				_buffers[1] = computeBuffer3;
				while (num > 1)
				{
					_compute.SetInt("input_size", num);
					num = Mathf.CeilToInt((float)num / 8f);
					_compute.SetInt("ceiled_size", num);
					_compute.SetBuffer(kernelIndex, "coefficients", computeBuffer);
					_compute.SetBuffer(kernelIndex, "input_buffer", _buffers[num2]);
					_compute.SetBuffer(kernelIndex, "output_buffer", _buffers[(num2 + 1) % 2]);
					_compute.Dispatch(kernelIndex, num, num, 1);
					num2 = (num2 + 1) % 2;
				}
			}
			computeBuffer.GetData(output);
			computeBuffer3.Release();
			computeBuffer2.Release();
			computeBuffer.Release();
			return true;
		}
	}
	public class ShaderCameraProperties : MonoBehaviour
	{
		private void OnPreRender()
		{
			int value = -1;
			Camera current = Camera.current;
			if (current.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left)
			{
				value = 0;
			}
			else if (current.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right)
			{
				value = 1;
			}
			Shader.SetGlobalInt("_stereoActiveEye", value);
			Shader.SetGlobalVector("_nonJitteredWorldSpaceCameraPos", base.transform.position);
		}
	}
	public class TextureDisplayBlitter : MonoBehaviour
	{
		public Texture Texture;

		public int DisplayIndex;

		public Color Color;

		public bool FlipHorizontally;

		public bool FlipVertically;

		private int lastRenderedDisplay = -1;

		private void OnDisable()
		{
			ClearDisplay();
		}

		private void OnEnable()
		{
			StartCoroutine(Blit());
		}

		private void OnDestroy()
		{
			ClearDisplay();
			Deinitialize();
		}

		internal void Deinitialize()
		{
			Texture = null;
		}

		private void ClearDisplay()
		{
			if (lastRenderedDisplay >= 0 && lastRenderedDisplay < Display.displays.Length)
			{
				Display display = Display.displays[lastRenderedDisplay];
				if (display.active)
				{
					Graphics.SetRenderTarget(display.colorBuffer, display.depthBuffer);
					GL.PushMatrix();
					GL.LoadOrtho();
					GL.Color(Color.white);
					GL.Clear(clearDepth: true, clearColor: true, new Color(0f, 0f, 0f, 1f));
					GL.PopMatrix();
					Graphics.SetRenderTarget(null);
				}
			}
			lastRenderedDisplay = -1;
		}

		private IEnumerator Blit()
		{
			Material material = new Material(Shader.Find("Unlit/Texture"));
			while (true)
			{
				yield return new WaitForEndOfFrame();
				if (DisplayIndex != lastRenderedDisplay)
				{
					ClearDisplay();
				}
				if (DisplayIndex < 0 || DisplayIndex >= Display.displays.Length)
				{
					continue;
				}
				if (Texture != null)
				{
					Display display = Display.displays[DisplayIndex];
					if (!display.active)
					{
						display.Activate();
					}
					lastRenderedDisplay = DisplayIndex;
					Vector2 vector = new Vector2(display.renderingWidth, display.renderingHeight);
					Vector2 vector2 = new Vector2(Texture.width, Texture.height);
					Vector2 vector3 = vector / Mathf.Max(vector.x, vector.y);
					Vector2 vector4 = vector2 / Mathf.Max(vector2.x, vector2.y);
					Rect rect;
					if (vector4.x > vector3.x || vector3.y > vector4.y)
					{
						vector4 *= vector3.x / vector4.x;
						float num = vector3.y - vector4.y;
						rect = new Rect(0f, num * 0.5f, 1f, 1f - num);
					}
					else
					{
						vector4 *= vector3.y / vector4.y;
						float num2 = vector3.x - vector4.x;
						rect = new Rect(num2 * 0.5f, 0f, 1f - num2, 1f);
					}
					rect = new Rect(rect.x * vector.x, rect.y * vector.y, rect.width * vector.x, rect.height * vector.y);
					Graphics.SetRenderTarget(display.colorBuffer, display.depthBuffer);
					if (FlipHorizontally ^ FlipVertically)
					{
						GL.invertCulling = !GL.invertCulling;
					}
					GL.PushMatrix();
					GL.LoadPixelMatrix(FlipHorizontally ? vector.x : 0f, FlipHorizontally ? 0f : vector.x, FlipVertically ? 0f : vector.y, FlipVertically ? vector.y : 0f);
					GL.Color(Color.white);
					GL.Clear(clearDepth: true, clearColor: true, Color);
					material.mainTexture = Texture;
					Graphics.DrawTexture(rect, Texture, material);
					GL.PopMatrix();
					if (FlipHorizontally ^ FlipVertically)
					{
						GL.invertCulling = !GL.invertCulling;
					}
					Graphics.SetRenderTarget(null);
				}
				else
				{
					ClearDisplay();
				}
			}
		}
	}
	public class RenderSpace : MonoBehaviour
	{
		private bool _lastPrivate;

		private int _shAssignmentIndex;

		public int Id { get; private set; }

		public bool IsActive { get; private set; }

		public bool IsOverlay { get; private set; }

		public bool IsPrivate { get; private set; }

		public int DefaultLayer { get; private set; }

		public bool WasUpdated { get; private set; }

		public Vector3 RootPosition { get; private set; }

		public Quaternion RootRotation { get; private set; }

		public Vector3 RootScale { get; private set; }

		public bool ViewPositionIsExternal { get; private set; }

		public bool OverrideViewPosition { get; private set; }

		public Vector3 OverridenViewPosition { get; private set; }

		public Quaternion OverridenViewRotation { get; private set; }

		public Vector3 OverridenViewScale { get; private set; }

		public TransformManager Transforms { get; private set; }

		public MeshRendererManager Meshes { get; private set; }

		public SkinnedMeshRendererManager SkinnedMeshes { get; private set; }

		public LightManager Lights { get; private set; }

		public CameraManager Cameras { get; private set; }

		public CameraPortalManager CameraPortals { get; private set; }

		public ReflectionProbeManager ReflectionProbes { get; private set; }

		public ReflectionProbeSH2Manager ReflectionProbeSH2s { get; private set; }

		public LayerManager Layers { get; private set; }

		public BillboardBufferRendererManager BillboardBufferRenderers { get; private set; }

		public MeshBufferRendererManager MeshBufferRenderers { get; private set; }

		public TrailsBufferRendererManager TrailsBufferRenderers { get; private set; }

		public LightsBufferRendererManager LightsBuffersRenderers { get; private set; }

		public RenderTransformOverrideManager RenderTransformOverrides { get; private set; }

		public RenderMaterialOverrideManager RenderMaterialOverrides { get; private set; }

		public BlitToDisplayManager BlitToDisplays { get; private set; }

		public LODGroupRenderableManager LODGroups { get; private set; }

		public GaussianSplatRenderableManager GaussianSplats { get; private set; }

		public void Initialize(int id)
		{
			Id = id;
			Transforms = new TransformManager(this, base.gameObject.transform);
			Meshes = new MeshRendererManager(this);
			SkinnedMeshes = new SkinnedMeshRendererManager(this);
			Lights = new LightManager(this);
			Cameras = new CameraManager(this);
			CameraPortals = new CameraPortalManager(this);
			ReflectionProbes = new ReflectionProbeManager(this);
			ReflectionProbeSH2s = new ReflectionProbeSH2Manager(this);
			Layers = new LayerManager(this);
			BillboardBufferRenderers = new BillboardBufferRendererManager(this);
			MeshBufferRenderers = new MeshBufferRendererManager(this);
			TrailsBufferRenderers = new TrailsBufferRendererManager(this);
			LightsBuffersRenderers = new LightsBufferRendererManager(this);
			RenderTransformOverrides = new RenderTransformOverrideManager(this);
			RenderMaterialOverrides = new RenderMaterialOverrideManager(this);
			BlitToDisplays = new BlitToDisplayManager(this);
			LODGroups = new LODGroupRenderableManager(this);
			GaussianSplats = new GaussianSplatRenderableManager(this);
		}

		public void ClearUpdated()
		{
			WasUpdated = false;
		}

		public void HandleUpdate(RenderSpaceUpdate data)
		{
			WasUpdated = true;
			IsActive = data.isActive;
			IsOverlay = data.isOverlay;
			IsPrivate = data.isPrivate;
			if (data.isActive)
			{
				if (!base.gameObject.activeSelf)
				{
					base.gameObject.SetActive(value: true);
				}
				if (data.isPrivate != _lastPrivate)
				{
					_lastPrivate = data.isPrivate;
					DefaultLayer = (data.isPrivate ? LayerMask.NameToLayer("Private") : LayerMask.NameToLayer("Default"));
					SetLayerRecursively(Transforms.Root, DefaultLayer);
				}
				if (!data.isOverlay)
				{
					Material material = ((data.skyboxMaterialAssetId >= 0) ? RenderingManager.Instance.Materials.Materials.GetAsset(data.skyboxMaterialAssetId).Material : RenderingManager.Instance.NullMaterial);
					if (RenderSettings.skybox != material)
					{
						RenderSettings.skybox = material;
					}
					if ((_shAssignmentIndex++ & 1) == 0)
					{
						data.ambientLight.sh0.x += 0.0001f;
					}
					RenderSettings.ambientProbe = data.ambientLight.ToUnity();
				}
			}
			else if (base.gameObject.activeSelf)
			{
				base.gameObject.SetActive(value: false);
			}
			RootPosition = data.rootTransform.position.ToUnity();
			RootRotation = data.rootTransform.rotation.ToUnity();
			RootScale = data.rootTransform.scale.ToUnity();
			ViewPositionIsExternal = data.viewPositionIsExternal;
			OverrideViewPosition = data.overrideViewPosition;
			OverridenViewPosition = data.overridenViewTransform.position.ToUnity();
			OverridenViewRotation = data.overridenViewTransform.rotation.ToUnity();
			OverridenViewScale = data.overridenViewTransform.scale.ToUnity();
			if (data.transformsUpdate != null)
			{
				Transforms.HandleUpdate(data.transformsUpdate);
			}
			if (data.meshRenderersUpdate != null)
			{
				Meshes.HandleUpdate(data.meshRenderersUpdate);
			}
			if (data.skinnedMeshRenderersUpdate != null)
			{
				SkinnedMeshes.HandleUpdate(data.skinnedMeshRenderersUpdate);
			}
			if (data.lightsUpdate != null)
			{
				Lights.HandleUpdate(data.lightsUpdate);
			}
			if (data.camerasUpdate != null)
			{
				Cameras.HandleUpdate(data.camerasUpdate);
			}
			if (data.cameraPortalsUpdate != null)
			{
				CameraPortals.HandleUpdate(data.cameraPortalsUpdate);
			}
			if (data.reflectionProbesUpdate != null)
			{
				ReflectionProbes.HandleUpdate(data.reflectionProbesUpdate);
			}
			if (data.reflectionProbeSH2Taks != null)
			{
				ReflectionProbeSH2s.HandleUpdate(data.reflectionProbeSH2Taks);
			}
			if (data.layersUpdate != null)
			{
				Layers.HandleUpdate(data.layersUpdate);
			}
			if (data.billboardBuffersUpdate != null)
			{
				BillboardBufferRenderers.HandleUpdate(data.billboardBuffersUpdate);
			}
			if (data.meshRenderBuffersUpdate != null)
			{
				MeshBufferRenderers.HandleUpdate(data.meshRenderBuffersUpdate);
			}
			if (data.trailRenderersUpdate != null)
			{
				TrailsBufferRenderers.HandleUpdate(data.trailRenderersUpdate);
			}
			if (data.lightsBufferRenderersUpdate != null)
			{
				LightsBuffersRenderers.HandleUpdate(data.lightsBufferRenderersUpdate);
			}
			if (data.reflectionProbeRenderTasks != null)
			{
				ReflectionProbes.HandleRenderTasks(data.reflectionProbeRenderTasks);
			}
			if (data.renderTransformOverridesUpdate != null)
			{
				RenderTransformOverrides.HandleUpdate(data.renderTransformOverridesUpdate);
			}
			if (data.renderMaterialOverridesUpdate != null)
			{
				RenderMaterialOverrides.HandleUpdate(data.renderMaterialOverridesUpdate);
			}
			if (data.blitToDisplaysUpdate != null)
			{
				BlitToDisplays.HandleUpdate(data.blitToDisplaysUpdate);
			}
			if (data.lodGroupUpdate != null)
			{
				LODGroups.HandleUpdate(data.lodGroupUpdate);
			}
			if (data.gaussianSplatRenderersUpdate != null)
			{
				GaussianSplats.HandleUpdate(data.gaussianSplatRenderersUpdate);
			}
		}

		public void UpdateOverlayPositioning(Transform referenceTransform)
		{
			if (!IsOverlay)
			{
				throw new InvalidOperationException("This space is not an overlay");
			}
			Transform obj = base.gameObject.transform;
			obj.position = referenceTransform.position - RootPosition;
			obj.rotation = referenceTransform.rotation * RootRotation;
			obj.localScale = referenceTransform.localScale;
		}

		private static void SetLayerRecursively(Transform transform, int layer)
		{
			transform.gameObject.layer = layer;
			for (int i = 0; i < transform.childCount; i++)
			{
				SetLayerRecursively(transform.GetChild(i), layer);
			}
		}

		public void Remove()
		{
			RenderTransformOverrides.Dispose();
			RenderMaterialOverrides.Dispose();
			UnityEngine.Object.Destroy(base.gameObject);
		}

		public override string ToString()
		{
			return $"RenderSpace. Id: {Id}, IsActive: {IsActive}, IsOverlay: {IsOverlay}";
		}
	}
	public class TransformManager
	{
		public struct TransformData(Transform transform)
		{
			public Transform transform = transform;

			public bool inUse = false;
		}

		public const string FORCE_LAYER = "FORCE_LAYER";

		private List<TransformData> transforms = new List<TransformData>();

		public RenderSpace Space { get; private set; }

		public Transform Root { get; private set; }

		public Transform this[int transformId] => transforms[transformId].transform;

		public TransformData GetTransformData(int transformId)
		{
			return transforms[transformId];
		}

		public void MarkInUse(int transformId)
		{
			TransformData value = transforms[transformId];
			if (value.inUse)
			{
				throw new InvalidOperationException("This transform is already in use.");
			}
			value.inUse = true;
			transforms[transformId] = value;
		}

		public void ClearInUse(int transformId)
		{
			TransformData value = transforms[transformId];
			if (!value.inUse)
			{
				throw new InvalidOperationException("This transform is not in use.");
			}
			value.inUse = false;
			transforms[transformId] = value;
		}

		public TransformManager(RenderSpace space, Transform root)
		{
			Space = space;
			Root = root;
		}

		public void HandleUpdate(TransformsUpdate update)
		{
			if (!update.removals.IsEmpty)
			{
				Span<int> span = RenderingManager.Instance.SharedMemory.AccessData(update.removals);
				for (int i = 0; i < span.Length; i++)
				{
					int num = span[i];
					if (num < 0)
					{
						break;
					}
					if (RenderingManager.IsDebug)
					{
						for (int j = 0; j < transforms[i].transform.childCount; j++)
						{
							transforms[i].transform.GetChild(j).name += $"-D:{num}";
						}
					}
					TransformData transformData = transforms[num];
					transformData.transform.DetachChildren();
					UnityEngine.Object.Destroy(transformData.transform.gameObject);
					transforms[num] = transforms[transforms.Count - 1];
					transforms.RemoveAt(transforms.Count - 1);
				}
			}
			while (transforms.Count < update.targetTransformCount)
			{
				transforms.Add(new TransformData(AlocateTransform(Space.Id, transforms.Count)));
			}
			if (!update.parentUpdates.IsEmpty)
			{
				Span<TransformParentUpdate> span2 = RenderingManager.Instance.SharedMemory.AccessData(update.parentUpdates);
				for (int k = 0; k < span2.Length; k++)
				{
					TransformParentUpdate transformParentUpdate = span2[k];
					if (transformParentUpdate.transformId < 0)
					{
						break;
					}
					transforms[transformParentUpdate.transformId].transform.SetParent(null, worldPositionStays: false);
					if (RenderingManager.IsDebug)
					{
						transforms[transformParentUpdate.transformId].transform.name += "-P:null";
					}
				}
				for (int l = 0; l < span2.Length; l++)
				{
					TransformParentUpdate transformParentUpdate2 = span2[l];
					if (transformParentUpdate2.transformId < 0)
					{
						break;
					}
					TransformData transformData2 = transforms[transformParentUpdate2.transformId];
					TransformData transformData3 = transforms[transformParentUpdate2.newParentId];
					transformData2.transform.SetParent(transformData3.transform);
					if (transformData2.transform.gameObject.layer != transformData3.transform.gameObject.layer)
					{
						LayerRenderable.SetLayerRecursively(transformData2.transform, transformData3.transform.gameObject.layer);
					}
					if (RenderingManager.IsDebug)
					{
						transforms[transformParentUpdate2.transformId].transform.name += $"-P:{transformParentUpdate2.newParentId}";
					}
				}
			}
			if (update.poseUpdates.IsEmpty)
			{
				return;
			}
			Span<UnityTransformPoseUpdate> span3 = RenderingManager.Instance.SharedMemory.AccessData(update.poseUpdates.As<UnityTransformPoseUpdate>());
			for (int m = 0; m < span3.Length; m++)
			{
				UnityTransformPoseUpdate unityTransformPoseUpdate = span3[m];
				if (unityTransformPoseUpdate.transformId >= 0)
				{
					Transform transform = transforms[unityTransformPoseUpdate.transformId].transform;
					transform.localPosition = unityTransformPoseUpdate.pose.position;
					transform.localRotation = unityTransformPoseUpdate.pose.rotation;
					transform.localScale = unityTransformPoseUpdate.pose.scale;
					continue;
				}
				break;
			}
		}

		private Transform AlocateTransform(int renderSpaceId, int transformId)
		{
			GameObject gameObject = new GameObject(RenderingManager.IsDebug ? $"{renderSpaceId}:{transformId}" : "");
			Transform transform = gameObject.transform;
			gameObject.layer = Space.DefaultLayer;
			transform.SetParent(Root, worldPositionStays: false);
			return transform;
		}
	}
	public interface IPoolable
	{
		void Clean();
	}
	public static class MemoryPool
	{
		public static T Borrow<T>() where T : IPoolable, new()
		{
			return MemoryPool<T>.Borrow();
		}

		public static void Return<T>(ref T instance) where T : IPoolable, new()
		{
			MemoryPool<T>.Return(ref instance);
		}
	}
	public static class MemoryPool<T> where T : IPoolable, new()
	{
		private static Stack<T> _instances = new Stack<T>();

		public static T Borrow()
		{
			lock (_instances)
			{
				if (_instances.Count == 0)
				{
					return new T();
				}
				return _instances.Pop();
			}
		}

		public static void Return(ref T instance)
		{
			instance.Clean();
			lock (_instances)
			{
				_instances.Push(instance);
			}
			instance = default(T);
		}
	}
	public class PackerMemoryPool : IMemoryPackerEntityPool
	{
		public static readonly PackerMemoryPool Instance = new PackerMemoryPool();

		public T Borrow<T>() where T : class, IMemoryPackable, new()
		{
			return PackerMemoryPool<T>.Borrow();
		}

		public void Return<T>(T value) where T : class, IMemoryPackable, new()
		{
			PackerMemoryPool<T>.Return(value);
		}
	}
	public static class PackerMemoryPool<T> where T : class, IMemoryPackable, new()
	{
		private static Stack<T> _instances = new Stack<T>();

		public static T Borrow()
		{
			lock (_instances)
			{
				if (_instances.Count == 0)
				{
					return new T();
				}
				return _instances.Pop();
			}
		}

		public static void Return(T instance)
		{
			lock (_instances)
			{
				_instances.Push(instance);
			}
			instance = null;
		}
	}
}
