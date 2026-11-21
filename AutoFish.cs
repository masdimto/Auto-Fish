using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace AutoFishingPlugin
{
    [ApiVersion(2, 1)]
    public class AutoFishing : TerrariaPlugin
    {
        public override string Name => "AutoFishing";
        public override Version Version => new Version(1, 4, 4);
        public override string Author => "CustomPlugin";
        public override string Description => "Auto fishing (/af), Visual Multi-Kail 5 Tier Pancingan di air, Monster Blood Moon, & Bonus +10 FP per 5 Quest.";

        private enum FishMode
        {
            Real,
            Mod
        }

        private Dictionary<int, FishMode> playerModes = new Dictionary<int, FishMode>();
        private Dictionary<int, DateTime> lastCatchTime = new Dictionary<int, DateTime>();
        private int tickCounter = 0;
        private MethodInfo fishingCheckMethod;

        // Menyimpan daftar proyektil kail visual (owner 255) per pemain agar bisa dihapus saat kail utama ditarik
        private Dictionary<int, List<int>> playerVisualBobbers = new Dictionary<int, List<int>>();
        private HashSet<int> spawnedForMainBobber = new HashSet<int>();
        private HashSet<int> manualBitingBobbers = new HashSet<int>();

        private Dictionary<int, int> lastVanillaQuests = new Dictionary<int, int>();
        private Dictionary<string, int> serverQuestCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private string SaveFilePath => Path.Combine(TShock.SavePath, "AnglerQuestProgress.txt");

        public AutoFishing(Main game) : base(game) { }

        public override void Initialize()
        {
            LoadQuestProgress();
            fishingCheckMethod = typeof(Projectile).GetMethod("FishingCheck", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            ServerApi.Hooks.ServerJoin.Register(this, OnServerJoin);
            ServerApi.Hooks.ServerLeave.Register(this, OnServerLeave);
            ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
            ServerApi.Hooks.ProjectileAIUpdate.Register(this, OnProjectileAIUpdate);

            Commands.ChatCommands.Add(new Command("autofish.use", ToggleAutoFish, "autofish", "af")
            {
                HelpText = "Gunakan /af [real/mod/off] untuk mengatur mode auto fishing."
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SaveQuestProgress();
                ServerApi.Hooks.ServerJoin.Deregister(this, OnServerJoin);
                ServerApi.Hooks.ServerLeave.Deregister(this, OnServerLeave);
                ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
                ServerApi.Hooks.ProjectileAIUpdate.Deregister(this, OnProjectileAIUpdate);
            }
            base.Dispose(disposing);
        }

        private void LoadQuestProgress()
        {
            try
            {
                serverQuestCounts.Clear();
                if (!File.Exists(SaveFilePath)) return;

                foreach (string line in File.ReadAllLines(SaveFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    int sep = line.LastIndexOf('=');
                    if (sep > 0 && int.TryParse(line.Substring(sep + 1), out int count))
                    {
                        string name = line.Substring(0, sep).Trim();
                        if (!string.IsNullOrEmpty(name))
                            serverQuestCounts[name] = count;
                    }
                }
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError($"[AutoFishing] Gagal memuat progres quest: {ex.Message}");
            }
        }

        private void SaveQuestProgress()
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (var kvp in serverQuestCounts)
                    lines.Add($"{kvp.Key}={kvp.Value}");
                File.WriteAllLines(SaveFilePath, lines);
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError($"[AutoFishing] Gagal menyimpan progres quest: {ex.Message}");
            }
        }

        private int GetServerQuests(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return 0;
            return serverQuestCounts.TryGetValue(playerName, out int count) ? count : 0;
        }

        private void OnServerJoin(JoinEventArgs args)
        {
            int who = args.Who;
            if (who >= 0 && who < Main.maxPlayers)
            {
                TSPlayer player = TShock.Players[who];
                if (player != null && player.TPlayer != null)
                    lastVanillaQuests[who] = player.TPlayer.anglerQuestsFinished;
            }
        }

        private void OnServerLeave(LeaveEventArgs args)
        {
            int who = args.Who;
            playerModes.Remove(who);
            lastCatchTime.Remove(who);
            lastVanillaQuests.Remove(who);
            ClearVisualBobbers(who);
        }

        private void ClearVisualBobbers(int playerIndex)
        {
            if (playerVisualBobbers.TryGetValue(playerIndex, out List<int> list))
            {
                foreach (int projIdx in list)
                {
                    if (projIdx >= 0 && projIdx < Main.maxProjectiles && Main.projectile[projIdx].active)
                    {
                        Main.projectile[projIdx].Kill();
                        NetMessage.SendData((int)PacketTypes.ProjectileDestroy, -1, -1, null, projIdx, Main.maxPlayers);
                    }
                }
                list.Clear();
            }
        }

        private void SpawnVisualBobbers(int playerIndex, Projectile mainBobber, int extraCount)
        {
            ClearVisualBobbers(playerIndex);
            if (!playerVisualBobbers.ContainsKey(playerIndex))
                playerVisualBobbers[playerIndex] = new List<int>();

            for (int k = 1; k <= extraCount; k++)
            {
                // Sebar posisi pelampung ke kiri dan kanan kail utama di air
                float offsetX = (k % 2 == 1 ? 1f : -1f) * (20f * ((k + 1) / 2));
                Vector2 spawnPos = new Vector2(mainBobber.position.X + offsetX, mainBobber.position.Y);

                // Gunakan owner = Main.maxPlayers (255 / server) agar HP wajib menggambar visual kailnya!
                int visIdx = Projectile.NewProjectile(
                    Projectile.GetNoneSource(),
                    spawnPos.X,
                    spawnPos.Y,
                    0f,
                    0f,
                    mainBobber.type,
                    0,
                    0f,
                    Main.maxPlayers
                );

                if (visIdx >= 0 && visIdx < Main.maxProjectiles)
                {
                    Projectile visProj = Main.projectile[visIdx];
                    visProj.ai[0] = 1f; // Status mengapung di air
                    visProj.netUpdate = true;
                    playerVisualBobbers[playerIndex].Add(visIdx);
                    NetMessage.SendData((int)PacketTypes.ProjectileNew, -1, -1, null, visIdx);
                }
            }
        }

        // Terapkan Bonus +10 Fishing Power per 5 Quest Server (Mulai 0, Maks 50 Quest = +100 FP)
        private void OnProjectileAIUpdate(ProjectileAiUpdateEventArgs args)
        {
            Projectile proj = args.Projectile;
            if (proj == null || !proj.active || !proj.bobber) return;

            int owner = proj.owner;
            if (owner >= 0 && owner < Main.maxPlayers)
            {
                TSPlayer tsPlayer = TShock.Players[owner];
                if (tsPlayer != null && tsPlayer.Active && tsPlayer.TPlayer != null)
                {
                    int cappedQuests = Math.Min(GetServerQuests(tsPlayer.Name), 50);
                    int bonusFP = (cappedQuests / 5) * 10;
                    if (bonusFP > 0)
                        tsPlayer.TPlayer.fishingSkill += bonusFP;
                }
            }
        }

        private int GetBobberCountForPole(int itemType)
        {
            switch (itemType)
            {
                case ItemID.GoldenFishingRod:      return 5; // Tier 5 (50%)
                case ItemID.SittingDucksFishingRod:
                case ItemID.HotlineFishingHook:    return 4; // Tier 4 (40-45%)
                case ItemID.ScarabFishingRod:
                case ItemID.FiberglassFishingPole:
                case ItemID.MechanicsRod:          return 3; // Tier 3 (30-35%)
                case ItemID.FisherofSouls:
                case ItemID.Fleshcatcher:
                case ItemID.BloodFishingRod:       return 2; // Tier 2 (20-25% - Chum Caster)
                default:                           return 1; // Tier 1 (5-15%)
            }
        }

        private void ToggleAutoFish(CommandArgs args)
        {
            int index = args.Player.Index;
            string subCommand = args.Parameters.Count > 0 ? args.Parameters[0].ToLower() : "";

            if (subCommand == "off" || subCommand == "stop" || subCommand == "matikan")
            {
                if (playerModes.Remove(index))
                {
                    lastCatchTime.Remove(index);
                    args.Player.SendInfoMessage("[AutoFish] Dimatikan.");
                }
                else
                {
                    args.Player.SendInfoMessage("[AutoFish] Memang sedang dalam keadaan mati.");
                }
                return;
            }

            FishMode targetMode;
            if (subCommand == "mod")
                targetMode = FishMode.Mod;
            else if (subCommand == "real" || string.IsNullOrEmpty(subCommand))
                targetMode = FishMode.Real;
            else
            {
                args.Player.SendErrorMessage("Format salah! Gunakan: /af, /af real, /af mod, atau /af off");
                return;
            }

            if (playerModes.TryGetValue(index, out FishMode currentMode) && currentMode == targetMode)
            {
                playerModes.Remove(index);
                lastCatchTime.Remove(index);
                args.Player.SendInfoMessage($"[AutoFish] Mode {targetMode} dimatikan.");
            }
            else
            {
                playerModes[index] = targetMode;
                if (targetMode == FishMode.Real)
                    args.Player.SendSuccessMessage("[AutoFish] Mode REAL aktif (100% mekanik Terraria). Lempar pancing ke air!");
                else
                    args.Player.SendSuccessMessage("[AutoFish] Mode MOD aktif (Anti line break / tidak pernah zonk). Lempar pancing ke air!");
            }
        }

        private void OnGameUpdate(EventArgs args)
        {
            // 1. Lacak progres quest Angler server
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                TSPlayer tsPlayer = TShock.Players[i];
                if (tsPlayer == null || !tsPlayer.Active || tsPlayer.TPlayer == null) continue;

                int currentVanillaQuests = tsPlayer.TPlayer.anglerQuestsFinished;
                if (!lastVanillaQuests.TryGetValue(i, out int prevVanillaQuests))
                {
                    lastVanillaQuests[i] = currentVanillaQuests;
                }
                else if (currentVanillaQuests > prevVanillaQuests)
                {
                    int delta = currentVanillaQuests - prevVanillaQuests;
                    lastVanillaQuests[i] = currentVanillaQuests;

                    int updatedServerQuests = GetServerQuests(tsPlayer.Name) + delta;
                    serverQuestCounts[tsPlayer.Name] = updatedServerQuests;
                    SaveQuestProgress();

                    if (updatedServerQuests <= 50)
                    {
                        if (updatedServerQuests % 5 == 0)
                        {
                            int totalBonusFP = (updatedServerQuests / 5) * 10;
                            if (updatedServerQuests == 50)
                                tsPlayer.SendSuccessMessage("[Angler Mastery MAX] 50/50 Quest tercapai! Bonus Fishing Power maksimal (+100 FP)!");
                            else
                                tsPlayer.SendSuccessMessage($"[Angler Mastery] Progres Quest: {updatedServerQuests}/50! Fishing Power permanen +10 (Total: +{totalBonusFP} FP)!");
                        }
                        else
                        {
                            tsPlayer.SendInfoMessage($"[Angler Mastery] Progres Quest: {updatedServerQuests}/50 (Bonus +10 FP berikutnya di quest ke-{((updatedServerQuests / 5) + 1) * 5}).");
                        }
                    }
                }
            }

            // 2. Pantau kail pemain -> Munculkan visual pelampung ekstra & bersihkan saat kail ditarik
            bool[] hasActiveMainBobber = new bool[Main.maxPlayers];

            for (int p = 0; p < Main.maxProjectiles; p++)
            {
                Projectile proj = Main.projectile[p];
                if (!proj.active || !proj.bobber)
                {
                    spawnedForMainBobber.Remove(p);
                    manualBitingBobbers.Remove(p);
                    continue;
                }

                int owner = proj.owner;
                if (owner < 0 || owner >= Main.maxPlayers) continue;

                hasActiveMainBobber[owner] = true;

                TSPlayer tsPlayer = TShock.Players[owner];
                if (tsPlayer == null || !tsPlayer.Active || tsPlayer.TPlayer == null) continue;

                Item heldItem = tsPlayer.TPlayer.HeldItem;
                int bobberCount = GetBobberCountForPole(heldItem != null ? heldItem.type : 0);

                // Saat kail utama menyentuh air (ai[0] == 1), munculkan visual kail tambahan di sebelahnya
                if (proj.ai[0] == 1f && !spawnedForMainBobber.Contains(p))
                {
                    spawnedForMainBobber.Add(p);
                    if (bobberCount > 1)
                    {
                        SpawnVisualBobbers(owner, proj, bobberCount - 1);
                    }
                }

                // Multi-kail saat memancing manual
                if (!playerModes.ContainsKey(owner))
                {
                    if (proj.ai[1] < 0f && !manualBitingBobbers.Contains(p))
                    {
                        manualBitingBobbers.Add(p);
                        if (bobberCount > 1)
                            GiveMultiCatchRewards(tsPlayer, proj, bobberCount - 1, FishMode.Real);
                    }
                    else if (proj.ai[1] >= 0f)
                    {
                        manualBitingBobbers.Remove(p);
                    }
                }
            }

            // Hapus visual pelampung ekstra jika pemain sudah menarik pancingannya
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                if (!hasActiveMainBobber[i] && playerVisualBobbers.TryGetValue(i, out List<int> list) && list.Count > 0)
                {
                    ClearVisualBobbers(i);
                }
            }

            // 3. Proses AutoFish (/af)
            tickCounter++;
            if (tickCounter < 10) return;
            tickCounter = 0;

            foreach (var kvp in playerModes)
            {
                int playerIndex = kvp.Key;
                FishMode mode = kvp.Value;

                TSPlayer tsPlayer = TShock.Players[playerIndex];
                if (tsPlayer == null || !tsPlayer.Active || tsPlayer.TPlayer.dead) continue;

                if (lastCatchTime.TryGetValue(playerIndex, out DateTime lastTime))
                {
                    if ((DateTime.UtcNow - lastTime).TotalSeconds < 1.2) continue;
                }

                for (int i = 0; i < Main.maxProjectiles; i++)
                {
                    Projectile proj = Main.projectile[i];
                    if (proj.active && proj.owner == playerIndex && proj.bobber && proj.ai[1] < 0f)
                    {
                        Item heldItem = tsPlayer.TPlayer.HeldItem;
                        int bobberCount = GetBobberCountForPole(heldItem != null ? heldItem.type : 0);

                        GiveMultiCatchRewards(tsPlayer, proj, bobberCount, mode);
                        lastCatchTime[playerIndex] = DateTime.UtcNow;

                        proj.ai[0] = 0f;
                        proj.ai[1] = 0f;
                        proj.localAI[1] = 0f;
                        proj.netUpdate = true;
                        break;
                    }
                }
            }
        }

        private void GiveMultiCatchRewards(TSPlayer tsPlayer, Projectile proj, int catchCount, FishMode mode)
        {
            for (int c = 0; c < catchCount; c++)
            {
                proj.ai[1] = -1f;
                proj.localAI[1] = 0f;

                int caughtResult = 0;
                if (mode == FishMode.Real)
                {
                    caughtResult = RunFishingCheck(proj);
                }
                else
                {
                    for (int attempt = 0; attempt < 15; attempt++)
                    {
                        proj.ai[1] = -1f;
                        proj.localAI[1] = 0f;
                        caughtResult = RunFishingCheck(proj);
                        if (caughtResult != 0) break;
                    }
                }

                if (caughtResult > 0)
                {
                    tsPlayer.GiveItem(caughtResult, 1);
                }
                else if (caughtResult < 0)
                {
                    int npcType = Math.Abs(caughtResult);
                    int spawnX = (int)proj.Center.X;
                    int spawnY = (int)proj.Center.Y - 16;
                    int npcIdx = NPC.NewNPC(NPC.GetBossSpawnSource(tsPlayer.Index), spawnX, spawnY, npcType);
                    if (npcIdx >= 0 && npcIdx < Main.maxNPCs)
                    {
                        Main.npc[npcIdx].netUpdate = true;
                        NetMessage.SendData((int)PacketTypes.NpcUpdate, -1, -1, null, npcIdx);
                    }
                }
            }
        }

        private int RunFishingCheck(Projectile proj)
        {
            if (fishingCheckMethod != null)
                fishingCheckMethod.Invoke(proj, null);

            int result = (int)proj.localAI[1];
            if (result == 0 && proj.ai[1] > 0f)
                result = (int)proj.ai[1];
            return result;
        }
    }
}
