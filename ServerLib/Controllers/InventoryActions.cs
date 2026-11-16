using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ServerLib.Json.Classes;
using ServerLib.Utilities;

namespace ServerLib.Controllers
{
    /// <summary>
    /// Out-of-raid inventory actions (items/moving), as the menu's inventory controller sends them (client GClass445):
    ///   Move {item, to}, Split {item, container, count}, Merge {item, with}, Transfer {item, with, count},
    ///   Swap {item, to, item2, to2}, Remove {item}, Apply {item, target}, Eat {item, parts:[{to, count}]},
    ///   Examine {type, id, item}, CreateMapMarker {item, mapMarker}, EditMapMarker {item, X, Y, mapMarker},
    ///   DeleteMapMarker {item, X, Y}, AddNote {note}, EditNote {index, note}, DeleteNote {index}.
    /// A container is {id, container, location}; location is {x, y, r} in a grid, a number in a cartridge slot, or null
    /// in a slot. The client applies its own change after the answer (except Split, which waits for the new item's id
    /// in items.new), so these only keep the server's profile in step; a refused action answers with an error.
    /// </summary>
    public static class InventoryActions
    {
        static readonly JsonSerializer Serializer = JsonSerializer.Create(Character.Converter.Settings);

        static Character.Item? Find(Character.Base ch, JToken? id)
        {
            string? s = id?.ToString();
            return string.IsNullOrEmpty(s) ? null : ch.Inventory.Items.FirstOrDefault(i => i != null && i.Id == s);
        }

        static Character.LocationUnion? Location(JToken? location)
        {
            if (location == null || location.Type == JTokenType.Null) return null;
            try
            {
                return location.ToObject<Character.LocationUnion>(Serializer);
            }
            catch (Exception ex)
            {
                Debug.PrintWarn("Unreadable location " + location.ToString(Formatting.None) + ": " + ex.Message, "INVENTORY");
                return null;
            }
        }

        /// <summary>Puts the item into the container the action names ({id, container, location}).</summary>
        static void Place(Character.Item item, JToken? to)
        {
            item.ParentId = to?["id"]?.ToString();
            item.SlotId = to?["container"]?.ToString();
            item.Location = Location(to?["location"]);
        }

        static int Count(Character.Item item) => item.Upd?.StackObjectsCount ?? 1;

        static void SetCount(Character.Item item, int count)
        {
            item.Upd ??= new Character.Upd();
            item.Upd.StackObjectsCount = count;
        }

        static void Delete(Character.Base ch, Character.Item item, ItemController.MoveItems result)
        {
            foreach (var part in InventoryHelper.GetTree(ch, item.Id))
            {
                ch.Inventory.Items.Remove(part);
                result.items.change.Remove(part);
                if (!result.items.del.Contains(part)) result.items.del.Add(part);
            }
        }

        static void Changed(ItemController.MoveItems result, Character.Item item)
        {
            if (!result.items.change.Contains(item)) result.items.change.Add(item);
        }

        public static void Move(Character.Base ch, JToken a, ItemController.MoveItems result)
        {
            var item = Find(ch, a["item"]);
            if (item == null) { ItemController.Fail(228, "Item to move not found"); return; }
            Place(item, a["to"]);
            Changed(result, item);
        }

        public static void Split(Character.Base ch, JToken a, ItemController.MoveItems result)
        {
            var item = Find(ch, a["item"]);
            int count = a["count"]?.Value<int>() ?? 0;
            if (item == null || count <= 0 || count >= Count(item)) { ItemController.Fail(228, "Cannot split that stack"); return; }
            // the reconstructed client picks the new stack's id itself ("newItem") and does not wait for the answer
            // (GClass445.Execute); older clients send none and wait for items.new[0]._id
            string? wanted = a["newItem"]?.ToString();
            bool useWanted = !string.IsNullOrEmpty(wanted) && wanted!.Length <= 64
                && !ch.Inventory.Items.Any(i => i != null && i.Id == wanted);
            if (!string.IsNullOrEmpty(wanted) && !useWanted)
                Debug.PrintWarn($"Split: client id {wanted} is taken, the client's copy will not match", "ITEMS");
            SetCount(item, Count(item) - count);
            var part = new Character.Item
            {
                Id = useWanted ? wanted! : InventoryHelper.NewId(),
                Tpl = item.Tpl,
                Upd = InventoryHelper.CopyUpd(item.Upd)
            };
            part.Upd!.StackObjectsCount = count;
            Place(part, a["container"]);
            ch.Inventory.Items.Add(part);
            Changed(result, item);
            // the client waits for this id (GClass445.Execute: items.new[0]._id becomes the split's new item)
            result.items.@new.Insert(0, part);
        }

