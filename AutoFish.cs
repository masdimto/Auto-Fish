using System;
using System.Collections.Generic;
using System.Reflection;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AutoFishingPlugin
{
    [ApiVersion(2, 1)]
    public class AutoFishing : TerrariaPlugin
    {
        public override string Name => "AutoFishing";
        public override Version Version => new Version(1, 3, 1);
        public override string Author => "CustomPlugin";
        public override string Description => "Auto fishing dengan pilihan mode real (100% vanilla) dan mod (anti line break).";

        private enum FishMode
        {
            Real,
            Mod
        }

        private Dictionary<int, FishMode> playerModes = new Dictionary<int, FishMode>();
        private Dictionary<int, DateTime> lastCatchTime = new Dictionary<int, DateTime>();
        private int tickCounter = 0;
        private MethodInfo fishingCheckMethod;

        public AutoFishing(Main game) : base(game) { }

        public override void Initialize()
        {
            fishingCheckMethod = typeof(Projectile).GetMethod("FishingCheck", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
            ServerApi.Hooks.ServerLeave.Register(this, OnServerLeave);
            Commands.ChatCommands.Add(new Command("autofish.use", ToggleAutoFish, "autofish", "af")
            {
                HelpText = "Gunakan /af [real/mod/off] untuk mengatur mode auto fishing."
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
                ServerApi.Hooks.ServerLeave.Deregister(this, OnServerLeave);
            }
            base.Dispose(disposing);
        }

        private void OnServerLeave(LeaveEventArgs args)
        {
            playerModes.Remove(args.Who);
            lastCatchTime.Remove(args.Who);
        }

        private void ToggleAutoFish(CommandArgs args)
        {
            int index = args.Player.Index;
            string subCommand = args.Parameters.Count > 0 ? args.Parameters[0].ToLower() : "";

            // Opsi mematikan secara eksplisit (/af off)
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

            // Tentukan mode target (default ke Real jika hanya ketik /af)
            FishMode targetMode;
            if (subCommand == "mod")
            {
                targetMode = FishMode.Mod;
            }
            else if (subCommand == "real" || string.IsNullOrEmpty(subCommand))
            {
                targetMode = FishMode.Real;
            }
            else
            {
                args.Player.SendErrorMessage("Format salah! Gunakan: /af, /af real, /af mod, atau /af off");
                return;
            }

            // Jika player sudah aktif di mode yang sama, matikan (toggle off)
            if (playerModes.TryGetValue(index, out FishMode currentMode) && currentMode == targetMode)
            {
                playerModes.Remove(index);
                lastCatchTime.Remove(index);
                args.Player.SendInfoMessage($"[AutoFish] Mode {targetMode} dimatikan.");
            }
            else
            {
                // Aktifkan atau pindah mode
                playerModes[index] = targetMode;
                if (targetMode == FishMode.Real)
                {
                    args.Player.SendSuccessMessage("[AutoFish] Mode REAL aktif (100% mekanik Terraria). Lempar pancing ke air!");
                }
                else
                {
                    args.Player.SendSuccessMessage("[AutoFish] Mode MOD aktif (Anti line break / tidak pernah zonk). Lempar pancing ke air!");
                }
            }
        }

        private void OnGameUpdate(EventArgs args)
        {
            tickCounter++;
            if (tickCounter < 10) return;
            tickCounter = 0;

            foreach (var kvp in playerModes)
            {
                int playerIndex = kvp.Key;
                FishMode mode = kvp.Value;

                TSPlayer tsPlayer = TShock.Players[playerIndex];
                if (tsPlayer == null || !tsPlayer.Active || tsPlayer.TPlayer.dead) continue;

                // Jeda 1.2 detik antar tarikan agar sinkronisasi HP ke server tetap rapi
                if (lastCatchTime.TryGetValue(playerIndex, out DateTime lastTime))
                {
                    if ((DateTime.UtcNow - lastTime).TotalSeconds < 1.2) continue;
                }

                for (int i = 0; i < Main.maxProjectiles; i++)
                {
                    Projectile proj = Main.projectile[i];
                    if (proj.active && proj.owner == playerIndex && proj.bobber)
                    {
                        if (proj.ai[1] < 0f)
                        {
                            int caughtItem = 0;

                            if (mode == FishMode.Real)
                            {
                                // Mode REAL: Jalankan 1x FishingCheck asli (bisa line break jika power/kolam kurang)
                                caughtItem = RunFishingCheck(proj);
                            }
                            else
                            {
                                // Mode MOD: Ulangi pengecekan hingga item benar-benar dapat (anti line break)
                                for (int attempt = 0; attempt < 15; attempt++)
                                {
                                    proj.ai[1] = -1f;
                                    proj.localAI[1] = 0f;
                                    caughtItem = RunFishingCheck(proj);
                                    if (caughtItem > 0) break;
                                }
                            }

                            if (caughtItem > 0)
                            {
                                tsPlayer.GiveItem(caughtItem, 1);
                            }

                            lastCatchTime[playerIndex] = DateTime.UtcNow;

                            // Reset pelampung untuk lemparan berikutnya
                            proj.ai[0] = 0f;
                            proj.ai[1] = 0f;
                            proj.localAI[1] = 0f;
                            proj.netUpdate = true;
                            break;
                        }
                    }
                }
            }
        }

        private int RunFishingCheck(Projectile proj)
        {
            if (fishingCheckMethod != null)
            {
                fishingCheckMethod.Invoke(proj, null);
            }

            int item = (int)proj.localAI[1];
            if (item <= 0 && proj.ai[1] > 0f)
            {
                item = (int)proj.ai[1];
            }
            return item;
        }
    }
}
