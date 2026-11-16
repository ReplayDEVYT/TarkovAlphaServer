using Newtonsoft.Json.Linq;
using ServerLib.Json.Classes;
using ServerLib.Utilities;

namespace ServerLib.Controllers
{
    /// <summary>
    /// Trader loyalty per profile (Character.TraderStandings[traderId]: level, sales_sum, standing, next_level).
    ///
    /// Loyalty levels come from the trader file (Files/traders/&lt;id&gt;.json, data.loyalty.loyaltyLevels "0".."3":
    /// minLevel, minSalesSum, minStanding). Entry "0" is loyalty level 1, "1" is level 2, and so on; a level is reached
    /// when the player's level, the trade volume with the trader (sales_sum, roubles, counted for buying and selling) and
    /// the standing (quest rewards) all meet it, and every level below it. The assort's loyal_level_items map each offer
    /// to the loyalty level it needs (TradingController filters the assort and refuses purchases above the level).
    /// </summary>
    public static class TraderProgress
    {
        sealed class Requirement
        {
            public int MinLevel;
            public long MinSalesSum;
            public float MinStanding;
        }

        static List<Requirement> Levels(string traderId)
        {
            var result = new List<Requirement>();
            try
            {
                if (string.IsNullOrEmpty(traderId) || traderId.Contains('/') || traderId.Contains('\\') || traderId.Contains("..")) return result;
                string path = $"Files/traders/{traderId}.json";
                if (!File.Exists(path)) return result;
                var root = JObject.Parse(File.ReadAllText(path));
                var levels = (root["data"] ?? root)["loyalty"]?["loyaltyLevels"] as JObject;
                if (levels == null) return result;
                foreach (var p in levels.Properties().OrderBy(p => int.TryParse(p.Name, out int n) ? n : 99))
                    result.Add(new Requirement
                    {
                        MinLevel = p.Value["minLevel"]?.Value<int>() ?? 0,
                        MinSalesSum = p.Value["minSalesSum"]?.Value<long>() ?? 0,
                        MinStanding = p.Value["minStanding"]?.Value<float>() ?? 0f
                    });
            }
            catch (Exception ex)
            {
                Debug.PrintWarn($"Loyalty levels of {traderId} unreadable: {ex.Message}", "TRADING");
            }
            return result;
        }

        public static int PlayerLevel(Character.Base ch) => QuestController.LevelFor(ch?.Info?.Experience ?? 0);

        static Character.TraderStanding Standing(Character.Base ch, string traderId)
        {
            ch.TraderStandings ??= new Dictionary<string, Character.TraderStanding>();
            if (!ch.TraderStandings.TryGetValue(traderId, out var s))
            {
                QuestController.EnsureTraderStandings(ch);
                if (!ch.TraderStandings.TryGetValue(traderId, out s))
                    ch.TraderStandings[traderId] = s = new Character.TraderStanding { level = 1 };
            }
            return s;
        }

        /// <summary>The profile's loyalty level with the trader (1 when the trader has no loyalty table).</summary>
        public static int LoyaltyLevel(Character.Base ch, string traderId)
        {
            if (ch == null) return 1;
            Update(ch, traderId);
            return Math.Max(1, Standing(ch, traderId).level);
        }

        /// <summary>Recomputes level and next_level for one trader. Returns true when the level changed.</summary>
        public static bool Update(Character.Base ch, string traderId)
        {
            if (ch == null || string.IsNullOrEmpty(traderId)) return false;
            var s = Standing(ch, traderId);
            var levels = Levels(traderId);
            int playerLevel = PlayerLevel(ch);
            int reached = 1;
            for (int i = 0; i < levels.Count; i++)
            {
                var r = levels[i];
                if (playerLevel < r.MinLevel || s.sales_sum < r.MinSalesSum || s.standing + 1e-4f < r.MinStanding) break;
                reached = i + 1;
            }
            int before = s.level;
            s.level = reached;
            var next = reached < levels.Count ? levels[reached] : null;
            // null at the highest level: the client then hides the next-level panel
            s.next_level = next == null ? null : new Character.TraderStanding.StandingLevel
            {
                min_level = next.MinLevel,
                min_sales_sum = (int)Math.Min(int.MaxValue, next.MinSalesSum),
                standing = next.MinStanding
            };
            if (before != reached)
                Debug.PrintInfo($"{ch.Info?.Nickname}: loyalty with {traderId} {before} -> {reached}", "TRADING");
            return before != reached;
        }

        public static void UpdateAll(Character.Base ch)
        {
            if (ch?.TraderStandings == null) return;
            foreach (var id in ch.TraderStandings.Keys.ToList()) Update(ch, id);
        }

        /// <summary>Counts a trade with the trader (roubles bought or sold) towards the loyalty levels.</summary>
        public static void AddTrade(Character.Base ch, string traderId, long roubles)
        {
            if (ch == null || string.IsNullOrEmpty(traderId) || roubles <= 0) return;
            var s = Standing(ch, traderId);
            s.sales_sum = (int)Math.Min(int.MaxValue, (long)s.sales_sum + roubles);
            Update(ch, traderId);
        }
    }
}
