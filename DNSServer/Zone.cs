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
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.Mail;

#endregion

namespace cloud.charging.open.DNSServer
{

    /// <summary>
    /// A zone the name server is authoritative for: a name, and everything
    /// below it. Its SOA and NS records are made by the server - they are not
    /// written among the records.
    /// </summary>
    /// <remarks>
    /// <code>
    /// "zones": [ {
    ///     "name":         "example.org.",
    ///     "nameServers":  [ "ns.example.org." ],
    ///     "hostmaster":   "hostmaster@example.org",
    ///     "serial":       2026112500,
    ///     "refresh":      86400,
    ///     "retry":        7200,
    ///     "expire":       1209600,
    ///     "minimum":      3600,
    ///     "transfer":     [ "192.0.2.53", "2001:db8:53::/48" ],
    ///     "transferKeys": [ "ns2-transfer." ],
    ///     "notify":       [ "192.0.2.53", "[2001:db8:53::1]:5353" ]
    /// } ]
    /// </code>
    /// <para>
    /// Only "name" is needed. The name servers are "ns." and the zone, the
    /// hostmaster is "hostmaster@" and the zone (RFC 2142 §7), and the timers
    /// are those RFC 1912 §2.2 suggests - with an hour as the negative TTL, as
    /// RFC 2308 §5 does.
    /// </para>
    /// <para>
    /// The serial is the date and a number of the day, YYYYMMDDnn (RFC 1912
    /// §2.2), counted on by every change of a record in the zone, and written
    /// with it: a resolver or a secondary sees a change by a greater serial.
    /// </para>
    /// <para>
    /// "transfer" are the addresses and prefixes the zone may be transferred
    /// to (AXFR, RFC 5936; IXFR, RFC 1995) - its secondaries - and
    /// "transferKeys" the TSIG keys of the "tsigKeys" of the section a request
    /// for it may be signed with instead (RFC 8945). Without either, the zone
    /// is transferred to nobody. "notify" are the secondaries told of each new
    /// serial (RFC 1996): an address, with a port where not 53.
    /// </para>
    /// </remarks>
    public sealed record Zone
    {

        #region Data

        /// <summary>
        /// How often a secondary asks whether the zone changed: a day.
        /// </summary>
        public static readonly TimeSpan  DefaultRefresh  = TimeSpan.FromDays(1);

        /// <summary>
        /// How soon a secondary asks again where that failed: two hours.
        /// </summary>
        public static readonly TimeSpan  DefaultRetry    = TimeSpan.FromHours(2);

        /// <summary>
        /// How long a secondary answers for the zone without reaching the
        /// primary: two weeks.
        /// </summary>
        public static readonly TimeSpan  DefaultExpire   = TimeSpan.FromDays(14);

        /// <summary>
        /// How long a resolver keeps an answer that a name or record is not
        /// there - the negative TTL of RFC 2308 §4: an hour.
        /// </summary>
        public static readonly TimeSpan  DefaultMinimum  = TimeSpan.FromHours(1);

        #endregion

        #region Properties

        /// <summary>
        /// The name of the zone, its apex: complete, with its trailing dot.
        /// </summary>
        public required DomainName                  Name           { get; init; }

        /// <summary>
        /// The name servers the zone names in its NS records; the first is the
        /// primary in its SOA.
        /// </summary>
        public required IReadOnlyList<DomainName>   NameServers    { get; init; }

        /// <summary>
        /// Who is responsible for the zone, as its SOA says.
        /// </summary>
        public required SimpleEMailAddress          Hostmaster     { get; init; }

        /// <summary>
        /// The serial of the zone, YYYYMMDDnn.
        /// </summary>
        public required UInt32                      Serial         { get; init; }

        /// <summary>
        /// How often a secondary asks whether the zone changed.
        /// </summary>
        public TimeSpan                             Refresh        { get; init; } = DefaultRefresh;

        /// <summary>
        /// How soon a secondary asks again where that failed.
        /// </summary>
        public TimeSpan                             Retry          { get; init; } = DefaultRetry;

        /// <summary>
        /// How long a secondary answers without reaching the primary.
        /// </summary>
        public TimeSpan                             Expire         { get; init; } = DefaultExpire;

        /// <summary>
        /// The negative TTL: how long a resolver keeps a "not there".
        /// </summary>
        public TimeSpan                             Minimum        { get; init; } = DefaultMinimum;

        /// <summary>
        /// The addresses and prefixes the zone may be transferred to: "192.0.2.53", "2001:db8::/32".
        /// </summary>
        public IReadOnlyList<System.Net.IPNetwork>  Transfer       { get; init; } = [];

