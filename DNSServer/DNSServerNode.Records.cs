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

using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;

#endregion

namespace cloud.charging.open.DNSServer
{

    /// <summary>
    /// What came of a change of the records: what to say, and - where nothing
    /// changed - why not, by whose fault.
    /// </summary>
    /// <param name="Said">What changed, in a sentence - or, where nothing did, why not.</param>
    /// <param name="Note">What else changed with it, if anything.</param>
    /// <param name="Outcome">Whether it changed, and why not.</param>
    public sealed record RecordsChange(String               Said,
                                       String?              Note,
                                       RecordsChangeOutcome Outcome)
    {

        /// <summary>
        /// Whether the records changed.
        /// </summary>
        public Boolean Changed
            => Outcome == RecordsChangeOutcome.Changed;

    }

    /// <summary>
    /// Whether a change of the records was made, and why not.
    /// </summary>
    public enum RecordsChangeOutcome
    {

        /// <summary>
        /// In effect, and written.
        /// </summary>
        Changed,

        /// <summary>
        /// What was asked for is no change that can be made: no name, no
        /// address, a record there already.
        /// </summary>
        Refused,

        /// <summary>
        /// There was nothing to take away.
        /// </summary>
        NothingMatched,

        /// <summary>
        /// The records could not be written, so nothing was put into effect.
        /// </summary>
        NotSaved

    }


    public partial class DNSServerNode
    {

        #region Add(Type, Name, Data, TTL, By)

        /// <summary>
        /// The given name answers with a record of the given type as well, from
        /// now on - as the command line's "aRecords add", "aaaaRecords add",
        /// "txtRecords add", "cnameRecords add", "mxRecords add", "srvRecords
        /// add" and "ptrRecords add" and the JSON API's
        /// ADD v1/dnsServer/records/{type} say it.
        /// </summary>
        /// <param name="Type">A, AAAA, TXT, CNAME, MX, SRV or PTR.</param>
        /// <param name="Name">The name, complete; a trailing dot is optional. For a PTR record an IPv4 or IPv6 address as well, for the name it is looked up by.</param>
        /// <param name="Data">What it answers with, as it was typed - as a zone file writes it: the IPv4 or IPv6 address, the text, the name it is an alias of, "10 mail.example.org" for an MX record, "0 5 443 host.example.org" for an SRV record, the name a PTR record points to.</param>
        /// <param name="TTL">The TTL as it was typed - seconds, or with s, m, h or d after it - or null for the "defaultTTL".</param>
        /// <param name="By">Who added it, for the log.</param>
        public RecordsChange Add(DNSResourceRecordTypes  Type,
                                 String                  Name,
                                 String                  Data,
                                 String?                 TTL,
                                 String                  By)
        {

            if (!ResourceRecords.Changeable.Contains(Type))
                return new ($"A record of the type {ADNSResourceRecord.TypeName(Type)} is written into the \"dnsServer\" section by hand.", null, RecordsChangeOutcome.Refused);

            if (!TryParseOwner(Type, Name, out var domainName, out var error))
                return new (error, null, RecordsChangeOutcome.Refused);

            var ttl = Settings.DefaultTTL;

            if (!String.IsNullOrWhiteSpace(TTL) && !DNSServerSettings.TryParseTTL(TTL, out ttl))
                return new ($"'{TTL}' is no TTL: a number of seconds, or one with s, m, h or d after it.", null, RecordsChangeOutcome.Refused);

            if (!TryMake(Type, domainName, Data, ttl, out var record, out error))
                return new (error, null, RecordsChangeOutcome.Refused);

            if (!Records.TryAdd(record, out var note, out error, out var notSaved))
                return new (error, null, notSaved ? RecordsChangeOutcome.NotSaved : RecordsChangeOutcome.Refused);

            var said = record is CNAME cname
                           ? $"{domainName.FullName} is an alias of {cname.CName.FullName} from now on, TTL {(Int64) ttl.TotalSeconds} s."
                           : $"{domainName.FullName} answers with {ResourceRecords.Said(record)} from now on, TTL {(Int64) ttl.TotalSeconds} s.";

            Log.Notice($"{said} ({By})", "dnsserver", "records");

            return new (said, note, RecordsChangeOutcome.Changed);

        }

        #endregion

        #region Remove(Type, Name, Data, By)

