using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ServerLib.Handlers;
using ServerLib.Json.Classes;
using ServerLib.Utilities;

namespace ServerLib.Controllers
{
    /// <summary>
    /// /client/game/profile/items/moving: a list of actions ({"data": [{"Action": ...}, ...]}). Inventory actions are in
    /// InventoryActions, trading in TradingController, quests in QuestController. The answer is the items the actions
    /// added, changed and removed; a refused action (Fail) answers with its error code and message, which the client
    /// shows (trading) or uses to undo (quests).
    /// </summary>
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

        /// <summary>Refuses the current items/moving request (first error wins).</summary>
        public static void Fail(int code, string message)
        {
            Debug.PrintWarn(message, "ITEMS");
            if (QuestController.LastErrorCode != 0) return;
            QuestController.LastErrorCode = code;
            QuestController.LastErrorMessage = message;
        }

        public static string HandleMoving(string SessionId, dynamic body)
        {
            MoveActionResult = new()
            {
                items = new() // Lists initialize clean and empty
            };
            QuestController.LastErrorCode = 0;
            QuestController.LastErrorMessage = null;

            JObject root = JObject.FromObject(body);
            var ch = CharacterController.GetCharacter(SessionId);

            if (ch != null && root["data"] is JArray actionArray)
            {
                foreach (var actionData in actionArray)
                {
                    string actionType = actionData["Action"]?.ToString() ?? string.Empty;
                    try
                    {
                        switch (actionType)
                        {
                            // the client sends "QuestAccept" {qid} and "QuestComplete" {qid, removeExcessItems} (GClass183.imethod_21/22)
                            case "QuestAccept":
                                QuestController.Accept(SessionId, actionData, MoveActionResult);
                                break;
                            case "QuestComplete":
                                QuestController.Complete(SessionId, actionData, MoveActionResult);
                                break;
                            case "QuestHandover":
                                // HANDOVER button of a HandoverItem objective (client EFT.Reconstruction.QuestHandover)
                                QuestController.Handover(SessionId, actionData, MoveActionResult);
                                break;
                            case "TradingConfirm":
                                TradingController.Confirm(SessionId, actionData, MoveActionResult);
                                break;
                            case "Move":
                                InventoryActions.Move(ch, actionData, MoveActionResult);
                                break;
                            case "Split":
                                InventoryActions.Split(ch, actionData, MoveActionResult);
                                break;
                            case "Merge":
                                InventoryActions.Merge(ch, actionData, MoveActionResult);
                                break;
                            case "Transfer":
                                InventoryActions.Transfer(ch, actionData, MoveActionResult);
                                break;
                            case "Swap":
                                InventoryActions.Swap(ch, actionData, MoveActionResult);
                                break;
                            case "Remove":
                                InventoryActions.Remove(ch, actionData, MoveActionResult);
                                break;
                            case "Apply":
                                InventoryActions.Apply(ch, actionData, MoveActionResult);
                                break;
                            case "Eat":
                                InventoryActions.Eat(ch, actionData, MoveActionResult);
                                break;
                            case "Examine":
                                InventoryActions.Examine(ch, actionData, MoveActionResult);
                                break;
                            case "CreateMapMarker":
                                InventoryActions.CreateMapMarker(ch, actionData, MoveActionResult);
                                break;
                            case "EditMapMarker":
                                InventoryActions.EditMapMarker(ch, actionData, MoveActionResult);
                                break;
                            case "DeleteMapMarker":
                                InventoryActions.DeleteMapMarker(ch, actionData, MoveActionResult);
                                break;
                            case "AddNote":
                                InventoryActions.AddNote(ch, actionData);
                                break;
                            case "EditNote":
                                InventoryActions.EditNote(ch, actionData);
                                break;
                            case "DeleteNote":
                                InventoryActions.DeleteNote(ch, actionData);
                                break;
                            case "":
                                Debug.PrintError("No action for data. Skipping.");
                                break;
                            default:
                                Debug.PrintError("Action Cannot be Handled! " + actionType);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.PrintError($"{actionType} failed: {ex}", "ITEMS");
                        Fail(228, $"{actionType} failed on the server");
                    }
                }

                SaveHandler.SaveCharacter(SessionId, ch);
            }

            // Character converters write locations as {x, y, r:"Horizontal"}, the way the client reads them.
            // Without them the LocationUnion struct is written field by field and the client cannot place new items.
            return JsonConvert.SerializeObject(MoveActionResult, Character.Converter.Settings);
        }
    }
}
