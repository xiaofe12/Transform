using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using PhotonPlayer = Photon.Realtime.Player;

namespace Transform.Core;

/// <summary>
/// 跨模块共享的 Photon 模组在场广告与检测逻辑（原 Wind/Critter 各有一份几乎相同的实现）。
/// 各模块只需传入自己的玩家属性键（propertyKey）。
/// </summary>
internal static class ModPresence
{
	/// <summary>在房间内时将模组标记写入本地玩家的自定义属性；离开房间后重置，便于下次入房重发。</summary>
	internal static void Advertise(string propertyKey, string version, ref bool advertised)
	{
		if (PhotonNetwork.InRoom)
		{
			if (!advertised)
			{
				advertised = true;
				PhotonNetwork.LocalPlayer.SetCustomProperties(
					new Hashtable { { propertyKey, version } });
			}
		}
		else
		{
			advertised = false;
		}
	}

	/// <summary>该玩家是否运行了本模组（其玩家属性带指定标记）。</summary>
	internal static bool PlayerHasMod(PhotonPlayer player, string propertyKey)
	{
		Hashtable properties = player != null ? player.CustomProperties : null;
		return properties != null && properties.ContainsKey(propertyKey);
	}

	/// <summary>房间内是否存在未运行本模组的远端玩家。</summary>
	internal static bool RoomHasUnmoddedPlayers(string propertyKey)
	{
		if (!PhotonNetwork.InRoom || PhotonNetwork.PlayerList == null)
		{
			return false;
		}

		foreach (PhotonPlayer player in PhotonNetwork.PlayerList)
		{
			if (player == null || player.IsLocal)
			{
				continue;
			}
			if (!PlayerHasMod(player, propertyKey))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>枚举房间内未运行本模组的远端玩家。</summary>
	internal static IEnumerable<PhotonPlayer> UnmoddedRemotePlayers(string propertyKey)
	{
		if (!PhotonNetwork.InRoom || PhotonNetwork.PlayerList == null)
		{
			yield break;
		}

		foreach (PhotonPlayer player in PhotonNetwork.PlayerList)
		{
			if (player == null || player.IsLocal)
			{
				continue;
			}
			if (!PlayerHasMod(player, propertyKey))
			{
				yield return player;
			}
		}
	}

	/// <summary>房主是否运行了本模组；离线/无房主时视为已运行（本地玩家即宿主）。</summary>
	internal static bool MasterHasMod(string propertyKey)
	{
		try
		{
			if (!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode)
			{
				return true;
			}
			PhotonPlayer master = PhotonNetwork.MasterClient;
			if (master == null)
			{
				return true;
			}
			return PlayerHasMod(master, propertyKey);
		}
		catch
		{
			return true;
		}
	}
}