        /// <summary>
        /// The given name no longer answers with the given record of the given
        /// type - with any of the type, where no data is given - as the command
        /// line's "... remove" and the JSON API's
        /// REMOVE v1/dnsServer/records/{type} say it.
        /// </summary>
        /// <param name="Type">A, AAAA, TXT, CNAME, MX, SRV or PTR.</param>
        /// <param name="Name">The name, complete; a trailing dot is optional. For a PTR record an IPv4 or IPv6 address as well.</param>
        /// <param name="Data">What the record is removed by, as it was typed - the IPv4 or IPv6 address, the text, the name it is an alias of, the mail exchange of an MX record, the target of an SRV or PTR record - or null for every record of the type.</param>
        /// <param name="By">Who removed it, for the log.</param>
        public RecordsChange Remove(DNSResourceRecordTypes  Type,
                                    String                  Name,
                                    String?                 Data,
                                    String                  By)
        {

            var type = ADNSResourceRecord.TypeName(Type);

            if (!ResourceRecords.Changeable.Contains(Type))
                return new ($"A record of the type {type} is taken out of the \"dnsServer\" section by hand.", null, RecordsChangeOutcome.Refused);

            Func<IDNSResourceRecord, Boolean>? matches = null;
            String?                            said    = null;

            if (!TryParseOwner(Type, Name, out var owner, out var ownerError))
                return new (ownerError, null, RecordsChangeOutcome.Refused);

            if (!String.IsNullOrEmpty(Data) && ResourceRecords.RemovedByName.Contains(Type))
            {

                // An MX, SRV or PTR record is removed by the name it answers
                // with - the last word, where the whole of its data was given.
                var words = Data.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (words.Length == 0)
                    return new ("Which name?", null, RecordsChangeOutcome.Refused);

                if (!ResourceRecords.TryParseName(words[^1], out var target, out var targetError))
                    return new (targetError, null, RecordsChangeOutcome.Refused);

                var wantedName = ResourceRecords.Normalized(target.FullName);

                matches  = record => ResourceRecords.ValueOf(record) is String value &&
                                     ResourceRecords.Normalized(value) == wantedName;
                said     = Type switch {
                               DNSResourceRecordTypes.MX   => $"the mail exchange {target.FullName}",
                               DNSResourceRecordTypes.SRV  => $"the service at {target.FullName}",
                               _                           => $"the name {target.FullName}"
                           };

            }

            else if (!String.IsNullOrEmpty(Data))
            {

                // The record it would be, made with the name and data given, and
                // matched by what it answers with.
                if (!ResourceRecords.TryParseName(Name, out var domainName, out var error) ||
                    !TryMake(Type, domainName, Data, Settings.DefaultTTL, out var wanted, out error))
                    return new (error, null, RecordsChangeOutcome.Refused);

                matches  = record => ResourceRecords.SameData(record, wanted);
                said     = ResourceRecords.Said(wanted);

            }

            if (!Records.TryRemove(owner.FullName, Type, matches, out var removed, out var notSaved))
                return new (notSaved, null, RecordsChangeOutcome.NotSaved);

            if (removed.Count == 0)
                return new (said is not null
                                ? $"{owner.FullName} does not answer with {said}: nothing was removed."
                                : $"{owner.FullName} has no {type} record: nothing was removed.",
                            null,
                            RecordsChangeOutcome.NothingMatched);

            var fullName  = removed[0].DomainName.FullName;

            var told      = removed[0] is CNAME cname
                                ? $"{fullName} is no longer an alias of {cname.CName.FullName}."
                                : $"{fullName} no longer answers with {String.Join(", ", removed.Select(ResourceRecords.Said))}.";

            Log.Notice($"{told} ({By})", "dnsserver", "records");

            return new (told, null, RecordsChangeOutcome.Changed);

        }

        #endregion

        #region AddZone(Name, NameServers, By)

