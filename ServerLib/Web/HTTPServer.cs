using NetCoreServer;
using ServerLib.Controllers;
using ServerLib.Handlers;
using ServerLib.Utilities;
using ServerLib.Utilities.Helpers;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace ServerLib.Web
{
    public class HTTPServer
    {
        static Dictionary<string, MethodInfo> HttpServerThingy = new();

        static HttpsBackendServer? server = null;

        /// <summary>The address the backend listens on (server.json "ip", or 0.0.0.0 when that is not this PC's).</summary>
        public static IPAddress BoundAddress { get; private set; } = IPAddress.Loopback;

        /// <summary>How processes on this PC (the launched game servers) reach the backend.</summary>
        public static string LocalUrl
        {
            get
            {
                var a = BoundAddress;
                bool local = IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any);
                return $"http://{(local ? "127.0.0.1" : a.ToString())}:{ConfigController.Configs.Server.Port}";
            }
        }

        public static void Start(string IP, int Port)
        {
            HttpServerThingy.Clear();
            // "ip" is the address to listen on. A public/router address is not on any of this PC's network cards
            // (home router, most VPS providers), and listening on it fails; listen on all addresses instead.
            IPAddress bind;
            if (!IPAddress.TryParse((IP ?? "").Trim(), out bind!))
            {
                Console.WriteLine($"[Network] \"ip\" in Files/configs/server.json is \"{IP}\", not an IP address: listening on all addresses (0.0.0.0).");
                bind = IPAddress.Any;
            }
            else if (!NetAddress.IsThisMachine(bind.ToString()))
            {
                Console.WriteLine($"[Network] \"ip\" {bind} is not an address of this PC (a public or router address?): listening on all addresses (0.0.0.0) instead.");
                bind = IPAddress.Any;
            }
            BoundAddress = bind;
            server = new HttpsBackendServer(bind, Port);
            try
            {
                if (!server.Start())
                    Console.WriteLine($"[Network] The backend could not start listening on {bind}:{Port}.");
                else
                    Console.WriteLine("[HTTP] Server started on http://" + bind + ":" + Port);
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[Network] The backend cannot listen on {bind}:{Port}: {ex.SocketErrorCode}" +
                    (ex.SocketErrorCode == SocketError.AddressAlreadyInUse ? " (another server or program already uses this port)" : ""));
                throw;
            }
            NetworkReport.Print(bind, Port);
            var methods = Assembly.GetExecutingAssembly().GetTypes().SelectMany(x => x.GetMethods()).ToArray();
            var basemethods = methods.Where(x => x.GetCustomAttribute<HTTPAttribute>() != null && x.ReturnType == typeof(bool)).ToArray();
            methods = basemethods.Where(x => x.GetCustomAttribute<HTTPAttribute>().method.Contains("GET")).ToArray();
            foreach (var method in methods)
            {
                if (method == null)
                    continue;
                var url = method.GetCustomAttribute<HTTPAttribute>().url;
                Utilities.Debug.PrintDebug(method.Name + $" ({url}) is added as an URL", "HTTPServer");
                HttpServerThingy.Add(url, method);
            }
            methods = basemethods.Where(x => x.GetCustomAttribute<HTTPAttribute>().method.Contains("POST")).ToArray();
            foreach (var method in methods)
            {
                if (method == null)
                    continue;
                var url = method.GetCustomAttribute<HTTPAttribute>().url;
                Utilities.Debug.PrintDebug(method.Name + $" ({url}) is added as an URL", "HTTPServer");
                HttpServerThingy.Add(url, method);
            }
        }

        public static void Stop()
        {
            server?.Stop();
            server?.Dispose();
            server = null;
            Console.WriteLine("[HTTPS] Server Stopped");

        }

        [HTTP("GET", "/test/{thing}")]
        public static bool TestParam(HttpRequest request, HttpsBackendSession session)
        {
            string resp = "test param: " + session.HttpParam["thing"];
            session.SendResponse(session.Response.MakeGetResponse(resp));
            return true;
        }

        [HTTP("GET", "/test")]
        public static bool Test(HttpRequest request, HttpsBackendSession session)
        {
            string resp = "test";
            session.SendResponse(session.Response.MakeGetResponse(resp));
            return true;
        }

        [HTTP("GET", "/getBundleList")]
        public static bool GetBundeList(HttpRequest request, HttpsBackendSession session)
        {
            Utils.PrintRequest(request, session);
            string resp = "[]"; //Need better handling on bundles
            return Utils.SendUnityResponse(session, resp);
        }

        [HTTP("GET", "/ServerInternalIPAddress")]
        public static bool ServerInternalIPAddress(HttpRequest request, HttpsBackendSession session)
        {
            string resp = ConfigController.Configs.Server.Ip;
            session.SendResponse(session.Response.MakeGetResponse(resp));
            return true;
        }

        [HTTP("GET", "/ServerExternalIPAddress")]
        public static bool ServerExternalIPAddress(HttpRequest request, HttpsBackendSession session)
        {
            string resp = ConfigController.Configs.Server.Ip;
            session.SendResponse(session.Response.MakeGetResponse(resp));
            return true;
        }

        public static HttpsBackendServer? GetServer()
        {
            return server;
        }

        public class HttpsBackendServer : HttpServer
        {
            public HttpsBackendServer(IPAddress address, int port) : base(address, port) { }

            HttpsBackendSession? session;

            public HttpsBackendSession? GetSession()
            {
                return session;
            }

            protected override TcpSession CreateSession()
            {
                session = new HttpsBackendSession(this);

                return session;
            }
            protected override void OnError(SocketError error)
            {
                Console.WriteLine($"HTTPS server caught an error: {error}");
            }

        }

        public class HttpsBackendSession : HttpSession
        {
            public Dictionary<string, string> HttpParam = new();
            public Dictionary<string, string> Headers = new();
            public HttpsBackendSession(HttpServer server) : base(server) { }

            HttpRequest? _request;

            public HttpRequest? LastRequest()
            {
                return _request;
            }

            protected override void OnReceivedRequest(HttpRequest request)
            {
                Headers.Clear();
                for (int i = 0; i < request.Headers; i++)
                {
                    var headerpart = request.Header(i);
                    Headers.Add(headerpart.Item1, headerpart.Item2);
                }
                string url = request.Url;
                url = Uri.UnescapeDataString(url);
                Utilities.Debug.PrintDebug(url);
                ClientServerInfo.Touch(Headers); // players online for /client/server/info
                var ret = PluginLoader.PluginHttpRequest(request, this);
                if (ret.Contains(true))
                    return;

                if (url.Contains("?retry="))
                {
                    var retry = url.Split("?retry=");
                    Utilities.Debug.PrintDebug("Retreid: " + retry[1]);
                    url = retry[0];
                }

                _request = request;
                bool Sent = false;
                ResponseSent = false;
                foreach (var item in HttpServerThingy)
                {
                    if (UrlHelper.Match(url, item.Key, out HttpParam) || item.Key == url)
                    {
                        Utilities.Debug.PrintDebug("Url Called function: " + item.Value.Name);
                        Debug.logger.Log("REQUEST", request.ToString());
                        item.Value.Invoke(this, new object[] { request, this });
                        Sent = true;
                    }

                }

                if (!Sent)
                {
                    File.AppendAllText("REQUESTED.txt", url + "\n" + request.Body + "\n");
                }

                // Reconstruction fix: this empty "200 OK" used to follow every answer. On a kept-alive connection the
                // client then read it as the answer to its next request (an empty reply). Only unanswered requests get it.
                if (!ResponseSent)
                    SendResponse(Response.MakeOkResponse());
            }

            /// <summary>An answer to the current request has been sent (set by SendResponse below).</summary>
            public bool ResponseSent;

            public new long SendResponse(HttpResponse response)
            {
                ResponseSent = true;
                return base.SendResponse(response);
            }

            /// <summary>The Host header the client used ("1.2.3.4:6969"), or "".</summary>
            public string HostHeader
            {
                get
                {
                    foreach (var pair in Headers)
                        if (pair.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) return pair.Value;
                    return "";
                }
            }

            protected override void OnReceivedRequestError(HttpRequest request, string error)
            {
                Console.WriteLine($"Request error: {error}");
            }

            protected override void OnError(SocketError error)
            {
                Console.WriteLine($"HTTPS session caught an error: {error}");
            }
        }
    }
}
