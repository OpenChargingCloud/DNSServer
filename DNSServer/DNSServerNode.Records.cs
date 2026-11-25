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
        /// "txtRecords add" and "cnameRecords add" and the JSON API's
        /// ADD v1/dnsServer/records/{type} say it.
        /// </summary>
        /// <param name="Type">A, AAAA, TXT or CNAME.</param>
        /// <param name="Name">The name, complete; a trailing dot is optional.</param>
        /// <param name="Data">What it answers with, as it was typed: the IPv4 or IPv6 address, the text, the name it is an alias of.</param>
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

            if (!ResourceRecords.TryParseName(Name, out var domainName, out var error))
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
        /// <param name="Type">A, AAAA, TXT or CNAME.</param>
        /// <param name="Name">The name, complete; a trailing dot is optional.</param>
        /// <param name="Data">What the record answers with, as it was typed - the IPv4 or IPv6 address, the text, the name it is an alias of - or null for every record of the type.</param>
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

            if (!String.IsNullOrEmpty(Data))
            {

                // The record it would be, made with the name and data given, and
                // matched by what it answers with.
                if (!ResourceRecords.TryParseName(Name, out var domainName, out var error) ||
                    !TryMake(Type, domainName, Data, Settings.DefaultTTL, out var wanted, out error))
                    return new (error, null, RecordsChangeOutcome.Refused);

                matches  = record => ResourceRecords.SameData(record, wanted);
                said     = ResourceRecords.Said(wanted);

            }

            if (!Records.TryRemove(Name, Type, matches, out var removed, out var notSaved))
                return new (notSaved, null, RecordsChangeOutcome.NotSaved);

            if (removed.Count == 0)
                return new (said is not null
                                ? $"{Name} does not answer with {said}: nothing was removed."
                                : $"{Name} has no {type} record: nothing was removed.",
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

                default:
                    Error = $"A record of the type {ADNSResourceRecord.TypeName(Type)} is written into the \"dnsServer\" section by hand.";
                    return false;

            }

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
