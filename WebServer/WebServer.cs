﻿//
// Copyright (c) 2020 Laurent Ellerbach and the project contributors
// See LICENSE file in the project root for full license information.
//

using System;
using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Windows.Storage;
using Windows.Storage.Streams;


namespace nanoFramework.WebServer
{
    public class WebServer : IDisposable
    {
        /// <summary>
        /// URL parameter separation character
        /// </summary>
        public const char ParamSeparator = '&';

        /// <summary>
        /// URL parameter start character
        /// </summary>
        public const char ParamStart = '?';

        /// <summary>
        /// URL parameter equal character
        /// </summary>
        public const char ParamEqual = '=';

        private const int MaxSizeBuffer = 1024;

        #region internal objects

        private bool _cancel = false;
        private Thread _serverThread = null;
        private ArrayList _callbackRoutes;
        private HttpListener _listener;

        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets the port the server listens on.
        /// </summary>
        public int Port { get; protected set; }

        /// <summary>
        /// The type of Http protocol used, http or https
        /// </summary>
        public HttpProtocol Protocol { get; protected set; }

        /// <summary>
        /// The Https certificate to use
        /// </summary>
        public X509Certificate HttpsCert
        {
            get => _listener.HttpsCert;

            set => _listener.HttpsCert = value;
        }

        /// <summary>
        /// SSL protocols
        /// </summary>
        public SslProtocols SslProtocols
        {
            get => _listener.SslProtocols;

            set => _listener.SslProtocols = value;
        }

        /// <summary>
        /// Network credential used for default user:password couple during basic authentication
        /// </summary>
        public NetworkCredential Credential { get; set; }

        /// <summary>
        /// Default APiKey to be used for authentication when no key is specified in the attribute
        /// </summary>
        public string ApiKey { get; set; }

        #endregion

        #region Param

        /// <summary>
        /// Get an array of parameters from a URL
        /// </summary>
        /// <param name="parameter"></param>
        /// <returns></returns>
        public static UrlParameter[] DecodeParam(string parameter)
        {
            UrlParameter[] retParams = null;
            int i = parameter.IndexOf(ParamStart);
            int j = i;
            int k;

            if (i >= 0)
            {
                //look at the number of = and ;

                while ((i < parameter.Length) || (i == -1))
                {
                    j = parameter.IndexOf(ParamEqual, i);
                    if (j > i)
                    {
                        //first param!
                        if (retParams == null)
                        {
                            retParams = new UrlParameter[1];
                            retParams[0] = new UrlParameter();
                        }
                        else
                        {
                            UrlParameter[] rettempParams = new UrlParameter[retParams.Length + 1];
                            retParams.CopyTo(rettempParams, 0);
                            rettempParams[rettempParams.Length - 1] = new UrlParameter();
                            retParams = new UrlParameter[rettempParams.Length];
                            rettempParams.CopyTo(retParams, 0);
                        }
                        k = parameter.IndexOf(ParamSeparator, j);
                        retParams[retParams.Length - 1].Name = parameter.Substring(i + 1, j - i - 1);
                        // Nothing at the end
                        if (k == j)
                        {
                            retParams[retParams.Length - 1].Value = "";
                        }
                        // Normal case
                        else if (k > j)
                        {
                            retParams[retParams.Length - 1].Value = parameter.Substring(j + 1, k - j - 1);
                        }
                        // We're at the end
                        else
                        {
                            retParams[retParams.Length - 1].Value = parameter.Substring(j + 1, parameter.Length - j - 1);
                        }
                        if (k > 0)
                            i = parameter.IndexOf(ParamSeparator, k);
                        else
                            i = parameter.Length;
                    }
                    else
                        i = -1;
                }
            }
            return retParams;
        }

        #endregion

        #region Constructors

        /// <summary>
        /// Instantiates a new webserver.
        /// </summary>
        /// <param name="port">Port number to listen on.</param>
        /// <param name="timeout">Timeout to listen and respond to a request in millisecond.</param>
        public WebServer(int port, HttpProtocol protocol) : this(port, protocol, null)
        { }