        /// <summary>
        /// The name server is authoritative for the zone of the given name from
        /// now on, with the SOA and NS records it makes - as the command line's
        /// "zones add" and the JSON API's ADD v1/dnsServer/zones say it.
        /// </summary>
        /// <param name="Name">The name of the zone; a trailing dot is optional.</param>
        /// <param name="NameServers">The name servers its NS records name, the first the primary of its SOA - or none for "ns." and the zone.</param>
        /// <param name="By">Who added it, for the log.</param>
        public RecordsChange AddZone(String               Name,
                                     IEnumerable<String>  NameServers,
                                     String               By)
        {

            if (!TryParseZoneName(Name, out var zoneName, out var error))
                return new (error, null, RecordsChangeOutcome.Refused);

            var nameServers = new List<DomainName>();

            foreach (var nameServer in NameServers.Where(nameServer => !String.IsNullOrWhiteSpace(nameServer)))
            {

                if (!ResourceRecords.TryParseName(nameServer, out var nameServerName, out error))
                    return new (error, null, RecordsChangeOutcome.Refused);

                if (nameServers.Any(known => ResourceRecords.Normalized(known.FullName) == ResourceRecords.Normalized(nameServerName.FullName)))
                    return new ($"{nameServerName.FullName} is given twice.", null, RecordsChangeOutcome.Refused);

                nameServers.Add(nameServerName);

            }

            var zone = Zone.New(zoneName, nameServers, DateTimeOffset.UtcNow);

            if (!Records.TryAddZone(zone, out error, out var notSaved))
                return new (error, null, notSaved ? RecordsChangeOutcome.NotSaved : RecordsChangeOutcome.Refused);

            var said = $"The name server is authoritative for {zone.Name.FullName} from now on: " +
                       $"its SOA names {zone.NameServers[0].FullName} as the primary and {zone.Hostmaster} as the hostmaster, " +
                       $"its NS records {String.Join(", ", zone.NameServers.Select(nameServer => nameServer.FullName))}; serial {zone.Serial}.";

            Log.Notice($"{said} ({By})", "dnsserver", "zones");

            return new (said, WithoutAddressNote(zone), RecordsChangeOutcome.Changed);

        }

        #endregion

        #region RemoveZone(Name, By)

        /// <summary>
        /// The name server is no longer authoritative for the zone of the given
        /// name - as the command line's "zones remove" and the JSON API's
        /// REMOVE v1/dnsServer/zones say it. Its records stay.
        /// </summary>
        /// <param name="Name">The name of the zone; a trailing dot is optional.</param>
        /// <param name="By">Who removed it, for the log.</param>
        public RecordsChange RemoveZone(String  Name,
                                        String  By)
        {

            if (!TryParseZoneName(Name, out var zoneName, out var error))
                return new (error, null, RecordsChangeOutcome.Refused);

            if (!Records.TryRemoveZone(zoneName.FullName, out var removed, out var notSaved))
                return new (notSaved, null, RecordsChangeOutcome.NotSaved);

            if (removed is null)
                return new ($"The name server is not authoritative for {zoneName.FullName}: nothing was removed.", null, RecordsChangeOutcome.NothingMatched);

            var said = $"The name server is no longer authoritative for {removed.Name.FullName}: its SOA and NS records are gone, " +
                        "and its records are answered by their name alone.";

            Log.Notice($"{said} ({By})", "dnsserver", "zones");

            return new (said, null, RecordsChangeOutcome.Changed);

        }

        #endregion

        #region WithoutAddressNote(Zone)

        /// <summary>
        /// What the zone lacks to be found: where a name server of it is in it
        /// and has no address, a resolver sent there cannot find it.
        /// </summary>
        public String? WithoutAddressNote(Zone Zone)
        {

            var missing = Records.NameServersWithoutAddress(Zone);

            return missing.Count > 0
                       ? $"{String.Join(", ", missing.Select(nameServer => nameServer.FullName))} {(missing.Count == 1 ? "has" : "have")} no address yet: " +
                         $"a resolver sent there cannot find this name server. Add one - aRecords add {missing[0].FullName} <IPv4>."
                       : null;

        }

        #endregion

        #region (private static) TryParseZoneName(Text, out Name, out Error)

        /// <summary>
        /// The name of a zone: complete, and not the root.
        /// </summary>
        private static Boolean TryParseZoneName(String                                Text,
                                                [NotNullWhen(true)]  out DomainName?  Name,
                                                [NotNullWhen(false)] out String?      Error)
        {

            Name = null;

            if (String.IsNullOrWhiteSpace(Text) || Text.Trim() == ".")
            {
                Error = "A zone needs a name, and the root is none for this name server.";
                return false;
            }

            if (Text.Contains('*'))
            {
                Error = $"'{Text.Trim()}' is no name of a zone: a zone has no wildcard in its name.";
                return false;
            }

            return ResourceRecords.TryParseName(Text, out Name, out Error);

        }

        #endregion


        #region (static) TryParseOwner(Type, Name, out Owner, out Error)

