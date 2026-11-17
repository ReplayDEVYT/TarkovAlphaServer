using NetCoreServer;
using Newtonsoft.Json;
using ServerLib.Controllers;
using ServerLib.Utilities;
using System.Net;
using static ServerLib.Web.HTTPServer;

namespace ServerLib.Web
{
    /// <summary>
    /// Matchmaking endpoints that the stock 2016 client never calls: for game-server processes,
    /// for players who host, and for debugging. See MatchController.
    /// </summary>
    public class ServerMatch
    {
        public static string? RemoteIp(HttpsBackendSession session)
        {
            try
            {
                return (session.Socket?.RemoteEndPoint as IPEndPoint)?.Address.ToString();
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }

        /// <summary>How this request reached the backend (for MatchController.AddressFor).</summary>
        public static MatchController.ClientEndpoint Endpoint(HttpsBackendSession session)
        {
            var ep = new MatchController.ClientEndpoint { Remote = RemoteIp(session) ?? "", Host = session.HostHeader };
            try
            {
                ep.Local = (session.Socket?.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "";
            }
            catch (ObjectDisposedException) { }
            return ep;
        }

        /// <summary>
        /// Why the player's last match ended before the raid ("" = nothing to tell): game server did not start in time,
        /// exited, ... The game asks after matching failed and shows it (EFT.Reconstruction.RaidConnectionNotice).
        /// </summary>
        [HTTP("POST", "/client/match/problem")]
        public static bool MatchProblem(HttpRequest request, HttpsBackendSession session)
        {
            var sessionId = Utils.GetSessionId(session.Headers);
            string text = MatchController.TakeProblem(sessionId);
            Utils.SendUnityResponse(session, ResponseControl.GetBody(JsonConvert.SerializeObject(text)));
            return true;
        }

        static bool IsLoopback(string? ip) => ip == "127.0.0.1" || ip == "::1" || ip == "::ffff:127.0.0.1";

        /// <summary>
        /// Only the backend's own machine or the machine the match was handed out to may control a match.
        /// </summary>
        static bool MayControl(HttpsBackendSession session, MatchController.MatchData? match)
        {
            var ip = RemoteIp(session);
            if (IsLoopback(ip)) return true;
            if (match == null || ip == null) return false;
            if (ip.StartsWith("::ffff:")) ip = ip.Substring(7);
            return ip == match.Ip;
        }

        static void SendJson(HttpsBackendSession session, object value, int code = 200)
        {
            session.SendResponse(Utils.BuildResponse(session.Response, code, "application/json", JsonConvert.SerializeObject(value)));
        }

        static object Describe(MatchController.MatchData m) => new
        {
            matchId = m.MatchId,
            location = m.Location,
            mode = m.Mode,
            ip = m.Ip,
            port = m.Port,
            hostType = m.HostType.ToString(),
            hostId = m.HostId,
            started = m.IsStarted,
            serverReady = m.ServerReady,
            createdAt = m.CreatedAt,
            startedAt = m.StartedAt,
            raidSeconds = Math.Max(0, (ConfigController.Configs.Server?.Matchmaking ?? new ServerLib.Json.MatchmakingConfig()).RaidMinutes) * 60,
            reconnectSeconds = (ConfigController.Configs.Server?.Matchmaking ?? new ServerLib.Json.MatchmakingConfig()).ReconnectSeconds,
            groupSpawnRadius = (ConfigController.Configs.Server?.Matchmaking ?? new ServerLib.Json.MatchmakingConfig()).GroupSpawnRadius,
            players = m.Users.Select(u => new
            {
                sessionId = u.sessionId,
                group = u.group,
                profileId = CharacterController.GetCharacter(u.sessionId)?.Id,
                nickname = CharacterController.GetCharacter(u.sessionId)?.Info?.Nickname
            })
        };

        /// <summary>All matches. Loopback only.</summary>
        [HTTP("GET", "/server/match/list")]
        public static bool List(HttpRequest request, HttpsBackendSession session)
        {
            if (!IsLoopback(RemoteIp(session))) { SendJson(session, new { error = "forbidden" }, 403); return true; }
            SendJson(session, MatchController.Snapshot().Select(Describe).ToList());
            return true;
        }

        /// <summary>A game server asks what it is hosting: location, mode, the players to expect.</summary>
        [HTTP("GET", "/server/match/info/{matchId}")]
        public static bool Info(HttpRequest request, HttpsBackendSession session)
        {
            var match = MatchController.FindById(session.HttpParam["matchId"]);
            if (!MayControl(session, match)) { SendJson(session, new { error = "forbidden" }, 403); return true; }
            if (match == null) { SendJson(session, new { error = "no such match" }, 404); return true; }
            SendJson(session, Describe(match));
            return true;
        }

        /// <summary>A game server reports it is listening on the match's port (needed when matchmaking.requireServerReady is on).</summary>
        [HTTP("GET", "/server/match/ready/{matchId}")]
        public static bool Ready(HttpRequest request, HttpsBackendSession session)
        {
            var id = session.HttpParam["matchId"];
            var match = MatchController.FindById(id);
            if (!MayControl(session, match)) { SendJson(session, new { error = "forbidden" }, 403); return true; }
            SendJson(session, new { ok = MatchController.SetServerReady(id) });
            return true;
        }

        /// <summary>A game server reports the raid is over; the match is removed and its port freed.</summary>
        [HTTP("GET", "/server/match/end/{matchId}")]
        public static bool End(HttpRequest request, HttpsBackendSession session)
        {
            var id = session.HttpParam["matchId"];
            var match = MatchController.FindById(id);
            if (!MayControl(session, match)) { SendJson(session, new { error = "forbidden" }, 403); return true; }
            SendJson(session, new { ok = MatchController.EndMatch(id) });
            return true;
        }

        /// <summary>
        /// A game server reports that a player left or disconnected, so the backend no longer reports them
        /// as "Busy" in that match (which would block their next queue). The match ends once empty.
        /// </summary>
        [HTTP("GET", "/server/match/leave/{profileId}")]
        public static bool Leave(HttpRequest request, HttpsBackendSession session)
        {
            var profileId = session.HttpParam["profileId"];
            Json.Classes.Character.Base? found = null;
            foreach (var c in CharacterController.Characters.Values)
                if (c != null && c.Id == profileId) { found = c; break; }
            MatchController.MatchData? match = null;
            if (found != null)
            {
                var (m, isNew) = MatchController.GetMatch(found.Aid);
                if (!isNew) match = m;
            }
            if (!MayControl(session, match)) { SendJson(session, new { error = "forbidden" }, 403); return true; }
            if (found != null && match != null) MatchController.Exit(found.Aid);
            SendJson(session, new { ok = match != null });
            return true;
        }

        /// <summary>
        /// A game server fetches the profile of a player who joined it (the client only sends its profile id).
        /// Plain JSON in the same format as /client/game/profile/list. Allowed from loopback or from the
        /// address of the match that player is in.
        /// </summary>
        [HTTP("GET", "/server/profile/{profileId}")]
        public static bool Profile(HttpRequest request, HttpsBackendSession session)
        {
            var profileId = session.HttpParam["profileId"];
            Json.Classes.Character.Base? found = null;
            foreach (var c in CharacterController.Characters.Values)
                if (c != null && c.Id == profileId) { found = c; break; }
            MatchController.MatchData? match = null;
            if (found != null)
            {
                var (m, isNew) = MatchController.GetMatch(found.Aid);
                if (!isNew) match = m;
            }
            if (!MayControl(session, match)) { SendJson(session, new { error = "forbidden" }, 403); return true; }
            if (found == null) { SendJson(session, new { error = "no such profile" }, 404); return true; }
            var rsp = session.Response.MakeGetResponse(Json.Classes.Converters.ToJson(found), "application/json");
            session.SendResponse(rsp);
            return true;
        }

        /// <summary>
        /// Asset bundles a client needs before it can show this player: the prefab of every item the
        /// player carries plus their head/body/feet/hands. JSON string[]; the game server puts the union
        /// for all players of the match into its join answer (147). Same access rule as /server/profile.
        /// </summary>
        [HTTP("GET", "/server/profile/{profileId}/bundles")]
        public static bool ProfileBundles(HttpRequest request, HttpsBackendSession session)
        {
            var profileId = session.HttpParam["profileId"];
            Json.Classes.Character.Base? found = null;
            foreach (var c in CharacterController.Characters.Values)
                if (c != null && c.Id == profileId) { found = c; break; }
            MatchController.MatchData? match = null;
            if (found != null)
            {
                var (m, isNew) = MatchController.GetMatch(found.Aid);
                if (!isNew) match = m;
            }
            if (!MayControl(session, match)) { SendJson(session, new { error = "forbidden" }, 403); return true; }
            if (found == null) { SendJson(session, new { error = "no such profile" }, 404); return true; }

            var bundles = new HashSet<string>();
            var items = DatabaseController.DataBase.Items;
            if (found.Inventory?.Items != null && items != null)
                foreach (var item in found.Inventory.Items)
                    if (item?.Tpl != null && items.TryGetValue(item.Tpl, out var template))
                    {
                        var path = template?.Props?.Prefab?.Path;
                        if (!string.IsNullOrEmpty(path)) bundles.Add(path);
                    }
            var look = found.Customization;
            foreach (var part in new[] { look?.Head, look?.Body, look?.Feet, look?.Hands })
                if (!string.IsNullOrEmpty(part?.Path)) bundles.Add(part.Path);
            SendJson(session, bundles.ToArray());
            return true;
        }

        /// <summary>
        /// Current health of a player's body parts for the game server's damage model: plain text, seven
        /// comma-separated numbers in the client's EBodyPart order (Head, Chest, Stomach, LeftArm, RightArm,
        /// LeftLeg, RightLeg). Same access rule as /server/profile.
        /// </summary>
        [HTTP("GET", "/server/profile/{profileId}/health")]
        public static bool ProfileHealth(HttpRequest request, HttpsBackendSession session)
        {
            var profileId = session.HttpParam["profileId"];
            Json.Classes.Character.Base? found = null;
            foreach (var c in CharacterController.Characters.Values)
                if (c != null && c.Id == profileId) { found = c; break; }
            MatchController.MatchData? match = null;
            if (found != null)
            {
                var (m, isNew) = MatchController.GetMatch(found.Aid);
                if (!isNew) match = m;
            }
            if (!MayControl(session, match)) { SendJson(session, new { error = "forbidden" }, 403); return true; }
            if (found == null) { SendJson(session, new { error = "no such profile" }, 404); return true; }
            var parts = found.Health?.BodyPartsHealth;
            var values = new[] { parts?.Head, parts?.Chest, parts?.Stomach, parts?.LeftArm, parts?.RightArm, parts?.LeftLeg, parts?.RightLeg }
                .Select(p => (p?.CurrAndMaxValue?.CurrentValue ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
            session.SendResponse(session.Response.MakeGetResponse(string.Join(",", values), "text/plain"));
            return true;
        }

        /// <summary>
        /// Bullet damage of every item template that has one, for the game server's damage model: plain text,
        /// one "templateId damage" per line. Loopback only (the backend starts its game servers locally).
        /// </summary>
        [HTTP("GET", "/server/ammo")]
        public static bool Ammo(HttpRequest request, HttpsBackendSession session)
        {
            if (!IsLoopback(RemoteIp(session))) { SendJson(session, new { error = "forbidden" }, 403); return true; }
            var sb = new System.Text.StringBuilder();
            var items = DatabaseController.DataBase.Items;
            if (items != null)
                foreach (var pair in items)
                {
                    var damage = pair.Value?.Props?.Damage;
                    if (damage != null && damage > 0) sb.Append(pair.Key).Append(' ').Append(damage.Value).Append('\n');
                }
            session.SendResponse(session.Response.MakeGetResponse(sb.ToString(), "text/plain"));
            return true;
        }

        /// <summary>
        /// Loot data of one location for the game server's loot generator (EFT/Reconstruction/RaidLoot.cs):
        /// Files/loot/{locationId}.json = spawn rules (editable), the scene's loot containers and loose-loot
        /// points (written by Tools/extract_loot_points.py). 404 when the location has none. Loopback only.
        /// </summary>
        [HTTP("GET", "/server/loot/{locationId}")]
        public static bool Loot(HttpRequest request, HttpsBackendSession session)
        {
            if (!IsLoopback(RemoteIp(session))) { SendJson(session, new { error = "forbidden" }, 403); return true; }
            string id = session.HttpParam["locationId"];
            string path = Path.Combine("Files", "loot", id + ".json");
            if (id.Contains('/') || id.Contains('\\') || id.Contains("..") || !File.Exists(path))
            {
                SendJson(session, new { error = "no loot data" }, 404);
                return true;
            }
            session.SendResponse(session.Response.MakeGetResponse(File.ReadAllText(path), "application/json"));
            return true;
        }

        /// <summary>
        /// Loot data for an offline raid, which the game generates itself (EFT/Reconstruction/RaidLoot.cs,
        /// smethod_OfflineLoot): {"location": Files/loot/{locationId}.json, "tables": Files/loot/tables.json}.
        /// Plain JSON (not zlib), read by the game with its own web request. Open to any address: the same data
        /// decides loot online, where the game server reads it through /server/loot/.
        /// </summary>
        [HTTP("GET", "/client/raid/loot/{locationId}")]
        public static bool ClientRaidLoot(HttpRequest request, HttpsBackendSession session)
        {
            string id = session.HttpParam["locationId"];
            string path = Path.Combine("Files", "loot", id + ".json");
            if (id.Contains('/') || id.Contains('\\') || id.Contains("..") || id == "tables" || !File.Exists(path))
            {
                SendJson(session, new { error = "no loot data" }, 404);
                return true;
            }
            string tablesPath = Path.Combine("Files", "loot", "tables.json");
            string tables = File.Exists(tablesPath) ? File.ReadAllText(tablesPath) : "null";
            string body = "{\"location\":" + File.ReadAllText(path) + ",\"tables\":" + tables + "}";
            session.SendResponse(session.Response.MakeGetResponse(body, "application/json"));
            return true;
        }

        /// <summary>
        /// A logged-in player sets how long the matches they create wait for other players, in seconds
        /// (0 = the server's startDelaySeconds; capped at maxStartDelaySeconds). Sent by the game right
        /// before each queue (EFT.Reconstruction.MatchOptions).
        /// </summary>
        [HTTP("POST", "/client/match/wait/{seconds}")]
        public static bool MatchWait(HttpRequest request, HttpsBackendSession session)
        {
            Utils.PrintRequest(request, session);
            var sessionId = Utils.GetSessionId(session.Headers);
            if (!int.TryParse(session.HttpParam["seconds"], out int seconds) || seconds < 0)
            {
                Utils.SendUnityResponse(session, ResponseControl.GetBody("null", 400, "Invalid wait"));
                return true;
            }
            int kept = MatchController.SetWaitSeconds(sessionId, seconds);
            Debug.PrintInfo(kept == 0 ? $"{sessionId}: matches wait the server default" : $"{sessionId}: new matches wait {kept} s for players", "MatchController");
            Utils.SendUnityResponse(session, ResponseControl.GetBody("true"));
            return true;
        }

        /// <summary>
        /// A logged-in player sets the group they queue with ("-" = none): players with the same group name join the
        /// same match and spawn next to each other. Sent by the game right before each queue (EFT.Reconstruction.MatchOptions).
        /// </summary>
        [HTTP("POST", "/client/match/group/{name}")]
        public static bool MatchGroup(HttpRequest request, HttpsBackendSession session)
        {
            Utils.PrintRequest(request, session);
            var sessionId = Utils.GetSessionId(session.Headers);
            string name = Uri.UnescapeDataString(session.HttpParam["name"] ?? "");
            if (name == "-" || name.Length > 32) name = "";
            MatchController.SetGroup(sessionId, name);
            Debug.PrintInfo(name == "" ? $"{sessionId}: queues alone" : $"{sessionId}: queues with group '{name}'", "MatchController");
            Utils.SendUnityResponse(session, ResponseControl.GetBody("true"));
            return true;
        }

        /// <summary>
        /// A logged-in player gives the address others connect to when he hosts ("-" = the address the backend sees,
        /// or matchmaking.hostPublicIp for a host on the backend's machine). Sent before each queue
        /// (EFT.Reconstruction.MatchOptions).
        /// </summary>
        [HTTP("POST", "/client/match/host/ip/{ip}")]
        public static bool HostIp(HttpRequest request, HttpsBackendSession session)
        {
            Utils.PrintRequest(request, session);
            var sessionId = Utils.GetSessionId(session.Headers);
            string ip = Uri.UnescapeDataString(session.HttpParam["ip"] ?? "").Trim();
            if (ip == "-" || ip.Length > 253 || ip.Any(char.IsWhiteSpace)) ip = "";
            MatchController.SetHostIp(sessionId, ip);
            if (ip != "") Debug.PrintInfo($"{sessionId}: others connect to {ip} when this player hosts", "MatchController");
            Utils.SendUnityResponse(session, ResponseControl.GetBody("true"));
            return true;
        }

        /// <summary>
        /// "Allow late joining" (1/0): with 0 the player never joins a raid in progress and his own matches take nobody
        /// once started. Sent before each queue (EFT.Reconstruction.MatchOptions).
        /// </summary>
        [HTTP("POST", "/client/match/latejoin/{allow}")]
        public static bool LateJoin(HttpRequest request, HttpsBackendSession session)
        {
            Utils.PrintRequest(request, session);
            var sessionId = Utils.GetSessionId(session.Headers);
            bool allow = (session.HttpParam["allow"] ?? "1").Trim() != "0";
            MatchController.SetLateJoin(sessionId, allow);
            if (!allow) Debug.PrintInfo($"{sessionId}: no late joining", "MatchController");
            Utils.SendUnityResponse(session, ResponseControl.GetBody("true"));
            return true;
        }

        /// <summary>
        /// A logged-in player registers the port they host matches on (0 = stop hosting).
        /// With matchmaking.mode "auto", their next match is hosted by them on this port.
        /// </summary>
        [HTTP("POST", "/client/match/host/port/{port}")]
        public static bool HostPort(HttpRequest request, HttpsBackendSession session)
        {
            Utils.PrintRequest(request, session);
            var sessionId = Utils.GetSessionId(session.Headers);
            if (!int.TryParse(session.HttpParam["port"], out int port) || port < 0 || port > 65535)
            {
                Utils.SendUnityResponse(session, ResponseControl.GetBody("null", 400, "Invalid port"));
                return true;
            }
            MatchController.SetHostPort(sessionId, port);
            Debug.PrintInfo(port == 0 ? $"{sessionId} stopped hosting" : $"{sessionId} hosts matches on port {port}", "MatchController");
            Utils.SendUnityResponse(session, ResponseControl.GetBody("true"));
            return true;
        }
    }
}
