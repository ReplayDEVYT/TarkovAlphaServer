using ServerLib.Json;
using ServerLib.Json.Classes;
using ServerLib.Utilities;
using ServerLib.Utilities.Helpers;

namespace ServerLib.Controllers
{
    /// <summary>
    /// Matchmaking. How the 2016 client starts an online raid (GClass183 / MainApplication.method_10):
    ///   1. POST /client/match/join {location, mode, develop} -> profile status "MatchWait"
    ///   2. it polls /client/profile/status while the matchmaker screen is shown
    ///   3. once the status is "Busy" with ip/port it connects to that game server over UNET and loads the map
    /// This class decides when a match goes from MatchWait to Busy and which address it gets:
    ///   - dedicated: its own port from matchmaking.portStart..portEnd, for a game-server process started per match
    ///   - host: the address of the player who created it and the port they registered (/client/match/host/port/{port})
    /// The game server itself (the UNET side) does not exist yet; see Unity_Server/.
    /// </summary>
    public class MatchController
    {
        static MatchController()
        {
            Matches = new();
            Debug.PrintInfo("Initialization Done!", "MatchController");
        }

        static readonly object Lock = new();

        public static Dictionary<string, MatchData> Matches;

        /// <summary>Ports players registered for hosting their own matches (session id -> port). In memory only.</summary>
        public static Dictionary<string, int> HostPorts = new();

        /// <summary>Seconds a player wants their new matches to wait for others (session id -> seconds). In memory only.</summary>
        public static Dictionary<string, int> WaitSeconds = new();

        /// <summary>Group name a player queues with (session id -> name; /client/match/group/{name}). Same name = same match, spawn together.</summary>
        public static Dictionary<string, string> Groups = new();

        /// <summary>Address a hosting player gave for others to connect to (session id -> address; /client/match/host/ip/{ip}).</summary>
        public static Dictionary<string, string> HostIps = new();

        /// <summary>Players who turned "Allow late joining" off (/client/match/latejoin/0). In memory only.</summary>
        public static HashSet<string> NoLateJoin = new();

        public enum EHostType { Dedicated, Player }

        public class MatchData
        {
            public string MatchId;
            public string CreatorId;
            public List<UserStruct> Users = new();
            public string Ip;
            public int Port;
            public string Location;
            public string Mode;
            public bool IsStarted;
            public EHostType HostType;
            /// <summary>Session id of the hosting player (HostType.Player) or null.</summary>
            public string HostId;
            /// <summary>Set by the game server through /server/match/ready/{matchId}.</summary>
            public bool ServerReady;
            /// <summary>The backend started this match's game server (GameServerLauncher).</summary>
            public bool ServerLaunched;
            public long CreatedAt;
            public long StartedAt;
            /// <summary>Seconds this match waits for more players: the creator's choice or startDelaySeconds.</summary>
            public int StartDelaySeconds;
            /// <summary>The creator turned "Allow late joining" off: nobody joins once the raid started.</summary>
            public bool NoLateJoin;
            /// <summary>Sessions that left this match after it started (died, extracted, quit). They cannot join it
            /// again: the game server still has their player (body, items), so a second copy desyncs everyone.</summary>
            public HashSet<string> Departed = new();
            public struct UserStruct
            {
                public string sessionId;
                /// <summary>Group the player queued with, or null.</summary>
                public string group;
            }
        }

        static MatchmakingConfig Cfg => ConfigController.Configs.Server?.Matchmaking ?? new MatchmakingConfig();

        static string DedicatedIp()
        {
            var cfg = Cfg;
            if (!string.IsNullOrEmpty(cfg.MatchIp)) return cfg.MatchIp;
            var servers = ConfigController.Configs.Server?.Servers;
            return servers != null && servers.Count > 0 ? servers[0].Ip : "127.0.0.1";
        }

        static bool Contains(MatchData m, string sessionId) => m.Users != null && m.Users.Any(u => u.sessionId == sessionId);

