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
        public override Version Version => new Version(1, 2, 1);
        public override string Author => "CustomPlugin";
        public override string Description => "Auto fishing menggunakan FishingCheck asli Terraria sesuai biome.";

        private HashSet<int> enabledPlayers = new HashSet<int>();
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
            if (tickCounter < 10) return;
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
                        if (proj.ai[1] < 0f)
                        {
                            if (fishingCheckMethod != null)
                            {
                                fishingCheckMethod.Invoke(proj, null);
                            }

                            int caughtItem = (int)proj.localAI[1];
                            if (caughtItem <= 0 && proj.ai[1] > 0f)
                            {
                                caughtItem = (int)proj.ai[1];
                            }

                            if (caughtItem > 0)
                            {
                                tsPlayer.GiveItem(caughtItem, 1);
                            }

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
