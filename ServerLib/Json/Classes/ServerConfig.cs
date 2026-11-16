using Newtonsoft.Json;

namespace ServerLib.Json
{
    public class ServerConfig
    {
        public class Base
        {
            [JsonProperty("ip")]
            public string Ip { get; set; }

            [JsonProperty("port")]
            public int Port { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("version")]
            public string Version { get; set; }

            [JsonProperty("debug")]     //  Can be used same as argument
            public bool Debug { get; set; }

            [JsonProperty("servers")]   //   Multiplayer server IPS
            public List<IPPort> Servers { get; set; }

            [JsonProperty("matchmaking")]   //   Optional; defaults apply when missing
            public MatchmakingConfig Matchmaking { get; set; } = new();

            [JsonProperty("quests")]        //   Optional; defaults apply when missing
            public QuestsConfig Quests { get; set; } = new();

            public class QuestsConfig
            {
                /// <summary>QuestAccept / QuestComplete check the conditions the backend can evaluate (level, standing,
                /// other quests, counters, items, sales). Off: the client's word is taken.</summary>
                [JsonProperty("verifyConditions")]
                public bool VerifyConditions { get; set; } = true;
            }

            [JsonProperty("trading")]
            public TradingConfig Trading { get; set; } = new();

            public class TradingConfig
            {
                /// <summary>Minutes between trader restocks (limited stock refills to the assort file's counts). 0 = never.</summary>
                [JsonProperty("restockMinutes")]
                public int RestockMinutes { get; set; } = 60;

                /// <summary>Offers with at least this many in the assort file never run out.</summary>
                [JsonProperty("unlimitedStock")]
                public int UnlimitedStock { get; set; } = 99999;

                /// <summary>Hide offers above the player's loyalty level (loyal_level_items) and refuse buying them.</summary>
                [JsonProperty("loyaltyLevels")]
                public bool LoyaltyLevels { get; set; } = true;

                /// <summary>Occasional sales: at each restock a trader starts one with this chance, lasting until the next restock.</summary>
                [JsonProperty("sales")]
                public bool Sales { get; set; } = true;

                /// <summary>Chance (0..1) per trader and restock that a sale starts.</summary>
                [JsonProperty("saleChance")]
                public double SaleChance { get; set; } = 0.2;

                /// <summary>Sale size in percent off money prices, picked between these two.</summary>
                [JsonProperty("saleMinPercent")]
                public int SaleMinPercent { get; set; } = 10;

                [JsonProperty("saleMaxPercent")]
                public int SaleMaxPercent { get; set; } = 30;
            }

            public class IPPort
            {
                [JsonProperty("ip")]
                public string Ip { get; set; }
                [JsonProperty("port")]
                public int Port { get; set; }
            
            }
        }
    }
}

namespace ServerLib.Json
{
    /// <summary>
    /// How matches get a game server address. See MatchController.
    /// </summary>
    public class MatchmakingConfig
    {
        /// <summary>
        /// "dedicated": every match gets its own port from [portStart, portEnd] on matchIp.
        /// "host": the player who creates the match hosts it on their own address and port.
        /// "auto": players who registered a host port host their matches, everyone else gets a dedicated port.
        /// </summary>
        [JsonProperty("mode")]
        public string Mode { get; set; } = "auto";

        /// <summary>Address handed to clients for dedicated matches. Empty = servers[0].ip.</summary>
        [JsonProperty("matchIp")]
        public string MatchIp { get; set; } = "";

        /// <summary>Address handed out for a player-hosted match when the host reached the backend over loopback. Empty = matchIp.</summary>
        [JsonProperty("hostPublicIp")]
        public string HostPublicIp { get; set; } = "";

        [JsonProperty("portStart")]
        public int PortStart { get; set; } = 7777;

        [JsonProperty("portEnd")]
        public int PortEnd { get; set; } = 7796;

        [JsonProperty("hostDefaultPort")]
        public int HostDefaultPort { get; set; } = 7777;

        [JsonProperty("minPlayers")]
        public int MinPlayers { get; set; } = 1;

        [JsonProperty("maxPlayers")]
        public int MaxPlayers { get; set; } = 10;

        /// <summary>Seconds a match stays open for more players before it starts.</summary>
        [JsonProperty("startDelaySeconds")]
        public int StartDelaySeconds { get; set; } = 10;

        /// <summary>
        /// Upper limit for the wait a player picks in the game (POST /client/match/wait/{seconds}); their
        /// choice replaces startDelaySeconds for the matches they create.
        /// </summary>
        [JsonProperty("maxStartDelaySeconds")]
        public int MaxStartDelaySeconds { get; set; } = 300;

        /// <summary>When true a match only starts after its game server called /server/match/ready/{matchId}.</summary>
        [JsonProperty("requireServerReady")]
        public bool RequireServerReady { get; set; } = false;

        /// <summary>Matches older than this are removed (raid crashed, nobody exited).</summary>
        /// <summary>
        /// Full path of the game exe with the reconstructed Assembly-CSharp (e.g. TestGame's). When set, the
        /// backend starts a headless game server for every dedicated match on that match's port. Empty = you
        /// start game servers yourself.
        /// </summary>
        [JsonProperty("gameServerPath")]
        public string GameServerPath { get; set; } = "";

        /// <summary>Extra command-line arguments for launched game servers.</summary>
        [JsonProperty("gameServerArgs")]
        public string GameServerArgs { get; set; } = "";

        /// <summary>A launched game server that has not reported ready by then cancels its match.</summary>
        [JsonProperty("gameServerStartTimeoutSeconds")]
        public int GameServerStartTimeoutSeconds { get; set; } = 90;

        [JsonProperty("matchTimeoutMinutes")]
        public int MatchTimeoutMinutes { get; set; } = 90;

        /// <summary>Raid length in minutes (the offline game uses 50). At the end everyone still in the raid leaves it.</summary>
        [JsonProperty("raidMinutes")]
        public int RaidMinutes { get; set; } = 50;

        /// <summary>
        /// A started match still takes new players while it has fewer than maxPlayers * lateJoinPlayerFraction
        /// players and started less than lateJoinMinutes ago (0 = never join a started match).
        /// </summary>
        [JsonProperty("lateJoinPlayerFraction")]
        public double LateJoinPlayerFraction { get; set; } = 0.5;

        [JsonProperty("lateJoinMinutes")]
        public int LateJoinMinutes { get; set; } = 10;

        /// <summary>
        /// Seconds a player who lost the connection (or closed the game) can rejoin their raid: the game server keeps
        /// their character, and the game offers "Reconnect" after the next login. 0 = leaving is final.
        /// </summary>
        [JsonProperty("reconnectSeconds")]
        public int ReconnectSeconds { get; set; } = 180;

        /// <summary>Save the character after an online raid (items found, used, lost, statistics). false = raids change nothing.</summary>
        [JsonProperty("saveOnlineRaids")]
        public bool SaveOnlineRaids { get; set; } = true;

        /// <summary>
        /// On death, missing in action (raid time ran out) or leaving the raid, the character loses everything it wore and carried except the secure container
        /// with its contents, the pockets item itself and the scabbard; health is restored. Online and offline raids.
        /// </summary>
        [JsonProperty("loseGearOnDeath")]
        public bool LoseGearOnDeath { get; set; } = true;

        /// <summary>Players who queue with the same group name (in the game's hosting box) join the same match and spawn together.</summary>
        [JsonProperty("groupSpawnRadius")]
        public float GroupSpawnRadius { get; set; } = 3f;
    }
}
