using System.Text.RegularExpressions;
using NetCoreServer;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ServerLib.Controllers;
using ServerLib.Handlers;
using ServerLib.Json.Classes;
using ServerLib.Utilities;
using static ServerLib.Web.HTTPServer;

namespace ServerLib.Web
{
    /// <summary>
    /// Character look and voice from the OVERALL screen (Unity project, EFT.Reconstruction.CharacterCustomization).
    /// POST /client/profile/customization {"Head","Body","Feet": bundle paths, "Voice"}; every field optional.
    /// Parts must be character bundles of the build (usec/bear/wild head, body, feet); the first-person hands follow
    /// the body (same mapping as the client). Answer "ok", or err 1 with the reason.
    /// </summary>
    public class ClientCustomization
    {
        const string Parts = "assets/content/characters/character/prefabs/";

        static readonly Regex PartRegex = new(@"^assets/content/characters/character/prefabs/(usec|bear|wild)_(head|body|feet)(_\d+)?\.bundle$", RegexOptions.Compiled);

        static readonly HashSet<string> Voices = new(StringComparer.OrdinalIgnoreCase)
        {
            "Usec_1", "Bear_1", "Scav_1", "Scav_2", "Scav_3", "Scav_4", "Scav_5", "Scav_6"
        };

        static string? HandsFor(string body)
        {
            string name = Path.GetFileNameWithoutExtension(body);
            if (name == "usec_body") return "assets/content/hands/usec/usec_hands_skin.bundle";
            if (name == "bear_body") return "assets/content/hands/bear/bear_hands_skin.bundle";
            if (name.StartsWith("wild_body")) return "assets/content/hands/wild/" + name + "_firsthands.bundle";
            return null;
        }

        static JObject? ReadBody(HttpRequest request)
        {
            try
            {
                return JObject.Parse(ResponseControl.DeCompressReq(request.BodyBytes));
            }
            catch
            {
                try { return JObject.Parse(request.Body); } catch { return null; }
            }
        }

        static bool Answer(HttpsBackendSession session, string? error)
        {
            string rsp = error == null
                ? ResponseControl.GetBody(JsonConvert.SerializeObject("ok"))
                : ResponseControl.GetBody("null", 1, JsonConvert.SerializeObject(error));
            return Utils.SendUnityResponse(session, rsp);
        }

        static string? Part(JObject body, string name, string kind, out string? error)
        {
            error = null;
            string? value = body[name]?.Type == JTokenType.String ? body[name]!.ToString() : null;
            if (string.IsNullOrEmpty(value)) return null;
            var match = PartRegex.Match(value);
            if (!match.Success || match.Groups[2].Value != kind)
                error = $"Not a {kind} bundle: {value}";
            return value;
        }

        [HTTP("POST", "/client/profile/customization")]
        public static bool Customize(HttpRequest request, HttpsBackendSession session)
        {
            string sessionId = Utils.GetSessionId(session.Headers);
            var character = CharacterController.GetCharacter(sessionId);
            if (character == null) return Answer(session, "No character for this session.");
            var body = ReadBody(request);
            if (body == null) return Answer(session, "Bad request.");
            string? head = Part(body, "Head", "head", out string? e1);
            string? top = Part(body, "Body", "body", out string? e2);
            string? feet = Part(body, "Feet", "feet", out string? e3);
            string? error = e1 ?? e2 ?? e3;
            string? voice = body["Voice"]?.Type == JTokenType.String ? body["Voice"]!.ToString() : null;
            if (error == null && !string.IsNullOrEmpty(voice) && !Voices.Contains(voice))
                error = "Unknown voice: " + voice;
            if (error != null)
            {
                Debug.PrintWarn(error, "CUSTOMIZATION");
                return Answer(session, error);
            }
            character.Customization ??= new Character.Customization();
            if (head != null) character.Customization.Head = new Character.Body { Path = head };
            if (top != null)
            {
                character.Customization.Body = new Character.Body { Path = top, Rcid = "" };
                string? hands = HandsFor(top);
                if (hands != null) character.Customization.Hands = new Character.Body { Path = hands };
            }
            if (feet != null) character.Customization.Feet = new Character.Body { Path = feet };
            if (!string.IsNullOrEmpty(voice) && character.Info != null) character.Info.Voice = voice;
            SaveHandler.SaveCharacter(sessionId, character);
            Debug.PrintInfo($"{character.Info?.Nickname}: head {Path.GetFileName(character.Customization.Head?.Path)}, body {Path.GetFileName(character.Customization.Body?.Path)}, feet {Path.GetFileName(character.Customization.Feet?.Path)}, voice {character.Info?.Voice}", "CUSTOMIZATION");
            return Answer(session, null);
        }
    }
}