        /// <summary>
        /// The match this session is in. IsNew = true means it is in none; the returned
        /// MatchData is then a blank one that is NOT registered.
        /// </summary>
        public static (MatchData matchData, bool IsNew) GetMatch(string ProfileId)
        {
            lock (Lock)
            {
                Update();
                foreach (var item in Matches.Values)
                {
                    if (Contains(item, ProfileId))
                        return (item, false);
                }
                return (new MatchData() { CreatorId = ProfileId }, true);
            }
        }

        public static List<MatchData> Snapshot()
        {
            lock (Lock)
            {
                Update();
                return Matches.Values.ToList();
            }
        }

        public static void ClearAll()
        {
            lock (Lock) { Matches.Clear(); }
            GameServerLauncher.StopAll();
        }

        public static MatchData? FindById(string MatchId)
        {
            lock (Lock)
            {
                return Matches.TryGetValue(MatchId, out var m) ? m : null;
            }
        }

        /// <summary>Starts matches whose wait is over and removes timed-out ones. Called on every status poll.</summary>
        static void Update()
        {
            var cfg = Cfg;
            long now = TimeHelper.UnixTimeNow_Int();
            foreach (var m in Matches.Values.ToList())
            {
                if (now - m.CreatedAt > cfg.MatchTimeoutMinutes * 60L)
                {
                    Matches.Remove(m.MatchId);
                    GameServerLauncher.Stop(m.MatchId);
                    Debug.PrintDebug($"Match [{m.MatchId}] timed out and was removed.", "MatchController");
                    continue;
                }
                if (m.ServerLaunched && !m.ServerReady && now - m.CreatedAt > cfg.GameServerStartTimeoutSeconds)
                {
                    SetProblem(m, $"The game server for this raid did not start within {cfg.GameServerStartTimeoutSeconds} s. " +
                        $"The server's log is logs/gameserver_{m.MatchId}.log on the server PC.");
                    Matches.Remove(m.MatchId);
                    GameServerLauncher.Stop(m.MatchId);
                    Debug.PrintError($"Game server for match [{m.MatchId}] did not report ready within {cfg.GameServerStartTimeoutSeconds} s; match cancelled.", "MatchController");
                    continue;
                }
                if (m.IsStarted) continue;
                int delay = m.StartDelaySeconds > 0 ? m.StartDelaySeconds : cfg.StartDelaySeconds;
                bool waited = now - m.CreatedAt >= delay || m.Users.Count >= cfg.MaxPlayers;
                bool enough = m.Users.Count >= Math.Max(1, cfg.MinPlayers);
                // A server the backend launched must report ready first (it needs a few seconds to start).
                bool ready = m.ServerLaunched ? m.ServerReady : (!cfg.RequireServerReady || m.ServerReady);
                if (waited && enough && ready)
                    Start(m);
            }
        }

        static void Start(MatchData m)
        {
            m.IsStarted = true;
            m.StartedAt = TimeHelper.UnixTimeNow_Int();
            Debug.PrintInfo($"Match [{m.MatchId}] started on {m.Location}: {m.Users.Count} player(s) -> {m.Ip}:{m.Port} ({m.HostType})", "MatchController");
        }

        /// <summary>Removes the session from its match; the match is deleted once empty or when its host leaves.</summary>
        public static void Exit(string ProfileId)
        {
            lock (Lock)
            {
                foreach (var match in Matches.Values.ToList())
                {
                    if (!Contains(match, ProfileId)) continue;
                    match.Users.RemoveAll(u => u.sessionId == ProfileId);
                    if (match.IsStarted) match.Departed.Add(ProfileId);
                    if (match.Users.Count == 0 || match.HostId == ProfileId)
                    {
                        Matches.Remove(match.MatchId);
                        GameServerLauncher.Stop(match.MatchId);
                        Debug.PrintDebug($"Match [{match.MatchId}] deleted ({(match.HostId == ProfileId ? "host left" : "empty")}).", "MatchController");
                    }
                    else
                    {
                        Debug.PrintDebug($"{ProfileId} left match [{match.MatchId}] ({match.Users.Count} left).", "MatchController");
                    }
                }
            }
        }