        public WebServer(int port, HttpProtocol protocol, Type[] controllers)
        {
            _callbackRoutes = new ArrayList();

            if (controllers != null)
            {
                foreach (var controller in controllers)
                {
                    var controlAttribs = controller.GetCustomAttributes(true);
                    Authentication authentication = null;
                    foreach (var ctrlAttrib in controlAttribs)
                    {
                        if (typeof(AuthenticationAttribute) == ctrlAttrib.GetType())
                        {
                            var strAuth = ((AuthenticationAttribute)ctrlAttrib).AuthenticationMethod;
                            // We do support only None, Basic and ApiKey, raising an exception if this doesn't start by any
                            authentication = ExtractAuthentication(strAuth);
                        }
                    }

                    var functions = controller.GetMethods();
                    foreach (var func in functions)
                    {
                        var attributes = func.GetCustomAttributes(true);
                        CallbackRoutes callbackRoutes = null;
                        foreach (var attrib in attributes)
                        {
                            if (typeof(RouteAttribute) == attrib.GetType())
                            {
                                callbackRoutes = new CallbackRoutes();
                                callbackRoutes.Route = ((RouteAttribute)attrib).Route;
                                callbackRoutes.CaseSensitive = false;
                                callbackRoutes.Method = string.Empty;
                                callbackRoutes.Authentication = authentication;

                                callbackRoutes.Callback = func;
                                foreach (var otherattrib in attributes)
                                {
                                    if (typeof(MethodAttribute) == otherattrib.GetType())
                                    {
                                        callbackRoutes.Method = ((MethodAttribute)otherattrib).Method;
                                    }
                                    else if (typeof(CaseSensitiveAttribute) == otherattrib.GetType())
                                    {
                                        callbackRoutes.CaseSensitive = true;
                                    }
                                    else if (typeof(AuthenticationAttribute) == otherattrib.GetType())
                                    {
                                        var strAuth = ((AuthenticationAttribute)otherattrib).AuthenticationMethod;
                                        // A method can have a different authentication than the main class, so we override if any
                                        callbackRoutes.Authentication = ExtractAuthentication(strAuth);
                                    }
                                }

                                _callbackRoutes.Add(callbackRoutes); ;
                            }
                        }
                    }

                }
            }

            foreach (var callback in _callbackRoutes)
            {
                var cb = (CallbackRoutes)callback;
                Debug.WriteLine($"{cb.Callback.Name}, {cb.Route}, {cb.Method}, {cb.CaseSensitive}");
            }

            Protocol = protocol;
            Port = port;
            string prefix = Protocol == HttpProtocol.Http ? "http" : "https";
            _listener = new HttpListener(prefix, port);
            _serverThread = new Thread(StartListener);
            Debug.WriteLine("Web server started on port " + port.ToString());
        }

        private Authentication ExtractAuthentication(string strAuth)
        {
            const string None = "None";
            const string Basic = "Basic";
            const string ApiKey = "ApiKey";

            Authentication authentication = null;
            if (strAuth.IndexOf(None) == 0)
            {
                if (strAuth.Length == None.Length)
                {
                    authentication = new Authentication();
                }
                else
                {
                    throw new ArgumentException($"Authentication attribute None can only be used alone");
                }
            }
            else if (strAuth.IndexOf(Basic) == 0)
            {
                if (strAuth.Length == Basic.Length)
                {
                    authentication = new Authentication((NetworkCredential)null);
                }
                else
                {
                    var sep = strAuth.IndexOf(':');
                    if (sep == Basic.Length)
                    {
                        var space = strAuth.IndexOf(' ');
                        if (space < 0)
                        {
                            throw new ArgumentException($"Authentication attribute Basic should be 'Basic:user passowrd'");
                        }

                        var user = strAuth.Substring(sep + 1, space - sep - 1);
                        var password = strAuth.Substring(space + 1);
                        authentication = new Authentication(new NetworkCredential(user, password, System.Net.AuthenticationType.Basic));
                    }
                    else
                    {
                        throw new ArgumentException($"Authentication attribute Basic should be 'Basic:user passowrd'");
                    }
                }
            }
            else if (strAuth.IndexOf(ApiKey) == 0)
            {
                if (strAuth.Length == ApiKey.Length)
                {
                    authentication = new Authentication(string.Empty);
                }
                else
                {
                    var sep = strAuth.IndexOf(':');
                    if (sep == ApiKey.Length)
                    {
                        var key = strAuth.Substring(sep + 1);
                        authentication = new Authentication(key);
                    }
                    else
                    {
                        throw new ArgumentException($"Authentication attribute ApiKey should be 'ApiKey:thekey'");
                    }
                }
            }
            else
            {
                throw new ArgumentException($"Authentication attribute can only start with Basic, None or ApiKey and case sensitive");
            }
            return authentication;
        }

