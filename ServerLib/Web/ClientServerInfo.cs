using System.Collections.Concurrent;
using NetCoreServer;
using Newtonsoft.Json;
using ServerLib.Controllers;
using ServerLib.Utilities;
using static ServerLib.Web.HTTPServer;

namespace ServerLib.Web
{
    /// <summary>
    /// What the game's server selection list shows for this backend (Unity project, EFT.Reconstruction.ServerBrowser):
    /// name and version from Files/configs/server.json, players active in the last few minutes (accounts whose
    /// requests carried a session), matches waiting and in progress. No session needed, answered for GET and POST.
    /// Answer: {"err":0,"data":"<json text of Info>"} (data is a string, like the account requests, so the client
    /// reads it without a typed answer class). Not written to ServerResponses/ (the list polls it).
    /// </summary>
    public class ClientServerInfo
    {
        /// <summary>A player counts as online this long after his last request.</summary>
        public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(5);

        static readonly ConcurrentDictionary<string, DateTime> LastSeen = new();

        class Info
        {
            public string name = "";
            public string version = "";
            public int players;
            public int matchesWaiting;
            public int matchesStarted;
            public int activeMinutes;
        }

        /// <summary>Called for every request (HTTPServer.OnReceivedRequest): remembers the session's account.</summary>
        public static void Touch(Dictionary<string, string> headers)
        {
            try
            {
                string id = Utils.GetSessionId(headers);
                if (!string.IsNullOrEmpty(id))
                    LastSeen[id] = DateTime.UtcNow;
            }
            catch
            {
                // a malformed X-WSSE header only means this request is not counted
            }
        }

        public static int ActivePlayers()
        {
            DateTime limit = DateTime.UtcNow - ActiveWindow;
            int count = 0;
            foreach (var pair in LastSeen)
            {
                if (pair.Value >= limit)
                    count++;
                else
                    LastSeen.TryRemove(pair.Key, out _);
            }
            return count;
        }

        static bool Answer(HttpsBackendSession session)
        {
            var info = new Info
            {
                name = ConfigController.Configs.Server?.Name ?? "",
                version = ConfigController.Configs.Server?.Version ?? "",
                players = ActivePlayers(),
                activeMinutes = (int)ActiveWindow.TotalMinutes,
            };
            try
            {
                foreach (var match in MatchController.Snapshot())
                {
                    if (match.IsStarted)
                        info.matchesStarted++;
                    else
                        info.matchesWaiting++;
                }
            }
            catch
            {
                // matchmaking not set up: the counts stay 0
            }
            string rsp = ResponseControl.GetBody(JsonConvert.SerializeObject(JsonConvert.SerializeObject(info)));
            return Utils.SendUnityResponse(session, ResponseControl.CompressRsp(rsp));
        }

        // One route only: HTTPServer keys routes by URL alone and ignores the HTTP method, so this answers GET too.
        [HTTP("POST", "/client/server/info")]
        public static bool InfoPost(HttpRequest request, HttpsBackendSession session)
        {
            return Answer(session);
        }
    }
}
