using System;
using System.Collections.Generic;
using System.Reflection;
using Terraria;
using Terraria.DataStructures;
using TerrariaApi.Server;
using TShockAPI;
using Microsoft.Xna.Framework;

namespace AutoFishingPlugin
{
    [ApiVersion(2, 1)]
    public class AutoFishing : TerrariaPlugin
    {
        public override string Name => "AutoFishing";
        public override Version Version => new Version(1, 1, 0);
        public override string Author => "CustomPlugin";
        public override string Description => "Auto fishing akurat sesuai biome (ikan, crate, quest fish, dan treasure).";

        private HashSet<int> enabledPlayers = new HashSet<int>();
        private int tickCounter = 0;

        public AutoFishing(Main game) : base(game) { }

        public override void Initialize()
        {
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
                args.Player.SendSuccessMessage("[AutoFish] Diaktifkan! Pegang joran dan lempar pancing sekali ke air.");
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

                Player player = tsPlayer.TPlayer;
                Item heldItem = player.HeldItem;

                // Pastikan player sedang memegang joran pancing (fishingPole > 0)
                if (heldItem == null || heldItem.fishingPole <= 0) continue;

                for (int i = 0; i < Main.maxProjectiles; i++)
                {
                    Projectile proj = Main.projectile[i];
                    if (proj.active && proj.owner == playerIndex && proj.bobber)
                    {
                        // ai[1] < 0 menandakan pelampung bergerak disambar ikan/crate/item
                        if (proj.ai[1] < 0f && proj.ai[0] == 0f)
                        {
                            // Simpan posisi dan kecepatan lemparan awal untuk melempar ulang otomatis
                            Vector2 spawnPos = player.RotatedRelativePoint(player.MountedCenter, true);
                            Vector2 throwVel = proj.position - spawnPos;
                            if (throwVel.Length() > 0)
                            {
                                throwVel.Normalize();
                                throwVel *= heldItem.shootSpeed;
                            }
                            else
                            {
                                throwVel = new Vector2(player.direction * heldItem.shootSpeed, -2f);
                            }

                            int bobberType = proj.type;

                            // 1. Paksa tarik pancing menggunakan mekanik asli Terraria (sesuai biome, bait, & quest)
                            proj.ai[0] = 1f;
                            proj.netUpdate = true;

                            // 2. Langsung lempar ulang pelampung baru agar mancing terus tanpa henti
                            int newProj = Projectile.NewProjectile(
                                new EntitySource_ItemUse(player, heldItem),
                                spawnPos,
                                throwVel,
                                bobberType,
                                0,
                                0f,
                                playerIndex
                            );

                            if (newProj >= 0 && newProj < Main.maxProjectiles)
                            {
                                Main.projectile[newProj].netUpdate = true;
                            }

                            break;
                        }
                    }
                }
            }
        }
    }
}
