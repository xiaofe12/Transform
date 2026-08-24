using BepInEx.Logging;

namespace Transform.Core;

/// <summary>
/// 各形态模块共用的变身预检逻辑，消除 7 份几乎相同的 CanTransform 重复实现。
/// 每个模块保留自己的专属检查（是否已在本形态、模块特有约束），把公共的状态校验集中到这里。
/// </summary>
internal static class FormValidation
{
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

	/// <summary>让模块在通过基础校验后，用统一的日志格式记录一次被拒原因。
	/// 返回 true 表示校验通过（reason 为 null）。</summary>
	internal static bool IsValid(ManualLogSource log, string moduleTag, string reason)
	{
		if (reason != null)
		{
			log?.LogWarning("[" + moduleTag + "] " + reason);
		}
		return reason == null;
	}
}
