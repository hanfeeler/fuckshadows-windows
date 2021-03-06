﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web;
using System.Windows.Forms;
using Fuckshadows.Model;
using Fuckshadows.Properties;
using Fuckshadows.Util;
using Newtonsoft.Json;
using System.Linq;
using System.Threading.Tasks;
using Fuckshadows.Util.Sockets;

namespace Fuckshadows.Controller
{
    public class FuckshadowsController
    {
        // controller:
        // handle user actions
        // manipulates UI
        // interacts with low level logic
        
        private Listener _listener;
        private PACServer _pacServer;
        private Configuration _config;
        private PrivoxyRunner privoxyRunner;
        private GFWListUpdater gfwListUpdater;

        private long _inboundCounter = 0;
        private long _outboundCounter = 0;
        public long InboundCounter => Interlocked.Read(ref _inboundCounter);
        public long OutboundCounter => Interlocked.Read(ref _outboundCounter);
        public Queue<TrafficPerSecond> trafficPerSecondQueue;
        public const int TrafficPerSecondQueueMaxSize = 61;
        private CancellationTokenSource trafficCancellationTokenSource = null;

        public long _tcpConnCounter = 0;
        public long TCPConnectionCounter => Interlocked.Read(ref _tcpConnCounter);

        private bool stopped = false;

        public class PathEventArgs : EventArgs
        {
            public string Path;
        }

        public class TrafficPerSecond
        {
            public long inboundCounter;
            public long outboundCounter;
            public long inboundIncreasement;
            public long outboundIncreasement;
        }

        public event EventHandler ConfigChanged;
        public event EventHandler EnableStatusChanged;
        public event EventHandler EnableGlobalChanged;
        public event EventHandler ShareOverLanStatusChanged;
        public event EventHandler VerboseLoggingStatusChanged;
        public event EventHandler TrafficChanged;

        // when user clicked Edit PAC, and PAC file has already created
        public event EventHandler<PathEventArgs> PacFileReadyToOpen;
        public event EventHandler<PathEventArgs> UserRuleFileReadyToOpen;

        public event EventHandler<GFWListUpdater.ResultEventArgs> UpdatePacFromGfwListCompleted;

        public event ErrorEventHandler UpdatePacFromGfwListError;

        public event ErrorEventHandler Errored;

        public FuckshadowsController()
        {
            _config = Configuration.Load();
            InitTrafficStatistics();
        }

        public void Start()
        {
            Reload();
        }

        protected void ReportError(Exception e)
        {
            Errored?.Invoke(this, new ErrorEventArgs(e));
        }

        public Server GetCurrentServer()
        {
            return _config.GetCurrentServer();
        }

        // always return copy
        public Configuration GetConfigurationCopy()
        {
            return Configuration.Load();
        }

        // always return current instance
        public Configuration GetCurrentConfiguration()
        {
            return _config;
        }

        public Server GetAServer(IPEndPoint localIPEndPoint, EndPoint destEndPoint)
        {
            if (_config.index < 0)
            {
                _config.index = 0;
            }
            return GetCurrentServer();
        }

        public void SaveServers(List<Server> servers, int localPort)
        {
            _config.configs = servers;
            _config.localPort = localPort;
            Configuration.Save(_config);
        }

        public bool AddServerBySSURL(string ssURL)
        {
            try
            {
                if (ssURL.IsNullOrEmpty() || ssURL.IsWhiteSpace()) return false;
                var servers = Server.GetServers(ssURL);
                if (servers == null || servers.Count == 0) return false;
                foreach (var server in servers) {
                    _config.configs.Add(server);
                }
                _config.index = _config.configs.Count - 1;
                SaveConfig(_config);
                return true;
            }
            catch (Exception e)
            {
                Logging.LogUsefulException(e);
                return false;
            }
        }

        public void ToggleEnable(bool enabled)
        {
            _config.enabled = enabled;
            SaveConfig(_config);
            EnableStatusChanged?.Invoke(this, new EventArgs());
        }

        public void ToggleGlobal(bool global)
        {
            _config.global = global;
            SaveConfig(_config);
            EnableGlobalChanged?.Invoke(this, new EventArgs());
        }

        public void ToggleShareOverLAN(bool enabled)
        {
            _config.shareOverLan = enabled;
            SaveConfig(_config);
            ShareOverLanStatusChanged?.Invoke(this, new EventArgs());
        }

        public void ToggleVerboseLogging(bool enabled)
        {
            _config.isVerboseLogging = enabled;
            SaveConfig(_config);
            VerboseLoggingStatusChanged?.Invoke(this, new EventArgs());
        }

        public void SelectServerIndex(int index)
        {
            _config.index = index;
            SaveConfig(_config);
        }

        public void Stop()
        {
            if (stopped)
            {
                return;
            }
            stopped = true;
            _listener?.Stop();
            privoxyRunner?.Stop();
            if (_config.enabled)
            {
                SystemProxy.Update(_config, true, null);
            }
            SaeaAwaitablePoolManager.Dispose();
            StopTrafficStatistics();
        }