        /// <summary>
        /// The name a record of the given type is made for: as it was typed,
        /// and for a PTR record an address as well - 192.0.2.10 is
        /// 10.2.0.192.in-addr.arpa., the name it is looked up by.
        /// </summary>
        public static Boolean TryParseOwner(DNSResourceRecordTypes                Type,
                                            String                                Name,
                                            [NotNullWhen(true)]  out DomainName?  Owner,
                                            [NotNullWhen(false)] out String?      Error)
        {

            if (Type == DNSResourceRecordTypes.PTR &&
                ResourceRecords.TryReverseName(Name, out Owner))
            {
                Error = null;
                return true;
            }

            return ResourceRecords.TryParseName(Name, out Owner, out Error);

        }

        #endregion

        #region (static) TryMake(Type, Name, Data, TTL, out Record, out Error)

        /// <summary>
        /// The most a TXT record's data may hold: 65535 octets of RDATA, each
        /// character-string of at most 255 octets with one of its length before it.
        /// </summary>
        private const Int32 MaxTXTRData = UInt16.MaxValue;

        /// <summary>
        /// The record of the given type the given name answers with, made from
        /// the data as it was typed.
        /// </summary>
        public static Boolean TryMake(DNSResourceRecordTypes                        Type,
                                      DomainName                                    Name,
                                      String                                        Data,
                                      TimeSpan                                      TTL,
                                      [NotNullWhen(true)]  out IDNSResourceRecord?  Record,
                                      [NotNullWhen(false)] out String?              Error)
        {

            Record  = null;
            Error   = null;

            switch (Type)
            {

                case DNSResourceRecordTypes.A:
                    if (!IPv4Address.TryParse(Data.Trim(), out var ipv4))
                    {
                        Error = $"'{Data}' is no IPv4 address.";
                        return false;
                    }
                    Record = new A(Name, DNSQueryClasses.IN, TTL, ipv4);
                    return true;

                case DNSResourceRecordTypes.AAAA:
                    if (!IPv6Address.TryParse(Data.Trim(), out var ipv6))
                    {
                        Error = $"'{Data}' is no IPv6 address.";
                        return false;
                    }
                    Record = new AAAA(Name, DNSQueryClasses.IN, TTL, ipv6);
                    return true;

                case DNSResourceRecordTypes.TXT:

                    if (Data.Length == 0)
                    {
                        Error = "A TXT record needs a text.";
                        return false;
                    }

                    var txt = new TXT(Name, DNSQueryClasses.IN, TTL, Data);

                    if (txt.Strings.Sum(text => Encoding.UTF8.GetByteCount(text) + 1) > MaxTXTRData)
                    {
                        Error = $"The text is too long for one TXT record: it holds {MaxTXTRData} octets at most.";
                        return false;
                    }

                    Record = txt;
                    return true;

                case DNSResourceRecordTypes.CNAME:

                    if (String.IsNullOrWhiteSpace(Data))
                    {
                        Error = "A CNAME needs the name it is an alias of.";
                        return false;
                    }

                    if (!ResourceRecords.TryParseName(Data, out var target, out Error))
                        return false;

                    Record = new CNAME(Name, DNSQueryClasses.IN, TTL, target);
                    return true;

                case DNSResourceRecordTypes.MX:
                {

                    // "10 mail.example.org": its preference, and the mail exchange.
                    var words = Data.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    if (words.Length != 2)
                    {
                        Error = "An MX record needs a preference and a mail exchange: '10 mail.example.org'.";
                        return false;
                    }

                    if (!UInt16.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out var preference))
                    {
                        Error = $"'{words[0]}' is no preference: a number from 0 to 65535, the lowest preferred.";
                        return false;
                    }

                    if (!TryParseHostName(words[1], "mail exchange", out var exchange, out Error))
                        return false;

                    Record = new MX(Name, DNSQueryClasses.IN, TTL, preference, exchange);
                    return true;

                }

                case DNSResourceRecordTypes.SRV:
                {

                    // "0 5 443 host.example.org": priority, weight, port and target (RFC 2782).
                    var words = Data.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    if (words.Length != 4)
                    {
                        Error = "An SRV record needs a priority, a weight, a port and a target: '0 5 443 host.example.org'.";
                        return false;
                    }

                    if (!UInt16.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out var priority))
                    {
                        Error = $"'{words[0]}' is no priority: a number from 0 to 65535, the lowest tried first.";
                        return false;
                    }

                    if (!UInt16.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out var weight))
                    {
                        Error = $"'{words[1]}' is no weight: a number from 0 to 65535, among those of one priority.";
                        return false;
                    }

