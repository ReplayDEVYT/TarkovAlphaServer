using ServerLib.Utilities;
using ServerLib.Json.Classes;
using ServerLib.Handlers;
using Newtonsoft.Json;
using System.Collections.Generic; // Added for List Support
using System.Linq;
using System.Runtime.Serialization;
using MongoDB.Bson;

namespace ServerLib.Controllers
{
    public class ItemController
    {
        public class MoveItems
        {
            public Items items { get; set; }
            public class Items
            {
                public List<Character.Item> @new { get; set; } = new();
                public List<Character.Item> change { get; set; } = new();
                public List<Character.Item> del { get; set; } = new();
            }
        }

        public static MoveItems MoveActionResult;
        public static void AcceptQuest(string SessionId, dynamic actionData)
        {
            var ch = CharacterController.GetCharacter(SessionId);
            if (ch != null)
            {
                ch.Quests = ch.Quests.Append(new Character.Quest() { qid = actionData.qid, startTime = 1337, status = 1 }).ToArray();
            }
        }

        public static void CompleteQuest(string SessionId, dynamic actionData)
        {
            var ch = CharacterController.GetCharacter(SessionId);
            if (ch != null)
            {
                foreach (var item in ch.Quests)
                {
                    if (item.qid == actionData.qid)
                    {
                        item.status = 4;
                    }
                }
            }
        }

        public static void RemoveItem(string SessionId, dynamic actionData)
        {
            var ch = CharacterController.GetCharacter(SessionId);
            if (ch != null)
            {
                string targetItem = actionData.item;
                List<string> ItemIds = new() { targetItem };
                while (true)
                {
                    if (ItemIds.Count != 0)
                    {
                        while (true)
                        {
                            string tmpEmpty = "yes";

                            for (int i = ch.Inventory.Items.Count - 1; i >= 0; i--)
                            {
                                var item = ch.Inventory.Items[i];
                                if (item != null && ((item.ParentId == ItemIds[0]) || (item.Id == ItemIds[0])))
                                {
                                    MoveActionResult.items.del.Add(item);
                                    ItemIds.Add(item.Id);
                                    ch.Inventory.Items.RemoveAt(i);
                                    tmpEmpty = "no";
                                }
                            }

                            if (tmpEmpty == "yes")
                            {
                                break;
                            }
                            ;
                        }
                        ItemIds.RemoveRange(0, 1);
                        continue;
                    }
                    break;
                }
            }
        }

        public static void AddNote(string SessionId, dynamic actionData)
        {
            var ch = CharacterController.GetCharacter(SessionId);
            if (ch != null)
            {
                ch.Notes.NotesNotes = ch.Notes.NotesNotes.Append(new Character.InsideNotes() { Time = actionData.note.Time, Text = actionData.note.Text }).ToArray();
            }
        }

        // asked gemini to help with some of this (mainly rotation enum conversion)
        // may be jank, im ass at C#
        public static void MoveItem(string SessionId, dynamic actionData)
        {
            var ch = CharacterController.GetCharacter(SessionId);
            if (ch != null)
            {
                string? targetItemId = actionData.item?.ToString();

                Character.Item? itemToMove = ch.Inventory.Items.FirstOrDefault(i => i != null && i.Id == targetItemId);

                if (itemToMove.Id != null)
                {
                    itemToMove.ParentId = actionData.to.id;
                    itemToMove.SlotId = actionData.to.container;

                    if (actionData.to.location != null)
                    {
                        string rawLocationJson = actionData.to.location.ToString();


                        int locX = (int)actionData.to.location.x;
                        int locY = (int)actionData.to.location.y;
                        string rawRotation = actionData.to.location.r?.ToString() ?? "Horizontal";

                        if (!Enum.TryParse<Character.REnum>(rawRotation, true, out Character.REnum parsedEnum))
                        {
                            parsedEnum = Character.REnum.Horizontal;
                        }

                        // this part was a pain to get gemini to fix
                        // i might as well just use an if statement LMAO, it seems more efficient
                        // it works for now, bandaid fix. i dont usually use ai but this was just annoying

                        Character.RUnion cleanRotation = new Character.RUnion();
                        cleanRotation.Enum = parsedEnum;
                        cleanRotation.Integer = null;

                        itemToMove.Location = new Character.LocationClass()
                        {
                            X = locX,
                            Y = locY,
                            R = cleanRotation
                        };
                    }
                }
                else
                {
                    // slot locations are always null
                    // eg: move backpack into backpack slot, location = null
                    itemToMove.Location = null;
                }

                MoveActionResult.items.change.Add(itemToMove);
            }
        }

