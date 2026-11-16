using Newtonsoft.Json;
using ServerLib.Handlers;
using ServerLib.Json.Classes;
using ServerLib.Utilities;
using ServerLib.Utilities.Helpers;

namespace ServerLib.Controllers
{
    public class CharacterController
    {
        static CharacterController()
        {
            Characters = new();
            Characters.Clear();
        }
        public static Dictionary<string, Character.Base> Characters;

        public static void Init()
        {
            ReloadCharacters();
            Debug.PrintInfo("Initialization Done!", "CHARACTER");
        }

        public static void ReloadCharacters()
        {
            foreach (var dir in Directory.GetDirectories("profiles"))
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    if (file.Contains("character.json"))
                    {
                        var character = Character.Base.FromJson(File.ReadAllText(file));
                        if (character != null)
                        {
                            // keep the in-memory copy: it is newer than the file or equal to it
                            Characters.TryAdd(character.Aid, character);
                        }
                    }
                }
            }
        }

        public static void CreateCharacter(string SessionId, string JSON)
        {
            var createReq = JsonConvert.DeserializeObject<Requests.Create>(JSON);
            if (createReq == null)
            {
                Debug.PrintError($"createReq not found", "CreateCharacter");
                return;
            }

            var account = AccountController.FindAccount(SessionId);
            if (account == null)
            {
                Debug.PrintError($"Account not found", "CreateCharacter");
                return;
            }

            // only the two playable sides; the templates are the server's own files (the client sends side + nickname)
            string side = (createReq.side ?? "").ToLowerInvariant();
            if (side != "usec" && side != "bear")
            {
                Debug.PrintError($"Refused character side '{createReq.side}'", "CreateCharacter");
                return;
            }
            createReq.side = side == "usec" ? "Usec" : "Bear";
            // copy the template; the database object is shared by every new character of this side
            var template = DatabaseController.DataBase.Characters.CharacterBase[side];
            var character = Character.Base.FromJson(template.ToJson());
            var ID = Utils.CreateNewID();
            var time = TimeHelper.UnixTimeNow_Int();

            character.Id = ID;
            character.Aid = SessionId;
            character.Info.Side = createReq.side;
            character.Info.Nickname = createReq.nickname;
            character.Info.RegistrationDate = time;
            // own item ids: the template's are shared by every character made from it (items are found by id in raids)
            InventoryHelper.ReIdItems(character);
            InventoryHelper.RepairGridOverlaps(character, "new character " + ID);

            SaveHandler.Save(SessionId, "Character", SaveHandler.GetCharacterPath(SessionId), Converters.ToJson(character));
            Characters[SessionId] = character;
            Debug.PrintInfo($"Character Created with Id {ID}!", "CHARACTER");
        }

        public static Character.Base? GetCharacter(string SessionId)
        {
            if (!Characters.ContainsKey(SessionId))
                ReloadCharacters();
            if (Characters.TryGetValue(SessionId, out var character))
            {
                return character;
            }
            Debug.PrintWarn($"Character isnt made for {SessionId}!", "GetCharacter");
            return null;
        }

        public static string GetCompleteCharacter(string SessionId)
        {
            List<Character.Base> ouptut = new();
            var character = GetCharacter(SessionId);
            if (character != null)
            {
                // quest conditions/rewards on trader standing need an entry per trader (QuestController)
                if (QuestController.EnsureTraderStandings(character))
                    SaveHandler.SaveCharacter(SessionId, character);
                ouptut.Add(character);
            }
            return JsonConvert.SerializeObject(ouptut, Character.Converter.Settings);
        }

        /// <summary>
        /// Replaces the stored character with the one the client sent (end of raid) and writes it to disk.
        /// Ids are kept from the stored character so a profile cannot be moved to another account.
        /// </summary>
        public static bool ReplaceCharacter(string SessionId, Character.Base incoming)
        {
            var current = GetCharacter(SessionId);
            if (current == null || incoming == null) return false;
            incoming.Id = current.Id;
            incoming.Aid = current.Aid;
            if (incoming.Info != null && current.Info != null)
            {
                incoming.Info.Nickname = current.Info.Nickname;
                incoming.Info.Side = current.Info.Side;
                incoming.Info.RegistrationDate = current.Info.RegistrationDate;
                // clients without the Voice field (shipped game) send none: keep the stored one
                if (string.IsNullOrEmpty(incoming.Info.Voice))
                    incoming.Info.Voice = current.Info.Voice;
            }
            if (incoming.Inventory?.Items != null)
                incoming.Inventory.Items.RemoveAll(i => i == null);
            Characters[SessionId] = incoming;
            SaveHandler.SaveCharacter(SessionId, incoming);
            return true;
        }

        public static List<Character.Base> SearchNickname(string Nickname)
        {
            List<Character.Base> ret = new();
            foreach (var profile in Characters.Values)
            {
                if (profile != null)
                {
                    if (profile.Info.Nickname.ToLower() == Nickname.ToLower())
                    {
                        ret.Add(profile);
                    }
                }
            }
            return ret;
        
        }


        public static string ChangeNickname(string json, string SessionId)
        {
            var nick = JsonConvert.DeserializeObject<Requests.Nickname>(json);
            if (nick == null) { return "taken"; }
            string output = AccountController.ValidateNickname(nick);

            if (output == "OK")
            {
                var character = GetCharacter(SessionId);
                if (character == null)
                {
                    Debug.PrintWarn($"Character not found, check if {SessionId} is correct", "ChangeNickname");
                    return "taken";
                }

                character.Info.Nickname = nick.nickname;
                SaveHandler.SaveCharacter(SessionId, character);
            }
            return output;
        }
    }
}