        public static void Merge(Character.Base ch, JToken a, ItemController.MoveItems result)
        {
            var item = Find(ch, a["item"]);
            var with = Find(ch, a["with"]);
            if (item == null || with == null || item.Tpl != with.Tpl) { ItemController.Fail(228, "Cannot merge those stacks"); return; }
            int max = InventoryHelper.StackMax(with.Tpl);
            int total = Count(item) + Count(with);
            SetCount(with, Math.Min(max, total));
            Changed(result, with);
            if (total <= max) Delete(ch, item, result);
            else { SetCount(item, total - max); Changed(result, item); }
        }

        public static void Transfer(Character.Base ch, JToken a, ItemController.MoveItems result)
        {
            var item = Find(ch, a["item"]);
            var with = Find(ch, a["with"]);
            int count = a["count"]?.Value<int>() ?? 0;
            if (item == null || with == null || count <= 0) { ItemController.Fail(228, "Cannot transfer between those stacks"); return; }
            int max = InventoryHelper.StackMax(with.Tpl);
            count = Math.Min(count, Math.Min(Count(item), max - Count(with)));
            if (count <= 0) return;
            SetCount(with, Count(with) + count);
            Changed(result, with);
            if (Count(item) - count <= 0) Delete(ch, item, result);
            else { SetCount(item, Count(item) - count); Changed(result, item); }
        }

        public static void Swap(Character.Base ch, JToken a, ItemController.MoveItems result)
        {
            var item = Find(ch, a["item"]);
            var item2 = Find(ch, a["item2"]);
            if (item == null || item2 == null) { ItemController.Fail(228, "Items to swap not found"); return; }
            Place(item, a["to"]);
            Place(item2, a["to2"]);
            Changed(result, item);
            Changed(result, item2);
        }

        public static void Remove(Character.Base ch, JToken a, ItemController.MoveItems result)
        {
            var item = Find(ch, a["item"]);
            if (item == null) return;
            Delete(ch, item, result);
        }

        /// <summary>
        /// Apply: an item dropped onto a weapon or mod (client IApplicable). It goes into the first free slot of the
        /// target, or of the target's attachments, whose filter accepts it (the item's template or one of its parent
        /// categories).
        /// </summary>
        public static void Apply(Character.Base ch, JToken a, ItemController.MoveItems result)
        {
            var item = Find(ch, a["item"]);
            var target = Find(ch, a["target"]);
            if (item == null || target == null) { ItemController.Fail(228, "Item or target not found"); return; }
            var lineage = new HashSet<string>();
            for (string? tpl = item.Tpl; !string.IsNullOrEmpty(tpl) && lineage.Add(tpl); tpl = InventoryHelper.GetTemplate(tpl)?.Parent) { }
            foreach (var host in InventoryHelper.GetTree(ch, target.Id))
            {
                if (host.Id == item.Id) continue;
                var slots = ItemTemplates.Slots(host.Tpl);
                foreach (var (slotName, filter) in slots)
                {
                    if (!filter.Any(lineage.Contains)) continue;
                    if (ch.Inventory.Items.Any(i => i != null && i.ParentId == host.Id && i.SlotId == slotName)) continue;
                    item.ParentId = host.Id;
                    item.SlotId = slotName;
                    item.Location = null;
                    Changed(result, item);
                    return;
                }
            }
            ItemController.Fail(228, "No free slot for that item");
        }

