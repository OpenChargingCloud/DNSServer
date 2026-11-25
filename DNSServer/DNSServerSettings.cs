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

using System.Globalization;
using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;

#endregion

namespace cloud.charging.open.DNSServer
{

    /// <summary>
    /// A machine that may ask the name server over SSH: its login name and the
    /// authorized_keys lines it signs in with.
    /// </summary>
    /// <param name="Name">The login name it signs in with.</param>
    /// <param name="Keys">Its keys, each as an authorized_keys line - options such as from="10.0.0.0/8" in front of it hold as OpenSSH holds them, and with cert-authority it is the key of an SSH CA.</param>
    /// <param name="Enabled">Whether it is let in at all.</param>
    public sealed record DNSClientEntry(String                 Name,
                                        IReadOnlyList<String>  Keys,
                                        Boolean                Enabled = true)
    {

        public JObject ToJSON()

            => new (
                   new JProperty("name",     Name),
                   new JProperty("keys",     new JArray(Keys)),
                   new JProperty("enabled",  Enabled)
               );

    }


    /// <summary>
    /// What the "dnsServer" section of the configuration file says: where the
    /// name server listens, over which transports, and the resource records it
    /// answers with.
    /// </summary>
    /// <remarks>
    /// <code>
    /// "dnsServer": {
    ///     "listen":      "any",
    ///     "port":        53,
    ///     "udp":         true,
    ///     "tcp":         true,
    ///     "defaultTTL":  3600,
    ///     "records":     [ "www.example.org. 3600 IN A 192.0.2.10" ],
    ///     "zones":       [ { "name": "example.org." } ],
    ///     "ssh":         { "enabled": true,
    ///                      "clients": [ { "name": "cs001", "keys": [ "ssh-ed25519 AAAA... cs001" ] } ] }
    /// }
    /// </code>
    /// Every key may be left out, and then reads as its default. The records
    /// are zone-file lines (RFC 1035 §5.1), each with its name complete: that
    /// is the one form every kind of record has, so that a record the command
    /// line cannot make yet can still be written into the file by hand. The
    /// zones are those the name server is authoritative for, with the SOA and
    /// NS records it makes for them (see <see cref="Zone"/>). "ssh" says
    /// whether DNS is answered over SSH as well, as the subsystem
    /// "dns@open.charging.cloud" of the node's SSH server, and which machines
    /// besides the accounts may ask there.
    /// </remarks>
    public sealed record DNSServerSettings
    {

        #region Data

        /// <summary>
        /// The section of the configuration file.
        /// </summary>
        public const            String    SectionName       = "dnsServer";

        /// <summary>
        /// The port DNS listens on, UDP and TCP, unless another is given.
        /// </summary>
        public static readonly  IPPort    DefaultDNSPort    = IPPort.DNS;

        /// <summary>
        /// The TTL of a record made without one.
        /// </summary>
        public static readonly  TimeSpan  DefaultTimeToLive = TimeSpan.FromHours(1);

        #endregion

        #region Properties

        /// <summary>
        /// The address DNS listens on: every address of both families by default.
        /// </summary>
        public IPAddress             Listen        { get; init; } = IPvXAddress.Any;

        /// <summary>
        /// The port DNS listens on, UDP and TCP.
        /// </summary>
        public IPPort                 Port          { get; init; } = DefaultDNSPort;

        /// <summary>
        /// Whether DNS is answered over UDP.
        /// </summary>
        public Boolean                UDP           { get; init; } = true;

        /// <summary>
        /// Whether DNS is answered over TCP.
        /// </summary>
        public Boolean                TCP           { get; init; } = true;

        /// <summary>
        /// The TTL of a record made without one.
        /// </summary>
        public TimeSpan               DefaultTTL    { get; init; } = DefaultTimeToLive;

        /// <summary>
        /// The resource records answered with, as zone-file lines.
        /// </summary>
        public IReadOnlyList<String>  Records       { get; init; } = [];

        /// <summary>
        /// The zones the name server is authoritative for.
        /// </summary>
        public IReadOnlyList<Zone>    Zones         { get; init; } = [];

