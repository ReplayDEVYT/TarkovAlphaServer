using NetCoreServer;
using Newtonsoft.Json;
using ServerLib.Controllers;
using ServerLib.Json.Classes;
using ServerLib.Utilities;
using static ServerLib.Controllers.MatchController;
using static ServerLib.Web.HTTPServer;

namespace ServerLib.Web
{
    public class ClientMatch
    {
        [HTTP("POST", "/client/match/available")]
        public static bool Available(HttpRequest request, HttpsBackendSession session)
        {
            Utils.PrintRequest(request, session);
            var rsp = ResponseControl.GetBody("true");
            Utils.SendUnityResponse(session, rsp);
            return true;
        }

        [HTTP("POST", "/client/match/join")]
        public static bool Join(HttpRequest request, HttpsBackendSession session)
        {
            Utils.PrintRequest(request, session);

            var sessionId = Utils.GetSessionId(session.Headers);

            var jsonreq = JsonConvert.DeserializeObject<Requests.MatchJoin>(ResponseControl.DeCompressReq(request.BodyBytes));
            Utilities.Debug.PrintDebug(JsonConvert.SerializeObject(jsonreq));
            var character = CharacterController.GetCharacter(sessionId);
            if (character == null || jsonreq == null)
            {
                Debug.PrintError("Character or join request not found!", "MatchJoin");
                Utils.SendUnityResponse(session, ResponseControl.GetBody("null", 500, "Character not found"));
                return true;
            }
            var match = JoinMatch(sessionId, jsonreq, ServerMatch.RemoteIp(session), out string error);
            if (match == null)
            {
                Utils.SendUnityResponse(session, ResponseControl.GetBody("null", 503, error));
                return true;
            }
            Other.ProfileStatus[] response = { StatusFor(sessionId, character.Id) };
            var rsp = ResponseControl.GetBody(JsonConvert.SerializeObject(response));
            Utils.SendUnityResponse(session, rsp);
            return true;
        }

        [HTTP("POST", "/client/match/exit")]
        public static bool GroupExit(HttpRequest request, HttpsBackendSession session)
        {
            Utils.PrintRequest(request, session);
            var sessionId = Utils.GetSessionId(session.Headers);
            Utilities.Debug.PrintDebug(ResponseControl.DeCompressReq(request.BodyBytes));
            Exit(sessionId);
            var rsp = ResponseControl.NullResponse();
            Utils.SendUnityResponse(session, rsp);
            return true;
        }
    }
}
