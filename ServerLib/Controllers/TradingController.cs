using Newtonsoft.Json.Linq;
using ServerLib.Json.Classes;
using ServerLib.Utilities;

namespace ServerLib.Controllers
{
    /// <summary>
    /// Trader assorts, purchases and sales.
    /// Client requests (from GClass183.imethod_29 / imethod_28):
    ///   buy:  { Action: "TradingConfirm", type: "buy_from_trader", tid, item_id, count, scheme_id, scheme_items: [{id, count}] }
    ///   sell: { Action: "TradingConfirm", type: "sell_to_trader",  tid, items: [{id, count, scheme_id}] }
    /// </summary>
    public static class TradingController
    {
        // reloaded when the file changes (the Trader Editor writes assorts while the server runs)
        static readonly Dictionary<string, (DateTime written, JObject data)> AssortCache = new();

        public static JObject? GetAssort(string traderId)
        {
            lock (AssortCache)
            {
                if (traderId.Contains('/') || traderId.Contains('\\') || traderId.Contains("..")) return null;
                var path = $"Files/assort/{traderId}.json";
                if (!File.Exists(path)) return null;
                var written = File.GetLastWriteTimeUtc(path);
                if (AssortCache.TryGetValue(traderId, out var cached) && cached.written == written) return cached.data;
                var root = JObject.Parse(File.ReadAllText(path));
                var data = (root["data"] ?? root) as JObject;
                if (data != null) AssortCache[traderId] = (written, data);
                return data;
            }
        }

        /// <summary>The loyalty level an offer needs (assort loyal_level_items, 1 when absent).</summary>
        static int RequiredLevel(JObject? assort, string offerId)
        {
            return assort?["loyal_level_items"]?[offerId]?.Value<int>() ?? 1;
        }

        static bool LoyaltyOn => ConfigController.Configs.Server?.Trading?.LoyaltyLevels ?? true;

        /// <summary>
        /// Answer body for /client/trading/api/getTraderAssort/{traderId} for this profile: offers above the profile's
        /// loyalty level with the trader are left out (with their attachments and price), and each offer's
        /// StackObjectsCount is the trader's remaining stock (TraderStock). The client shows offers as sent.
        /// </summary>
        public static string AssortFor(string sessionId, string traderId)
        {
            var assort = GetAssort(traderId);
            if (assort == null) return Web.ResponseControl.GetBody("null", 1, "\"Trader assort not found\"");
            var copy = (JObject)assort.DeepClone();
            var ch = CharacterController.GetCharacter(sessionId);
            int level = ch == null ? 1 : TraderProgress.LoyaltyLevel(ch, traderId);
            var items = copy["items"] as JArray ?? new JArray();
            var byParent = items.OfType<JObject>().GroupBy(i => i["parentId"]?.ToString() ?? "").ToDictionary(g => g.Key, g => g.ToList());
            var remove = new HashSet<string>();
            int hidden = 0;
            foreach (var root in items.OfType<JObject>().Where(i => i["parentId"]?.ToString() == "hideout").ToList())
            {
                string id = root["_id"]?.ToString() ?? "";
                if (LoyaltyOn && RequiredLevel(assort, id) > level)
                {
                    hidden++;
                    var queue = new Queue<string>();
                    queue.Enqueue(id);
                    while (queue.Count > 0)
                    {
                        string next = queue.Dequeue();
                        if (!remove.Add(next)) continue;
                        if (byParent.TryGetValue(next, out var children))
                            foreach (var c in children) queue.Enqueue(c["_id"]?.ToString() ?? "");
                    }
                    (copy["barter_scheme"] as JObject)?.Remove(id);
                    (copy["loyal_level_items"] as JObject)?.Remove(id);
                    continue;
                }
                int? fileStock = root["upd"]?["StackObjectsCount"]?.Value<int>();
                int? left = TraderStock.Remaining(traderId, id, fileStock);
                if (left.HasValue && fileStock.HasValue && left.Value != fileStock.Value)
                    root["upd"]!["StackObjectsCount"] = left.Value;
            }
            if (remove.Count > 0)
                for (int i = items.Count - 1; i >= 0; i--)
                    if (remove.Contains(items[i]["_id"]?.ToString() ?? "")) items.RemoveAt(i);
            // a running sale: the client shows and pays these prices (Buy checks against the same TraderStock.Cost)
            if (TraderStock.SalePercent(traderId) > 0 && copy["barter_scheme"] is JObject schemes)
                foreach (var offer in schemes.Properties())
                    foreach (var scheme in offer.Value.OfType<JArray>())
                        foreach (var cost in scheme.OfType<JObject>())
                        {
                            double count = cost["count"]?.Value<double>() ?? 0;
                            double now = TraderStock.Cost(traderId, cost["_tpl"]?.ToString() ?? "", count);
                            if (now != count) cost["count"] = now;
                        }
            if (hidden > 0) Debug.PrintDebug($"Assort {traderId}: {hidden} offers above loyalty level {level} hidden", "TRADING");
            return Web.ResponseControl.GetBody(copy.ToString(Newtonsoft.Json.Formatting.None));
        }

