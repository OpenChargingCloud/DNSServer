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
    ///     "minimum":      3600
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

            Zone = zone;
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
                   new JProperty("name",         Name.FullName),
                   new JProperty("nameServers",  new JArray(NameServers.Select(nameServer => nameServer.FullName))),
                   new JProperty("hostmaster",   Hostmaster.ToString()),
                   new JProperty("serial",       Serial),
                   new JProperty("refresh",      (Int64) Refresh.TotalSeconds),
                   new JProperty("retry",        (Int64) Retry.  TotalSeconds),
                   new JProperty("expire",       (Int64) Expire. TotalSeconds),
                   new JProperty("minimum",      (Int64) Minimum.TotalSeconds)
               );

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{Name.FullName} (serial {Serial.ToString(CultureInfo.InvariantCulture)})";

        #endregion

    }

}