        /// <summary>Ends a match from the game server or console. Frees its port.</summary>
        public static bool EndMatch(string MatchId)
        {
            lock (Lock)
            {
                if (!Matches.Remove(MatchId)) return false;
                GameServerLauncher.Stop(MatchId);
                Debug.PrintInfo($"Match [{MatchId}] ended.", "MatchController");
                return true;
            }
        }

        /// <summary>Starts a match now, skipping the wait (console command).</summary>
        public static bool ForceStart(string MatchId)
        {
            lock (Lock)
            {
                if (!Matches.TryGetValue(MatchId, out var m)) return false;
                if (!m.IsStarted) Start(m);
                return true;
            }
        }

        /// <summary>The game server for this match is listening.</summary>
        public static bool SetServerReady(string MatchId)
        {
            lock (Lock)
            {
                if (!Matches.TryGetValue(MatchId, out var m)) return false;
                m.ServerReady = true;
                Debug.PrintDebug($"Game server for match [{MatchId}] is ready.", "MatchController");
                return true;
            }
        }

        /// <summary>Kept for older callers.</summary>
        public static void SetStarted(string MatchId) => ForceStart(MatchId);

        public static void SetHostPort(string SessionId, int port)
        {
            lock (Lock)
            {
                if (port <= 0) HostPorts.Remove(SessionId);
                else HostPorts[SessionId] = port;
            }
        }

        /// <summary>Address others connect to when this session hosts (null/empty = the address the backend sees).</summary>
        public static void SetHostIp(string SessionId, string? ip)
        {
            lock (Lock)
            {
                if (string.IsNullOrWhiteSpace(ip)) HostIps.Remove(SessionId);
                else HostIps[SessionId] = ip.Trim();
            }
        }

        /// <summary>"Allow late joining": off = this player never joins a raid in progress, and his own matches take
        /// nobody once started.</summary>
        public static void SetLateJoin(string SessionId, bool allow)
        {
            lock (Lock)
            {
                if (allow) NoLateJoin.Remove(SessionId);
                else NoLateJoin.Add(SessionId);
            }
        }

        /// <summary>Group the session queues with (null/empty = none).</summary>
        public static void SetGroup(string SessionId, string? group)
        {
            lock (Lock)
            {
                if (string.IsNullOrWhiteSpace(group)) Groups.Remove(SessionId);
                else Groups[SessionId] = group.Trim();
            }
        }

        /// <summary>A started match still taking players (matchmaking.lateJoin*): fewer than maxPlayers * lateJoinPlayerFraction, recently started, server up.</summary>
        static bool LateJoinable(MatchData m, MatchmakingConfig cfg, long now) =>
            m.IsStarted && !m.NoLateJoin && (!m.ServerLaunched || m.ServerReady) && cfg.LateJoinMinutes > 0
            && now - m.StartedAt < cfg.LateJoinMinutes * 60L && m.Users.Count < cfg.MaxPlayers * cfg.LateJoinPlayerFraction;

        /// <summary>Wait for players the session picked in the game (0 or less = use startDelaySeconds). Returns the value kept.</summary>
        public static int SetWaitSeconds(string SessionId, int seconds)
        {
            lock (Lock)
            {
                if (seconds <= 0)
                {
                    WaitSeconds.Remove(SessionId);
                    return 0;
                }
                seconds = Math.Min(seconds, Math.Max(1, Cfg.MaxStartDelaySeconds));
                WaitSeconds[SessionId] = seconds;
                return seconds;
            }
        }

