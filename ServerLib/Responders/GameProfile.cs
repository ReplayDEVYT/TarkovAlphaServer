using Newtonsoft.Json;
using ServerLib.Controllers;
using ServerLib.Handlers;
using ServerLib.Json.Classes;
using ServerLib.Utilities;
using static ServerLib.Web.ResponseControl;

namespace ServerLib.Responders
{
    public static class GameProfile
    {
        public static string Template(string SessionId)
        {
            return GetBody(JsonConvert.SerializeObject(""));
        }

        public static string Save(string SessionId, string JsonBody)
        {
            var character = CharacterController.GetCharacter(SessionId);
            if (character == null)
            {
                Debug.PrintError("Character not found!", "ProfileStatus");
            }
            try
            {
                // the profile has to go through the Character converters (location/rotation unions)
                var root = Newtonsoft.Json.Linq.JObject.Parse(JsonBody);
                // the game sends {"profile": ..., "status": "Survived"} (Class1030: profile, status)
                var exitStatus = (root["status"] ?? root["exitStatus"])?.ToString();
                var profileToken = root["profile"] ?? root["Profile"];
                var cfg = ConfigController.Configs.Server?.Matchmaking ?? new ServerLib.Json.MatchmakingConfig();
                // a save while the session is in a started match comes from an online raid (the game sends it at the end)
                var (match, notInMatch) = MatchController.GetMatch(SessionId);
                bool online = !notInMatch && match.IsStarted;
                Utilities.Debug.PrintDebug("User exitStatus: " + exitStatus + (online ? " (online raid)" : ""));
                if (online && !cfg.SaveOnlineRaids)
                {
                    Utilities.Debug.PrintInfo("Online raid result not saved (matchmaking.saveOnlineRaids is off)", "GameProfile.SAVE");
                }
                else if (profileToken != null && profileToken.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                {
                    if ((exitStatus == "Killed" || exitStatus == "MissingInAction" || exitStatus == "Left") && cfg.LoseGearOnDeath)
                        LoseGear((Newtonsoft.Json.Linq.JObject)profileToken);
                    var profile = Character.Base.FromJson(profileToken.ToString());
                    // a death comes back alive for the next raid (online and local raids)
                    if (exitStatus == "Killed" || exitStatus == "MissingInAction" || (profile.Health != null && !profile.Health.IsAlive))
                        Revive(profile, "raid result " + exitStatus);
                    // Started / Success / Fail stay the backend's; quests the client reports failed get their penalties
                    QuestController.MergeOnSave(character, profile);
                    // the client adds the raid's experience (GClass658.imethod_1); the stored level follows it, and
                    // loyalty levels follow the new player level
                    if (profile.Info != null)
                    {
                        int before = character?.Info?.Experience ?? profile.Info.Experience;
                        // a raid never takes experience; a NaN multiplier on the client arrives as int.MinValue
                        if (profile.Info.Experience < before || profile.Info.Experience < 0)
                        {
                            Debug.PrintWarn($"Raid result sent experience {profile.Info.Experience}; kept {Math.Max(0, before)}", "GameProfile.SAVE");
                            profile.Info.Experience = Math.Max(0, before);
                        }
                        profile.Info.Level = QuestController.LevelFor(profile.Info.Experience);
                        Debug.PrintInfo($"Raid result ({exitStatus}): experience {before} -> {profile.Info.Experience}, level {profile.Info.Level}", "GameProfile.SAVE");
                    }
                    QuestController.EnsureTraderStandings(profile);
                    TraderProgress.UpdateAll(profile);
                    InventoryHelper.RepairItemIds(profile, "raid result " + exitStatus);
                    InventoryHelper.RepairGridOverlaps(profile, "raid result " + exitStatus);
                    if (!CharacterController.ReplaceCharacter(SessionId, profile))
                        Debug.PrintError("Profile could not be saved", "GameProfile.SAVE");
                }
            }
            catch (Exception ex)
            {
                Debug.PrintError(ex.ToString(), "GameProfile.SAVE");
            }
            // the raid is over for this player even when the save failed (a failed save used to leave the player in
            // the match, so the menu offered to rejoin the raid he had died in)
            try { MatchController.Exit(SessionId); }
            catch (Exception ex) { Debug.PrintError(ex.ToString(), "GameProfile.SAVE"); }

            return GetBody("null");
        }
        /// <summary>
        /// Death: everything under the equipment is lost except the secure container (with its contents), the
        /// pockets item itself (its contents are lost) and the scabbard; body-part health is restored to its maximum.
        /// </summary>
        static void LoseGear(Newtonsoft.Json.Linq.JObject profile)
        {
            var inventory = profile["Inventory"] as Newtonsoft.Json.Linq.JObject;
            var items = inventory?["items"] as Newtonsoft.Json.Linq.JArray;
            string? equipment = inventory?["equipment"]?.ToString();
            if (items == null || string.IsNullOrEmpty(equipment)) return;
            var keepSlots = new HashSet<string> { "SecuredContainer", "Scabbard" };
            var byId = new Dictionary<string, Newtonsoft.Json.Linq.JToken>();
            foreach (var item in items) { var id = item["_id"]?.ToString(); if (id != null) byId[id] = item; }
            var remove = new HashSet<string>();
            foreach (var item in items)
            {
                // walk up to the equipment; the slot the chain enters the equipment through decides
                string? id = item["_id"]?.ToString();
                var node = item;
                string? lastSlot = null;
                bool underEquipment = false, isPocketsItem = false;
                for (int depth = 0; node != null && depth < 64; depth++)
                {
                    string? parent = node["parentId"]?.ToString();
                    if (parent == equipment)
                    {
                        underEquipment = true;
                        lastSlot = node["slotId"]?.ToString();
                        isPocketsItem = node == item && lastSlot == "Pockets";
                        break;
                    }
                    node = parent != null && byId.TryGetValue(parent, out var p) ? p : null;
                }
                if (id == null || !underEquipment || isPocketsItem || (lastSlot != null && keepSlots.Contains(lastSlot))) continue;
                remove.Add(id);
            }
            for (int i = items.Count - 1; i >= 0; i--)
                if (remove.Contains(items[i]["_id"]?.ToString() ?? "")) items.RemoveAt(i);
            if (profile["Health"]?["BodyPartsHealth"] is Newtonsoft.Json.Linq.JObject parts)
                foreach (var part in parts.Properties())
                    if (part.Value["CurrAndMaxValue"] is Newtonsoft.Json.Linq.JObject h && h["MaxValue"] != null) h["CurrentValue"] = h["MaxValue"];
            Utilities.Debug.PrintInfo($"Raid lost: {remove.Count} items lost, health restored", "GameProfile.SAVE");
        }

        public static string ProfileStatus(string SessionId, MatchController.ClientEndpoint? endpoint = null)
        {
            var character = CharacterController.GetCharacter(SessionId);
            if (character == null)
            {
                Debug.PrintError("Character not found!", "ProfileStatus");
                return GetBody("[]");
            }

            Other.ProfileStatus[] response = { MatchController.StatusFor(SessionId, character.Id, endpoint) };
            return GetBody(JsonConvert.SerializeObject(response));
        }

        public static string ProfileNicknameValidate(string Uncompressed)
        {
            var nickname = AccountController.ValidateNickname(JsonConvert.DeserializeObject<Json.Classes.Requests.Nickname>(Uncompressed));
            var resp = GetBody("{status: \"ok\"}");
            if (nickname == "taken")
            {
                resp = GetBody("null", 255, "The nickname is already in use");
            }

            if (nickname == "tooshort")
            {
                resp = GetBody("null", 256, "The nickname is too short");
            }
            return resp;
        }

        public static string ProfileList(string SessionId)
        {
            // profiles stored dead are healed here (the client spawns a dead profile as a dead player: no movement,
            // empty hands, no frames to the game server)
            var stored = CharacterController.GetCharacter(SessionId);
            bool repaired = false;
            if (stored?.Health != null && !stored.Health.IsAlive)
            {
                Revive(stored, "stored profile was dead");
                repaired = true;
            }
            // duplicated or template-shared item ids (raid desync, "same key already exists" on load), then overlapping
            // items (the usec/bear templates have two; the game refuses them on every load)
            if (stored != null && InventoryHelper.RepairItemIds(stored, "profile " + stored.Id))
                repaired = true;
            if (stored != null && InventoryHelper.RepairGridOverlaps(stored, "profile " + stored.Id) > 0)
                repaired = true;
            // a NaN experience from the client was stored as int.MinValue (see Save)
            if (stored?.Info != null && stored.Info.Experience < 0)
            {
                Debug.PrintWarn($"profile {stored.Id}: experience {stored.Info.Experience} reset to 0", "GameProfile.LIST");
                stored.Info.Experience = 0;
                stored.Info.Level = QuestController.LevelFor(0);
                repaired = true;
            }
            if (repaired && !CharacterController.ReplaceCharacter(SessionId, stored!))
                Debug.PrintError("Repaired profile could not be saved", "GameProfile.LIST");
            return GetBody(CharacterController.GetCompleteCharacter(SessionId));
        }

        /// <summary>
        /// Back alive after a death: IsAlive, no destroyed parts, no effects (bleeding, fractures, ...), every body part at
        /// its maximum. The game sends the dead state in the raid result (Health.IsAlive false, DestroyedParts) and
        /// nothing ever cleared it, so the next raid started with a dead player.
        /// </summary>
        public static void Revive(Character.Base profile, string reason)
        {
            var health = profile.Health;
            if (health == null) return;
            health.IsAlive = true;
            health.DestroyedParts = new object[0];
            health.EffectInfoList = new object[0];
            var parts = health.BodyPartsHealth;
            if (parts != null)
            {
                foreach (var part in new[] { parts.Head, parts.Chest, parts.Stomach, parts.LeftArm, parts.RightArm, parts.LeftLeg, parts.RightLeg })
                {
                    if (part?.CurrAndMaxValue != null)
                        part.CurrAndMaxValue.CurrentValue = part.CurrAndMaxValue.MaxValue;
                }
            }
            Debug.PrintInfo($"Profile {profile.Id} revived ({reason})", "GameProfile");
        }

        public static string ProfileSelect(string SessionId, string? hostHeader = null)
        {
            // Reconstruction fix: the game long-polls http://<server>/push/notifier/get/..., and "server" was server.json's
            // ip:port (127.0.0.1 or 0.0.0.0 for anyone on another PC). The address the game used to reach us works for it.
            string notifierServer = !string.IsNullOrWhiteSpace(hostHeader) && !hostHeader.Contains('/') ? hostHeader.Trim() : ServerLibClass.ip_port;
            SelectProfileResponse response = new()
            {
                status = "ok",
                notifier = new()
                {
                    { "server", notifierServer },
                    { "channel_id", SessionId }
                }
            };
            return GetBody(JsonConvert.SerializeObject(response));
        }

        public static string ProfileCreate(string SessionId, string Uncompressed)
        {
            CharacterController.CreateCharacter(SessionId, Uncompressed);
            return GetBody(CharacterController.GetCharacter(SessionId).ToJson());
        }
    }
}
