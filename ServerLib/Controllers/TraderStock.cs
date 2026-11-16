using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ServerLib.Utilities;

namespace ServerLib.Controllers
{
    /// <summary>
    /// Limited trader stock. The assort file's StackObjectsCount on an offer's root item is the trader's full stock
    /// (what the client shows and clamps the buy quantity to). Purchases take from it for everyone on the server; every
    /// trader refills to the file's values each trading.restockMinutes (default 60; 0 = never). Offers with at least
    /// trading.unlimitedStock items (default 99999, e.g. the "Everything" trader's 999999) or without a count never run
    /// out. Occasional sales (trading.sales...) are rolled at restocks. State: TraderStock.json in the server's working folder (not Files, which the editor tools write).
    /// </summary>
    public static class TraderStock
    {
        const string StatePath = "TraderStock.json";

        sealed class TraderState
        {
            public long RestockAt;
            public Dictionary<string, int> Sold = new();
            /// <summary>Sale running until SaleUntil (unix seconds): money prices SalePercent lower. 0 = no sale.</summary>
            public int SalePercent;
            public long SaleUntil;
        }

        static readonly Random random = new();

        static Json.ServerConfig.Base.TradingConfig Cfg => ConfigController.Configs.Server?.Trading ?? new Json.ServerConfig.Base.TradingConfig();

        /// <summary>Money items: a sale lowers these costs only (barter goods stay as they are).</summary>
        static readonly HashSet<string> MoneyTpls = new() { InventoryHelper.RoublesTpl, "5696686a4bdc2da3298b456a", "569668774bdc2da2298b4568" };

        static Dictionary<string, TraderState>? state;

        static int RestockMinutes => ConfigController.Configs.Server?.Trading?.RestockMinutes ?? 60;

        static int UnlimitedAt => ConfigController.Configs.Server?.Trading?.UnlimitedStock ?? 99999;

        static Dictionary<string, TraderState> State
        {
            get
            {
                if (state != null) return state;
                try
                {
                    state = File.Exists(StatePath) ? JsonConvert.DeserializeObject<Dictionary<string, TraderState>>(File.ReadAllText(StatePath)) : null;
                }
                catch (Exception ex)
                {
                    Debug.PrintWarn("TraderStock.json unreadable, stock starts full: " + ex.Message, "TRADING");
                }
                state ??= new Dictionary<string, TraderState>();
                return state;
            }
        }

        static void Save()
        {
            try { File.WriteAllText(StatePath, JsonConvert.SerializeObject(State, Formatting.Indented)); }
            catch (Exception ex) { Debug.PrintWarn("TraderStock.json not written: " + ex.Message, "TRADING"); }
        }

        static TraderState For(string traderId)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!State.TryGetValue(traderId, out var s))
            {
                State[traderId] = s = new TraderState { RestockAt = RestockMinutes > 0 ? now + RestockMinutes * 60L : 0 };
                Save();
            }
            // restock switched on after the state was written without a restock time
            if (s.RestockAt <= 0 && RestockMinutes > 0)
            {
                s.RestockAt = now + RestockMinutes * 60L;
                Save();
            }
            if (s.RestockAt > 0 && now >= s.RestockAt)
            {
                s.Sold.Clear();
                s.RestockAt = RestockMinutes > 0 ? now + RestockMinutes * 60L : 0;
                RollSale(traderId, s, now);
                Debug.PrintInfo($"Trader {traderId} restocked" + (s.SalePercent > 0 ? $", sale -{s.SalePercent}% until the next restock" : "") +
                    (s.RestockAt > 0 ? $"; next restock in {RestockMinutes} min" : ""), "TRADING");
                Save();
            }
            if (s.SalePercent > 0 && now >= s.SaleUntil)
            {
                s.SalePercent = 0;
                s.SaleUntil = 0;
                Save();
            }
            return s;
        }

        /// <summary>At each restock: with trading.saleChance a sale of saleMinPercent..saleMaxPercent until the next one.</summary>
        static void RollSale(string traderId, TraderState s, long now)
        {
            var cfg = Cfg;
            s.SalePercent = 0;
            s.SaleUntil = 0;
            if (!cfg.Sales || s.RestockAt <= 0 || random.NextDouble() >= cfg.SaleChance) return;
            int min = Math.Clamp(Math.Min(cfg.SaleMinPercent, cfg.SaleMaxPercent), 1, 95);
            int max = Math.Clamp(Math.Max(cfg.SaleMinPercent, cfg.SaleMaxPercent), 1, 95);
            s.SalePercent = random.Next(min, max + 1);
            s.SaleUntil = s.RestockAt;
        }

        /// <summary>Console/test: start a sale now, until the next restock (percent 0 ends it).</summary>
        public static void SetSale(string traderId, int percent)
        {
            var s = For(traderId);
            s.SalePercent = Math.Clamp(percent, 0, 95);
            s.SaleUntil = s.SalePercent > 0 ? (s.RestockAt > 0 ? s.RestockAt : DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600) : 0;
            Save();
        }

        /// <summary>Console/test: restock now (rolls a sale like a timed restock).</summary>
        public static void RestockNow(string traderId)
        {
            var s = For(traderId);
            s.RestockAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            For(traderId);
        }

        /// <summary>Current sale in percent (0 = none).</summary>
        public static int SalePercent(string traderId) => For(traderId).SalePercent;

        /// <summary>
        /// Cost of one unit of an offer in this payment item: money costs lowered by the running sale (rounded up, at least
        /// 1). The assort the client gets and the purchase check use this same value.
        /// </summary>
        public static double Cost(string traderId, string tpl, double count)
        {
            int p = SalePercent(traderId);
            if (p <= 0 || !MoneyTpls.Contains(tpl) || count <= 0) return count;
            return Math.Max(1, Math.Ceiling(count * (100 - p) / 100.0));
        }

        /// <summary>
        /// Trader info fields the client shows (TraderPanel, TraderInfoPanel): supply_next_time = restock time (the
        /// countdown), discount = -sale percent (the client shows a negative value as a discount: cyan, "good" icon),
        /// discount_end = when the sale ends. Values from the trader files are placeholders and are replaced.
        /// </summary>
        public static void ApplyInfo(JObject trader)
        {
            string? id = trader["_id"]?.ToString();
            if (string.IsNullOrEmpty(id)) return;
            var s = For(id);
            trader["supply_next_time"] = s.RestockAt;
            trader["discount"] = -s.SalePercent;
            trader["discount_end"] = s.SaleUntil;
        }

        /// <summary>Seconds until the trader restocks (0 when it never does).</summary>
        public static long SecondsToRestock(string traderId)
        {
            var s = For(traderId);
            return s.RestockAt <= 0 ? 0 : Math.Max(0, s.RestockAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        public static bool IsLimited(int? fileStock) => fileStock.HasValue && fileStock.Value < UnlimitedAt;

        /// <summary>How many the trader has now (fileStock = the assort file's count; null = unlimited).</summary>
        public static int? Remaining(string traderId, string offerId, int? fileStock)
        {
            if (!IsLimited(fileStock)) return fileStock;
            int sold = For(traderId).Sold.GetValueOrDefault(offerId);
            return Math.Max(0, fileStock!.Value - sold);
        }

        public static void Take(string traderId, string offerId, int? fileStock, int count)
        {
            if (!IsLimited(fileStock) || count <= 0) return;
            var s = For(traderId);
            s.Sold[offerId] = s.Sold.GetValueOrDefault(offerId) + count;
            Save();
        }
    }
}