        static int? FreeDedicatedPort()
        {
            var cfg = Cfg;
            var used = Matches.Values.Where(m => m.HostType == EHostType.Dedicated || m.ServerLaunched).Select(m => m.Port).ToHashSet();
            for (int p = cfg.PortStart; p <= cfg.PortEnd; p++)
            {
                if (used.Contains(p)) continue;
                // a port another program (or a game server left running) holds would make the new server quit at once
                if (GameServerLauncher.Enabled && NetAddress.UdpPortProblem(p) != null) continue;
                return p;
            }
            return null;
        }

        /// <summary>
        /// Why the last match of a session ended before the raid (game server did not start, exited, ...). The game asks
        /// for it (/client/match/problem) when matching fails, and shows it. Cleared when read and on the next queue.
        /// </summary>
        public static Dictionary<string, string> Problems = new();

        static void SetProblem(MatchData m, string text)
        {
            foreach (var u in m.Users) Problems[u.sessionId] = text;
        }

        public static string TakeProblem(string SessionId)
        {
            lock (Lock)
            {
                if (!Problems.TryGetValue(SessionId, out var text)) return "";
                Problems.Remove(SessionId);
                return text;
            }
        }

        /// <summary>A launched game server exited before it reported ready: the match is cancelled with this reason.</summary>
        public static void ServerFailed(string MatchId, string text)
        {
            lock (Lock)
            {
                if (!Matches.TryGetValue(MatchId, out var m) || m.ServerReady) return;
                SetProblem(m, text);
            }
        }