        /// <summary>
        /// The TSIG keys a request to transfer the zone may be signed with instead.
        /// </summary>
        public IReadOnlyList<DomainName>            TransferKeys   { get; init; } = [];

        /// <summary>
        /// The secondaries told of each new serial (RFC 1996).
        /// </summary>
        public IReadOnlyList<System.Net.IPEndPoint> Notify         { get; init; } = [];

        /// <summary>
        /// The name of the zone as two names are compared.
        /// </summary>
        public String                               Apex
            => ResourceRecords.Normalized(Name.FullName);

        #endregion


        #region (static) New(Name, NameServers, Now)

        /// <summary>
        /// A new zone: the given name servers, or "ns." and the zone where none
        /// are given, "hostmaster@" and the zone, and the first serial of the day.
        /// </summary>
        public static Zone New(DomainName                 Name,
                               IEnumerable<DomainName>?   NameServers,
                               DateTimeOffset             Now)
        {

            var nameServers = NameServers?.ToArray() ?? [];

            return new Zone {
                       Name         = Name,
                       NameServers  = nameServers.Length > 0
                                          ? nameServers
                                          : [ DomainName.ParseLenient("ns." + Name.FullName) ],
                       Hostmaster   = SimpleEMailAddress.Parse("hostmaster@" + Name.FullName.TrimEnd('.')),
                       Serial       = NextSerial(0, Now)
                   };

        }

        #endregion

        #region (static) NextSerial(Serial, Now)

        /// <summary>
        /// The serial after the given one: the first of today, YYYYMMDD00, where
        /// the given one is older - and one more otherwise, so that it never
        /// goes back, however often the zone changes in a day.
        /// </summary>
        public static UInt32 NextSerial(UInt32          Serial,
                                        DateTimeOffset  Now)
        {

            var today = (UInt32) (Now.UtcDateTime.Year  * 1000000 +
                                  Now.UtcDateTime.Month * 10000 +
                                  Now.UtcDateTime.Day   * 100);

            return Serial < today
                       ? today
                       : Serial + 1;

        }

        #endregion

        #region Contains(Name)

        /// <summary>
        /// Whether the given name - normalized - is the apex or below it.
        /// </summary>
        public Boolean Contains(String NormalizedName)

            => IsAtOrBelow(NormalizedName, Apex);

        /// <summary>
        /// Whether the one name - both normalized - is the other or below it.
        /// </summary>
        public static Boolean IsAtOrBelow(String NormalizedName,
                                          String NormalizedApex)

            => NormalizedName == NormalizedApex ||
               NormalizedName.EndsWith("." + NormalizedApex, StringComparison.Ordinal);

        #endregion

        #region MayTransferTo(Address, TSIGKeyName)

        /// <summary>
        /// Whether the zone may be transferred to a request from the given
        /// address, signed with the given TSIG key or with none.
        /// </summary>
        public Boolean MayTransferTo(System.Net.IPAddress  Address,
                                     DomainName?           TSIGKeyName)
        {

            if (TSIGKeyName is not null &&
                TransferKeys.Any(key => ResourceRecords.Normalized(key.FullName) == ResourceRecords.Normalized(TSIGKeyName.FullName)))
                return true;

            // An IPv4 client on a socket of both families is ::ffff:a.b.c.d.
            var address = Address.IsIPv4MappedToIPv6
                              ? Address.MapToIPv4()
                              : Address;

            return Transfer.Any(network => network.Contains(address));

        }

        #endregion

        #region (static) TryParseTransfer(Text, out Network) / TryParseNotify(Text, out EndPoint)

        /// <summary>
        /// An address - every one of its prefix length - or a prefix a zone may
        /// be transferred to: "192.0.2.53", "192.0.2.0/24", "2001:db8::/32".
        /// </summary>
        public static Boolean TryParseTransfer(String                       Text,
                                               out System.Net.IPNetwork    Network)
        {

            var text = Text.Trim();

            if (!text.Contains('/') &&
                System.Net.IPAddress.TryParse(text, out var address) &&
                (text.Contains(':') || text.Count(c => c == '.') == 3))
            {
                Network = new System.Net.IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
                return true;
            }

            if (text.Contains('/') &&
                System.Net.IPNetwork.TryParse(text, out Network))
                return true;

            Network = default;
            return false;

        }

