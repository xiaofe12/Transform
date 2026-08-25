using System.Collections.Generic;
using BepInEx.Logging;

namespace Transform.Core;

/// <summary>
/// 各形态模块共用的变身预检逻辑，消除 7 份几乎相同的 CanTransform 重复实现。
/// 每个模块保留自己的专属检查（是否已在本形态、模块特有约束），把公共的状态校验集中到这里。
/// </summary>
internal static class FormValidation
{
	/// <summary>模块标签 → 最近失败消息。同键消息"沿变沿打"去重（菜单每帧查询会重复调用），恢复后再次失败重新记录。</summary>
	private static readonly Dictionary<string, string> _lastFailureByKey = new Dictionary<string, string>();

	/// <summary>记录一次失败；与上次相同则静默（菜单每帧查询去重）。</summary>
	internal static void ReportFailure(ManualLogSource log, string key, string message)
	{
		if (_lastFailureByKey.TryGetValue(key, out string last) && last == message)
		{
			return;
		}
		_lastFailureByKey[key] = message;
		log?.LogWarning(message);
	}

	/// <summary>条件恢复时清除失败状态，使下一次失败能重新记录。</summary>
	internal static void ClearFailure(string key)
	{
		_lastFailureByKey.Remove(key);
	}

	/// <summary>
	/// 校验角色是否处于可变身的基础状态：非空、数据就绪、未死亡/未晕倒/未攀爬。
	/// checkSpecialForm=false 时跳过"是否已处于其它特殊形态"检查（Zombie 模块原实现不含此检查，
	/// 它通过 ZombieController.IsOtherTransformModActive 独立判断）。
	/// 返回 null 表示通过，否则返回失败原因（供模块带各自前缀记录日志）。
	/// </summary>
	internal static string ValidateTransformable(Character character, bool checkSpecialForm = true)
	{
		if (character == null)
		{
			return "No local character found.";
		}
		if (checkSpecialForm && (character.isZombie || character.isBot || character.isScoutmaster))
		{
			return "Cannot transform while in another special form.";
		}
		if (character.data == null || character.refs == null || character.photonView == null)
		{
			return "Local character is not ready yet.";
		}
		if (character.data.dead
			|| character.data.passedOut
			|| character.data.fullyPassedOut
			|| character.data.fallSeconds > 0f
			|| character.data.isClimbing
			|| character.data.isRopeClimbing
			|| character.data.isVineClimbing)
		{
			return "Local character is not in a valid state to transform.";
		}
		return null;
	}

	/// <summary>记录一次被拒原因（经 ReportFailure 去重）。返回 true 表示通过。</summary>
	internal static bool IsValid(ManualLogSource log, string moduleTag, string reason)
	{
		if (reason != null)
		{
			ReportFailure(log, moduleTag, "[" + moduleTag + "] " + reason);
		}
		else
		{
			ClearFailure(moduleTag);
		}
		return reason == null;
	}
}