        #endregion

        #region Events

        /// <summary>
        /// Delegate for the CommandReceived event.
        /// </summary>
        public delegate void GetRequestHandler(object obj, WebServerEventArgs e);

        /// <summary>
        /// CommandReceived event is triggered when a valid command (plus parameters) is received.
        /// Valid commands are defined in the AllowedCommands property.
        /// </summary>
        public event GetRequestHandler CommandReceived;

        #endregion

        #region Public and private methods

        /// <summary>
        /// Start the multi threaded server.
        /// </summary>
        public bool Start()
        {
            bool bStarted = true;
            // List Ethernet interfaces, so we can determine the server's address
            ListInterfaces();
            // start server           
            try
            {
                _cancel = false;
                _serverThread.Start();
                Debug.WriteLine("Started server in thread " + _serverThread.GetHashCode().ToString());
            }
            catch
            {   //if there is a problem, maybe due to the fact we did not wait enough
                _cancel = true;
                bStarted = false;
            }
            return bStarted;
        }

        /// <summary>
        /// Restart the server.
        /// </summary>
        private bool Restart()
        {
            Stop();
            return Start();
        }

        /// <summary>
        /// Stop the multi threaded server.
        /// </summary>
        public void Stop()
        {
            _cancel = true;
            Thread.Sleep(100);
            _serverThread.Abort();
            Debug.WriteLine("Stoped server in thread ");
        }

        /// <summary>
        /// Output a stream
        /// </summary>
        /// <param name="response">the socket stream</param>
        /// <param name="strResponse">the stream to output</param>
        public static void OutPutStream(HttpListenerResponse response, string strResponse)
        {
            if (response == null)
            {
                return;
            }

            byte[] messageBody = Encoding.UTF8.GetBytes(strResponse);
            response.ContentLength64 = messageBody.Length;
            response.OutputStream.Write(messageBody, 0, messageBody.Length);
        }

        /// <summary>
        /// Output an HTTP Code and close the connection
        /// </summary>
        /// <param name="response">the socket stream</param>
        /// <param name="code">the http code</param>
        public static void OutputHttpCode(HttpListenerResponse response, HttpStatusCode code)
        {
            if (response == null)
            {
                return;
            }

            // This is needed to force the 200 OK without body to go thru
            response.ContentLength64 = 0;
            response.KeepAlive = false;
            response.StatusCode = (int)code;
        }

        /// <summary>
        /// Read the timeout for a request to be send.
        /// </summary>
        public static void SendFileOverHTTP(HttpListenerResponse response, StorageFile strFilePath)
        {
            string ContentType = GetContentTypeFromFileName(strFilePath.FileType);
           
            try
            {
                IBuffer readBuffer = FileIO.ReadBuffer(strFilePath);
                long fileLength = readBuffer.Length;

                response.ContentType = ContentType;
                response.ContentLength64 = fileLength;
                // Now loops sending all the data.

                byte[] buf = new byte[MaxSizeBuffer];
                using (DataReader dataReader = DataReader.FromBuffer(readBuffer))
                {
                    for (long bytesSent = 0; bytesSent < fileLength;)
                    {
                        // Determines amount of data left.
                        long bytesToRead = fileLength - bytesSent;
                        bytesToRead = bytesToRead < MaxSizeBuffer ? bytesToRead : MaxSizeBuffer;
                        // Reads the data.
                        dataReader.ReadBytes(buf);
                        // Writes data to browser
                        response.OutputStream.Write(buf, 0, (int)bytesToRead);
                        // allow some time to physically send the bits. Can be reduce to 10 or even less if not too much other code running in parallel
                        // Updates bytes read.
                        bytesSent += bytesToRead;
                    }
                }
            }
            catch (Exception e)
            {
                throw e;
            }

        }