        /// <summary>Answer for /client/trading/api/getTrader/{traderId}: the trader file with the profile's standing, the
        /// restock time and the running sale (the client's reload button asks for it and copies the standing over its own).</summary>
        public static string TraderFor(string sessionId, string traderId)
        {
            if (traderId.Contains('/') || traderId.Contains('\\') || traderId.Contains("..")) return Web.ResponseControl.GetBody("null", 1, "\"Trader not found\"");
            var path = $"Files/traders/{traderId}.json";
            if (!File.Exists(path)) return Web.ResponseControl.GetBody("null", 1, "\"Trader not found\"");
            var root = JToken.Parse(File.ReadAllText(path));
            bool wrapped = root is JObject o && o["data"] is JObject;
            var trader = (wrapped ? root["data"] : root) as JObject;
            if (trader != null)
            {
                var ch = CharacterController.GetCharacter(sessionId);
                if (ch != null) QuestController.ApplyStanding(ch, trader);
                TraderStock.ApplyInfo(trader);
            }
            // trader files are stored with or without the {err, data} answer wrapper
            return wrapped ? root.ToString(Newtonsoft.Json.Formatting.None) : Web.ResponseControl.GetBody(root.ToString(Newtonsoft.Json.Formatting.None));
        }

        /// <summary>Rouble price the trader pays for one item (with its contents and attachments).</summary>
        public static int SellPrice(Character.Base ch, Character.Item item)
        {
            int total = 0;
            foreach (var part in InventoryHelper.GetTree(ch, item.Id))
            {
                if (part.Tpl == InventoryHelper.RoublesTpl) continue;
                int each = InventoryHelper.GetTemplate(part.Tpl)?.Props?.CreditsPrice ?? 0;
                total += each * (part.Upd?.StackObjectsCount ?? 1);
            }
            return Math.Max(total, 1);
        }

        /// <summary>
        /// Answer for /client/trading/api/getUserAssortPrice/trader/{id}:
        /// Dictionary of item id -> list of schemes -> list of {_tpl, count}. Items missing here cannot be sold.
        /// </summary>
        public static Dictionary<string, List<List<ItemPrice.Base>>> GetUserAssortPrice(string SessionId)
        {
            var output = new Dictionary<string, List<List<ItemPrice.Base>>>();
            var ch = CharacterController.GetCharacter(SessionId);
            if (ch?.Inventory?.Items == null) return output;

            foreach (var item in ch.Inventory.Items)
            {
                if (item == null || item.Id == ch.Inventory.Stash || item.Id == ch.Inventory.Equipment) continue;
                if (item.Tpl == InventoryHelper.RoublesTpl) continue;
                output[item.Id] = new List<List<ItemPrice.Base>>
                {
                    new List<ItemPrice.Base> { new ItemPrice.Base { Tpl = InventoryHelper.RoublesTpl, Price = SellPrice(ch, item) } }
                };
            }
            return output;
        }

        public static void Confirm(string SessionId, JToken actionData, ItemController.MoveItems result)
        {
            var ch = CharacterController.GetCharacter(SessionId);
            if (ch == null) return;
            string type = actionData["type"]?.ToString() ?? "";
            switch (type)
            {
                case "buy_from_trader":
                    Buy(ch, actionData, result);
                    break;
                case "sell_to_trader":
                    Sell(ch, actionData, result);
                    break;
                default:
                    Debug.PrintError("Unknown trading type " + type, "TRADING");
                    break;
            }
        }

