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
    ///     "records":     [ "www.example.org. 3600 IN A 192.0.2.10" ]
    /// }
    /// </code>
    /// Every key may be left out, and then reads as its default. The records
    /// are zone-file lines (RFC 1035 §5.1), each with its name complete: that
    /// is the one form every kind of record has, so that a record the command
    /// line cannot make yet can still be written into the file by hand.
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
        public IIPAddress             Listen        { get; init; } = IPvXAddress.Any;

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

            if (!settings.UDP && !settings.TCP)
            {
                Error = $"The \"{SectionName}\" section switches off both UDP and TCP: DNS would be answered nowhere.";
                return false;
            }

            Settings = settings;
            return true;

        }

        #endregion

        #region (static) TryParseListen(Text, out Address)

        /// <summary>
        /// An IPv4 or IPv6 address, or "any" for every address of both families.
        /// </summary>
        public static Boolean TryParseListen(String?                              Text,
                                             [NotNullWhen(true)] out IIPAddress?  Address)
        {

            Address = null;

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
        public static String TextOf(IIPAddress Address)

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
                   new JProperty("records",     new JArray(Records))
               );

        #endregion

    }

}
