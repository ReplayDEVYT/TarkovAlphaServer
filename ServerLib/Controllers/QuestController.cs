using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ServerLib.Json.Classes;
using ServerLib.Utilities;

namespace ServerLib.Controllers
{
    /// <summary>
    /// Quests (tasks) for the 0.1 client.
    ///
    /// Templates: one file per quest, Files/quests/&lt;id&gt;.json, in the client's own template format (GClass665):
    /// _id, traderId, name, description, image, type, isKey, rewards {Started|Success|Fail: [GClass671]},
    /// conditions {AvailableForStart|AvailableForFinish|Fail: [{_parent: "Level", _props: {...}}]}.
    /// An optional "_server" object holds settings the client never sees (it is stripped from /client/quest/list):
    ///   handOverItems (default false): QuestComplete also takes the items of plain CollectItem conditions.
    /// Objectives of type "HandoverItem" (a client addition, ConditionHandoverItem) are handed in one by one with the
    /// objective's HANDOVER button: action "QuestHandover" {qid, conditionId} takes the held items (as many as the
    /// objective still needs) and counts them in the backend counter keyed by the condition id; the objective is met
    /// when that counter reaches its value. Backend counters belong to the backend (MergeOnSave keeps them).
    /// Texts: Files/locales/&lt;lang&gt;/quests.json, {"&lt;quest id&gt;": {name, description, conditions: {"&lt;condition id&gt;": text}}},
    /// for the language the client last asked a locale for; the template's own texts otherwise.
    /// Files are read on every request, so edits (Unity quest editor) apply without a restart.
    ///
    /// Progress is tracked by the client (GClass668): it computes Locked / AvailableForStart / AvailableForFinish from the
    /// conditions and saves counters at the end of a raid. The backend owns Started / Success / Fail: QuestAccept and
    /// QuestComplete (items/moving) check the conditions it can evaluate, hand out rewards, and a profile save cannot
    /// change those three statuses on its own (MergeOnSave).
    /// </summary>
    public static class QuestController
    {
        const string QuestFolder = "Files/quests";

        static readonly Dictionary<string, string> SessionLanguage = new();

        /// <summary>Error of the last QuestAccept/QuestComplete (client GClass187 codes), sent with the items/moving answer.</summary>
        public static int LastErrorCode;
        public static string? LastErrorMessage;

