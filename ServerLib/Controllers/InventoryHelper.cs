using MongoDB.Bson;
using ServerLib.Json.Classes;

namespace ServerLib.Controllers
{
    /// <summary>
    /// Stash grid, stacks and item trees for a character's inventory.
    /// </summary>
    public static class InventoryHelper
    {
        public const string RoublesTpl = "5449016a4bdc2d6f028b456f";

        public static string NewId() => ObjectId.GenerateNewId().ToString();

        public static Item.Base? GetTemplate(string tpl)
        {
            if (DatabaseController.DataBase.Items != null && DatabaseController.DataBase.Items.TryGetValue(tpl, out var b))
                return b;
            return null;
        }

        public static int StackMax(string tpl)
        {
            var max = GetTemplate(tpl)?.Props?.StackMaxSize ?? 1;
            return max < 1 ? 1 : max;
        }

        public static (int w, int h) BaseSize(string tpl)
        {
            var p = GetTemplate(tpl)?.Props;
            int w = p?.Width ?? 1, h = p?.Height ?? 1;
            return (w < 1 ? 1 : w, h < 1 ? 1 : h);
        }

        /// <summary>A copy of an item's saved state (null stays null).</summary>
        public static Character.Upd? CopyUpd(Character.Upd? upd)
        {
            if (upd == null) return null;
            return new Character.Upd
            {
                Durability = upd.Durability,
                MaxDurability = upd.MaxDurability,
                StackObjectsCount = upd.StackObjectsCount,
                HpPercent = upd.HpPercent,
                HpResource = upd.HpResource,
                Extra = upd.Extra?.ToDictionary(p => p.Key, p => p.Value.DeepClone())
            };
        }

        /// <summary>The item and everything attached to or contained in it.</summary>
        public static List<Character.Item> GetTree(Character.Base ch, string rootId)
        {
            var result = new List<Character.Item>();
            var queue = new Queue<string>();
            queue.Enqueue(rootId);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                foreach (var it in ch.Inventory.Items)
                {
                    if (it == null) continue;
                    if ((it.Id == id && id == rootId) || it.ParentId == id)
                    {
                        if (result.Contains(it)) continue;
                        result.Add(it);
                        if (it.Id != rootId) queue.Enqueue(it.Id);
                    }
                }
            }
            return result;
        }

        /// <summary>Copies an item tree from any item list with fresh ids. The root comes first.</summary>
        public static List<Character.Item> CloneTree(List<Character.Item> source, string rootId)
        {
            var ids = new Dictionary<string, string>();
            var output = new List<Character.Item>();
            var queue = new Queue<string>();
            queue.Enqueue(rootId);
            ids[rootId] = NewId();
            while (queue.Count > 0)
            {
                var oldId = queue.Dequeue();
                foreach (var it in source)
                {
                    bool isRoot = it.Id == oldId && oldId == rootId;
                    if (!isRoot && it.ParentId != oldId) continue;
                    if (!isRoot)
                    {
                        if (ids.ContainsKey(it.Id)) continue;
                        ids[it.Id] = NewId();
                        queue.Enqueue(it.Id);
                    }
                    output.Add(new Character.Item
                    {
                        Id = ids[it.Id],
                        Tpl = it.Tpl,
                        ParentId = isRoot ? it.ParentId : ids[it.ParentId],
                        SlotId = it.SlotId,
                        Location = it.Location,
                        Upd = CopyUpd(it.Upd)
                    });
                }
            }
            return output;
        }

        /// <summary>
        /// Finds a free cell in the stash for an item of the given size, trying both rotations.
        /// Occupancy is computed from items directly in the stash grid, using their template size
        /// (attachments that make a weapon bigger are not counted).
        /// </summary>
        public static Character.LocationClass? FindFreeStashSpot(Character.Base ch, int w, int h)
        {
            string stashId = ch.Inventory.Stash;
            var stash = ch.Inventory.Items.FirstOrDefault(i => i != null && i.Id == stashId);
            int cols = 10, rows = 30;
            var grid = stash == null ? null : GetTemplate(stash.Tpl)?.Props?.Grids?.FirstOrDefault();
            if (grid?.Props != null)
            {
                cols = (int)grid.Props.CellsH;
                rows = (int)grid.Props.CellsV;
            }
            string gridName = grid?.Name ?? "hideout";

            var used = new bool[cols, rows];
            foreach (var it in ch.Inventory.Items)
            {
                if (it == null || it.ParentId != stashId || it.SlotId != gridName) continue;
                var loc = it.Location?.LocationClass;
                if (loc == null) continue;
                var (iw, ih) = BaseSize(it.Tpl);
                if (loc.R.Enum == Character.REnum.Vertical || loc.R.Integer == 1) (iw, ih) = (ih, iw);
                for (int x = (int)loc.X; x < loc.X + iw && x < cols; x++)
                    for (int y = (int)loc.Y; y < loc.Y + ih && y < rows; y++)
                        if (x >= 0 && y >= 0) used[x, y] = true;
            }

            foreach (var vertical in new[] { false, true })
            {
                int fw = vertical ? h : w, fh = vertical ? w : h;
                for (int y = 0; y + fh <= rows; y++)
                    for (int x = 0; x + fw <= cols; x++)
                    {
                        bool free = true;
                        for (int dx = 0; dx < fw && free; dx++)
                            for (int dy = 0; dy < fh && free; dy++)
                                if (used[x + dx, y + dy]) free = false;
                        if (free)
                            return new Character.LocationClass
                            {
                                X = x,
                                Y = y,
                                R = vertical ? Character.REnum.Vertical : Character.REnum.Horizontal
                            };
                    }
            }
            return null;
        }

