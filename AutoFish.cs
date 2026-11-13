using System;
using System.Collections.Generic;
using System.Reflection;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using Microsoft.Xna.Framework;

namespace AutoFishingPlugin
{
    [ApiVersion(2, 1)]
    public class AutoFishing : TerrariaPlugin
    {
        public override string Name => "AutoFishing";
        public override Version Version => new Version(1, 2, 0);
        public override string Author => "CustomPlugin";
        public override string Description => "Auto fishing menggunakan FishingCheck asli Terraria sesuai biome.";

        private HashSet<int> enabledPlayers = new HashSet<int>();
        private int tickCounter = 0;
        private MethodInfo fishingCheckMethod;

        public AutoFishing(Main game) : base(game) { }

        public override void Initialize()
        {
            // Ambil metode FishingCheck asli milik Terraria
            fishingCheckMethod = typeof(Projectile).GetMethod("FishingCheck", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
            ServerApi.Hooks.ServerLeave.Register(this, OnServerLeave);
            Commands.ChatCommands.Add(new Command("autofish.use", ToggleAutoFish, "autofish", "af")
            {
                HelpText = "Menyalakan atau mematikan fitur auto fishing."
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
            enabledPlayers.Remove(args.Who);
        }

        private void ToggleAutoFish(CommandArgs args)
        {
            int index = args.Player.Index;
            if (enabledPlayers.Contains(index))
            {
                enabledPlayers.Remove(index);
                args.Player.SendInfoMessage("[AutoFish] Dimatikan.");
            }
            else
            {
                enabledPlayers.Add(index);
                args.Player.SendSuccessMessage("[AutoFish] Diaktifkan! Lempar pancing sekali ke air.");
            }
        }

        private void OnGameUpdate(EventArgs args)
        {
            tickCounter++;
            if (tickCounter < 10) return; // Cek setiap ~0.16 detik
            tickCounter = 0;

            foreach (int playerIndex in enabledPlayers)
            {
                TSPlayer tsPlayer = TShock.Players[playerIndex];
                if (tsPlayer == null || !tsPlayer.Active || tsPlayer.TPlayer.dead) continue;

                for (int i = 0; i < Main.maxProjectiles; i++)
                {
                    Projectile proj = Main.projectile[i];
                    if (proj.active && proj.owner == playerIndex && proj.bobber)
                    {
                        // ai[1] < 0 menandakan pelampung bergerak disambar ikan/crate/item
                        if (proj.ai[1] < 0f)
                        {
                            // Panggil FishingCheck() asli Terraria agar biome, crate, quest, & umpan diproses
                            if (fishingCheckMethod != null)
                            {
                                fishingCheckMethod.Invoke(proj, null);
                            }

                            // Jika item hasil tangkapan tersimpan di localAI[1] (atau ai[1] positif setelah check), berikan/munculkan
                            int caughtItem = (int)proj.localAI[1];
                            if (caughtItem <= 0 && proj.ai[1] > 0f)
                            {
                                caughtItem = (int)proj.ai[1];
                            }

                            if (caughtItem > 0)
                            {
                                int itemIdx = Item.NewItem(
                                    proj.GetSource_FromThis(),
                                    (int)tsPlayer.TPlayer.position.X,
                                    (int)tsPlayer.TPlayer.position.Y,
                                    tsPlayer.TPlayer.width,
                                    tsPlayer.TPlayer.height,
                                    caughtItem,
                                    1
                                );
                                NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, itemIdx);
                            }

                            // Reset pelampung supaya otomatis lanjut memancing tanpa hilang dari air
                            proj.ai[0] = 0f;
                            proj.ai[1] = 0f;
                            proj.localAI[1] = 0f;
                            proj.netUpdate = true;
                        }
                    }
                }
            }
        }
    }
}