        /// <summary>
        /// A secondary to tell of a new serial: an address, and a port where not
        /// 53 - "192.0.2.53", "192.0.2.53:5353", "[2001:db8::53]:5353".
        /// </summary>
        public static Boolean TryParseNotify(String                                         Text,
                                             [NotNullWhen(true)] out System.Net.IPEndPoint? EndPoint)
        {

            EndPoint = null;

            var text = Text.Trim();

            // An IPv6 address without a port has colons, and is no endpoint. One
            // in brackets is given with its port: .NET would read it and drop it.
            if (!text.StartsWith('[') &&
                System.Net.IPAddress.TryParse(text, out var address) &&
                (text.Contains(':') || text.Count(c => c == '.') == 3))
            {
                EndPoint = new System.Net.IPEndPoint(address, 53);
                return true;
            }

            if (!System.Net.IPEndPoint.TryParse(text, out var endPoint) ||
                endPoint.Port == 0 ||
                !(text.StartsWith('[') || text.Count(c => c == '.') == 3))
                return false;

            EndPoint = endPoint;
            return true;

        }

        /// <summary>
        /// A secondary to tell of a new serial, as the zone writes it: its
        /// address, and its port where not 53.
        /// </summary>
        public static String TextOf(System.Net.IPEndPoint EndPoint)

            => EndPoint.Port == 53
                   ? EndPoint.Address.ToString()
                   : EndPoint.ToString();

        /// <summary>
        /// An address or prefix the zone may be transferred to, as the zone
        /// writes it: an address alone where it is one.
        /// </summary>
        public static String TextOf(System.Net.IPNetwork Network)

            => Network.PrefixLength == (Network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)
                   ? Network.BaseAddress.ToString()
                   : Network.ToString();

        #endregion

        #region SOA(TTL) / NS(TTL)

        /// <summary>
        /// The SOA record of the zone: its first name server as the primary.
        /// </summary>
        public SOA SOA(TimeSpan TTL)

            => new (Name,
                    DNSQueryClasses.IN,
                    TTL,
                    NameServers[0],
                    Hostmaster,
                    Serial,
                    Refresh,
                    Retry,
                    Expire,
                    Minimum);

        /// <summary>
        /// The NS records of the zone: one for every name server.
        /// </summary>
        public IReadOnlyList<NS> NS(TimeSpan TTL)

            => [ .. NameServers.Select(nameServer => new NS(Name, DNSQueryClasses.IN, TTL, nameServer)) ];

        #endregion


        #region (static) TryParse(JSON, Now, out Zone, out Error)

        /// <summary>
        /// One zone of the "zones" of the "dnsServer" section.
        /// </summary>
        public static Boolean TryParse(JToken                          JSON,
                                       DateTimeOffset                  Now,
                                       [NotNullWhen(true)]  out Zone?  Zone,
                                       [NotNullWhen(false)] out String? Error)
        {

            Zone   = null;
            Error  = null;

            if (JSON is not JObject json)
            {
                Error = $"'{JSON}' is no JSON object with a \"name\".";
                return false;
            }

            if (json["name"]?.Type != JTokenType.String ||
                !ResourceRecords.TryParseName(json["name"]!.Value<String>()!, out var name, out var problem))
            {
                Error = $"'{json["name"]}' is no name of a zone.";
                return false;
            }

            var where = $"The zone {name.FullName}";

            #region nameServers

            var nameServers = new List<DomainName>();

            if (json["nameServers"] is JToken nameServersToken && nameServersToken.Type != JTokenType.Null)
            {

                if (nameServersToken is not JArray nameServersArray)
                {
                    Error = $"{where}: \"nameServers\" is no list of names.";
                    return false;
                }

                foreach (var nameServer in nameServersArray)
                {

                    if (nameServer.Type != JTokenType.String ||
                        !ResourceRecords.TryParseName(nameServer.Value<String>()!, out var nameServerName, out problem))
                    {
                        Error = $"{where}: '{nameServer}' of its \"nameServers\" is no name.";
                        return false;
                    }

                    nameServers.Add(nameServerName);

                }

            }

            var zone = New(name, nameServers, Now);

            #endregion

            #region hostmaster

            if (json["hostmaster"] is JToken hostmasterToken && hostmasterToken.Type != JTokenType.Null)
            {

                if (hostmasterToken.Type != JTokenType.String ||
                    !TryParseHostmaster(hostmasterToken.Value<String>()!, out var hostmaster))
                {
                    Error = $"{where}: \"hostmaster\" is no e-mail address with no dot before its @: '{hostmasterToken}'.";
                    return false;
                }

                zone = zone with { Hostmaster = hostmaster };

            }

            #endregion

            #region serial

            if (json["serial"] is JToken serialToken && serialToken.Type != JTokenType.Null)
            {

                if (serialToken.Type != JTokenType.Integer ||
                    serialToken.Value<Int64>() is < 0 or > UInt32.MaxValue)
                {
                    Error = $"{where}: \"serial\" is no number from 0 to {UInt32.MaxValue}: '{serialToken}'.";
                    return false;
                }

                zone = zone with { Serial = serialToken.Value<UInt32>() };

            }

            #endregion

            #region refresh, retry, expire, minimum

            foreach (var (key, set) in new (String, Func<Zone, TimeSpan, Zone>)[] {
                                           ("refresh",  (z, value) => z with { Refresh  = value }),
                                           ("retry",    (z, value) => z with { Retry    = value }),
                                           ("expire",   (z, value) => z with { Expire   = value }),
                                           ("minimum",  (z, value) => z with { Minimum  = value })
                                       })
            {

                if (json[key] is not JToken token || token.Type == JTokenType.Null)
                    continue;

                // RFC 1035 §3.3.13: 32-bit numbers of seconds, which RFC 2181 §8
                // keeps below 2^31 as every TTL.
                if (token.Type != JTokenType.Integer ||
                    token.Value<Int64>() is < 0 or > Int32.MaxValue)
                {
                    Error = $"{where}: \"{key}\" is no number of seconds: '{token}'.";
                    return false;
                }

                zone = set(zone, TimeSpan.FromSeconds(token.Value<Int64>()));

            }

            #endregion

            #region transfer, transferKeys, notify

            if (!TryParseList(json, "transfer", where, "address or prefix it may be transferred to", text => TryParseTransfer(text, out var network) ? network : (System.Net.IPNetwork?) null, out var transfer, out Error) ||
                !TryParseList(json, "notify",   where, "address to tell of a new serial, with a port where not 53", text => TryParseNotify(text, out var endPoint) ? endPoint : null, out var notify, out Error) ||
                !TryParseList(json, "transferKeys", where, "name of a TSIG key", text => ResourceRecords.TryParseName(text, out var key, out _) ? key : null, out var transferKeys, out Error))
                return false;

            zone = zone with {
                       Transfer      = [ .. transfer.Select(network => network!.Value) ],
                       Notify        = [ .. notify!       ],
                       TransferKeys  = [ .. transferKeys! ]
                   };

            #endregion

            Zone = zone;
            return true;

        }