        public static void SetLanguage(string sessionId, string lang)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(lang)) return;
            lock (SessionLanguage) SessionLanguage[sessionId] = lang.ToLowerInvariant();
        }

        static string Language(string sessionId)
        {
            lock (SessionLanguage) return SessionLanguage.TryGetValue(sessionId ?? "", out var l) ? l : "en";
        }

        // ---------------------------------------------------------------- templates

        /// <summary>All templates as stored (with "_server"), by id.</summary>
        public static Dictionary<string, JObject> LoadTemplates()
        {
            var result = new Dictionary<string, JObject>();
            if (!Directory.Exists(QuestFolder)) return result;
            foreach (var file in Directory.GetFiles(QuestFolder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var token = JToken.Parse(File.ReadAllText(file));
                    if (token is JObject o && o["_id"] != null)
                        result[o["_id"]!.ToString()] = o;
                }
                catch (Exception ex)
                {
                    Debug.PrintError($"Quest file {file}: {ex.Message}", "QUESTS");
                }
            }
            return result;
        }

        static JObject? Template(string qid)
        {
            LoadTemplates().TryGetValue(qid ?? "", out var t);
            return t;
        }

        static IEnumerable<JObject> Conditions(JObject template, string status)
        {
            if (template["conditions"]?[status] is JArray a)
                foreach (var c in a.OfType<JObject>()) yield return c;
        }

        static IEnumerable<JObject> Rewards(JObject template, string status)
        {
            if (template["rewards"]?[status] is JArray a)
                foreach (var r in a.OfType<JObject>()) yield return r;
        }

        /// <summary>/client/quest/list: {"Templates": [...]}. Without "completed", finished quests are left out (the
        /// client asks again with completed = true when "Show completed" is ticked).</summary>
        public static string QuestList(string sessionId, bool completed)
        {
            var ch = CharacterController.GetCharacter(sessionId);
            var finished = new HashSet<string>();
            if (ch?.Quests != null)
                foreach (var q in ch.Quests)
                    if (q.status == "Success" || q.status == "Fail") finished.Add(q.qid);

            JObject? texts = null;
            string localePath = $"Files/locales/{Language(sessionId)}/quests.json";
            if (!File.Exists(localePath)) localePath = "Files/locales/en/quests.json";
            if (File.Exists(localePath))
            {
                try { texts = JObject.Parse(File.ReadAllText(localePath)); }
                catch (Exception ex) { Debug.PrintError($"{localePath}: {ex.Message}", "QUESTS"); }
            }

            var list = new JArray();
            foreach (var template in LoadTemplates().Values)
            {
                string id = template["_id"]!.ToString();
                if (!completed && finished.Contains(id)) continue;
                list.Add(ForClient(template, texts?[id] as JObject));
            }
            return JsonConvert.SerializeObject(new JObject { ["Templates"] = list });
        }

        /// <summary>The template as the client needs it: texts applied, server settings removed, and every status key the
        /// quest screen indexes directly present (QuestView reads rewards[Started/Success/Fail] and
        /// conditions[AvailableForStart/AvailableForFinish/Fail] without checking).</summary>
        static JObject ForClient(JObject template, JObject? texts)
        {
            var o = (JObject)template.DeepClone();
            o.Remove("_server");
            if (texts != null)
            {
                if (texts["name"] != null) o["name"] = texts["name"]!.ToString();
                if (texts["description"] != null) o["description"] = texts["description"]!.ToString();
            }
            var rewards = o["rewards"] as JObject ?? new JObject();
            foreach (var key in new[] { "Started", "Success", "Fail" })
                if (rewards[key] is not JArray) rewards[key] = new JArray();
            o["rewards"] = rewards;
            var conditions = o["conditions"] as JObject ?? new JObject();
            foreach (var key in new[] { "AvailableForStart", "AvailableForFinish", "Fail" })
                if (conditions[key] is not JArray) conditions[key] = new JArray();
            o["conditions"] = conditions;
            var conditionTexts = texts?["conditions"] as JObject;
            if (conditionTexts != null)
                foreach (var list in conditions.Properties().Select(p => p.Value).OfType<JArray>())
                    foreach (var c in list.OfType<JObject>())
                    {
                        var props = c["_props"] as JObject;
                        string? cid = props?["id"]?.ToString();
                        if (props != null && !string.IsNullOrEmpty(cid) && conditionTexts[cid] != null)
                            props["description"] = conditionTexts[cid]!.ToString();
                    }
            return o;
        }

        // ---------------------------------------------------------------- profile helpers

        static Character.Quest GetOrAddQuest(Character.Base ch, string qid)
        {
            ch.Quests ??= Array.Empty<Character.Quest>();
            var q = ch.Quests.FirstOrDefault(x => x.qid == qid);
            if (q == null)
            {
                q = new Character.Quest { qid = qid, status = "Locked" };
                ch.Quests = ch.Quests.Append(q).ToArray();
            }
            return q;
        }

        static string QuestStatus(Character.Base ch, string qid) => ch.Quests?.FirstOrDefault(x => x.qid == qid)?.status ?? "Locked";

        static int CounterValue(Character.Base ch, string counterId) =>
            ch.ConditionCounters?.Counters?.FirstOrDefault(c => c.id == counterId)?.value ?? 0;

        static void SetCounter(Character.Base ch, string counterId, int value)
        {
            ch.ConditionCounters ??= new Character.ConditionCounters();
            ch.ConditionCounters.Counters ??= new List<Character.ConditionCounter>();
            var c = ch.ConditionCounters.Counters.FirstOrDefault(x => x.id == counterId);
            if (c == null) ch.ConditionCounters.Counters.Add(c = new Character.ConditionCounter { id = counterId });
            c.value = value;
        }

        static int BackendCounter(Character.Base ch, string id) =>
            ch.BackendCounters != null && id != null && ch.BackendCounters.TryGetValue(id, out var c) ? c.value : 0;

        static void SetBackendCounter(Character.Base ch, string id, int value)
        {
            if (string.IsNullOrEmpty(id)) return;
            ch.BackendCounters ??= new Dictionary<string, Character.BackendCounters>();
            if (!ch.BackendCounters.TryGetValue(id, out var c)) ch.BackendCounters[id] = c = new Character.BackendCounters();
            c.value = value;
        }

        /// <summary>Level from experience, as the client computes it (GClass632.smethod_0 over the globals' exp table).</summary>
        public static int LevelFor(int experience)
        {
            try
            {
                var root = JObject.Parse(File.ReadAllText("Files/static/globals.json"));
                var table = (root["data"] ?? root)["config"]?["exp"]?["level"]?["exp_table"] as JArray;
                if (table == null) return 1;
                long sum = 0;
                for (int i = 0; i < table.Count; i++)
                {
                    sum += table[i]["exp"]?.Value<long>() ?? 0;
                    if (experience < sum) return i;
                }
                return table.Count;
            }
            catch { return 1; }
        }

        static JArray TraderList()
        {
            try
            {
                var token = JToken.Parse(File.ReadAllText("Files/static/traderList.json"));
                return (token is JObject o ? o["data"] : token) as JArray ?? new JArray();
            }
            catch { return new JArray(); }
        }

        /// <summary>
        /// Gives the profile a TraderStandings entry per trader (the trader's starting standing from traderList.json).
        /// The client only takes standings for traders already in the profile (GClass183.method_27), and quest conditions
        /// and rewards on trader standing/loyalty need the entry. Returns true when something was added.
        /// </summary>
        public static bool EnsureTraderStandings(Character.Base ch)
        {
            if (ch == null) return false;
            ch.TraderStandings ??= new Dictionary<string, Character.TraderStanding>();
            bool changed = false;
            foreach (var trader in TraderList().OfType<JObject>())
            {
                string? id = trader["_id"]?.ToString();
                if (string.IsNullOrEmpty(id) || ch.TraderStandings.ContainsKey(id)) continue;
                var s = trader["standing"];
                ch.TraderStandings[id] = new Character.TraderStanding
                {
                    level = s?["level"]?.Value<int>() ?? 1,
                    sales_sum = s?["sales_sum"]?.Value<int>() ?? 0,
                    standing = s?["standing"]?.Value<float>() ?? 0,
                    next_level = new Character.TraderStanding.StandingLevel
                    {
                        min_level = s?["next_level"]?["min_level"]?.Value<int>() ?? 0,
                        min_sales_sum = s?["next_level"]?["min_sales_sum"]?.Value<int>() ?? 0,
                        standing = (s?["next_level"]?["standing"] ?? s?["next_level"]?["min_standing"])?.Value<float>() ?? 0
                    }
                };
                changed = true;
            }
            return changed;
        }

        /// <summary>traderList.json with this profile's standing per trader (the client copies it over its own,
        /// GClass183.method_27, so without this every trader's standing went back to the file's value).</summary>
        public static string TraderListFor(string sessionId)
        {
            var root = JToken.Parse(File.ReadAllText("Files/static/traderList.json"));
            var ch = CharacterController.GetCharacter(sessionId);
            var list = (root is JObject o ? o["data"] : root) as JArray;
            if (ch != null) EnsureTraderStandings(ch);
            // "display": false = hidden. The client has no such field and lists every trader it gets, so hidden ones
            // are left out here (standings, assorts and getTrader/{id} still work for them).
            if (list != null)
                foreach (var hidden in list.OfType<JObject>().Where(t => t["display"]?.Type == JTokenType.Boolean && !t["display"]!.Value<bool>()).ToList())
                    list.Remove(hidden);
            if (list != null)
                foreach (var trader in list.OfType<JObject>())
                {
                    if (ch != null) ApplyStanding(ch, trader);
                    // restock countdown and running sale (TraderStock)
                    TraderStock.ApplyInfo(trader);
                }
            return root.ToString(Formatting.None);
        }

        /// <summary>The profile's standing with this trader into a trader-info object (the client copies it over its own).</summary>
        public static void ApplyStanding(Character.Base ch, JObject trader)
        {
            EnsureTraderStandings(ch);
            string? id = trader["_id"]?.ToString();
            if (id == null || ch.TraderStandings == null || !ch.TraderStandings.TryGetValue(id, out var s)) return;
            TraderProgress.Update(ch, id); // loyalty level from player level, trade volume and standing
            var standing = trader["standing"] as JObject ?? new JObject();
            standing["level"] = s.level;
            standing["sales_sum"] = s.sales_sum;
            standing["standing"] = s.standing;
            // the client reads next_level.standing (Standing.GClass630); "min_standing" (the trader files' spelling) is
            // ignored by it, so the requirement showed as 0.00 and as met. Null at the top level hides the panel.
            standing["next_level"] = s.next_level == null ? JValue.CreateNull() : new JObject
            {
                ["min_level"] = s.next_level.min_level,
                ["min_sales_sum"] = s.next_level.min_sales_sum,
                ["standing"] = s.next_level.standing,
                ["min_standing"] = s.next_level.standing
            };
            trader["standing"] = standing;
        }

        // ---------------------------------------------------------------- conditions

        static bool Compare(double current, double value, string? method)
        {
            switch (method ?? ">=")
            {
                case "==": case "Equal": return Math.Abs(current - value) < 1e-4;
                case "!=": case "NotEqual": return Math.Abs(current - value) >= 1e-4;
                case ">": case "More": return current > value;
                case "<": case "Less": return current < value;
                case "<=": case "LessOrEqual": return current <= value;
                default: return current >= value;
            }
        }

        /// <summary>Items with this template in the equipment and the stash (counted as the client does, GClass432.method_11:
        /// one per item, not per stack).</summary>
        static List<Character.Item> ItemsOf(Character.Base ch, string tpl)
        {
            var roots = new[] { ch.Inventory?.Equipment, ch.Inventory?.Stash }.Where(r => !string.IsNullOrEmpty(r)).ToList();
            var result = new List<Character.Item>();
            foreach (var root in roots)
                foreach (var it in InventoryHelper.GetTree(ch, root!))
                    if (it.Id != root && it.Tpl == tpl) result.Add(it);
            return result;
        }

        /// <summary>
        /// Whether one condition holds for the profile, or null when the backend cannot tell (in-raid conditions without a
        /// counter: Skill, Experience, VisitPlace, StatisticsCounter). Those are left to the client.
        /// </summary>
        static bool? Test(Character.Base ch, Character.Quest? quest, JObject condition)
        {
            string type = condition["_parent"]?.ToString() ?? "";
            var p = condition["_props"] as JObject ?? new JObject();
            double value = p["value"]?.Value<double>() ?? 0;
            string? cmp = p["compareMethod"]?.ToString();
            string target = p["target"]?.ToString() ?? "";
            switch (type)
            {
                case "Level":
                    return Compare(LevelFor(ch.Info?.Experience ?? 0), value, cmp);
                case "Quest":
                {
                    // client: 1 when the quest has the status, -1 otherwise, tested against value (0 >= by default)
                    string want = p["status"]?.Type == JTokenType.Integer
                        ? Character.QuestStatusNames[Math.Clamp(p["status"]!.Value<int>(), 0, 5)]
                        : p["status"]?.ToString() ?? "Success";
                    return Compare(QuestStatus(ch, target) == want ? 1 : -1, value, cmp);
                }
                case "TraderStanding":
                    return ch.TraderStandings != null && ch.TraderStandings.TryGetValue(target, out var s1) ? Compare(s1.standing, value, cmp) : Compare(-1, value, cmp);
                case "TraderLoyalty":
                    return ch.TraderStandings != null && ch.TraderStandings.TryGetValue(target, out var s2) ? Compare(s2.level, value, cmp) : Compare(-1, value, cmp);
                case "CounterCreator":
                {
                    string? counterId = p["counter"]?["id"]?.ToString();
                    return counterId == null ? null : Compare(CounterValue(ch, counterId), value, cmp);
                }
                case "CollectItem":
                    return Compare(ItemsOf(ch, target).Count, value, cmp);
                case "HandoverItem":
                    return Compare(BackendCounter(ch, p["id"]?.ToString() ?? ""), value, cmp);
                case "SellItemToTrader":
                    return Compare(BackendCounter(ch, p["id"]?.ToString() ?? ""), value, cmp);
                case "QuestTime":
                    if (quest == null || quest.startTime <= 0) return false;
                    return Compare(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - quest.startTime, value, cmp);
                default:
                    return null;
            }
        }

        static bool VerifyConditions => ConfigController.Configs.Server?.Quests?.VerifyConditions ?? true;

        /// <summary>All conditions of a status hold (unknown ones count as met). Fail conditions: any one is enough.</summary>
        static bool Met(Character.Base ch, Character.Quest? quest, JObject template, string status, out string failed)
        {
            failed = "";
            if (status == "Fail")
            {
                foreach (var c in Conditions(template, "Fail"))
                    if (Test(ch, quest, c) == true) { failed = c["_parent"]?.ToString() ?? ""; return true; }
                return false;
            }
            foreach (var c in Conditions(template, status))
                if (Test(ch, quest, c) == false) { failed = Describe(c); return false; }
            return true;
        }

        static string Describe(JObject c)
        {
            var p = c["_props"];
            return $"{c["_parent"]} {p?["target"]} {p?["compareMethod"] ?? ">="} {p?["value"]}".Replace("  ", " ").Trim();
        }

        // ---------------------------------------------------------------- rewards

        /// <summary>
        /// Hands out the rewards of one status. Items go into the stash (new ids). When an item does not fit:
        /// removeExcess drops it with a warning; otherwise everything placed so far is taken back and false is returned.
        /// </summary>
        static bool GiveRewards(Character.Base ch, JObject template, string status, bool removeExcess, ItemController.MoveItems result)
        {
            var added = new List<Character.Item>();
            var changed = new List<Character.Item>();
            var stackBefore = new Dictionary<Character.Item, int?>();
            var settings = JsonSerializer.Create(Character.Converter.Settings);
            bool ok = true;

            foreach (var r in Rewards(template, status).Where(r => r["type"]?.ToString() == "Item"))
            {
                var items = (r["items"] as JArray)?.ToObject<List<Character.Item>>(settings);
                string target = r["target"]?.ToString() ?? "";
                int count = Math.Max(1, (int)Math.Round(r["value"]?.Value<double>() ?? 1));
                var root = items?.FirstOrDefault(i => i.Id == target) ?? items?.FirstOrDefault(i => string.IsNullOrEmpty(i.ParentId));
                if (items == null || root == null)
                {
                    Debug.PrintWarn($"Quest {template["_id"]}: item reward without items", "QUESTS");
                    continue;
                }
                bool hasChildren = items.Any(i => i.ParentId == root.Id);
                int max = InventoryHelper.StackMax(root.Tpl);
                if (max > 1 && !hasChildren)
                {
                    // remember the stacks it tops up so a failed delivery can be undone
                    foreach (var it in ch.Inventory.Items.Where(i => i != null && i.Tpl == root.Tpl && i.ParentId == ch.Inventory.Stash))
                        if (!stackBefore.ContainsKey(it)) stackBefore[it] = it.Upd?.StackObjectsCount;
                    int total = count * Math.Max(1, root.Upd?.StackObjectsCount ?? 1);
                    int left = InventoryHelper.AddStackable(ch, root.Tpl, total, changed, added);
                    if (left > 0)
                    {
                        if (!removeExcess) { ok = false; break; }
                        Debug.PrintWarn($"Quest reward: stash full, {left} x {root.Tpl} dropped", "QUESTS");
                    }
                    continue;
                }
                for (int n = 0; n < count; n++)
                {
                    var tree = InventoryHelper.CloneTree(items, root.Id);
                    if (!InventoryHelper.PlaceInStash(ch, tree, added))
                    {
                        if (!removeExcess) { ok = false; break; }
                        Debug.PrintWarn($"Quest reward: stash full, {count - n} x {root.Tpl} dropped", "QUESTS");
                        break;
                    }
                }
                if (!ok) break;
            }

            if (!ok)
            {
                foreach (var it in added) ch.Inventory.Items.Remove(it);
                foreach (var kv in stackBefore)
                    if (kv.Key.Upd != null) kv.Key.Upd.StackObjectsCount = kv.Value;
                return false;
            }

            foreach (var r in Rewards(template, status))
            {
                string type = r["type"]?.ToString() ?? "";
                double value = r["value"]?.Value<double>() ?? 0;
                string target = r["target"]?.ToString() ?? "";
                switch (type)
                {
                    case "Item":
                        break;
                    case "Experience":
                        if (ch.Info != null)
                        {
                            ch.Info.Experience += (int)value;
                            ch.Info.Level = LevelFor(ch.Info.Experience);
                        }
                        break;
                    case "TraderStanding":
                        EnsureTraderStandings(ch);
                        if (ch.TraderStandings.TryGetValue(target, out var s))
                        {
                            s.standing += (float)value;
                            TraderProgress.Update(ch, target); // a loyalty level may need this standing
                        }
                        else Debug.PrintWarn($"Quest reward: unknown trader {target}", "QUESTS");
                        break;
                    default:
                        // the 0.1 client shows only Item / Experience / TraderStanding rewards (QuestRewardList)
                        Debug.PrintWarn($"Quest reward type {type} is not applied", "QUESTS");
                        break;
                }
            }
            result.items.@new.AddRange(added);
            foreach (var c in changed) if (!result.items.change.Contains(c)) result.items.change.Add(c);
            return true;
        }

        static void Fail(int code, string message)
        {
            LastErrorCode = code;
            LastErrorMessage = message;
            Debug.PrintWarn(message, "QUESTS");
        }

        // ---------------------------------------------------------------- actions (items/moving)

        /// <summary>{"Action": "QuestAccept", "qid": ...}</summary>
        public static void Accept(string sessionId, JToken action, ItemController.MoveItems result)
        {
            var ch = CharacterController.GetCharacter(sessionId);
            string qid = action["qid"]?.ToString() ?? "";
            if (ch == null) { Fail(702, "No character"); return; }
            var template = Template(qid);
            if (template == null) { Fail(703, $"Quest {qid} not found"); return; }
            var quest = GetOrAddQuest(ch, qid);
            if (quest.status != "Locked" && quest.status != "AvailableForStart")
            {
                Fail(706, $"Quest {qid} cannot be accepted: status {quest.status}");
                return;
            }
            if (VerifyConditions && !Met(ch, quest, template, "AvailableForStart", out string failed))
            {
                Fail(704, $"Quest {qid} is not available: {failed}");
                return;
            }
            if (!GiveRewards(ch, template, "Started", false, result))
            {
                Fail(705, $"Quest {qid}: no room in the stash for the starting equipment");
                return;
            }
            quest.status = "Started";
            quest.startTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            quest.completedConditions = new List<string>();
            // progress starts now (the client resets the same counters, GClass668.method_ResetProgress)
            foreach (var list in new[] { "AvailableForStart", "AvailableForFinish", "Fail" })
                foreach (var c in Conditions(template, list))
                {
                    string type = c["_parent"]?.ToString() ?? "";
                    if (type == "CounterCreator" && c["_props"]?["counter"]?["id"] is JToken counterId) SetCounter(ch, counterId.ToString(), 0);
                    if (type == "SellItemToTrader" || type == "HandoverItem") SetBackendCounter(ch, c["_props"]?["id"]?.ToString() ?? "", 0);
                }
            Debug.PrintInfo($"Quest {template["name"]} ({qid}) accepted", "QUESTS");
        }

        /// <summary>{"Action": "QuestComplete", "qid": ..., "removeExcessItems": true}</summary>
        public static void Complete(string sessionId, JToken action, ItemController.MoveItems result)
        {
            var ch = CharacterController.GetCharacter(sessionId);
            string qid = action["qid"]?.ToString() ?? "";
            bool removeExcess = action["removeExcessItems"]?.Value<bool>() ?? true;
            if (ch == null) { Fail(702, "No character"); return; }
            var template = Template(qid);
            if (template == null) { Fail(703, $"Quest {qid} not found"); return; }
            var quest = GetOrAddQuest(ch, qid);
            if (quest.status != "Started" && quest.status != "AvailableForFinish")
            {
                Fail(706, $"Quest {qid} cannot be completed: status {quest.status}");
                return;
            }
            if (Met(ch, quest, template, "Fail", out string failReason))
            {
                SetFailed(ch, quest, template, result);
                Fail(707, $"Quest {qid} has failed ({failReason})");
                return;
            }
            if (VerifyConditions && !Met(ch, quest, template, "AvailableForFinish", out string failed))
            {
                Fail(707, $"Quest {qid} is not finished: {failed}");
                return;
            }
            if (!GiveRewards(ch, template, "Success", removeExcess, result))
            {
                Fail(705, $"Quest {qid}: no room in the stash for the rewards");
                return;
            }
            bool handOverCollect = template["_server"]?["handOverItems"]?.Value<bool>() ?? false;
            foreach (var c in Conditions(template, "AvailableForFinish").Where(c => handOverCollect && c["_parent"]?.ToString() == "CollectItem"))
                {
                    string tpl = c["_props"]?["target"]?.ToString() ?? "";
                    int need = (int)Math.Round(c["_props"]?["value"]?.Value<double>() ?? 0);
                    foreach (var it in ItemsOf(ch, tpl).Take(need))
                        foreach (var part in InventoryHelper.GetTree(ch, it.Id))
                        {
                            ch.Inventory.Items.Remove(part);
                            result.items.change.Remove(part);
                            result.items.del.Add(part);
                        }
                }
            quest.status = "Success";
            quest.completedConditions = Conditions(template, "AvailableForFinish").Select(c => c["_props"]?["id"]?.ToString()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
            Debug.PrintInfo($"Quest {template["name"]} ({qid}) completed", "QUESTS");
        }

        /// <summary>{"Action": "QuestHandover", "qid": ..., "conditionId": ...}: the HANDOVER button of a HandoverItem
        /// objective. Takes as many held items of the objective's template as it still needs (stash before equipment)
        /// and adds them to the objective's backend counter.</summary>
        public static void Handover(string sessionId, JToken action, ItemController.MoveItems result)
        {
            var ch = CharacterController.GetCharacter(sessionId);
            string qid = action["qid"]?.ToString() ?? "";
            string conditionId = action["conditionId"]?.ToString() ?? "";
            if (ch == null) { Fail(702, "No character"); return; }
            var template = Template(qid);
            if (template == null) { Fail(703, $"Quest {qid} not found"); return; }
            var quest = GetOrAddQuest(ch, qid);
            if (quest.status != "Started")
            {
                Fail(706, $"Quest {qid}: cannot hand over items, status {quest.status}");
                return;
            }
            var condition = Conditions(template, "AvailableForFinish").FirstOrDefault(c => c["_parent"]?.ToString() == "HandoverItem" && c["_props"]?["id"]?.ToString() == conditionId);
            if (condition == null) { Fail(702, $"Quest {qid} has no hand-over objective {conditionId}"); return; }
            string tpl = condition["_props"]?["target"]?.ToString() ?? "";
            int need = (int)Math.Round(condition["_props"]?["value"]?.Value<double>() ?? 0);
            int done = BackendCounter(ch, conditionId);
            int remaining = need - done;
            if (remaining <= 0) { Fail(706, $"Quest {qid}: objective {conditionId} is already handed over"); return; }
            string stash = ch.Inventory?.Stash ?? "";
            var held = ItemsOf(ch, tpl);
            var take = held.Where(it => IsUnder(ch, it, stash)).Concat(held.Where(it => !IsUnder(ch, it, stash))).Take(remaining).ToList();
            if (take.Count == 0) { Fail(707, $"Quest {qid}: no {tpl} to hand over"); return; }
            foreach (var it in take)
                foreach (var part in InventoryHelper.GetTree(ch, it.Id))
                {
                    ch.Inventory.Items.Remove(part);
                    result.items.change.Remove(part);
                    result.items.del.Add(part);
                }
            SetBackendCounter(ch, conditionId, done + take.Count);
            Debug.PrintInfo($"Quest {template["name"]} ({qid}): handed over {take.Count} x {tpl} ({done + take.Count}/{need})", "QUESTS");
        }

        /// <summary>Whether the item lies (directly or nested) under the container with this id.</summary>
        static bool IsUnder(Character.Base ch, Character.Item item, string rootId)
        {
            var byId = ch.Inventory.Items.Where(i => i.Id != null).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
            string? parent = item.ParentId;
            for (int guard = 0; parent != null && guard < 64; guard++)
            {
                if (parent == rootId) return true;
                parent = byId.TryGetValue(parent, out var p) ? p.ParentId : null;
            }
            return false;
        }

        static void SetFailed(Character.Base ch, Character.Quest quest, JObject template, ItemController.MoveItems? result)
        {
            quest.status = "Fail";
            GiveRewards(ch, template, "Fail", true, result ?? new ItemController.MoveItems { items = new() });
            Debug.PrintInfo($"Quest {template["name"]} ({quest.qid}) failed", "QUESTS");
        }

        /// <summary>
        /// Profile save (end of raid): the client's quest list is taken, except that Started / Success / Fail stay what
        /// the backend decided (QuestAccept / QuestComplete). A quest the client reports failed (time limit, fail
        /// conditions) becomes Fail with its penalties when the backend had it in progress.
        /// </summary>
        public static void MergeOnSave(Character.Base? stored, Character.Base incoming)
        {
            if (stored == null || incoming == null) return;
            var templates = LoadTemplates();
            var before = (stored.Quests ?? Array.Empty<Character.Quest>()).ToDictionary(q => q.qid ?? "", q => q);
            var merged = new List<Character.Quest>();
            foreach (var q in incoming.Quests ?? Array.Empty<Character.Quest>())
            {
                before.TryGetValue(q.qid ?? "", out var old);
                string oldStatus = old?.status ?? "Locked";
                bool oldInProgress = oldStatus == "Started" || oldStatus == "AvailableForFinish";
                if (oldStatus == "Success" || oldStatus == "Fail")
                {
                    q.status = oldStatus;
                }
                else if (q.status == "Fail" && oldInProgress)
                {
                    q.startTime = old!.startTime;
                    if (templates.TryGetValue(q.qid ?? "", out var t)) SetFailed(incoming, q, t, null);
                }
                else if (q.status == "Success" || q.status == "Fail" || ((q.status == "Started" || q.status == "AvailableForFinish") && !oldInProgress))
                {
                    Debug.PrintWarn($"Profile save: quest {q.qid} {oldStatus} -> {q.status} not taken (only the backend changes it)", "QUESTS");
                    q.status = oldStatus;
                }
                if (oldInProgress && old != null) q.startTime = old.startTime;
                merged.Add(q);
                before.Remove(q.qid ?? "");
            }
            merged.AddRange(before.Values);
            incoming.Quests = merged.ToArray();
            // sales and hand-overs are counted by the backend (OnSold, Handover); a save never changes them
            if (stored.BackendCounters != null) incoming.BackendCounters = stored.BackendCounters;
            // trader standing, trade volume and loyalty level are the backend's too (quest rewards, trades)
            if (stored.TraderStandings != null) incoming.TraderStandings = stored.TraderStandings;
        }

        /// <summary>Selling to a trader counts for SellItemToTrader conditions of quests in progress (client GClass729.method_17).</summary>
        public static void OnSold(Character.Base ch, string traderId, string tpl, int count)
        {
            if (ch?.Quests == null || count <= 0) return;
            var templates = LoadTemplates();
            foreach (var q in ch.Quests.Where(q => q.status == "Started" || q.status == "AvailableForFinish"))
            {
                if (!templates.TryGetValue(q.qid ?? "", out var t)) continue;
                foreach (var c in Conditions(t, "AvailableForFinish").Where(c => c["_parent"]?.ToString() == "SellItemToTrader"))
                {
                    var p = c["_props"];
                    if (p?["target"]?.ToString() != tpl) continue;
                    string trader = p?["trader"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(trader) && trader != traderId) continue;
                    string id = p?["id"]?.ToString() ?? "";
                    SetBackendCounter(ch, id, BackendCounter(ch, id) + count);
                }
            }
        }
    }
}