        /// <summary>
        /// Eat: medicine or food used in the menu (client GClass532). parts = the health given to each body part (the
        /// client works it out from the missing health and the item's resource). Medicine loses that much hpResource
        /// (removed at 0); food and drink add their hydration/energy (template effects_health) and are used up.
        /// </summary>
        public static void Eat(Character.Base ch, JToken a, ItemController.MoveItems result)
        {
            var item = Find(ch, a["item"]);
            if (item == null) { ItemController.Fail(228, "Item to use not found"); return; }
            var template = ItemTemplates.Raw(item.Tpl);
            var props = template?["_props"];
            int healed = 0;
            var parts = ch.Health?.BodyPartsHealth;
            foreach (var p in a["parts"] as JArray ?? new JArray())
            {
                int amount = p["count"]?.Value<int>() ?? 0;
                if (amount <= 0 || parts == null) continue;
                var part = BodyPart(parts, p["to"]);
                if (part?.CurrAndMaxValue == null) continue;
                double before = part.CurrAndMaxValue.CurrentValue;
                part.CurrAndMaxValue.CurrentValue = Math.Min(part.CurrAndMaxValue.MaxValue, before + amount);
                healed += (int)Math.Round(part.CurrAndMaxValue.CurrentValue - before);
            }
            double maxHp = props?["MaxHpResource"]?.Value<double>() ?? 0;
            if (maxHp > 0)
            {
                item.Upd ??= new Character.Upd();
                double resource = item.Upd.HpResource ?? maxHp;
                double used = (a["parts"] as JArray ?? new JArray()).Sum(p => p["count"]?.Value<double>() ?? 0);
                resource -= Math.Max(used, 1);
                if (resource <= 0) Delete(ch, item, result);
                else { item.Upd.HpResource = (float)resource; Changed(result, item); }
            }
            else
            {
                var effects = props?["effects_health"];
                AddLimited(ch.Health?.HydrationLimitedValue, effects?["hydration"]?["value"]?.Value<double>() ?? 0);
                AddLimited(ch.Health?.EnergyLimitedValue, effects?["energy"]?["value"]?.Value<double>() ?? 0);
                Delete(ch, item, result);
            }
            Debug.PrintDebug($"Used {item.Tpl}: {healed} health", "INVENTORY");
        }

        static void AddLimited(Character.EnergyLimitedValue? value, double amount)
        {
            if (value?.CurrAndMaxValue == null || amount == 0) return;
            value.CurrAndMaxValue.CurrentValue = Math.Clamp(value.CurrAndMaxValue.CurrentValue + amount, 0, value.CurrAndMaxValue.MaxValue);
        }

        static Character.EnergyLimitedValue? BodyPart(Character.BodyPartsHealth parts, JToken? to)
        {
            string name = to?.ToString() ?? "";
            if (to?.Type == JTokenType.Integer)
                name = new[] { "Head", "Chest", "Stomach", "LeftArm", "RightArm", "LeftLeg", "RightLeg" }.ElementAtOrDefault(to.Value<int>()) ?? "";
            switch (name)
            {
                case "Head": return parts.Head;
                case "Chest": return parts.Chest;
                case "Stomach": return parts.Stomach;
                case "LeftArm": return parts.LeftArm;
                case "RightArm": return parts.RightArm;
                case "LeftLeg": return parts.LeftLeg;
                case "RightLeg": return parts.RightLeg;
                default: return null;
            }
        }

        /// <summary>Examine: the item's template becomes known (Encyclopedia). The item is in the inventory or, when
        /// examined in a trader's offer list (type "Trader", id = trader), in that trader's assort.</summary>
        public static void Examine(Character.Base ch, JToken a, ItemController.MoveItems result)
        {
            string itemId = a["item"]?.ToString() ?? "";
            string? tpl = Find(ch, itemId)?.Tpl;
            if (tpl == null)
            {
                string traderId = a["id"]?.ToString() ?? "";
                var assortItems = TradingController.GetAssort(traderId)?["items"] as JArray;
                tpl = assortItems?.FirstOrDefault(i => i["_id"]?.ToString() == itemId)?["_tpl"]?.ToString();
            }
            if (tpl == null) { Debug.PrintWarn($"Examined item {itemId} not found", "INVENTORY"); return; }
            var known = (ch.Encyclopedia ?? Array.Empty<string>()).ToList();
            if (!known.Contains(tpl)) { known.Add(tpl); ch.Encyclopedia = known.ToArray(); }
        }

