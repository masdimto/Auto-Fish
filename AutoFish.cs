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
        public override Version Version => new Version(1, 4, 2);
        public override string Author => "CustomPlugin";
        public override string Description => "Auto fishing (/af) support Blood Moon enemy, Multi-Kail 5 Tier Pancingan stabil, dan Bonus +10 FP per 5 Quest (mulai 0, maks 50 quest).";

        private enum FishMode
        {
            Real,
            Mod
        }

        private Dictionary<int, FishMode> playerModes = new Dictionary<int, FishMode>();
        private Dictionary<int, DateTime> lastCatchTime = new Dictionary<int, DateTime>();
        private int tickCounter = 0;
        private MethodInfo fishingCheckMethod;

        // Pelacak kail yang sudah digandakan agar tidak memicu kloning berulang
        private HashSet<int> clonedBobberIdentities = new HashSet<int>();
        private int[] multiBobberCooldown = new int[Main.maxPlayers];

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
                multiBobberCooldown[who] = 0;
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
            if (who >= 0 && who < Main.maxPlayers)
                multiBobberCooldown[who] = 0;
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
            // 1. Update cooldown & lacak progres quest Angler server
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                if (multiBobberCooldown[i] > 0)
                    multiBobberCooldown[i]--;

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

            // 2. Deteksi Multi-Kail langsung dari Main.projectile (100% stabil di Mobile/HP)
            for (int p = 0; p < Main.maxProjectiles; p++)
            {
                Projectile proj = Main.projectile[p];
                if (!proj.active || !proj.bobber) continue;

                int owner = proj.owner;
                if (owner < 0 || owner >= Main.maxPlayers) continue;

                // Hanya proses kail baru yang belum pernah dikloning (timeLeft default kail = 36000)
                if (proj.timeLeft >= 35990 && !clonedBobberIdentities.Contains(p) && multiBobberCooldown[owner] == 0)
                {
                    TSPlayer tsPlayer = TShock.Players[owner];
                    if (tsPlayer != null && tsPlayer.Active && tsPlayer.TPlayer != null)
                    {
                        Item heldItem = tsPlayer.TPlayer.HeldItem;
                        int totalBobbers = GetBobberCountForPole(heldItem != null ? heldItem.type : 0);

                        if (totalBobbers > 1)
                        {
                            multiBobberCooldown[owner] = 20;
                            clonedBobberIdentities.Add(p);

                            int extraCount = totalBobbers - 1;
                            for (int k = 1; k <= extraCount; k++)
                            {
                                float spreadX = (k % 2 == 1 ? 1f : -1f) * (0.55f * ((k + 1) / 2));
                                float spreadY = -0.3f * ((k + 1) / 2);
                                Vector2 newVel = new Vector2(proj.velocity.X + spreadX, proj.velocity.Y + spreadY);

                                int newIdx = Projectile.NewProjectile(
                                    Projectile.GetNoneSource(),
                                    proj.position.X,
                                    proj.position.Y,
                                    newVel.X,
                                    newVel.Y,
                                    proj.type,
                                    0,
                                    0f,
                                    owner
                                );

                                if (newIdx >= 0 && newIdx < Main.maxProjectiles)
                                {
                                    clonedBobberIdentities.Add(newIdx);
                                    Main.projectile[newIdx].netUpdate = true;
                                    NetMessage.SendData((int)PacketTypes.ProjectileNew, -1, -1, null, newIdx);
                                }
                            }
                        }
                    }
                }
                else if (proj.timeLeft < 35980)
                {
                    clonedBobberIdentities.Remove(p);
                }
            }

            // 3. Proses AutoFish (/af) untuk item & monster Blood Moon
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

                bool caughtAny = false;
                for (int i = 0; i < Main.maxProjectiles; i++)
                {
                    Projectile proj = Main.projectile[i];
                    if (proj.active && proj.owner == playerIndex && proj.bobber && proj.ai[1] < 0f)
                    {
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
                            // Hasil positif = Item / Ikan / Crate
                            tsPlayer.GiveItem(caughtResult, 1);
                        }
                        else if (caughtResult < 0)
                        {
                            // Hasil negatif = Monster Blood Moon (-NPCID)
                            int npcType = Math.Abs(caughtResult);
                            int spawnX = (int)proj.Center.X;
                            int spawnY = (int)proj.Center.Y - 16;
                            int npcIdx = NPC.NewNPC(NPC.GetBossSpawnSource(playerIndex), spawnX, spawnY, npcType);
                            if (npcIdx >= 0 && npcIdx < Main.maxNPCs)
                            {
                                Main.npc[npcIdx].netUpdate = true;
                                NetMessage.SendData((int)PacketTypes.NpcUpdate, -1, -1, null, npcIdx);
                            }
                        }

                        proj.ai[0] = 0f;
                        proj.ai[1] = 0f;
                        proj.localAI[1] = 0f;
                        proj.netUpdate = true;
                        caughtAny = true;
                    }
                }

                if (caughtAny)
                    lastCatchTime[playerIndex] = DateTime.UtcNow;
            }
        }

        private int RunFishingCheck(Projectile proj)
        {
            if (fishingCheckMethod != null)
                fishingCheckMethod.Invoke(proj, null);

            // localAI[1] bisa positif (ID Item) atau negatif (-ID Monster Blood Moon)
            int result = (int)proj.localAI[1];
            if (result == 0 && proj.ai[1] > 0f)
                result = (int)proj.ai[1];
            return result;
        }
    }
}