        /// <summary>
        /// Whether DNS is answered over SSH as well, as the subsystem
        /// "dns@open.charging.cloud" of the node's SSH server.
        /// </summary>
        public Boolean                        SSHEnabled    { get; init; } = true;

        /// <summary>
        /// The machines that may ask over SSH.
        /// </summary>
        public IReadOnlyList<DNSClientEntry>  SSHClients    { get; init; } = [];

        #endregion


        #region (static) TryParse(JSON, out Settings, out Error)

        /// <summary>
        /// Read the "dnsServer" section; a missing section is every default.
        /// </summary>
        public static Boolean TryParse(JToken?                                      JSON,
                                       [NotNullWhen(true)]  out DNSServerSettings?  Settings,
                                       [NotNullWhen(false)] out String?             Error)
        {

            Settings  = null;
            Error     = null;

            var settings = new DNSServerSettings();

            if (JSON is null || JSON.Type == JTokenType.Null)
            {
                Settings = settings;
                return true;
            }

            if (JSON is not JObject json)
            {
                Error = $"The \"{SectionName}\" section is no JSON object.";
                return false;
            }

            #region listen

            if (json["listen"] is JToken listenToken && listenToken.Type != JTokenType.Null)
            {

                if (!TryParseListen(listenToken.Value<String>(), out var listen))
                {
                    Error = $"\"listen\" of the \"{SectionName}\" section is no address to listen on: '{listenToken}'. " +
                             "An IPv4 or IPv6 address, or \"any\" for both families.";
                    return false;
                }

                settings = settings with { Listen = listen };

            }

            #endregion

            #region port

            if (json["port"] is JToken portToken && portToken.Type != JTokenType.Null)
            {

                if (portToken.Type != JTokenType.Integer ||
                    portToken.Value<Int64>() is < 1 or > UInt16.MaxValue)
                {
                    Error = $"\"port\" of the \"{SectionName}\" section is no port number: '{portToken}'.";
                    return false;
                }

                settings = settings with { Port = IPPort.Parse(portToken.Value<UInt16>()) };

            }

            #endregion

            #region udp / tcp

            foreach (var (key, set) in new (String, Func<DNSServerSettings, Boolean, DNSServerSettings>)[] {
                                           ("udp", (s, value) => s with { UDP = value }),
                                           ("tcp", (s, value) => s with { TCP = value })
                                       })
            {

                if (json[key] is not JToken token || token.Type == JTokenType.Null)
                    continue;

                if (token.Type != JTokenType.Boolean)
                {
                    Error = $"\"{key}\" of the \"{SectionName}\" section is neither true nor false: '{token}'.";
                    return false;
                }

                settings = set(settings, token.Value<Boolean>());

            }

            #endregion

            #region defaultTTL

            if (json["defaultTTL"] is JToken ttlToken && ttlToken.Type != JTokenType.Null)
            {

                if (ttlToken.Type != JTokenType.Integer ||
                    ttlToken.Value<Int64>() is < 0 or > Int32.MaxValue)
                {
                    Error = $"\"defaultTTL\" of the \"{SectionName}\" section is no number of seconds: '{ttlToken}'.";
                    return false;
                }

                settings = settings with { DefaultTTL = TimeSpan.FromSeconds(ttlToken.Value<Int64>()) };

            }

            #endregion

            #region records

            if (json["records"] is JToken recordsToken && recordsToken.Type != JTokenType.Null)
            {

                if (recordsToken is not JArray recordsArray ||
                    recordsArray.Any(record => record.Type != JTokenType.String))
                {
                    Error = $"\"records\" of the \"{SectionName}\" section is no list of zone-file lines.";
                    return false;
                }

                settings = settings with { Records = [ .. recordsArray.Select(record => record.Value<String>()!) ] };

            }

            #endregion

            #region zones

            if (json["zones"] is JToken zonesToken && zonesToken.Type != JTokenType.Null)
            {

                if (zonesToken is not JArray zonesArray)
                {
                    Error = $"\"zones\" of the \"{SectionName}\" section is no list of zones.";
                    return false;
                }

                var zones = new List<Zone>();
                var now   = DateTimeOffset.UtcNow;

                foreach (var zoneToken in zonesArray)
                {

                    if (!Zone.TryParse(zoneToken, now, out var zone, out var zoneError))
                    {
                        Error = $"\"zones\" of the \"{SectionName}\" section: {zoneError}";
                        return false;
                    }

                    zones.Add(zone);

                }

                settings = settings with { Zones = zones };

            }

            #endregion

            #region ssh

            if (json["ssh"] is JToken sshToken && sshToken.Type != JTokenType.Null)
            {

                if (sshToken is not JObject ssh)
                {
                    Error = $"\"ssh\" of the \"{SectionName}\" section is no JSON object.";
                    return false;
                }

                if (ssh["enabled"] is JToken enabledToken && enabledToken.Type is not (JTokenType.Null or JTokenType.Boolean))
                {
                    Error = $"\"ssh\".\"enabled\" of the \"{SectionName}\" section is neither true nor false: '{enabledToken}'.";
                    return false;
                }

                var enabled  = ssh.Value<Boolean?>("enabled") ?? true;
                var clients  = new List<DNSClientEntry>();

                if (ssh["clients"] is JToken clientsToken && clientsToken.Type != JTokenType.Null)
                {

                    if (clientsToken is not JArray clientsArray)
                    {
                        Error = $"\"ssh\".\"clients\" of the \"{SectionName}\" section is a list of {{ \"name\", \"keys\" }}.";
                        return false;
                    }

                    foreach (var clientToken in clientsArray)
                    {

                        if (!TryParseClient(clientToken, out var client, out Error))
                            return false;

                        if (clients.Any(known => String.Equals(known.Name, client.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            Error = $"The DNS client '{client.Name}' is listed twice in \"ssh\".\"clients\" of the \"{SectionName}\" section.";
                            return false;
                        }

                        clients.Add(client);

                    }

                }

                settings = settings with {
                               SSHEnabled  = enabled,
                               SSHClients  = clients
                           };

            }

            #endregion

            if (!settings.UDP && !settings.TCP)
            {
                Error = $"The \"{SectionName}\" section switches off both UDP and TCP: DNS would be answered nowhere.";
                return false;
            }

            Settings = settings;
            return true;

        }

        #endregion

        #region (static) TryParseClient(JSON, out Client, out Error)

        /// <summary>
        /// One DNS client: { "name": "cs001", "keys": [ "ssh-ed25519 AAAA..." ], "enabled": true }.
        /// </summary>
        public static Boolean TryParseClient(JToken?                                  JSON,
                                             [NotNullWhen(true)]  out DNSClientEntry? Client,
                                             [NotNullWhen(false)] out String?         Error)
        {

            Client = null;

            if (JSON is not JObject json)
            {
                Error = $"An entry of \"ssh\".\"clients\" of the \"{SectionName}\" section is no JSON object.";
                return false;
            }

            var name = json["name"]?.Type == JTokenType.String
                           ? json.Value<String>("name")!.Trim()
                           : "";

            if (!IsClientName(name))
            {
                Error = $"'{name}' can not name a DNS client: letters, digits and . _ @ + -, not beginning with a dot, at most 128 characters.";
                return false;
            }

            var keys = new List<String>();

            if (json["keys"] is JArray keysArray)
            {
                foreach (var keyToken in keysArray)
                {

                    var line = keyToken.Type == JTokenType.String
                                   ? keyToken.Value<String>()!.Trim()
                                   : "";

                    if (!IsAuthorizedKeyLine(line, out var why))
                    {
                        Error = $"A key of the DNS client '{name}' is no authorized_keys line: {why}";
                        return false;
                    }

                    if (!keys.Contains(line))
                        keys.Add(line);

                }
            }

            else if (json["keys"] is JToken other && other.Type != JTokenType.Null)
            {
                Error = $"\"keys\" of the DNS client '{name}' is a list of authorized_keys lines.";
                return false;
            }

            if (json["enabled"] is JToken enabledToken && enabledToken.Type is not (JTokenType.Null or JTokenType.Boolean))
            {
                Error = $"\"enabled\" of the DNS client '{name}' is neither true nor false: '{enabledToken}'.";
                return false;
            }

            Client = new DNSClientEntry(
                         name,
                         keys,
                         json.Value<Boolean?>("enabled") ?? true
                     );

            Error = null;
            return true;

        }

        #endregion

        #region (static) IsClientName(Name) / IsAuthorizedKeyLine(Line, out Why)

        /// <summary>
        /// What a login name of a DNS client may be: what the node's accounts
        /// may be called.
        /// </summary>
        public static Boolean IsClientName(String Name)

            => protocols.WWCP.Node.SecureShell.AuthorizedKeysStore.IsAccountName(Name);

        /// <summary>
        /// Whether the line is one authorized_keys line Hermod can hold a key to:
        /// a client's own key, or with cert-authority the key of an SSH CA whose
        /// user certificates let the client in.
        /// </summary>
        public static Boolean IsAuthorizedKeyLine(String                            Line,
                                                  [NotNullWhen(false)] out String?  Why)
        {

            if (Line.Length == 0 || Line.Contains('\n'))
            {
                Why = "it is empty, or more than one line.";
                return false;
            }

            if (!org.GraphDefined.Vanaheimr.Hermod.SSH.AuthorizedKeysFile.TryParseLine(Line, out var entry) || entry is null)
            {
                Why = $"'{(Line.Length > 40 ? Line[..40] + "..." : Line)}' could not be read as one.";
                return false;
            }

            Why = null;
            return true;

        }

        #endregion

        #region (static) TryParseListen(Text, out Address)

        /// <summary>
        /// An IPv4 or IPv6 address, or "any" for every address of both families.
        /// </summary>
        public static Boolean TryParseListen(String?                              Text,
                                             out IPAddress                       Address)
        {

            Address = default;

            var text = Text?.Trim() ?? "";

            if (text.Equals("any", StringComparison.OrdinalIgnoreCase) || text == "::")
            {
                Address = IPvXAddress.Any;
                return true;
            }

            return org.GraphDefined.Vanaheimr.Hermod.IPAddress.TryParse(text, out Address);

        }

        #endregion

        #region (static) TryParseTTL(Text, out TTL)

        /// <summary>
        /// A TTL as somebody types it: a number of seconds, or one with s, m, h
        /// or d after it - "3600", "60m", "1h", "1d".
        /// </summary>
        public static Boolean TryParseTTL(String Text, out TimeSpan TTL)
        {

            TTL = TimeSpan.Zero;

            var text = Text.Trim().ToLowerInvariant();

            if (text.Length == 0)
                return false;

            var factor = text[^1] switch {
                             's' => 1L,
                             'm' => 60L,
                             'h' => 3600L,
                             'd' => 86400L,
                             _   => 0L
                         };

            if (factor > 0)
                text = text[..^1];
            else
                factor = 1;

            if (!Int64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                number > Int32.MaxValue)
                return false;

            var seconds = number * factor;

            // RFC 2181 §8: a TTL is an unsigned 31-bit number.
            if (seconds is < 0 or > Int32.MaxValue)
                return false;

            TTL = TimeSpan.FromSeconds(seconds);
            return true;

        }

        #endregion

        #region (static) TextOf(Address)

        /// <summary>
        /// The address as the section writes it: "any", or the address.
        /// </summary>
        public static String TextOf(IPAddress Address)

            => Address is IPvXAddress
                   ? "any"
                   : Address.ToString() ?? "";

        #endregion

        #region ToJSON()

        /// <summary>
        /// The section as it is written.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("listen",      TextOf(Listen)),
                   new JProperty("port",        Port.ToUInt16()),
                   new JProperty("udp",         UDP),
                   new JProperty("tcp",         TCP),
                   new JProperty("defaultTTL",  (Int64) DefaultTTL.TotalSeconds),
                   new JProperty("records",     new JArray(Records)),
                   new JProperty("zones",       new JArray(Zones.Select(zone => zone.ToJSON()))),
                   new JProperty("ssh",         SSHToJSON())
               );

        /// <summary>
        /// The "ssh" of the section: whether DNS is answered over SSH, and to whom.
        /// </summary>
        public JObject SSHToJSON()

            => new (
                   new JProperty("enabled",  SSHEnabled),
                   new JProperty("clients",  new JArray(SSHClients.Select(client => client.ToJSON())))
               );

        #endregion

    }

}
