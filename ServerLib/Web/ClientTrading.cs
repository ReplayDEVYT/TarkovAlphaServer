using NetCoreServer;
using ServerLib.Utilities;
using static ServerLib.Web.HTTPServer;

namespace ServerLib.Web
{
    public class ClientTrading
    {
        [HTTP("POST", "/client/trading/api/getTradersList")]
        public static bool ClientTradingApiGetTradersList(HttpRequest request, HttpsBackendSession session)
        {
            Utils.PrintRequest(request, session);
            // with the profile's standing per trader (quest rewards change it; the client copies it over its own)
            string resp = Controllers.QuestController.TraderListFor(Utils.GetSessionId(session.Headers));
            Utils.SendUnityResponse(session, resp);
            return true;
        }

        [HTTP("POST", "/client/trading/api/getTraderAssort/{traderId}")]
        public static bool ClientTradingApiGetTraderAssort(HttpRequest request, HttpsBackendSession session)
        {
            Utils.PrintRequest(request, session);
            string traderId = session.HttpParam["traderId"];
            // per profile: offers above the loyalty level hidden, remaining stock (TradingController.AssortFor)
            string resp = Controllers.TradingController.AssortFor(Utils.GetSessionId(session.Headers), traderId);
            Utils.SendUnityResponse(session, resp);
            return true;
        }

        [HTTP("POST", "/client/trading/api/getTrader/{traderId}")]
        public static bool ClientTradingApiGetTrader(HttpRequest request, HttpsBackendSession session)
        {
            Utils.PrintRequest(request, session);
            string traderId = session.HttpParam["traderId"];
            // standing, restock time and running sale filled in (TradingController.TraderFor)
            string resp = Controllers.TradingController.TraderFor(Utils.GetSessionId(session.Headers), traderId);
            Utils.SendUnityResponse(session, resp);
            return true;
        }

        [HTTP("POST", "/client/trading/api/getUserAssortPrice/trader/{traderId}")]
        public static bool ClientTradingApiGetUserAssortPriceTrader(HttpRequest request, HttpsBackendSession session)
        {
            Utils.PrintRequest(request, session);
            string traderId = session.HttpParam["traderId"];
            var sessionId = Utils.GetSessionId(session.Headers);

            var prices = Controllers.TradingController.GetUserAssortPrice(sessionId);
            string resp = ResponseControl.GetBody(Newtonsoft.Json.JsonConvert.SerializeObject(prices));
            Utils.SendUnityResponse(session, resp);
            return true;
        }
    }
}