        public static void SplitItem(string SessionId, dynamic actionData)
        {
            var ch = CharacterController.GetCharacter(SessionId);
            if (ch != null)
            {
                string? targetItemId = actionData.item?.ToString();

                Character.Item? itemToSplit = ch.Inventory.Items.FirstOrDefault(i => i != null && i.Id == targetItemId);



                int splitCount = (int)actionData.count;

                if (itemToSplit.Id != null)
                {
                    itemToSplit.Upd.StackObjectsCount = itemToSplit.Upd.StackObjectsCount - splitCount;

                    Character.Item newItemSplit = new()
                    {
                        Id = ObjectId.GenerateNewId().ToString(),
                        Tpl = itemToSplit.Tpl,
                        ParentId = actionData.container.id,
                        SlotId = actionData.container.container,
                        Upd = new Character.Upd()
                        {
                            StackObjectsCount = splitCount
                        }
                    };

                    if (actionData.container.location == null)
                    {
                        newItemSplit.Location = null;
                    }
                    else
                    {
                        newItemSplit.Location = new Character.LocationClass()
                        {
                            X = actionData.container.location.x,
                            Y = actionData.container.location.y,
                            // fix later
                            R = Character.REnum.Horizontal,
                        };
                    }

                    Utilities.Debug.PrintDebug(newItemSplit.Id);

                    ch.Inventory.Items.Add(newItemSplit);

                    MoveActionResult.items.change.Add(itemToSplit);
                    MoveActionResult.items.@new.Add(newItemSplit);
                }
            }
        }

        public static void MergeItem(string SessionId, dynamic actionData)
        {
            var ch = CharacterController.GetCharacter(SessionId);
            if (ch != null)
            {
                string? targetItemId = actionData.item?.ToString();
                string? targetItemId2 = actionData.with?.ToString();

                Character.Item? itemToMerge = ch.Inventory.Items.FirstOrDefault(i => i != null && i.Id == targetItemId);
                Character.Item? itemToMergeWith = ch.Inventory.Items.FirstOrDefault(i => i != null && i.Id == targetItemId2);

                int count = (int)actionData.count;

                if ((itemToMerge.Id != null) && (itemToMergeWith.Id != null))
                {
                    Item.Base itemMergeBase = DatabaseController.DataBase.Items[itemToMergeWith.Tpl];

                    if (count > itemMergeBase.Props.StackMaxSize)
                    {
                        itemToMergeWith.Upd.StackObjectsCount = itemMergeBase.Props.StackMaxSize;
                    }
                    else { itemToMergeWith.Upd.StackObjectsCount += count; }

                    itemToMerge.Upd.StackObjectsCount -= count;

                    if (itemToMerge.Upd.StackObjectsCount <= 0)
                    {
                        ch.Inventory.Items.Remove(itemToMerge);

                        MoveActionResult.items.del.Add(itemToMerge);
                        MoveActionResult.items.change.Add(itemToMergeWith);
                    }
                    else
                    {
                        MoveActionResult.items.change.Add(itemToMerge);
                        MoveActionResult.items.change.Add(itemToMergeWith);
                    }
                }
            }
        }

        public static void BuyFromTrader(string SessionId, dynamic actionData)
        {
            var ch = CharacterController.GetCharacter(SessionId);
            if (ch != null)
            {
                string buyType = actionData.type;

                switch (buyType)
                {
                    case "buy_from_trader":
                        foreach (var item in actionData.scheme_items)
                        {
                            string? targetItemId = item.ToString();

                            Character.Item? itemCurrency = ch.Inventory.Items.FirstOrDefault(i => i != null && i.Id == targetItemId);

                            itemCurrency.Upd.StackObjectsCount -= item.count;

                            if (itemCurrency.Upd.StackObjectsCount <= 0)
                            {
                                ch.Inventory.Items.Remove(itemCurrency);

                                MoveActionResult.items.del.Add(itemCurrency);
                            }
                            else
                            {
                                MoveActionResult.items.change.Add(itemCurrency);
                            }
                        }

                        break;
                };
            }
        }

        public static string HandleMoving(string SessionId, dynamic body)
        {
            MoveActionResult = new()
            {
                items = new() // Lists initialize clean and empty
            };

            Newtonsoft.Json.Linq.JObject root = Newtonsoft.Json.Linq.JObject.FromObject(body);

            if (root["data"] is Newtonsoft.Json.Linq.JArray actionArray)
            {
                foreach (var actionData in actionArray)
                {
                    string actionType = actionData["Action"]?.ToString() ?? string.Empty;

                    if (string.IsNullOrEmpty(actionType))
                    {
                        Debug.PrintError("No action for data. Skipping.");
                        continue;
                    }

                    switch (actionType)
                    {
                        case "QuestAccept":
                            AcceptQuest(SessionId, actionData);
                            break;
                        case "CompleteQuest":
                            CompleteQuest(SessionId, actionData);
                            break;
                        case "Remove":
                            RemoveItem(SessionId, actionData);
                            break;
                        case "AddNote":
                            AddNote(SessionId, actionData);
                            break;
                        case "Move":
                            MoveItem(SessionId, actionData);
                            break;
                        case "Split":
                            SplitItem(SessionId, actionData);
                            break;
                        case "Transfer":
                            MergeItem(SessionId, actionData);
                            break;
                        case "TradingConfirm":
                            BuyFromTrader(SessionId, actionData);
                            break;
                        default:
                            Debug.PrintError("Action Cannot be Handled! " + actionType);
                            break;
                    }
                }

                var ch = CharacterController.GetCharacter(SessionId);

                // why was it saving every single time an item was moved??
                if (ch != null) { SaveHandler.SaveCharacter(SessionId, ch); }
            }

            return JsonConvert.SerializeObject(MoveActionResult);
        }
    }
}