        static JArray Markers(Character.Item map)
        {
            map.Upd ??= new Character.Upd();
            map.Upd.Extra ??= new Dictionary<string, JToken>();
            if (!(map.Upd.Extra.TryGetValue("Markers", out var token) && token is JArray markers))
                map.Upd.Extra["Markers"] = markers = new JArray();
            return markers;
        }

        static bool At(JToken marker, JToken a) =>
            marker["X"]?.Value<int>() == a["X"]?.Value<int>() && marker["Y"]?.Value<int>() == a["Y"]?.Value<int>();

        public static void CreateMapMarker(Character.Base ch, JToken a, ItemController.MoveItems result)
        {
            var map = Find(ch, a["item"]);
            if (map == null || a["mapMarker"] == null) return;
            Markers(map).Add(a["mapMarker"]!.DeepClone());
            Changed(result, map);
        }

        public static void EditMapMarker(Character.Base ch, JToken a, ItemController.MoveItems result)
        {
            var map = Find(ch, a["item"]);
            if (map == null || a["mapMarker"] == null) return;
            var markers = Markers(map);
            var old = markers.FirstOrDefault(m => At(m, a));
            if (old != null) old.Replace(a["mapMarker"]!.DeepClone());
            else markers.Add(a["mapMarker"]!.DeepClone());
            Changed(result, map);
        }

        public static void DeleteMapMarker(Character.Base ch, JToken a, ItemController.MoveItems result)
        {
            var map = Find(ch, a["item"]);
            if (map == null) return;
            var old = Markers(map).FirstOrDefault(m => At(m, a));
            old?.Remove();
            Changed(result, map);
        }

        static Character.InsideNotes Note(JToken? note) => new Character.InsideNotes
        {
            Time = note?["Time"]?.Value<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Text = note?["Text"]?.ToString() ?? ""
        };

        public static void AddNote(Character.Base ch, JToken a)
        {
            ch.Notes ??= new Character.Notes();
            ch.Notes.NotesNotes = (ch.Notes.NotesNotes ?? Array.Empty<Character.InsideNotes>()).Append(Note(a["note"])).ToArray();
        }

        public static void EditNote(Character.Base ch, JToken a)
        {
            int index = a["index"]?.Value<int>() ?? -1;
            var notes = ch.Notes?.NotesNotes;
            if (notes == null || index < 0 || index >= notes.Length) return;
            notes[index] = Note(a["note"]);
        }

        public static void DeleteNote(Character.Base ch, JToken a)
        {
            int index = a["index"]?.Value<int>() ?? -1;
            var notes = ch.Notes?.NotesNotes;
            if (notes == null || index < 0 || index >= notes.Length) return;
            ch.Notes!.NotesNotes = notes.Where((n, i) => i != index).ToArray();
        }
    }

    /// <summary>Raw item templates (Files/static/items.json) for fields the typed model does not carry (slot filters,
    /// health effects).</summary>
    public static class ItemTemplates
    {
        static JObject? raw;

        public static JObject? Raw(string tpl)
        {
            try
            {
                if (raw == null)
                {
                    var root = JObject.Parse(File.ReadAllText("Files/static/items.json"));
                    raw = (root["data"] ?? root) as JObject;
                }
                return raw?[tpl] as JObject;
            }
            catch (Exception ex)
            {
                Debug.PrintWarn("items.json unreadable: " + ex.Message, "INVENTORY");
                return null;
            }
        }

        /// <summary>The template's slots: name and the templates/categories its first filter accepts.</summary>
        public static List<(string name, HashSet<string> filter)> Slots(string tpl)
        {
            var list = new List<(string, HashSet<string>)>();
            foreach (var slot in Raw(tpl)?["_props"]?["Slots"] as JArray ?? new JArray())
            {
                string name = slot["_name"]?.ToString() ?? "";
                var accepted = new HashSet<string>();
                foreach (var f in slot["_props"]?["filters"] as JArray ?? new JArray())
                    foreach (var t in f["Filter"] as JArray ?? new JArray())
                    {
                        // entries are "node:<template or category id>" in this items.json, plain ids in later ones
                        string id = t.ToString();
                        accepted.Add(id.StartsWith("node:") ? id.Substring(5) : id);
                    }
                if (name.Length > 0) list.Add((name, accepted));
            }
            return list;
        }
    }
}