        static void Buy(Character.Base ch, JToken a, ItemController.MoveItems result)
        {
            string tid = a["tid"]?.ToString() ?? "";
            string offerId = a["item_id"]?.ToString() ?? "";
            int count = a["count"]?.Value<int>() ?? 1;
            int schemeId = a["scheme_id"]?.Value<int>() ?? 0;
            if (count < 1) return;

            var assort = GetAssort(tid);
            var assortItems = assort?["items"]?.ToObject<List<Character.Item>>(Newtonsoft.Json.JsonSerializer.Create(Character.Converter.Settings));
            var offer = assortItems?.FirstOrDefault(i => i.Id == offerId);
            if (offer == null)
            {
                ItemController.Fail(228, $"Offer {offerId} not found at trader {tid}");
                return;
            }
            if (LoyaltyOn && RequiredLevel(assort, offerId) > TraderProgress.LoyaltyLevel(ch, tid))
            {
                ItemController.Fail(228, $"Loyalty level {RequiredLevel(assort, offerId)} with this trader is needed for this item");
                return;
            }

            // what has to be paid, per scheme
            var scheme = assort?["barter_scheme"]?[offerId]?[schemeId] as JArray;
            var required = new Dictionary<string, int>();
            if (scheme != null)
                foreach (var cost in scheme)
                {
                    string tpl = cost["_tpl"]?.ToString() ?? "";
                    int c = (int)Math.Ceiling(TraderStock.Cost(tid, tpl, cost["count"]?.Value<double>() ?? 0) * count);
                    required[tpl] = required.GetValueOrDefault(tpl) + c;
                }

            // check that what the client offers covers it
            var payItems = new List<(Character.Item item, int count)>();
            var offered = new Dictionary<string, int>();
            foreach (var p in a["scheme_items"] ?? new JArray())
            {
                string id = p["id"]?.ToString() ?? "";
                int c = p["count"]?.Value<int>() ?? 0;
                var it = ch.Inventory.Items.FirstOrDefault(i => i != null && i.Id == id);
                if (it == null || c <= 0) continue;
                int have = it.Upd?.StackObjectsCount ?? 1;
                c = Math.Min(c, have);
                payItems.Add((it, c));
                offered[it.Tpl] = offered.GetValueOrDefault(it.Tpl) + c;
            }
            foreach (var r in required)
            {
                if (offered.GetValueOrDefault(r.Key) < r.Value)
                {
                    ItemController.Fail(228, $"Not enough to pay: {r.Value} needed, {offered.GetValueOrDefault(r.Key)} offered");
                    return;
                }
            }

            // stock (StackObjectsCount on the assort root is the trader's full stock; TraderStock tracks what is left)
            int? fileStock = offer.Upd?.StackObjectsCount;
            int? left = TraderStock.Remaining(tid, offerId, fileStock);
            if (left.HasValue && left.Value < count)
            {
                ItemController.Fail(228, left.Value <= 0 ? "The trader is out of stock" : $"The trader has only {left.Value} left");
                return;
            }

            // give the goods first, so a full stash does not cost the player money
            var added = new List<Character.Item>();
            var changed = new List<Character.Item>();
            int max = InventoryHelper.StackMax(offer.Tpl);
            bool hasChildren = assortItems!.Any(i => i.ParentId == offer.Id);
            int delivered = count;
            if (max > 1 && !hasChildren)
            {
                int notPlaced = InventoryHelper.AddStackable(ch, offer.Tpl, count, changed, added);
                if (notPlaced >= count)
                {
                    ItemController.Fail(228, "No room in the stash");
                    return;
                }
                if (notPlaced > 0) Debug.PrintWarn($"Stash full, {notPlaced} not delivered", "TRADING");
                delivered = count - notPlaced;
            }
            else
            {
                for (int n = 0; n < count; n++)
                {
                    var tree = InventoryHelper.CloneTree(assortItems!, offer.Id);
                    tree[0].Upd = max > 1 ? new Character.Upd { StackObjectsCount = 1 } : null;
                    if (!InventoryHelper.PlaceInStash(ch, tree, added))
                    {
                        Debug.PrintWarn($"Stash full after {n} of {count}", "TRADING");
                        if (n == 0)
                        {
                            ItemController.Fail(228, "No room in the stash");
                            return;
                        }
                        delivered = n;
                        break;
                    }
                }
            }
            // a partly delivered order is paid for what arrived
            if (delivered < count)
                foreach (var key in required.Keys.ToList())
                    required[key] = (int)Math.Ceiling((double)required[key] * delivered / count);
            TraderStock.Take(tid, offerId, fileStock, delivered);

            // take the payment
            var stillOwed = new Dictionary<string, int>(required);
            foreach (var (it, c) in payItems)
            {
                int owed = stillOwed.GetValueOrDefault(it.Tpl);
                int take = scheme == null ? c : Math.Min(c, owed);
                if (take <= 0) continue;
                stillOwed[it.Tpl] = owed - take;
                TakeFromStack(ch, it, take, result);
            }

            result.items.@new.AddRange(added);
            foreach (var c in changed) if (!result.items.change.Contains(c)) result.items.change.Add(c);
            // the trade volume counts towards the loyalty levels (paid goods at their rouble value)
            long paidValue = required.Sum(r => MoneyValue(r.Key, r.Value));
            TraderProgress.AddTrade(ch, tid, paidValue);
            Debug.PrintDebug($"Bought {delivered}x {offer.Tpl} from {tid} ({paidValue} RUB of trade)", "TRADING");
        }