                    if (!UInt16.TryParse(words[2], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port == 0)
                    {
                        Error = $"'{words[2]}' is no port: a number from 1 to 65535.";
                        return false;
                    }

                    if (!TryParseHostName(words[3], "target", out var srvTarget, out Error))
                        return false;

                    // An SRV record is owned by a service name: "_ocpp._tcp.example.org.".
                    Record = new SRV(DNSServiceName.Parse(Name.FullName), DNSQueryClasses.IN, TTL, priority, weight, IPPort.Parse(port), srvTarget);
                    return true;

                }

                case DNSResourceRecordTypes.PTR:
                {

                    if (String.IsNullOrWhiteSpace(Data))
                    {
                        Error = "A PTR record needs the name it points to.";
                        return false;
                    }

                    if (!ResourceRecords.TryParseName(Data, out var pointsTo, out Error))
                        return false;

                    Record = new PTR(DNSServiceName.Parse(Name.FullName), DNSQueryClasses.IN, TTL, DNSServiceName.Parse(pointsTo.FullName));
                    return true;

                }

                default:
                    Error = $"A record of the type {ADNSResourceRecord.TypeName(Type)} is written into the \"dnsServer\" section by hand.";
                    return false;

            }

        }

        #endregion


        #region (private static) TryParseHostName(Text, What, out Name, out Error)

        /// <summary>
        /// The name of a host an MX or SRV record points to: a name, and no
        /// address - an MX or SRV record names its host, whose A and AAAA
        /// records say where it is (RFC 1035 §3.3.9, RFC 2782).
        /// </summary>
        private static Boolean TryParseHostName(String                                Text,
                                                String                                What,
                                                [NotNullWhen(true)]  out DomainName?  Name,
                                                [NotNullWhen(false)] out String?      Error)
        {

            Name = null;

            if (System.Net.IPAddress.TryParse(Text.Trim(), out _) &&
                (Text.Contains(':') || Text.Count(c => c == '.') == 3))
            {
                Error = $"'{Text}' is an address, and the {What} is a name: one with an A or AAAA record.";
                return false;
            }

            return ResourceRecords.TryParseName(Text, out Name, out Error);

        }

        #endregion


        #region RecordsJSON()

        /// <summary>
        /// Every record, in the order they were added: each with its name, type,
        /// class, TTL and data, and as the zone-file line it is written as.
        /// </summary>
        public JArray RecordsJSON()

            => new (Records.All.Select(RecordJSON));

        #endregion

        #region ZonesJSON()

        /// <summary>
        /// Every zone, in the order they were added: what it is, the SOA and NS
        /// records it makes, and what it lacks to be found.
        /// </summary>
        public JArray ZonesJSON()

            => new (Records.Zones.Select(zone => {

                        var json = zone.ToJSON();

                        json.Add(new JProperty("records",  new JArray(Records.MadeBy(zone).Select(RecordJSON))));
                        json.Add(new JProperty("note",     WithoutAddressNote(zone)));

                        return json;

                    }));

        #endregion

        #region (static) RecordJSON(Record)

        /// <summary>
        /// The presentation form, name TTL class type data, taken apart.
        /// </summary>
        private static readonly Regex zoneFileLine = new (@"^(\S+)\s+(\d+)\s+(\S+)\s+(\S+)\s*(.*)$", RegexOptions.Compiled);

        /// <summary>
        /// One record, as the JSON API and the web interface say it.
        /// </summary>
        public static JObject RecordJSON(IDNSResourceRecord Record)
        {

            var line   = Record.ToZoneFileString();
            var parts  = zoneFileLine.Match(line);

            return new (
                       new JProperty("name",   Record.DomainName.FullName),
                       new JProperty("type",   ADNSResourceRecord.TypeName (Record.Type)),
                       new JProperty("class",  ADNSResourceRecord.ClassName(Record.Class)),
                       new JProperty("ttl",    (Int64) Record.TimeToLive.TotalSeconds),
                       new JProperty("data",   parts.Success ? parts.Groups[5].Value.Trim() : null),
                       new JProperty("value",  ResourceRecords.ValueOf(Record)),
                       new JProperty("line",   Regex.Replace(line.Trim(), @"\s+", " "))
                   );

        }

        #endregion

    }

}