        #endregion

        #region (private static) TryParseList(JSON, Key, Where, What, Parse, out List, out Error)

        /// <summary>
        /// A list of texts of the zone, each read as what it is.
        /// </summary>
        private static Boolean TryParseList<T>(JObject                           JSON,
                                               String                            Key,
                                               String                            Where,
                                               String                            What,
                                               Func<String, T?>                  Parse,
                                               out List<T>                       List,
                                               [NotNullWhen(false)] out String?  Error)
        {

            List   = [];
            Error  = null;

            if (JSON[Key] is not JToken token || token.Type == JTokenType.Null)
                return true;

            if (token is not JArray array)
            {
                Error = $"{Where}: \"{Key}\" is no list.";
                return false;
            }

            foreach (var entry in array)
            {

                if (entry.Type != JTokenType.String || Parse(entry.Value<String>()!) is not T value)
                {
                    Error = $"{Where}: '{entry}' of its \"{Key}\" is no {What}.";
                    return false;
                }

                List.Add(value);

            }

            return true;

        }

        #endregion

        #region (static) TryParseHostmaster(Text, out Hostmaster)

        /// <summary>
        /// The e-mail address of who is responsible for a zone. Its local part
        /// has no dot: the SOA writes the @ as a dot (RFC 1035 §8), and one
        /// before it would be read back as the @.
        /// </summary>
        public static Boolean TryParseHostmaster(String                  Text,
                                                 out SimpleEMailAddress  Hostmaster)
        {

            Hostmaster = default;

            var text = Text.Trim();
            var at   = text.IndexOf('@');

            if (at <= 0 || text[..at].Contains('.') ||
                !SimpleEMailAddress.TryParse(text, out var hostmaster))
                return false;

            Hostmaster = hostmaster;
            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The zone as the "zones" of the "dnsServer" section write it.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("name",          Name.FullName),
                   new JProperty("nameServers",   new JArray(NameServers.Select(nameServer => nameServer.FullName))),
                   new JProperty("hostmaster",    Hostmaster.ToString()),
                   new JProperty("serial",        Serial),
                   new JProperty("refresh",       (Int64) Refresh.TotalSeconds),
                   new JProperty("retry",         (Int64) Retry.  TotalSeconds),
                   new JProperty("expire",        (Int64) Expire. TotalSeconds),
                   new JProperty("minimum",       (Int64) Minimum.TotalSeconds),
                   new JProperty("transfer",      new JArray(Transfer.    Select(network  => TextOf(network)))),
                   new JProperty("transferKeys",  new JArray(TransferKeys.Select(key      => key.FullName))),
                   new JProperty("notify",        new JArray(Notify.      Select(endPoint => TextOf(endPoint))))
               );

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{Name.FullName} (serial {Serial.ToString(CultureInfo.InvariantCulture)})";

        #endregion

    }

}