        public void TouchPACFile()
        {
            string pacFilename = _pacServer.TouchPACFile();
            PacFileReadyToOpen?.Invoke(this, new PathEventArgs() {Path = pacFilename});
        }

        public void TouchUserRuleFile()
        {
            string userRuleFilename = _pacServer.TouchUserRuleFile();
            UserRuleFileReadyToOpen?.Invoke(this, new PathEventArgs() {Path = userRuleFilename});
        }

        public string GetQRCodeForCurrentServer()
        {
            Server server = GetCurrentServer();
            return GetQRCode(server);
        }

        public static string GetQRCode(Server server)
        {
            string tag = string.Empty;
            string parts = $"{server.method}:{server.password}@{server.server}:{server.server_port}";
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(parts));
            if (!server.remarks.IsNullOrEmpty())
            {
                tag = $"#{HttpUtility.UrlEncode(server.remarks, Encoding.UTF8)}";
            }
            return $"ss://{base64}{tag}";
        }

        public void UpdatePACFromGFWList()
        {
            if (gfwListUpdater != null)
            {
                Task.Factory.StartNew(async () => { await gfwListUpdater.UpdatePACFromGFWList(_config); },
                    TaskCreationOptions.PreferFairness);
            }
        }

        public void SavePACUrl(string pacUrl)
        {
            _config.pacUrl = pacUrl;
            SaveConfig(_config);
            ConfigChanged?.Invoke(this, new EventArgs());
        }

        public void UseOnlinePAC(bool useOnlinePac)
        {
            _config.useOnlinePac = useOnlinePac;
            SaveConfig(_config);
            ConfigChanged?.Invoke(this, new EventArgs());
        }

        public void ToggleSecureLocalPac(bool enabled)
        {
            _config.secureLocalPac = enabled;
            SaveConfig(_config);
            ConfigChanged?.Invoke(this, new EventArgs());
        }

        public void ToggleCheckingUpdate(bool enabled)
        {
            _config.autoCheckUpdate = enabled;
            Configuration.Save(_config);
            ConfigChanged?.Invoke(this, new EventArgs());
        }

        public void ToggleCheckingPreRelease(bool enabled)
        {
            _config.checkPreRelease = enabled;
            Configuration.Save(_config);
            ConfigChanged?.Invoke(this, new EventArgs());
        }

        public void SaveLogViewerConfig(LogViewerConfig newConfig)
        {
            _config.logViewer = newConfig;
            newConfig.SaveSize();
            Configuration.Save(_config);
            ConfigChanged?.Invoke(this, new EventArgs());
        }

        public void SaveHotkeyConfig(HotkeyConfig newConfig)
        {
            _config.hotkey = newConfig;
            SaveConfig(_config);
            ConfigChanged?.Invoke(this, new EventArgs());
        }

        public void UpdateInboundCounter(Server server, long n)
        {
            Interlocked.Add(ref _inboundCounter, n);
        }

        public void UpdateOutboundCounter(Server server, long n)
        {
            Interlocked.Add(ref _outboundCounter, n);
        }

        public void IncrementTCPConnectionCounter()
        {
            Interlocked.Increment(ref _tcpConnCounter);
        }

        public void DecrementTCPConnectionCounter()
        {
            Interlocked.Decrement(ref _tcpConnCounter);
        }

        protected void Reload()
        {
            // some logic in configuration updated the config when saving, we need to read it again
            _config = Configuration.Load();

            SaeaAwaitablePoolManager.Init();
            ReloadTrafficStatistics();

            if (privoxyRunner == null)
            {
                privoxyRunner = new PrivoxyRunner();
            }
            if (_pacServer == null)
            {
                _pacServer = new PACServer();
                _pacServer.PACFileChanged += pacServer_PACFileChanged;
                _pacServer.UserRuleFileChanged += pacServer_UserRuleFileChanged;
            }
            _pacServer.UpdateConfiguration(_config);
            if (gfwListUpdater == null)
            {
                gfwListUpdater = new GFWListUpdater();
                gfwListUpdater.UpdateCompleted += pacServer_PACUpdateCompleted;
                gfwListUpdater.Error += pacServer_PACUpdateError;
            }

            _listener?.Stop();
            // don't put PrivoxyRunner.Start() before pacServer.Stop()
            // or bind will fail when switching bind address from 0.0.0.0 to 127.0.0.1
            // though UseShellExecute is set to true now
            // http://stackoverflow.com/questions/10235093/socket-doesnt-close-after-application-exits-if-a-launched-process-is-open
            privoxyRunner.Stop();
            try
            {
                privoxyRunner.Start(_config);

                TCPRelay tcpRelay = new TCPRelay(this, _config);
                UDPRelay udpRelay = new UDPRelay(this);
                List<IService> services = new List<IService>
                {
                    tcpRelay,
                    udpRelay,
                    _pacServer,
                    new PortForwarder(privoxyRunner.RunningPort)
                };
                _listener = new Listener(services);
                _listener.Start(_config);
            }
            catch (Exception e)
            {
                // translate Microsoft language into human language
                // i.e. An attempt was made to access a socket in a way forbidden by its access permissions => Port already in use
                if (e is SocketException)
                {
                    SocketException se = (SocketException) e;
                    if (se.SocketErrorCode == SocketError.AccessDenied)
                    {
                        e = new Exception(I18N.GetString("Port already in use"), e);
                    }
                }
                Logging.LogUsefulException(e);
                ReportError(e);
            }

            ConfigChanged?.Invoke(this, new EventArgs());

            UpdateSystemProxy();
        }