        /// <summary>
        /// Puts the session into an open match with the same location and mode, or creates one.
        /// remoteIp is the address the player reached the backend from (used when they host).
        /// Returns null with an error when no match could be made.
        /// </summary>
        public static MatchData? JoinMatch(string ProfileId, Requests.MatchJoin joinMatch, string? remoteIp, out string error)
        {
            error = "";
            Exit(ProfileId); // a player can only queue for one match
            var cfg = Cfg;
            lock (Lock)
            {
                Problems.Remove(ProfileId);
                Update();
                long now = TimeHelper.UnixTimeNow_Int();
                string? group = Groups.TryGetValue(ProfileId, out var g) ? g : null;
                bool joinsLate = !NoLateJoin.Contains(ProfileId);
                bool SameRaid(MatchData m) => m.Location == joinMatch.location && m.Mode == joinMatch.mode && m.Users.Count < cfg.MaxPlayers
                    && !m.Departed.Contains(ProfileId);
                MatchData? Join(MatchData m, string how)
                {
                    m.Users.Add(new() { sessionId = ProfileId, group = group });
                    Debug.PrintDebug($"{ProfileId} joined match [{m.MatchId}] {how} ({m.Users.Count} players)", "MatchController");
                    return m;
                }
                // 1. a match a group mate is in (open, or started and still taking players)
                if (group != null)
                    foreach (var m in Matches.Values)
                        if (SameRaid(m) && m.Users.Any(u => u.group == group) && (!m.IsStarted || (joinsLate && LateJoinable(m, cfg, now))))
                            return Join(m, "with group " + group);
                // 2. a match that has not started yet
                foreach (var m in Matches.Values)
                    if (!m.IsStarted && SameRaid(m))
                        return Join(m, "");
                // 3. a started match that is still short of players
                if (joinsLate)
                    foreach (var m in Matches.Values)
                        if (SameRaid(m) && LateJoinable(m, cfg, now))
                            return Join(m, "late, raid in progress");

                var match = new MatchData()
                {
                    MatchId = Utils.CreateNewID(),
                    CreatorId = ProfileId,
                    Users = new() { new() { sessionId = ProfileId, group = group } },
                    Location = joinMatch.location,
                    Mode = joinMatch.mode,
                    CreatedAt = TimeHelper.UnixTimeNow_Int(),
                    StartDelaySeconds = WaitSeconds.TryGetValue(ProfileId, out int wait) ? wait : cfg.StartDelaySeconds,
                    NoLateJoin = !joinsLate
                };

                string mode = (cfg.Mode ?? "auto").ToLowerInvariant();
                bool hasHostPort = HostPorts.TryGetValue(ProfileId, out int hostPort);
                bool playerHosts = mode == "host" || (mode == "auto" && hasHostPort);

                if (playerHosts)
                {
                    match.HostType = EHostType.Player;
                    match.HostId = ProfileId;
                    match.Port = hasHostPort ? hostPort : cfg.HostDefaultPort;
                    match.Ip = HostIps.TryGetValue(ProfileId, out var givenIp) ? givenIp : HostAddress(remoteIp);
                }
                else
                {
                    var port = FreeDedicatedPort();
                    if (port == null)
                    {
                        error = $"No free raid port on the server: UDP {cfg.PortStart}-{cfg.PortEnd} are all in use (running raids or other programs). Try again later.";
                        Debug.PrintWarn(error, "MatchController");
                        return null;
                    }
                    match.HostType = EHostType.Dedicated;
                    match.Port = port.Value;
                    match.Ip = DedicatedIp();
                }

                // The game client cannot host a raid in-process yet. A host on the backend's own machine gets
                // the backend's game server on the port they chose (e.g. one their tunnel/router forwards).
                bool launchForHost = playerHosts && GameServerLauncher.Enabled && IsLocal(remoteIp);
                if (playerHosts && !launchForHost)
                {
                    // Reconstruction fix: such a match used to be created anyway, with nothing listening at its address,
                    // so everyone who joined failed to connect and was thrown out. Refuse it with the reason instead.
                    error = GameServerLauncher.Enabled
                        ? "Hosting only works on the server's own PC: the game cannot host raids itself. Turn off \"Host matches\" in the matching options to play on the server's game servers."
                        : "Hosting is not available: this server has no game server set up (matchmaking.gameServerPath). Turn off \"Host matches\" in the matching options.";
                    Debug.PrintWarn($"{ProfileId} wanted to host on {match.Ip}:{match.Port}: refused. " + error, "MatchController");
                    return null;
                }
                if (launchForHost)
                {
                    string? portProblem = Matches.Values.Any(m => m.ServerLaunched && m.Port == match.Port)
                        ? $"Port {match.Port} is already used by another match on the server PC"
                        : NetAddress.UdpPortProblem(match.Port);
                    if (portProblem != null)
                    {
                        error = portProblem + ". Pick another port in the matching options.";
                        Debug.PrintWarn(error, "MatchController");
                        return null;
                    }
                }

                Matches.Add(match.MatchId, match);
                if (launchForHost)
                {
                    match.ServerLaunched = GameServerLauncher.Launch(match, out string launchError);
                    if (!match.ServerLaunched)
                    {
                        Matches.Remove(match.MatchId);
                        error = "The game server could not be started: " + launchError;
                        return null;
                    }
                }
                if (match.HostType == EHostType.Dedicated && !GameServerLauncher.Enabled)
                {
                    Debug.PrintWarn($"No game server is started for match [{match.MatchId}]: matchmaking.gameServerPath is not set in Files/configs/server.json. " +
                        $"Clients will fail to connect to {match.Ip}:{match.Port} unless you start a game server there yourself.", "MatchController");
                }
                if (match.HostType == EHostType.Dedicated && GameServerLauncher.Enabled)
                {
                    match.ServerLaunched = GameServerLauncher.Launch(match, out string launchError);
                    if (!match.ServerLaunched)
                    {
                        Matches.Remove(match.MatchId);
                        error = "The game server could not be started: " + launchError;
                        return null;
                    }
                }
                Debug.PrintInfo($"Match [{match.MatchId}] created for {ProfileId} on {match.Location} -> {match.Ip}:{match.Port} ({match.HostType}), waits {match.StartDelaySeconds} s for players", "MatchController");
                return match;
            }
        }