        /// <summary>
        /// Gives every item except the stash a fresh id (parents, the equipment id and fast-panel bindings follow).
        /// Characters were copied from Files/characters/usec|bear.json with the template's ids, so every player's rig,
        /// rifle, magazine... had the same id; in a raid items are found by id, so one player dropping his rig took
        /// another's, and loot from another player brought home ids the profile already had (the client then cannot
        /// load the profile: "An element with the same key already exists").
        /// </summary>
        public static void ReIdItems(Character.Base ch)
        {
            var items = ch?.Inventory?.Items;
            if (items == null) return;
            string stash = ch!.Inventory.Stash;
            var map = new Dictionary<string, string>();
            foreach (var it in items)
                if (it?.Id != null && it.Id != stash && !map.ContainsKey(it.Id)) map[it.Id] = NewId();
            foreach (var it in items)
            {
                if (it == null) continue;
                if (it.Id != null && map.TryGetValue(it.Id, out var id)) it.Id = id;
                if (it.ParentId != null && map.TryGetValue(it.ParentId, out var pid)) it.ParentId = pid;
            }
            if (ch.Inventory.Equipment != null && map.TryGetValue(ch.Inventory.Equipment, out var eq)) ch.Inventory.Equipment = eq;
            if (ch.Inventory.FastPanel is Newtonsoft.Json.Linq.JObject panel)
                foreach (var p in panel.Properties().ToList())
                    if (p.Value.Type == Newtonsoft.Json.Linq.JTokenType.String && map.TryGetValue(p.Value.ToString(), out var fid)) p.Value = fid;
        }

        /// <summary>
        /// Gives later copies of an item id a new id. The client writes the inventory parent first (each item followed by
        /// what is attached to / inside it), so an item naming a duplicated id as its parent belongs to the copy seen last
        /// before it. Returns how many items were renamed.
        /// </summary>
        public static int RenameDuplicateIds(Character.Base ch)
        {
            var items = ch?.Inventory?.Items;
            if (items == null) return 0;
            var seen = new HashSet<string>();
            var current = new Dictionary<string, string>(); // duplicated id -> id of its latest copy
            int renamed = 0;
            foreach (var it in items)
            {
                if (it == null) continue;
                if (it.ParentId != null && current.TryGetValue(it.ParentId, out var parent)) it.ParentId = parent;
                if (it.Id == null) continue;
                if (seen.Add(it.Id))
                {
                    current.Remove(it.Id);
                    continue;
                }
                string fresh = NewId();
                current[it.Id] = fresh;
                it.Id = fresh;
                seen.Add(fresh);
                renamed++;
            }
            return renamed;
        }

        /// <summary>
        /// Login and raid-save repair: renames duplicated ids, and gives a profile still carrying the character templates' item ids its
        /// own ids (once: afterwards none are left). Returns true when something changed.
        /// </summary>
        public static bool RepairItemIds(Character.Base ch, string reason)
        {
            if (ch?.Inventory?.Items == null) return false;
            int dropped = RenameDuplicateIds(ch);
            if (dropped > 0) Utilities.Debug.PrintWarn($"{reason}: {dropped} item(s) had an id another item already had; given new ids", "INVENTORY");
            var templateIds = new HashSet<string>();
            foreach (var t in DatabaseController.DataBase.Characters?.CharacterBase?.Values ?? Enumerable.Empty<Character.Base>())
                foreach (var it in t?.Inventory?.Items ?? new List<Character.Item>())
                    if (it?.Id != null && it.Id != t!.Inventory.Stash) templateIds.Add(it.Id);
            bool shared = ch.Inventory.Items.Any(it => it?.Id != null && templateIds.Contains(it.Id))
                || (ch.Inventory.Equipment != null && templateIds.Contains(ch.Inventory.Equipment));
            if (shared)
            {
                ReIdItems(ch);
                Utilities.Debug.PrintInfo($"{reason}: items had the character template's ids (shared by every player); new ids given", "INVENTORY");
            }
            return dropped > 0 || shared;
        }