        protected void SaveConfig(Configuration newConfig)
        {
            Configuration.Save(newConfig);
            Reload();
        }

        private void UpdateSystemProxy()
        {
            SystemProxy.Update(_config, false, _pacServer);
        }

        private void pacServer_PACFileChanged(object sender, EventArgs e)
        {
            UpdateSystemProxy();
        }

        private void pacServer_PACUpdateCompleted(object sender, GFWListUpdater.ResultEventArgs e)
        {
            UpdatePacFromGfwListCompleted?.Invoke(this, e);
        }

        private void pacServer_PACUpdateError(object sender, ErrorEventArgs e)
        {
            UpdatePacFromGfwListError?.Invoke(this, e);
        }

        private static readonly IEnumerable<char> IgnoredLineBegins = new[] {'!', '['};

        private void pacServer_UserRuleFileChanged(object sender, EventArgs e)
        {
            // TODO: this is a dirty hack. (from code GListUpdater.http_DownloadStringCompleted())
            if (!File.Exists(Utils.GetTempPath("gfwlist.txt")))
            {
                UpdatePACFromGFWList();
                return;
            }
            List<string> lines =
                GFWListUpdater.ParseResult(FileManager.NonExclusiveReadAllText(Utils.GetTempPath("gfwlist.txt")));
            if (File.Exists(PACServer.USER_RULE_FILE))
            {
                string local = FileManager.NonExclusiveReadAllText(PACServer.USER_RULE_FILE, Encoding.UTF8);
                using (var sr = new StringReader(local))
                {
                    foreach (var rule in sr.NonWhiteSpaceLines())
                    {
                        if (rule.BeginWithAny(IgnoredLineBegins))
                            continue;
                        lines.Add(rule);
                    }
                }
            }
            var abpContent = File.Exists(PACServer.USER_ABP_FILE)
                ? FileManager.NonExclusiveReadAllText(PACServer.USER_ABP_FILE, Encoding.UTF8)
                : Utils.UnGzip(Resources.abp_js);

            abpContent = abpContent.Replace("__RULES__", JsonConvert.SerializeObject(lines, Formatting.Indented));
            if (File.Exists(PACServer.PAC_FILE))
            {
                string original = FileManager.NonExclusiveReadAllText(PACServer.PAC_FILE, Encoding.UTF8);
                if (original == abpContent)
                {
                    return;
                }
            }
            File.WriteAllText(PACServer.PAC_FILE, abpContent, Encoding.UTF8);
        }

        public void CopyPacUrl()
        {
            Clipboard.SetDataObject(_pacServer.PacUrl);
        }

        #region Traffic Statistics

        private void InitTrafficStatistics()
        {
            trafficPerSecondQueue = new Queue<TrafficPerSecond>();
            for (int i = 0; i < TrafficPerSecondQueueMaxSize; i++)
            {
                trafficPerSecondQueue.Enqueue(new TrafficPerSecond());
            }
        }

        private void ReloadTrafficStatistics()
        {
            StopTrafficStatistics();
            StartTrafficStatistics();
        }

        private void StartTrafficStatistics()
        {
            trafficCancellationTokenSource = new CancellationTokenSource();
            try
            {
                Task.Factory.StartNew(() => TrafficStatistics(1000, trafficCancellationTokenSource.Token)).ConfigureAwait(false);
            }
            catch (AggregateException ae)
            {
                Logging.Error(ae.InnerExceptions.ToString());
            }
        }

        private void StopTrafficStatistics()
        {
            if (trafficCancellationTokenSource == null) return;
            trafficCancellationTokenSource.Cancel();
            trafficCancellationTokenSource.Dispose();
            trafficCancellationTokenSource = null;
        }

        private async Task TrafficStatistics(int repeatMS, CancellationToken ct)
        {
            try
            {
                TrafficPerSecond previous, current;
                while (true)
                {
                    previous = trafficPerSecondQueue.Last();
                    current = new TrafficPerSecond
                              {
                                  inboundCounter = InboundCounter,
                                  outboundCounter = OutboundCounter
                              };

                    current.inboundIncreasement = current.inboundCounter - previous.inboundCounter;
                    current.outboundIncreasement = current.outboundCounter - previous.outboundCounter;

                    trafficPerSecondQueue.Enqueue(current);
                    if (trafficPerSecondQueue.Count > TrafficPerSecondQueueMaxSize)
                        trafficPerSecondQueue.Dequeue();

                    TrafficChanged?.Invoke(this, new EventArgs());

                    await Task.Delay(repeatMS, ct);
                    if (ct.IsCancellationRequested) return;
                }
            }
            catch (TaskCanceledException)
            {
                // ignore when this task got canceled
            }
        }

        #endregion
    }
}