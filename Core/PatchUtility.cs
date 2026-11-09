using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace Transform.Core;

/// <summary>
/// 跨形态模块共享的 Harmony 补丁安装工具，消除各 Plugin 中重复的
/// TryPatchOptionalMethod / ConfigureCameraFallbackPatch 实现。
/// </summary>
internal static class PatchUtility
{
	/// <summary>
	/// 尝试安装一个可选的 Harmony 前缀补丁。目标方法在当前游戏版本中不存在时静默跳过
	/// （记录一条 Info 日志），返回 true 表示已安装。
	/// </summary>
	internal static bool TryPatchOptionalMethod(
		Harmony harmony,
		ManualLogSource log,
		string moduleTag,
		Type declaringType,
		string methodName,
		Type[] parameterTypes,
		Type patchClassType,
		string prefixName,
		string description)
	{
		try
		{
			// Current PEAK adds PhotonMessageInfo directly to native RPC signatures.
			// Resolve the known arguments plus that optional suffix without issuing
			// Harmony's missing-method warning for every module at startup.
			MethodInfo targetMethod = FindCompatibleMethod(declaringType, methodName, parameterTypes);
			if (targetMethod == null)
			{
				log?.LogInfo("[" + moduleTag + "] Optional " + description + " patch skipped; method is not present in this game build.");
				return false;
			}

			MethodInfo prefixMethod = AccessTools.Method(patchClassType, prefixName);
			if (prefixMethod == null)
			{
				log?.LogWarning("[" + moduleTag + "] Optional " + description + " patch skipped; prefix method was not found.");
				return false;
			}

			harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefixMethod));
			return true;
		}
		catch (Exception ex)
		{
			log?.LogWarning("[" + moduleTag + "] Optional " + description + " patch failed: " + ex.Message);
			return false;
		}
	}

	internal static MethodInfo FindCompatibleMethod(Type type, string name, Type[] arguments)
	{
		arguments = arguments ?? Type.EmptyTypes;
		const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
		// Prefer an exact overload if both signatures exist in a transitional build.
		for (int suffix = 0; suffix <= 1; suffix++)
		for (Type current = type; current != null; current = current.BaseType)
		foreach (MethodInfo method in current.GetMethods(flags | BindingFlags.DeclaredOnly))
		{
			if (method.Name != name || method.ContainsGenericParameters) continue;
			ParameterInfo[] parameters = method.GetParameters();
			if (parameters.Length != arguments.Length + suffix) continue;
			if (suffix == 1 && parameters[parameters.Length - 1].ParameterType != typeof(Photon.Pun.PhotonMessageInfo)) continue;
			bool matches = true;
			for (int i = 0; i < arguments.Length; i++)
				if (parameters[i].ParameterType != arguments[i]) { matches = false; break; }
			if (matches) return method;
		}
		return null;
	}

	/// <summary>
	/// 安装相机回退补丁：MainCameraMovement.LateUpdate 的 postfix，让原版相机代码运行后重新
	/// 对准形态角色。成功时返回 true（调用方应把对应 Controller 的 CameraOverridePatchActive
	/// 置为 true，让控制器停止从自身 LateUpdate 双重驱动相机）。
	/// </summary>
	internal static bool TryPatchCameraFallback(Harmony harmony, ManualLogSource log, string moduleTag, Type patchClassType, string postfixName)
	{
		try
		{
			Type cameraType = AccessTools.TypeByName("MainCameraMovement");
			if (cameraType == null)
			{
				// The type lives in the global namespace; fall back to a full-name scan.
				foreach (Type candidate in AccessTools.AllTypes())
				{
					if (candidate.FullName == "MainCameraMovement")
					{
						cameraType = candidate;
						break;
					}
				}
			}
			if (cameraType == null)
			{
				log?.LogWarning("[" + moduleTag + "] Camera fallback patch skipped; MainCameraMovement type not found.");
				return false;
			}

			MethodInfo lateUpdate = AccessTools.Method(cameraType, "LateUpdate");
			if (lateUpdate == null)
			{
				log?.LogWarning("[" + moduleTag + "] Camera fallback patch skipped; MainCameraMovement.LateUpdate not found.");
				return false;
			}

			MethodInfo postfix = AccessTools.Method(patchClassType, postfixName);
			if (postfix == null)
			{
				log?.LogWarning("[" + moduleTag + "] Camera fallback patch skipped; postfix method not found.");
				return false;
			}

			harmony.Patch(lateUpdate, postfix: new HarmonyMethod(postfix));
			return true;
		}
		catch (Exception ex)
		{
			log?.LogWarning("[" + moduleTag + "] Camera fallback patch failed: " + ex.Message);
			return false;
		}
	}
}