        private void StartListener()
        {
            _listener.Start();
            while (!_cancel)
            {
                HttpListenerContext context = _listener.GetContext();

                new Thread(() =>
                {
                    bool isRoute = false;
                    CallbackRoutes route;
                    int urlParam;
                    bool isFound;
                    int incForSlash;
                    string toCompare;
                    string routeStr;
                    string rawUrl;

                    foreach (var rt in _callbackRoutes)
                    {
                        route = (CallbackRoutes)rt;
                        urlParam = context.Request.RawUrl.IndexOf(ParamStart);
                        isFound = false;
                        routeStr = route.Route;
                        rawUrl = context.Request.RawUrl;
                        incForSlash = routeStr.IndexOf('/') == 0 ? 0 : 1;
                        toCompare = route.CaseSensitive ? rawUrl : rawUrl.ToLower();
                        if (toCompare.IndexOf(routeStr) == incForSlash)
                        {
                            if (urlParam > 0)
                            {
                                if (urlParam == routeStr.Length + incForSlash)
                                {
                                    isFound = true;
                                }
                            }
                            else
                            {
                                if (toCompare.Length == routeStr.Length + incForSlash)
                                {
                                    isFound = true;
                                }
                            }

                            if (isFound && ((route.Method == string.Empty || (context.Request.HttpMethod == route.Method))))
                            {
                                // Starting a new thread to be able to handle a new request in parallel
                                isRoute = true;

                                // Check auth first
                                bool isAuthOk = false;
                                if (route.Authentication != null)
                                {
                                    if (route.Authentication.AuthenticationType == AuthenticationType.None)
                                    {
                                        isAuthOk = true;
                                    }
                                }
                                else
                                {
                                    isAuthOk = true;
                                }

                                if (!isAuthOk)
                                {
                                    if (route.Authentication.AuthenticationType == AuthenticationType.Basic)
                                    {
                                        var credSite = route.Authentication.Credentials == null ? Credential : route.Authentication.Credentials;
                                        var credReq = context.Request.Credentials;
                                        if (credReq != null)
                                        {
                                            if ((credSite.UserName == credReq.UserName) && (credSite.Password == credSite.Password))
                                            {
                                                isAuthOk = true;
                                            }
                                        }
                                    }
                                    else if (route.Authentication.AuthenticationType == AuthenticationType.ApiKey)
                                    {
                                        var apikeySite = route.Authentication.ApiKey == null ? ApiKey : route.Authentication.ApiKey;
                                        var apikeyReq = GetApiKeyFromHeaders(context.Request.Headers);

                                        if (apikeyReq != null)
                                        {
                                            if (apikeyReq == apikeySite)
                                            {
                                                isAuthOk = true;
                                            }
                                        }
                                    }
                                }

                                if (isAuthOk)
                                {
                                    route.Callback.Invoke(null, new object[] { new WebServerEventArgs(context) });
                                    context.Response.Close();
                                    context.Close();
                                }
                                else
                                {
                                    if (route.Authentication.AuthenticationType == AuthenticationType.Basic)
                                    {
                                        context.Response.Headers.Add("WWW-Authenticate", $"Basic realm=\"Access to {routeStr}\"");
                                    }

                                    context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                                    context.Response.ContentLength64 = 0;
                                    context.Response.Close();
                                    context.Close();
                                }
                            }
                        }
                    }

                    if (!isRoute)
                    {
                        if (CommandReceived != null)
                        {
                            // Starting a new thread to be able to handle a new request in parallel
                            CommandReceived.Invoke(this, new WebServerEventArgs(context));
                            context.Response.Close();
                            context.Close();
                        }
                        else
                        {
                            context.Response.StatusCode = 404;
                            context.Response.ContentLength64 = 0;
                            context.Response.Close();
                            context.Close();
                        }
                    }
                }).Start();

            }
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
        }

        private string GetApiKeyFromHeaders(WebHeaderCollection headers)
        {
            var sec = headers.GetValues("ApiKey");
            if (sec != null)
            {
                if (sec.Length > 0)
                {
                    return sec[0];
                }
            }

            return null;
        }

        /// <summary>
        /// List all IP address, useful for debug only
        /// </summary>
        private void ListInterfaces()
        {
            NetworkInterface[] ifaces = NetworkInterface.GetAllNetworkInterfaces();
            Debug.WriteLine("Number of Interfaces: " + ifaces.Length.ToString());
            foreach (NetworkInterface iface in ifaces)
            {
                Debug.WriteLine("IP:  " + iface.IPv4Address + "/" + iface.IPv4SubnetMask);
            }
        }

        /// Get the MIME-type for a file name.
        /// <summary>
        /// Dispose of any resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Release resources.
        /// </summary>
        /// <param name="disposing">Dispose of resources?</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _serverThread = null;
            }
        }

        #endregion
    }
}