        /// <summary>
        /// The address other players should connect to when this player hosts. A host that reached the
        /// backend over loopback is on the backend's machine, so hand out the configured public address.
        /// </summary>
        /// <summary>The player runs the game on the backend's own PC (loopback, or one of this PC's LAN addresses).</summary>
        static bool IsLocal(string? remoteIp) => NetAddress.IsThisMachine(remoteIp);

        static string HostAddress(string? remoteIp)
        {
            if (string.IsNullOrEmpty(remoteIp) || remoteIp == "127.0.0.1" || remoteIp == "::1")
            {
                var cfg = Cfg;
                return !string.IsNullOrEmpty(cfg.HostPublicIp) ? cfg.HostPublicIp : DedicatedIp();
            }
            if (remoteIp.StartsWith("::ffff:")) return remoteIp.Substring(7);
            return remoteIp;
        }

        /// <summary>How a player's game reached the backend (one HTTP request): its address, this PC's address it
        /// connected to, and the Host header it used.</summary>
        public class ClientEndpoint
        {
            public string Remote = "";
            public string Local = "";
            public string Host = "";
        }

        static readonly HashSet<string> Announced = new();

        /// <summary>
        /// Reconstruction fix: every player of a match used to get the same address (matchIp / servers[0].ip). With
        /// that set to the public address, players on the server's LAN went out to the router and back in (hairpin
        /// NAT: slow, or no connection at all on many routers); with it on a LAN address, internet players could not
        /// connect. For a game server on this PC each player now gets the address that fits:
        ///   - same PC (loopback): 127.0.0.1
        ///   - LAN / VPN (private address), or this PC has the public address itself: the address of this PC the
        ///     player's game just reached the backend at
        ///   - internet through NAT (router, most VPS): the configured public address (matchIp / hostPublicIp / the
        ///     host's given IP), or when that is empty, loopback or a LAN address, the host name the player typed to
        ///     reach the backend.
        /// A game server elsewhere (started by hand) keeps the configured address.
        /// </summary>
        public static string AddressFor(MatchData m, ClientEndpoint? ep)
        {
            if (ep == null || !m.ServerLaunched) return m.Ip;
            string remote = NetAddress.Normalize(ep.Remote), local = NetAddress.Normalize(ep.Local);
            if (NetAddress.IsLoopback(remote) || remote == "") return "127.0.0.1";
            if (NetAddress.IsIPv4(local) && !NetAddress.IsLoopback(local))
            {
                if (NetAddress.IsPrivate(remote) || !NetAddress.IsPrivate(local) || remote == local) return local;
            }
            string configured = NetAddress.Normalize(m.Ip);
            if (configured != "" && !NetAddress.IsLoopback(configured) && !NetAddress.IsPrivate(configured)) return configured;
            string host = NetAddress.HostOf(ep.Host);
            if (host != "" && !NetAddress.IsLoopback(host) && !NetAddress.IsPrivate(host)) return host;
            return configured != "" ? configured : host;
        }

        /// <summary>The profile-status entry for this session (what /client/profile/status reports).</summary>
        public static Other.ProfileStatus StatusFor(string SessionId, string ProfileId, ClientEndpoint? endpoint = null)
        {
            var status = new Other.ProfileStatus { profileid = ProfileId, status = "Free", ip = "", port = 0 };
            var (match, isNew) = GetMatch(SessionId);
            if (isNew) return status;
            status.status = match.IsStarted ? "Busy" : "MatchWait";
            status.ip = AddressFor(match, endpoint);
            if (match.IsStarted)
            {
                lock (Announced)
                {
                    if (Announced.Add(match.MatchId + "/" + SessionId))
                        Debug.PrintInfo($"{SessionId} connects to the raid at {status.ip}:{match.Port}" +
                            (endpoint != null ? $" (game reached the backend from {NetAddress.Normalize(endpoint.Remote)} at {NetAddress.Normalize(endpoint.Local)})" : ""), "MatchController");
                }
            }
            status.port = match.Port;
            status.location = match.Location;
            status.gameMode = "deathmatch";
            return status;
        }
    }
}
