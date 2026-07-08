using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CupheadOnline.UI
{
    /// <summary>
    /// Minimal localization layer for the mod's own UI text.
    ///
    /// Keys are the exact English source strings; lookups that miss fall back to
    /// the English input, so partially translated surfaces degrade gracefully.
    /// Logs, bug-report contents, and internal state markers stay English on
    /// purpose — only player-facing UI goes through <see cref="T"/>/<see cref="F"/>.
    /// </summary>
    internal static class Loc
    {
        static Font _cjkFallbackFont;
        static bool _cjkFallbackAttempted;

        public static bool IsChinese
        {
            get
            {
                string mode = Plugin.LanguageMode;
                if (string.Equals(mode, "Chinese", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(mode, "English", StringComparison.OrdinalIgnoreCase))
                    return false;

                // Auto: follow the game's own language setting. SettingsData may
                // not be loaded during very early boot — treat that as English.
                try
                {
                    return Localization.language == Localization.Languages.SimplifiedChinese;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static string T(string en)
        {
            if (string.IsNullOrEmpty(en) || !IsChinese)
                return en;

            string zh;
            return Zh.TryGetValue(en, out zh) ? zh : en;
        }

        public static string F(string enFormat, params object[] args)
        {
            return string.Format(T(enFormat), args);
        }

        /// <summary>
        /// Make sure a label's font can render CJK. Text cloned from the game's
        /// own menu keeps the game font (which already has CJK glyphs when the
        /// game language is Chinese); only fonts without CJK coverage — e.g. the
        /// vintage English menu font or builtin Arial — get swapped for an OS
        /// dynamic font.
        /// </summary>
        public static void EnsureCjkFont(Text label)
        {
            if (label == null || !IsChinese)
                return;

            var font = label.font;
            if (font != null && FontHasCjk(font))
                return;

            var fallback = GetCjkFallbackFont();
            if (fallback != null)
                label.font = fallback;
        }

        static bool FontHasCjk(Font font)
        {
            try
            {
                return font.HasCharacter('联') && font.HasCharacter('机'); // 联机
            }
            catch
            {
                return false;
            }
        }

        static Font GetCjkFallbackFont()
        {
            if (_cjkFallbackAttempted)
                return _cjkFallbackFont;

            _cjkFallbackAttempted = true;
            string[] candidates = { "Microsoft YaHei", "微软雅黑", "SimHei", "SimSun" };
            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    var font = Font.CreateDynamicFontFromOSFont(candidates[i], 32);
                    if (font != null && FontHasCjk(font))
                    {
                        _cjkFallbackFont = font;
                        Plugin.Log.LogInfo("[Loc] CJK fallback font: " + candidates[i]);
                        break;
                    }
                }
                catch
                {
                }
            }

            if (_cjkFallbackFont == null)
                Plugin.Log.LogWarning("[Loc] No CJK-capable OS font found; Chinese UI may show missing glyphs.");
            return _cjkFallbackFont;
        }

        static readonly Dictionary<string, string> Zh = new Dictionary<string, string>
        {
            // ── Main menu / lobby labels ─────────────────────────────────────
            { "MULTIPLAYER", "联机模式" },
            { "CREDITS", "制作名单" },
            { "MULTIPLAYER LOBBY", "联机大厅" },
            { "Host, queue friends, sync saves, and launch together.", "创建房间、集结好友、同步存档，一起开打。" },
            { "ACTIONS", "操作" },
            { "ROSTER", "成员" },
            { "HOST GAME", "创建房间" },
            { "JOIN GAME", "加入房间" },
            { "INVITE FRIEND", "邀请好友" },
            { "RETRY LAST", "重试上次" },
            { "COPY LOBBY ID", "复制房间号" },
            { "COPY DIAGNOSTICS", "复制诊断信息" },
            { "BACK", "返回" },
            { "SWATCHES", "配色" },
            { "AUTO", "自动" },
            { "Selected: ", "已选：" },
            { "Escape / Controller B: Back", "Esc / 手柄 B：返回" },
            { "Steam unavailable outside Steam", "未通过 Steam 启动，联机不可用" },
            { "[ Press Escape to go back ]", "[ 按 Esc 返回 ]" },
            { "Multiplayer Mod", "联机 Mod" },
            { "Made for Daniel", "献给 Daniel" },
            { "Special thanks to Internallinked", "特别感谢 Internallinked" },
            { "cuz me and him wanna play.", "因为我俩想一起玩。" },
            { "SAVE SLOT: ", "存档槽位：" },
            { "LEAD: ", "主机角色：" },
            { "HOST SAVE: ", "主机存档：" },
            { "WAIT FOR HOST", "等待主机" },
            { "REQUEST HOST SAVE", "请求主机存档" },
            { "OPEN FRIENDS", "打开好友列表" },
            { "JOIN CLIPBOARD", "加入剪贴板房间" },
            { "SEND RESYNC", "发送重同步" },
            { "REQUEST RESYNC", "请求重同步" },
            { "START GAME", "开始游戏" },
            { "EXPORT BUG REPORT", "导出问题报告" },
            { "DISCONNECT", "断开连接" },
            { "PLAYER COLOR", "玩家颜色" },

            // ── Lobby hints ──────────────────────────────────────────────────
            { "Choose the save slot for this run here in the lobby. The host can start as soon as a save is selected.", "在大厅里选择本局使用的存档槽位。选好存档后主机即可开始。" },
            { "The host picked the current save. You will follow when the host starts.", "主机已选好存档。主机开始后你会自动跟随进入。" },
            { "Connected. Wait for the host to choose a save slot.", "已连接。等待主机选择存档槽位。" },
            { "Leave the current session before starting a fresh host lobby.", "请先退出当前会话，再重新创建房间。" },
            { "Create a friends-only Steam lobby for one guest.", "创建一个仅好友可见的 Steam 房间。" },
            { "Choose whether the host starts as {0}. The guest automatically becomes {1}.", "选择主机是否以 {0} 开局，客机将自动成为 {1}。" },
            { "Ask the host for the current save selection again. This fixes missed lobby sync packets.", "重新向主机请求当前存档选择，可修复丢失的大厅同步包。" },
            { "Waiting for the host to start the selected save.", "等待主机启动所选存档。" },
            { "Open Steam Friends and wait for the host invite.", "打开 Steam 好友列表，等待主机邀请。" },
            { "Join lobby #{0} straight from the clipboard.", "直接加入剪贴板中的房间 #{0}。" },
            { "Wait for a Steam invite. The Friends overlay opens automatically.", "等待 Steam 邀请，好友界面会自动打开。" },
            { "Wait for a Steam invite, or copy a lobby ID to the clipboard to join directly.", "等待 Steam 邀请，或将房间号复制到剪贴板后直接加入。" },
            { "Use Left, Right, or Accept to choose your lobby swatch and in-game tint. Auto keeps the first two gameplay slots classic and gives extra participants stable colors.", "用左右方向键或确认键选择你的配色和游戏内着色。自动模式让前两名玩家保持原版外观，并为额外玩家分配固定颜色。" },
            { "Send a fresh sync bundle and boss-priority burst to the guest.", "向客机发送一份全新同步包和 Boss 优先突发同步。" },
            { "Ask the host to resend the current session state.", "请求主机重发当前会话状态。" },
            { "Open Steam's invite dialog for the current lobby.", "为当前房间打开 Steam 邀请窗口。" },
            { "Available once you host a lobby.", "创建房间后可用。" },
            { "Start the run directly from the multiplayer lobby with the selected save and character order.", "使用所选存档和角色顺序，直接从联机大厅开局。" },
            { "Retry the last host or join action without leaving the menu.", "无需离开菜单，重试上一次创建或加入操作。" },
            { "Becomes available after a host or join attempt.", "在尝试过创建或加入后可用。" },
            { "Copy the current Steam lobby ID so someone can join from the clipboard.", "复制当前 Steam 房间号，好友可通过剪贴板直接加入。" },
            { "Available once a lobby exists.", "房间存在后可用。" },
            { "Export a bug report folder with diagnostics, logs, and config files.", "导出包含诊断、日志和配置文件的问题报告文件夹。" },
            { "Disconnect the current Steam session and return to the main menu.", "断开当前 Steam 会话并返回主菜单。" },
            { "Return to the main menu.", "返回主菜单。" },

            // ── Lobby statuses ───────────────────────────────────────────────
            { "Only the host can choose the multiplayer save.", "只有主机能选择联机存档。" },
            { "Waiting for the host to choose a save slot.", "等待主机选择存档槽位。" },
            { "Leave the current session before hosting again.", "请先退出当前会话再重新创建。" },
            { "Steam is still busy. Please wait.", "Steam 正忙，请稍候。" },
            { "Start Game is available now.", "现在可以开始游戏了。" },
            { "Pick a save first.", "请先选择存档。" },
            { "Waiting for the host to start.", "等待主机开始。" },
            { "Waiting for a Steam invite...", "等待 Steam 邀请…" },
            { "Waiting for a Steam invite...\nPress Shift+Tab to open Steam overlay.", "等待 Steam 邀请…\n按 Shift+Tab 打开 Steam 界面。" },
            { "Leave the current session before joining another lobby.", "请先退出当前会话，再加入其他房间。" },
            { "Guest connected.\nChoose SAVE SLOT and LEAD, then press START GAME.", "客机已连接。\n选择存档槽位和主机角色，然后按开始游戏。" },
            { "Connected.\nWait for the host to choose a save and start.", "已连接。\n等待主机选择存档并开始。" },
            { "Select an option.", "请选择一个选项。" },
            { "Operation timed out.\nPlease try again.", "操作超时。\n请重试。" },
            { "Overlay closed. Select an option.", "已关闭 Steam 界面。请选择一个选项。" },
            { "Connect a guest before opening the save slots.", "请先等客机连接，再打开存档槽位。" },
            { "Could not open the save slots.", "无法打开存档槽位。" },
            { "Connect a guest before choosing the multiplayer save.", "请先等客机连接，再选择联机存档。" },
            { "Only the host can choose the character order.", "只有主机能选择角色顺序。" },
            { "That save slot is unavailable right now.", "该存档槽位当前不可用。" },
            { "Host will play {0}. Guest will play {1}.", "主机将使用 {0}，客机将使用 {1}。" },
            { "Only the host can start the run.", "只有主机能开始游戏。" },
            { "Selected save slot {0}. The host can start when ready.", "已选择存档槽位 {0}。主机就绪后即可开始。" },
            { "No invite received yet.\nPress Join Game again to open your Friends list.", "尚未收到邀请。\n再按一次加入房间即可打开好友列表。" },
            { "Bug report exported to:\n{0}", "问题报告已导出到：\n{0}" },
            { "Peer: {0}\nState: {1}", "对方：{0}\n状态：{1}" },
            { "No lobby yet.\n\nHost a game to create a Steam lobby, or join one to see the full party roster here.", "尚未加入房间。\n\n创建房间即可开启 Steam 大厅，加入房间后这里会显示完整成员列表。" },
            { "Steam is not ready.\n\nLaunch Cuphead through Steam to populate the lobby roster and use invites.", "Steam 未就绪。\n\n请通过 Steam 启动茶杯头，以显示大厅成员并使用邀请功能。" },

            // ── Network statuses ─────────────────────────────────────────────
            { "Creating lobby...", "正在创建房间…" },
            { "Player connecting...", "玩家连接中…" },
            { "Almost there…", "马上就好…" },
            { "Additional participant authorizing...", "额外参与者授权中…" },
            { "Participant connected.\nSelect OPEN SAVE SLOT to choose a file.", "玩家已连接。\n请选择存档槽位。" },
            { "Connected.\nWaiting for the host to choose a save slot.", "已连接。\n等待主机选择存档槽位。" },
            { "Joining lobby #{0}...", "正在加入房间 #{0}…" },
            { "Waiting for a gameplay peer...\nExtra lobby members will queue automatically.\nUse Invite Friend to send another Steam invite.", "等待玩家加入…\n多余的房间成员会自动排队。\n可用邀请好友再发一个 Steam 邀请。" },
            { "Steam took too long to create the lobby.\nUse Retry Last or Host Game to try again.", "Steam 创建房间超时。\n请用重试上次或创建房间再试。" },
            { "Steam took too long to join the lobby.\nUse Retry Last or Join Game to try again.", "Steam 加入房间超时。\n请用重试上次或加入房间再试。" },
            { "The handshake with {0} timed out.", "与 {0} 的握手超时。" },
            { "{0} stopped responding.", "{0} 已无响应。" },
            { "{0} left the lobby.", "{0} 离开了房间。" },
            { "{0} disconnected.", "{0} 已断开连接。" },
            { "Connection closed.", "连接已断开。" },
            { "Waiting for the next gameplay peer...\nExtra lobby members will queue automatically.", "等待下一位玩家加入…\n多余的房间成员会自动排队。" },
            { "Use Retry Last or Join Game to try again.", "请用重试上次或加入房间再试。" },
            { "Connecting to {0}...", "正在连接 {0}…" },
            { "Lobby joined.\n{0} is using the active gameplay slot.\nWaiting for the host to open the next slot...", "已加入房间。\n{0} 正在使用当前游玩位。\n等待主机空出下一个位置…" },
            { "another player", "另一位玩家" },
            { "Session shut down locally.", "已在本地关闭会话。" },
            { "CupHeads report saved", "CupHeads 报告已保存" },
            { "Connected - {0}", "已连接 - {0}" },

            // Retry labels
            { "REOPEN LOBBY", "重开房间" },
            { "RETRY HOST", "重试创建" },
            { "RECONNECT", "重新连接" },
            { "REJOIN RUN", "重回对局" },
            { "RETRY JOIN", "重试加入" },
            { "REJOIN LOBBY", "重回房间" },

            // Steam badge (translated at display; internal logic stays English)
            { "LAN CONNECTED", "LAN 已连接" },
            { "LAN HOST", "LAN 主机" },
            { "LAN CLIENT", "LAN 客机" },
            { "NOT VIA STEAM", "未经 Steam 启动" },
            { "STEAM OFFLINE", "Steam 离线" },
            { "OVERLAY OFF", "界面叠加已关" },
            { "CONNECTED", "已连接" },
            { "HOSTING LOBBY", "房主等待中" },
            { "IN LOBBY", "已在房间" },
            { "STEAM READY", "Steam 就绪" },

            // Invite / copy / retry statuses
            { "Host a lobby first, then invite a friend.", "请先创建房间，再邀请好友。" },
            { "Steam overlay is unavailable.\nEnable the overlay and try again.", "Steam 界面叠加不可用。\n请启用后重试。" },
            { "Invite dialog opened for lobby #{0}.", "已为房间 #{0} 打开邀请窗口。" },
            { "Steam overlay opened.\nWaiting for invite...", "已打开 Steam 界面。\n等待邀请…" },
            { "Retrying host setup...", "正在重试创建…" },
            { "Retrying lobby join...", "正在重试加入…" },
            { "No previous lobby is available to rejoin yet.", "暂无可重新加入的房间。" },
            { "No previous action is available to retry.", "没有可重试的操作。" },
            { "No Steam lobby ID was found in the clipboard.", "剪贴板里没有找到 Steam 房间号。" },
            { "Lobby ID copied to clipboard.", "房间号已复制到剪贴板。" },
            { "Host or join a lobby first.", "请先创建或加入房间。" },
            { "Connect first before requesting a resync.", "请先连接，再请求重同步。" },

            // Presence / roster
            { "LOBBY #", "房间 #" },
            { " (Host)", "（主机）" },
            { " (Queued)", "（排队中）" },
            { " (Connecting…)", "（连接中…）" },
            { " (You)", "（你）" },
            { " (Active)", "（游戏中）" },
            { "Connecting", "连接中" },
            { "Authorizing", "授权中" },
            { "Active", "游戏中" },
            { "Connected", "已连接" },
            { "Queued", "排队中" },
            { "Unknown", "未知" },
            { "You", "你" },
            { "Unknown Player", "未知玩家" },
            { "{0} connecting, {1} queued", "{0} 连接中，{1} 人排队" },
            { "{0} connecting", "{0} 连接中" },
            { "{0} active, {1} extra connected, {2} queued", "{0} 游戏中，另有 {1} 人已连接，{2} 人排队" },
            { "{0} active, {1} extra connected", "{0} 游戏中，另有 {1} 人已连接" },
            { "{0} active, {1} queued", "{0} 游戏中，{1} 人排队" },
            { "{0} active", "{0} 游戏中" },
            { "{0} queued in lobby", "{0} 人在房间排队" },
            { "No gameplay peer connected", "尚无玩家连接" },
            { "Queued for the next gameplay slot", "排队等待下一个游玩位" },
            { "Connected to {0}", "已连接到 {0}" },

            // Steam failure descriptions
            { "Steam is unavailable.\nLaunch Cuphead through Steam.", "Steam 不可用。\n请通过 Steam 启动茶杯头。" },
            { "If testing outside Steam, add steam_appid.txt next to Cuphead.exe.", "如需在 Steam 外测试，请在 Cuphead.exe 旁放置 steam_appid.txt。" },
            { "Steam could not create the lobby.\nCheck Steam and try again.", "Steam 无法创建房间。\n请检查 Steam 后重试。" },
            { "Steam is offline.\nReconnect Steam and try again.", "Steam 处于离线状态。\n请重新连接后再试。" },
            { "Steam timed out while creating the lobby.\nUse Retry Last.", "Steam 创建房间超时。\n请使用重试上次。" },
            { "Steam blocked lobby creation.\nCheck the overlay and privacy settings.", "Steam 拒绝了创建房间。\n请检查界面叠加与隐私设置。" },
            { "Steam could not create the lobby ({0}).\nUse Retry Last or Host Game to try again.", "Steam 无法创建房间（{0}）。\n请用重试上次或创建房间再试。" },
            { "Steam could not join the lobby.\nCheck Steam and try again.", "Steam 无法加入房间。\n请检查 Steam 后重试。" },
            { "That Steam lobby no longer exists.\nAsk the host for a fresh invite.", "该 Steam 房间已不存在。\n请让主机重新发一个邀请。" },
            { "That Steam lobby is already full.", "该 Steam 房间已满。" },
            { "Steam reported that this account is blocked from the lobby.", "Steam 提示此账号已被该房间屏蔽。" },
            { "Steam account restrictions prevented the lobby join.", "Steam 账号受限，无法加入房间。" },
            { "Steam blocked the lobby join.\nCheck your invite and privacy settings.", "Steam 拒绝了加入请求。\n请检查邀请与隐私设置。" },
            { "Steam could not join the lobby ({0}).\nUse Retry Last or Join Game to try again.", "Steam 无法加入房间（{0}）。\n请用重试上次或加入房间再试。" },
            { "Steam P2P timed out while contacting the other player.", "Steam P2P 联系对方玩家超时。" },
            { "The other player is not running Cuphead with the mod yet.", "对方尚未运行装有联机 Mod 的茶杯头。" },
            { "Steam denied the P2P session for this app.", "Steam 拒绝了本应用的 P2P 会话。" },
            { "The other player's Steam session went offline.", "对方的 Steam 会话已离线。" },
            { "Steam P2P reported an unknown error.", "Steam P2P 报告了未知错误。" },
            { "Steam P2P failed ({0}).", "Steam P2P 失败（{0}）。" },

            // ── Connection HUD ───────────────────────────────────────────────
            { "PING ---", "延迟 ---" },
            { "PING {0}ms - {1}", "延迟 {0}ms - {1}" },
            { "GOOD", "优" },
            { "OKAY", "中" },
            { "POOR", "差" },
            { "DISCONNECTED", "已断线" },
            { "Waiting for peer...", "等待玩家加入…" },
            { "Steam P2P connected.", "Steam P2P 已连接。" },
            { "Open Multiplayer to retry.", "打开联机菜单可重试。" },
            { "HOST", "主机" },
            { "CLIENT", "客机" },
            { "HOST LOBBY", "房主大厅" },
            { " queued", " 人排队" },
            { " extra", " 名额外玩家" },
            { "Peer", "玩家" },

            // ── Session panel / battle assist / hotkeys ──────────────────────
            { "SESSION PANEL", "会话面板" },
            { "[ F8 toggles this panel ]", "[ F8 开关此面板 ]" },
            { "BATTLE ASSIST", "战斗助手" },
            { "F6 RESYNC  F7 BARS  F9 COPY DIAG  F10 HIDE", "F6 重同步  F7 血条  F9 复制诊断  F10 隐藏" },
            { "TIME ", "用时 " },
            { "TIME LOCAL/HOST ", "用时(本机/主机) " },
            { "TIME LOCAL ", "本机用时 " },
            { "HOST ", "主机 " },
            { "   OFFSET ", "   偏移 " },
            { "DEATHS ", "死亡 " },
            { "   RETRIES ", "   重试 " },
            { "   PARRIES ", "   招架 " },
            { "Boss health bars enabled.", "Boss 血条已开启。" },
            { "Boss health bars disabled.", "Boss 血条已关闭。" },
            { "Battle assist HUD enabled.", "战斗助手已开启。" },
            { "Battle assist HUD disabled.", "战斗助手已关闭。" },
            { "No active multiplayer session to resync.", "当前没有可重同步的联机会话。" },
            { "Diagnostics copied to clipboard.", "诊断信息已复制到剪贴板。" },

            // ── Player colors ────────────────────────────────────────────────
            { "Auto", "自动" },
            { "Classic", "经典" },
            { "Teal", "青色" },
            { "Coral", "珊瑚" },
            { "Amber", "琥珀" },
            { "Mint", "薄荷" },
            { "Violet", "紫罗兰" },
            { "Lime", "青柠" },
            { "Classic for the first two gameplay slots, stable unique colors for extra players.", "前两名玩家保持经典外观，额外玩家获得固定专属颜色。" },
            { "Keep the vanilla look with no runtime tint.", "保持原版外观，不做任何着色。" },
            { "Bright teal tint for clean readability.", "明亮的青色着色，辨识度高。" },
            { "Warm coral tint that stands out in motion.", "温暖的珊瑚色着色，运动中依然醒目。" },
            { "Golden amber tint with a classic arcade feel.", "金色琥珀着色，复古街机风味。" },
            { "Soft mint tint for a lighter look.", "柔和的薄荷色着色，观感更轻盈。" },
            { "Violet tint for high contrast in crowded runs.", "紫罗兰着色，混战中对比度最高。" },
            { "Lime tint for the strongest map-side visibility.", "青柠着色，地图上的可见度最强。" },
            { "Runtime tint applied without editing sprite files.", "运行时着色，不修改任何贴图文件。" },
        };
    }
}