        /// <summary>
        /// Moves items that overlap another item in the same grid (same parent and grid name) to a free stash spot.
        /// The game refuses such items when it loads the profile ("Could not add item ... Add method returned false"),
        /// in the menu and on the game server; the shipped usec/bear templates have two (a water bottle, a backpack),
        /// so every character made from them carried the error. Earlier items in the list keep their place.
        /// Sizes are template sizes (attachments not counted). Returns how many items were moved.
        /// </summary>
        public static int RepairGridOverlaps(Character.Base ch, string reason)
        {
            if (ch?.Inventory?.Items == null) return 0;
            var occupied = new Dictionary<string, HashSet<(int, int)>>();
            var bad = new List<Character.Item>();
            foreach (var it in ch.Inventory.Items)
            {
                var loc = it?.Location?.LocationClass;
                if (it == null || loc == null || it.ParentId == null || it.SlotId == null) continue;
                var (w, h) = BaseSize(it.Tpl);
                if (loc.R.Enum == Character.REnum.Vertical || loc.R.Integer == 1) (w, h) = (h, w);
                string key = it.ParentId + "/" + it.SlotId;
                if (!occupied.TryGetValue(key, out var cells)) occupied[key] = cells = new HashSet<(int, int)>();
                var mine = new List<(int, int)>();
                bool clash = false;
                for (int x = (int)loc.X; x < loc.X + w; x++)
                    for (int y = (int)loc.Y; y < loc.Y + h; y++)
                    {
                        if (cells.Contains((x, y))) clash = true;
                        mine.Add((x, y));
                    }
                if (clash) bad.Add(it);
                else foreach (var c in mine) cells.Add(c);
            }
            int moved = 0;
            foreach (var it in bad)
            {
                // take it out of every grid first, so the search does not count its old cells
                var oldLocation = it.Location;
                it.Location = null;
                var (w, h) = BaseSize(it.Tpl);
                var spot = FindFreeStashSpot(ch, w, h);
                if (spot == null)
                {
                    // the game refuses this item on every load anyway (invisible, unusable); keeping it only keeps the
                    // error, so it goes, with whatever is inside it
                    it.Location = oldLocation;
                    var tree = GetTree(ch, it.Id);
                    foreach (var part in tree) ch.Inventory.Items.Remove(part);
                    Utilities.Debug.PrintWarn($"{reason}: item {it.Id} ({it.Tpl}) overlapped another and the stash has no room for it; " +
                        $"removed it ({tree.Count} item(s) incl. contents; the game never loaded it)", "INVENTORY");
                    moved++;
                    continue;
                }
                it.ParentId = ch.Inventory.Stash;
                it.SlotId = "hideout";
                it.Location = spot;
                moved++;
            }
            if (moved > 0) Utilities.Debug.PrintInfo($"{reason}: moved {moved} overlapping item(s) to free stash cells", "INVENTORY");
            return moved;
        }

        /// <summary>
        /// Puts an item tree (root first) into the stash. Returns false if there is no room.
        /// </summary>
        public static bool PlaceInStash(Character.Base ch, List<Character.Item> tree, List<Character.Item> added)
        {
            var root = tree[0];
            var (w, h) = BaseSize(root.Tpl);
            var spot = FindFreeStashSpot(ch, w, h);
            if (spot == null) return false;
            root.ParentId = ch.Inventory.Stash;
            root.SlotId = "hideout";
            root.Location = spot;
            ch.Inventory.Items.AddRange(tree);
            added.AddRange(tree);
            return true;
        }

        /// <summary>
        /// Adds count of a stackable template to the stash: tops up existing stacks first, then makes new ones.
        /// Returns how many could not be placed.
        /// </summary>
        public static int AddStackable(Character.Base ch, string tpl, int count, List<Character.Item> changed, List<Character.Item> added)
        {
            int max = StackMax(tpl);
            foreach (var it in ch.Inventory.Items)
            {
                if (count <= 0) break;
                if (it == null || it.Tpl != tpl || it.ParentId != ch.Inventory.Stash) continue;
                it.Upd ??= new Character.Upd { StackObjectsCount = 1 };
                int have = it.Upd.StackObjectsCount ?? 1;
                int add = Math.Min(max - have, count);
                if (add <= 0) continue;
                it.Upd.StackObjectsCount = have + add;
                count -= add;
                if (!changed.Contains(it)) changed.Add(it);
            }
            while (count > 0)
            {
                int n = Math.Min(max, count);
                var item = new Character.Item { Id = NewId(), Tpl = tpl, Upd = new Character.Upd { StackObjectsCount = n } };
                if (!PlaceInStash(ch, new List<Character.Item> { item }, added)) break;
                count -= n;
            }
            return count;
        }
    }
}