        const string DollarsTpl = "5696686a4bdc2da3298b456a";
        const string EurosTpl = "569668774bdc2da2298b4568";

        /// <summary>Rouble value of what was paid: roubles 1:1; dollars, euros and barter goods at their template price
        /// (CreditsPrice).</summary>
        static long MoneyValue(string tpl, int count)
        {
            if (tpl == InventoryHelper.RoublesTpl) return count;
            long rate = InventoryHelper.GetTemplate(tpl)?.Props?.CreditsPrice ?? 0;
            return (rate > 0 ? rate : 1) * (long)count;
        }

        static void Sell(Character.Base ch, JToken a, ItemController.MoveItems result)
        {
            int roubles = 0;
            string tid = a["tid"]?.ToString() ?? "";
            foreach (var s in a["items"] ?? new JArray())
            {
                string id = s["id"]?.ToString() ?? "";
                var it = FindSold(ch, id);
                if (it == null)
                {
                    Debug.PrintWarn($"Sold item {id} not in the inventory", "TRADING");
                    continue;
                }
                if (it.Id == ch.Inventory.Stash || it.Id == ch.Inventory.Equipment) continue;
                int have = it.Upd?.StackObjectsCount ?? 1;
                int c = Math.Clamp(s["count"]?.Value<int>() ?? have, 1, have);

                int priceAll = SellPrice(ch, it);
                roubles += (int)Math.Round((double)priceAll * c / have);
                string soldTpl = it.Tpl;
                TakeFromStack(ch, it, c, result);
                QuestController.OnSold(ch, tid, soldTpl, c);
            }
            if (roubles <= 0)
            {
                ItemController.Fail(228, "Nothing was sold");
                return;
            }
            TraderProgress.AddTrade(ch, tid, roubles);

            if (roubles > 0)
            {
                var changed = new List<Character.Item>();
                var added = new List<Character.Item>();
                int left = InventoryHelper.AddStackable(ch, InventoryHelper.RoublesTpl, roubles, changed, added);
                if (left > 0) Debug.PrintWarn($"Stash full, {left} roubles lost", "TRADING");
                foreach (var c in changed) if (!result.items.change.Contains(c)) result.items.change.Add(c);
                result.items.@new.AddRange(added);
            }
            Debug.PrintDebug($"Sold for {roubles} roubles", "TRADING");
        }

        /// <summary>
        /// The inventory item a sale names. The shipped client sends the sale-table copy's id cut to 24 characters
        /// (GClass183.imethod_28: Item.Id.Substring(0, 24) on "&lt;id&gt; clone", written for 24-character ids), so for other
        /// ids the server gets e.g. "1622580111520542635 clon"; the item is the one whose "&lt;id&gt; clone" starts with it.
        /// </summary>
        static Character.Item? FindSold(Character.Base ch, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var exact = ch.Inventory.Items.FirstOrDefault(i => i != null && i.Id == id);
            if (exact != null) return exact;
            string trimmed = id.EndsWith(" clone") ? id.Substring(0, id.Length - 6) : id;
            exact = ch.Inventory.Items.FirstOrDefault(i => i != null && i.Id == trimmed);
            if (exact != null) return exact;
            var candidates = ch.Inventory.Items.Where(i => i != null && i.Id != null && (i.Id + " clone").StartsWith(id, StringComparison.Ordinal)).ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        /// <summary>Takes count from a stack; removes the item (with its children) when it reaches zero.</summary>
        static void TakeFromStack(Character.Base ch, Character.Item it, int count, ItemController.MoveItems result)
        {
            int have = it.Upd?.StackObjectsCount ?? 1;
            if (have - count > 0)
            {
                it.Upd!.StackObjectsCount = have - count;
                if (!result.items.change.Contains(it)) result.items.change.Add(it);
                return;
            }
            foreach (var part in InventoryHelper.GetTree(ch, it.Id))
            {
                ch.Inventory.Items.Remove(part);
                result.items.change.Remove(part);
                result.items.del.Add(part);
            }
        }
    }
}
