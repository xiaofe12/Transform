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
			MethodInfo targetMethod = AccessTools.Method(declaringType, methodName, parameterTypes);
			if (targetMethod == null)
			{
				// PEAKERRpcInfo patcher appends a PhotonMessageInfo parameter to every [PunRPC]
				// method at runtime, so exact-signature lookups miss. Retry by name only: our
				// prefixes inject at most Character __instance, which patching by name supports.
				targetMethod = AccessTools.Method(declaringType, methodName);
			}
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
