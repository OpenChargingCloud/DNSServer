/*
 * Copyright (c) 2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of DNSServer <https://github.com/OpenChargingCloud/DNSServer>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using IPAddress = System.Net.IPAddress;
using IPEndPoint = System.Net.IPEndPoint;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;

using cloud.charging.open.DNSServer.CommandLine;
using cloud.charging.open.DNSServer.SecureShell;

// Inside cloud.charging.open.DNSServer the bare name DNSServer is this
// namespace, so Hermod's server goes by another one.
using HermodDNSServer = org.GraphDefined.Vanaheimr.Hermod.DNS.DNSServer;

#endregion

namespace cloud.charging.open.DNSServer
{

    /// <summary>
    /// One name server for a local network: a WWCP node that answers DNS over
    /// UDP and TCP - and over SSH, to the machines it lets in there - with the
    /// records it was given in its web interface, at its command line, over
    /// SSH, or in the "dnsServer" section of its configuration file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is this kind's own is the name server: Hermod's DNS server, asked
    /// with an authoritative request handler that looks up in the records (see
    /// <see cref="ResourceRecords"/>), which are written back to the
    /// configuration file whenever they change.
    /// </para>
    /// <para>
    /// DNS over SSH is the subsystem "dns@open.charging.cloud" of the node's
    /// SSH server, which carries DNS over TCP unchanged: a charging station
    /// that runs OCPP over SSH resolves its names over the connection it has
    /// anyway, against the host key it pins for OCPP. It is served to the DNS
    /// clients of the section - machines that sign in with their own keys and
    /// get nothing else - and to every account allowed to run "dnsquery" (see
    /// <see cref="DNSClientSSHSignIns"/>).
    /// </para>
    /// <para>
    /// What every program of this family has - the sign-in, the name servers
    /// it asks itself, the time servers, the certificate store, the SSH server
    /// with its command line, and the log - is the node below.
    /// </para>
    /// </remarks>
    public partial class DNSServerNode : WWCPNode
    {

        #region Data

        /// <summary>
        /// The TCP port the web interface listens on, unless another is given.
        /// </summary>
        /// <remarks>
        /// Beside the PKI's 2360 and the NTS server's 2361, and clear of
        /// everything else of the family, so that it can run on the same bench
        /// as everything it names. Its SSH server is then on 22362.
        /// </remarks>
        public static new readonly  IPPort    DefaultHTTPPort   = IPPort.Parse(2362);

        /// <summary>
        /// What kind of node a DNS server is.
        /// </summary>
        /// <remarks>
        /// "DNSServer" as the organization its accounts are in - which a first
        /// start writes into the accounts file and every start after it reads
        /// back, so it is the one that must never change.
        /// </remarks>
        public static readonly      NodeKind  DNSServerKind     = new (
                                                                      Name:           "DNS server",
                                                                      Tag:            "dnsserver",
                                                                      Product:        "DNSServer",
                                                                      Organization:   "DNSServer",
                                                                      LogFilePrefix:  "dnsserver"
                                                                  );

        /// <summary>
        /// The kinds of certificate the node's store keeps: the four of TLS -
        /// the roots and certificates the name and time servers this node asks
        /// itself are held to.
        /// </summary>
        public static readonly      IReadOnlyList<CertificateKind>  CertificateKinds  = [ .. CertificateKindExtensions.TLS ];

        /// <summary>
        /// The port DNS over UDP listens on, as a start that cannot have it says.
        /// </summary>
        public static readonly      NodePort  DNSUDPPort        = new ("DNS over UDP");

        /// <summary>
        /// The port DNS over TCP listens on, as a start that cannot have it says.
        /// </summary>
        public static readonly      NodePort  DNSTCPPort        = new ("DNS over TCP");

        /// <summary>
        /// The manifest resource prefix of the embedded web interface (see the
        /// EmbedFrontend target of DNSServer.csproj).
        /// </summary>
        public const                String    HTTPRoot          = "cloud.charging.open.DNSServer.HTTPRoot.";

        /// <summary>
        /// What the log and the SSH page call DNS over SSH.
        /// </summary>
        public const                String    DNSOverSSHLabel   = "DNS";

        private Int64 requestsReceived;
        private Int64 responsesSent;

        private Boolean dnsOverSSHServed;

        private readonly ConcurrentDictionary<String, DNSClientActivity> sshActivity = new (StringComparer.Ordinal);

        #endregion

        #region Properties

        /// <summary>
        /// What the "dnsServer" section and the switches of this start said.
        /// </summary>
        public DNSServerSettings  Settings     { get; private set; }

        /// <summary>
        /// The records answered with.
        /// </summary>
        public ResourceRecords    Records      { get; }

        /// <summary>
        /// The JSON API.
        /// </summary>
        public DNSServerHTTPAPI   API          { get; }

        /// <summary>
        /// Hermod's DNS server, which answers.
        /// </summary>
        public HermodDNSServer    Server       { get; }

        /// <summary>
        /// The machines that may ask over SSH.
        /// </summary>
        public DNSClientSSHSignIns  SSHClients  { get; }

        /// <summary>
        /// Whether DNS is answered over SSH now: switched on in the section, and
        /// the node's SSH server running.
        /// </summary>
        public Boolean            DNSOverSSH
            => dnsOverSSHServed && SSHEnabled;

        /// <summary>
        /// What each login asked over SSH since the start, by its name: the DNS
        /// clients, and the accounts allowed to run "dnsquery".
        /// </summary>
        public IReadOnlyDictionary<String, DNSClientActivity>  SSHActivity
            => sshActivity;

        /// <summary>
        /// Where DNS over UDP listens now, or null while it does not.
        /// </summary>
        public IPSocket?          UDPSocket
            => Server.ActiveUDPUnicastSocket;

        /// <summary>
        /// Where DNS over TCP listens now, or null while it does not.
        /// </summary>
        public IPSocket?          TCPSocket
            => Server.ActiveTCPUnicastSocket;

        /// <summary>
        /// Whether DNS is answered now.
        /// </summary>
        public Boolean            IsServing
            => Server.IsRunning;

        /// <summary>
        /// How many DNS requests arrived since the start.
        /// </summary>
        public Int64              RequestsReceived
            => Interlocked.Read(ref requestsReceived);

        /// <summary>
        /// How many DNS responses were sent since the start.
        /// </summary>
        public Int64              ResponsesSent
            => Interlocked.Read(ref responsesSent);

        #endregion

        #region Constructor(s)

        /// <summary>
        /// One DNS server, with its web interface and its command line over SSH.
        /// </summary>
        /// <param name="HTTPHostname">The address the web interface listens on; 127.0.0.1 by default.</param>
        /// <param name="HTTPPort">The TCP port it listens on; <see cref="DefaultHTTPPort"/> by default.</param>
        /// <param name="HTTPServer">An HTTP server to register within, or null to make one.</param>
        /// <param name="BasePath">What everything of this node sits below; the root by default.</param>
        /// <param name="HTTPRootPath">Where the JSON API sits; "/api" below <paramref name="BasePath"/> by default.</param>
        /// <param name="ExtAPI">An HTTPExt API to sign in against, or null for one of this node's own.</param>
        /// <param name="AccountsPath">The directory the accounts live in between starts.</param>
        /// <param name="ConfigFile">Where the configuration - and with it the records - lives between starts.</param>
        /// <param name="DNSClient">How this node resolves names itself, or null to make a client.</param>
        /// <param name="NTSClient">Where the node reads the time for its own check, or null to make a client.</param>
        /// <param name="Frontend">Where the web interface comes from, or null for the embedded bundle.</param>
        /// <param name="CertificatesPath">The directory the node's certificate store lives in between starts.</param>
        /// <param name="Log">Where everything that happens is written, or null to make a log.</param>
        /// <param name="LogToConsole">Whether the log is also written to the console.</param>
        /// <param name="ConsoleLogLevel">How much of it reaches the console.</param>
        /// <param name="LogPath">The directory the log files are written to, or null to write none.</param>
        /// <param name="BridgeDebugLog">Whether what the libraries below write with DebugX is picked up.</param>
        /// <param name="SSH">What the program says about the SSH server.</param>
        /// <param name="Web">What the program says of HTTPS and of the reverse proxies in front.</param>
        /// <param name="CommandLine">What the switches of this start say over the "dnsServer" section, or null for nothing.</param>
        public DNSServerNode(org.GraphDefined.Vanaheimr.Hermod.IPAddress? HTTPHostname       = null,
                             IPPort?                                        HTTPPort           = null,
                             HTTPServer?                                    HTTPServer         = null,
                             HTTPPath?                                      BasePath           = null,
                             HTTPPath?                                      HTTPRootPath       = null,
                             HTTPExtAPI?                                    ExtAPI             = null,
                             String?                                        AccountsPath       = null,
                             WWCPConfigFile?                                ConfigFile         = null,
                             DNSClient?                                     DNSClient          = null,
                             NTSClient?                                     NTSClient          = null,
                             IStaticContentSource?                          Frontend           = null,
                             String?                                        CertificatesPath   = null,
                             EventLog?                                      Log                = null,
                             Boolean                                        LogToConsole       = true,
                             LogLevel                                       ConsoleLogLevel    = LogLevel.Info,
                             String?                                        LogPath            = null,
                             Boolean                                        BridgeDebugLog     = true,
                             SSHSettings?                                   SSH                = null,
                             WebConfiguration?                              Web                = null,
                             Func<DNSServerSettings, DNSServerSettings>?    CommandLine        = null)

            : base(Kind:                  DNSServerKind,
                   Version:               typeof(DNSServerNode).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                   HTTPPort:              HTTPPort ?? DefaultHTTPPort,
                   HTTPHostname:          HTTPHostname,
                   HTTPServer:            HTTPServer,
                   BasePath:              BasePath,
                   HTTPRootPath:          HTTPRootPath,
                   ExtAPI:                ExtAPI,
                   AccountsPath:          AccountsPath,
                   Resources:             DNSServerAccess.Resources,
                   RoleDefinitions:       DNSServerAccess.Roles,
                   ConfigFile:            ConfigFile,
                   DNSClient:             DNSClient,
                   NTSClient:             NTSClient,
                   Frontend:              Frontend ?? new EmbeddedContentSource(HTTPRoot, typeof(DNSServerNode).Assembly),
                   CertificatesPath:      CertificatesPath,
                   CertificateKinds:      CertificateKinds,
                   Log:                   Log,
                   LogToConsole:          LogToConsole,
                   ConsoleLogLevel:       ConsoleLogLevel,
                   LogPath:               LogPath,
                   BridgeDebugLog:        BridgeDebugLog,
                   SSH:                   SSH,
                   Web:                   Web)

        {

            #region The "dnsServer" section, and what the switches say over it

            if (!DNSServerSettings.TryParse(ConfigurationDocument[DNSServerSettings.SectionName], out var settings, out var settingsError))
                throw new InvalidOperationException($"'{this.ConfigFile.Path}': {settingsError} Repair or remove '{this.ConfigFile.Path}' and start again.");

            this.Settings = CommandLine is not null
                                ? CommandLine(settings)
                                : settings;

            #endregion

            #region The records

            if (!ResourceRecords.TryParse(Settings.Records, Settings.DefaultTTL, out var records, out var recordsError))
                throw new InvalidOperationException($"'{this.ConfigFile.Path}': \"records\" of the \"{DNSServerSettings.SectionName}\" section: {recordsError} " +
                                                    $"Repair or remove the record and start again.");

            this.Records = new ResourceRecords(Settings.DefaultTTL, WriteRecords);

            if (!this.Records.TryLoad(records, Settings.Zones, out var zonesError))
                throw new InvalidOperationException($"'{this.ConfigFile.Path}': \"zones\" of the \"{DNSServerSettings.SectionName}\" section: {zonesError}");

            this.Log.Info($"The name server answers with {records.Count} record(s) of the \"{DNSServerSettings.SectionName}\" section" +
                          (Settings.Zones.Count > 0
                               ? $", and is authoritative for {Settings.Zones.Count} zone(s): {String.Join(", ", Settings.Zones.Select(zone => zone.Name.FullName))}"
                               : "."),
                          "dnsserver");

            #endregion

            #region The name server

            this.Server = new HermodDNSServer(
                              new AuthoritativeDNSRequestHandler(Records),
                              new DNSServerOptions {
                                  EnableUDPUnicast    = Settings.UDP,
                                  EnableTCPUnicast    = Settings.TCP,
                                  EnableUDPMulticast  = false,
                                  UDPUnicastSocket    = new IPSocket(Settings.Listen, Settings.Port),
                                  TCPUnicastSocket    = new IPSocket(Settings.Listen, Settings.Port)
                              }
                          );

            this.Server.OnDNSRequestReceived += (timestamp, server, serverType, request, cancellationToken) => {

                Interlocked.Increment(ref requestsReceived);

                // Over SSH the server knows the client by its login.
                if (serverType == HermodDNSServer.SSHServerType && request.ClientIdentity is String login)
                    sshActivity.AddOrUpdate(login,
                                            _         => DNSClientActivity.None.With(timestamp, request),
                                            (_, seen) => seen.With(timestamp, request));

                return Task.CompletedTask;

            };

            this.Server.OnDNSResponseSent    += (timestamp, server, serverType, response, cancellationToken) => {
                Interlocked.Increment(ref responsesSent);
                return Task.CompletedTask;
            };

            #endregion

            #region The JSON API

            this.API = new DNSServerHTTPAPI(
                           HTTPServer:  this.HTTPServer,
                           Node:        this,
                           ExtAPI:      this.ExtAPI,
                           Log:         this.Log,
                           APIPath:     this.HTTPRootPath,
                           Version:     Version
                       );

            #endregion

            #region DNS over SSH: the subsystem, and the machines let in for it

            this.SSHClients = new DNSClientSSHSignIns(
                                  Settings.SSHClients,
                                  HermodDNSServer.SSHSubsystemName,
                                  () => DNSOverSSH
                              );

            this.SSHClients.OnNotice += (level, message) => this.Log.Log(level, message, "dnsserver", "ssh", "auth");

            AddSSHSignIns(SSHClients);

            if (Settings.SSHEnabled)
                ServeDNSOverSSH();

            #endregion

            #region What somebody signed in over SSH gets

            // The DNS server's command line, with its commands beside the
            // node's - whichever program runs this DNS server, and not only
            // DNSServerCLI. Before the node starts, as the node asks.
            CommandLines = (terminal, caller) => new DNSServerCLI(this, terminal, caller);

            #endregion

        }

        #endregion


        #region (private) ServeDNSOverSSH() / StopServingDNSOverSSH()

        /// <summary>
        /// DNS on the node's SSH server, answered by the name server that
        /// answers over UDP and TCP - with the same records and zones.
        /// </summary>
        private void ServeDNSOverSSH()
        {

            if (dnsOverSSHServed)
                return;

            AddSSHSubsystem(new SSHSubsystem(
                                HermodDNSServer.SSHSubsystemName,
                                DNSOverSSHLabel,
                                Server.ServeSSHSubsystemAsync,
                                Permission.Run(DNSServerAccess.Query)
                            ));

            dnsOverSSHServed = true;

        }

        private void StopServingDNSOverSSH()
        {

            if (!dnsOverSSHServed)
                return;

            dnsOverSSHServed = false;

            RemoveSSHSubsystem(HermodDNSServer.SSHSubsystemName);

        }

        #endregion

        #region TrySetSSHClients(Clients, out Error)

        /// <summary>
        /// The machines that may ask over SSH from now on - kept in the "ssh"
        /// of the "dnsServer" section.
        /// </summary>
        public Boolean TrySetSSHClients(IEnumerable<DNSClientEntry>       Clients,
                                        [NotNullWhen(false)] out String?  Error)
        {

            var clients = Clients.ToArray();

            var updated = Settings with { SSHClients = clients };

            if (!ConfigFile.TryMergeSection(DNSServerSettings.SectionName,
                                            new JObject(new JProperty("ssh", updated.SSHToJSON())),
                                            out Error))
                return false;

            Settings = updated;
            SSHClients.Set(clients);

            // Whoever is no longer let in is ended now rather than at the next check.
            RecheckSSHSessions();

            return true;

        }

        #endregion

        #region TrySetSSHEnabled(Enabled, out Error)

        /// <summary>
        /// Answer DNS over SSH from now on, or not - kept in the "ssh" of the
        /// "dnsServer" section.
        /// </summary>
        public Boolean TrySetSSHEnabled(Boolean                           Enabled,
                                        [NotNullWhen(false)] out String?  Error)
        {

            var updated = Settings with { SSHEnabled = Enabled };

            if (!ConfigFile.TryMergeSection(DNSServerSettings.SectionName,
                                            new JObject(new JProperty("ssh", updated.SSHToJSON())),
                                            out Error))
                return false;

            Settings = updated;

            if (Enabled)
                ServeDNSOverSSH();
            else
                StopServingDNSOverSSH();

            Log.Notice($"DNS over SSH is switched {(Enabled ? "on" : "off")}.", "dnsserver", "ssh");

            return true;

        }

        #endregion

        #region (private) WriteRecords(Lines, Zones)

        /// <summary>
        /// The records and zones into the "dnsServer" section, and nothing else
        /// of it.
        /// </summary>
        private String? WriteRecords(IReadOnlyList<String>  Lines,
                                     IReadOnlyList<Zone>    Zones)
        {

            if (!ConfigFile.TryMergeSection(DNSServerSettings.SectionName,
                                            new JObject(new JProperty("records", new JArray(Lines)),
                                                        new JProperty("zones",   new JArray(Zones.Select(zone => zone.ToJSON())))),
                                            out var error))
                return error;

            Settings = Settings with { Records = Lines, Zones = Zones };

            return null;

        }

        #endregion

        #region ProbePorts()

        /// <summary>
        /// Whether the ports of DNS can be had now: bound and let go of at once,
        /// so that a start that cannot have them ends there and says why.
        /// </summary>
        /// <exception cref="PortUnavailableException">Where one cannot be had.</exception>
        public void ProbePorts()
        {

            if (Settings.Port == IPPort.Zero)
                return;

            foreach (var address in ListenAddresses())
            {

                if (Settings.UDP)
                    Probe(address, SocketType.Dgram,  ProtocolType.Udp, Settings.Port, DNSUDPPort);

                if (Settings.TCP)
                    Probe(address, SocketType.Stream, ProtocolType.Tcp, Settings.Port, DNSTCPPort);

            }

        }

        private static void Probe(IPAddress     Address,
                                  SocketType    SocketType,
                                  ProtocolType  ProtocolType,
                                  IPPort        Port,
                                  NodePort      Whose)
        {

            using var socket = new Socket(Address.AddressFamily, SocketType, ProtocolType);

            // As Hermod binds: one socket per family, so that an IPv4 client is
            // not handed over as ::ffff:a.b.c.d.
            if (Address.AddressFamily == AddressFamily.InterNetworkV6)
                socket.DualMode = false;

            try
            {

                socket.Bind(new IPEndPoint(Address, Port.ToUInt16()));

                if (SocketType == SocketType.Stream)
                    socket.Listen(1);

            }
            catch (SocketException problem)
            {
                throw new PortUnavailableException(Port, problem, Whose);
            }

        }

        /// <summary>
        /// The addresses DNS is bound to: both wildcards for "any", the one
        /// address otherwise.
        /// </summary>
        private IEnumerable<IPAddress> ListenAddresses()

            => Settings.Listen is IPvXAddress
                   ? [ IPAddress.IPv6Any, IPAddress.Any ]
                   : [ Settings.Listen.ToDotNet() ];

        #endregion


        #region (protected override) OnListening()

        /// <summary>
        /// DNS answered, before the node calls itself started.
        /// </summary>
        protected override async Task OnListening()
        {

            ProbePorts();

            await Server.Start();

            // Hermod binds before its listeners first wait, and they are not
            // waited for: a bind that failed after the probe is only seen in a
            // socket that is not there.
            if (Settings.UDP && Server.ActiveUDPUnicastSocket is null)
                throw new PortUnavailableException(Settings.Port, new SocketException((Int32) SocketError.AddressAlreadyInUse), DNSUDPPort);

            if (Settings.TCP && Server.ActiveTCPUnicastSocket is null)
                throw new PortUnavailableException(Settings.Port, new SocketException((Int32) SocketError.AddressAlreadyInUse), DNSTCPPort);

        }

        #endregion

        #region (protected override) OnStarted()

        protected override Task OnStarted()
        {

            Log.Info($"The JSON API of the name server is at {APIURL}v1/dnsServer", "web", "http");

            Log.Notice($"DNS is answered {ListenersText()}, with {Records.Count} record(s)" +
                       (Records.Zones.Count > 0
                            ? $" and {Records.Zones.Count} zone(s)."
                            : "."),
                       "dnsserver");

            if (Settings.SSHEnabled)
            {
                if (SSHEnabled)
                {

                    Log.Notice($"DNS is answered over SSH as well: {SSHURL}, subsystem {HermodDNSServer.SSHSubsystemName}, host key {SSHHostKey ?? "not known yet"} " +
                               $"({SSHClients.Clients.Count(client => client.Enabled && client.Keys.Count > 0)} DNS client(s) with a key, and every account allowed to run '{DNSServerAccess.Query}').",
                               "dnsserver", "ssh");

                    // The SSH server listens where the web interface does, and
                    // that is the loopback address unless the start said --any.
                    if (SSHURL is String sshURL &&
                        (sshURL.Contains("127.0.0.1") || sshURL.Contains("[::1]") || sshURL.Contains("localhost")))
                        Log.Notice("DNS over SSH reaches this machine only: the SSH server listens where the web interface does, on the loopback address. " +
                                   "Start with --any to offer it in the network.",
                                   "dnsserver", "ssh");

                }
                else
                    Log.Warning("DNS over SSH is switched on, but the SSH server of this node is off - so nobody can reach it. Switch SSH on in the \"ssh\" section, or leave out --no-ssh.",
                                "dnsserver", "ssh");
            }

            return Task.CompletedTask;

        }

        #endregion

        #region (protected override) OnStopping()

        protected override async Task OnStopping()
        {

            await Server.Stop();

            await base.OnStopping();

        }

        #endregion


        #region ListenersText()

        /// <summary>
        /// Where DNS listens, as the banner and the command line say it.
        /// </summary>
        public String ListenersText()
        {

            var listen    = DNSServerSettings.TextOf(Settings.Listen);
            var listeners = new List<String>();

            if (Settings.UDP)
                listeners.Add($"udp://{listen}:{UDPSocket?.Port ?? Settings.Port}");

            if (Settings.TCP)
                listeners.Add($"tcp://{listen}:{TCPSocket?.Port ?? Settings.Port}");

            return String.Join(" and ", listeners);

        }

        #endregion

        #region StatusJSON()

        /// <summary>
        /// How the name server stands.
        /// </summary>
        public JObject StatusJSON()

            => new (
                   new JProperty("serving",           IsServing),
                   new JProperty("listen",            DNSServerSettings.TextOf(Settings.Listen)),
                   new JProperty("udp",               UDPSocket?.ToString()),
                   new JProperty("tcp",               TCPSocket?.ToString()),
                   new JProperty("records",           Records.Count),
                   new JProperty("zones",             new JArray(Records.Zones.Select(zone => zone.Name.FullName))),
                   new JProperty("recordTypes",       new JObject(Records.All.
                                                                      GroupBy (record => ADNSResourceRecord.TypeName(record.Type)).
                                                                      OrderBy (group  => group.Key, StringComparer.Ordinal).
                                                                      Select  (group  => new JProperty(group.Key, group.Count())))),
                   new JProperty("requestsReceived",  RequestsReceived),
                   new JProperty("responsesSent",     ResponsesSent),
                   new JProperty("ssh",               new JObject(
                       new JProperty("enabled",           Settings.SSHEnabled),
                       new JProperty("served",            DNSOverSSH),
                       new JProperty("subsystem",         HermodDNSServer.SSHSubsystemName),
                       new JProperty("url",               SSHURL),
                       new JProperty("hostKey",           SSHHostKey),
                       new JProperty("clients",           Settings.SSHClients.Count)
                   ))
               );

        #endregion

        #region SSHJSON()

        /// <summary>
        /// DNS over SSH as the DNS clients page shows it: whether it is switched
        /// on and served, where a client reaches it and what host key it holds
        /// the server to - and the DNS clients, each key with what it is: its
        /// algorithm, its fingerprint, its comment and its options, beside the
        /// line it was given as, which is what is sent back; and each client
        /// with what it asked over SSH since the start, as the accounts that
        /// asked are.
        /// </summary>
        public JObject SSHJSON()

            => new (
                   new JProperty("enabled",    Settings.SSHEnabled),
                   new JProperty("served",     DNSOverSSH),
                   new JProperty("subsystem",  HermodDNSServer.SSHSubsystemName),
                   new JProperty("url",        SSHURL),
                   new JProperty("hostKey",    SSHHostKey),
                   new JProperty("clients",    new JArray(Settings.SSHClients.Select(client => new JObject(
                       new JProperty("name",       client.Name),
                       new JProperty("enabled",    client.Enabled),
                       new JProperty("keys",       new JArray(client.Keys.Select(KeyJSON))),
                       new JProperty("activity",   ActivityOf(client.Name).ToJSON())
                   )))),
                   new JProperty("accounts",   new JArray(AccountsThatAsked().Select(account => new JObject(
                       new JProperty("name",       account.Key),
                       new JProperty("activity",   account.Value.ToJSON())
                   ))))
               );

        /// <summary>
        /// What the login of this name asked over SSH since the start.
        /// </summary>
        public DNSClientActivity ActivityOf(String Name)

            => sshActivity.TryGetValue(Name, out var activity)
                   ? activity
                   : DNSClientActivity.None;

        /// <summary>
        /// The logins that asked over SSH but are no DNS client: accounts of the
        /// node that may run "dnsquery", each with what it asked.
        /// </summary>
        public IEnumerable<KeyValuePair<String, DNSClientActivity>> AccountsThatAsked()

            => sshActivity.
                   Where  (activity => !(User_Id.TryParse(activity.Key) is User_Id client && SSHClients.Knows(client))).
                   OrderBy(activity => activity.Key, StringComparer.OrdinalIgnoreCase);

        private static JObject KeyJSON(String Line)
        {

            // Every line was parsed before it was taken in; one that does not
            // parse now is said as it stands, with nothing made up about it.
            if (!org.GraphDefined.Vanaheimr.Hermod.SSH.AuthorizedKeysFile.TryParseLine(Line, out var key) ||
                key is null)
            {
                return new JObject(new JProperty("line", Line));
            }

            return new JObject(
                       new JProperty("line",           Line),
                       new JProperty("algorithm",      key.PublicKey.Algorithm),
                       new JProperty("fingerprint",    key.PublicKey.Sha256Fingerprint),
                       new JProperty("comment",        key.PublicKey.Comment.Length > 0 ? key.PublicKey.Comment : null),
                       new JProperty("notAfter",       key.NotAfter?.ToString("o")),
                       new JProperty("options",        new JArray(key.Options)),
                       new JProperty("certAuthority",  key.IsCertAuthority),
                       new JProperty("principals",     key.Principals.Count > 0 ? new JArray(key.Principals) : null)
                   );

        }

        #endregion

        #region ConfigurationJSON()

        /// <summary>
        /// What this DNS server is, as the Configuration page reads it: what the
        /// node below says of itself, and on top the name server.
        /// </summary>
        public override JObject ConfigurationJSON()
        {

            var json = base.ConfigurationJSON();

            json.AddFirst(new JProperty("dnsServer", new JObject(
                              new JProperty("version",   Version),
                              new JProperty("createdAt", CreatedAt.ToString("o")),
                              new JProperty("machine",   Environment.MachineName),
                              new JProperty("runtime",   Environment.Version.ToString()),
                              new JProperty("os",        Environment.OSVersion.ToString()),
                              new JProperty("settings",  Settings.ToJSON()),
                              new JProperty("status",    StatusJSON())
                          )));

            return json;

        }

        #endregion

    }

}
