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

using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;

#endregion

namespace cloud.charging.open.DNSServer
{

    /// <summary>
    /// The resource records a name server answers with, and the zones it is
    /// authoritative for: what Hermod's DNS server asks, and the lists they
    /// are written back to the configuration file from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hermod's in-memory zone holds one zone - the one its SOA starts - so
    /// every zone has one of its own here: with the SOA and NS records the
    /// zone makes, and the records at its name and below. A question goes to
    /// the zone its name is in, the one with the longest name where zones are
    /// within each other. The records in no zone are answered as before there
    /// were zones: by their name exactly.
    /// </para>
    /// <para>
    /// The zones cannot say what they hold, and the file has to be written in
    /// the order somebody added to it, so the records are kept twice: here as
    /// a list, and in the zones for the lookups. Every change goes through here,
    /// and makes the zones anew from the list.
    /// </para>
    /// <para>
    /// Written first, put into effect afterwards: a change the file cannot take
    /// is refused with nothing changed, so that what is answered is always what
    /// the next start answers too.
    /// </para>
    /// </remarks>
    public sealed class ResourceRecords : IDNSZoneStore
    {

        #region Data

        private readonly Lock                                                       gate     = new();
        private readonly List<IDNSResourceRecord>                                   records  = [];
        private readonly List<Zone>                                                 zones    = [];
        private readonly Func<IReadOnlyList<String>, IReadOnlyList<Zone>, String?>? persist;
        private readonly Func<DateTimeOffset>                                       now;

        /// <summary>
        /// What a question is looked up in: every zone with its own store,
        /// the longest name first, and the store of the records in none.
        /// </summary>
        private sealed record Stores(IReadOnlyList<(Zone Zone, InMemoryDNSZone Store)>  Zones,
                                     InMemoryDNSZone                                     Rest);

        private volatile Stores stores = new ([], new InMemoryDNSZone());

        /// <summary>
        /// The types of record that are added and removed at the command line,
        /// over the JSON API and in the web interface; every other type is
        /// written into the "dnsServer" section by hand.
        /// </summary>
        public static readonly IReadOnlyList<DNSResourceRecordTypes>  Changeable  = [
                                                                                        DNSResourceRecordTypes.A,
                                                                                        DNSResourceRecordTypes.AAAA,
                                                                                        DNSResourceRecordTypes.TXT,
                                                                                        DNSResourceRecordTypes.CNAME,
                                                                                        DNSResourceRecordTypes.MX,
                                                                                        DNSResourceRecordTypes.SRV,
                                                                                        DNSResourceRecordTypes.PTR
                                                                                    ];

        /// <summary>
        /// The types of record that answer with a name - and are removed by
        /// it: the mail exchange of an MX record, the target of an SRV or PTR
        /// record.
        /// </summary>
        public static readonly IReadOnlyList<DNSResourceRecordTypes>  RemovedByName  = [
                                                                                           DNSResourceRecordTypes.MX,
                                                                                           DNSResourceRecordTypes.SRV,
                                                                                           DNSResourceRecordTypes.PTR
                                                                                       ];

        #endregion

        #region Properties

        /// <summary>
        /// The TTL of the SOA and NS records the zones make.
        /// </summary>
        public TimeSpan  DefaultTTL  { get; }

        /// <summary>
        /// Every zone the name server is authoritative for, in the order they
        /// were added.
        /// </summary>
        public IReadOnlyList<Zone> Zones
        {
            get
            {
                lock (gate)
                    return [ .. zones ];
            }
        }

        /// <summary>
        /// Every record, in the order they were added.
        /// </summary>
        public IReadOnlyList<IDNSResourceRecord> All
        {
            get
            {
                lock (gate)
                    return [ .. records ];
            }
        }

        /// <summary>
        /// How many records there are.
        /// </summary>
        public Int32 Count
        {
            get
            {
                lock (gate)
                    return records.Count;
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// The records and zones were changed and written: the zones before and
        /// after - where a serial went on, the secondaries of that zone are to
        /// be told. Called within the lock of the records: whoever is told does
        /// what takes time elsewhere.
        /// </summary>
        public event Action<IReadOnlyList<Zone>, IReadOnlyList<Zone>>? OnCommitted;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The records and zones of a name server.
        /// </summary>
        /// <param name="DefaultTTL">The TTL of the SOA and NS records the zones make.</param>
        /// <param name="Persist">Writes the records, as zone-file lines, and the zones to where they are kept between starts: null where that worked, why not otherwise. Null to keep them in memory only.</param>
        /// <param name="Now">What time it is, for the serials of the zones; the clock by default.</param>
        public ResourceRecords(TimeSpan                                                     DefaultTTL,
                               Func<IReadOnlyList<String>, IReadOnlyList<Zone>, String?>?  Persist  = null,
                               Func<DateTimeOffset>?                                        Now      = null)
        {
            this.DefaultTTL  = DefaultTTL;
            this.persist     = Persist;
            this.now         = Now ?? (() => DateTimeOffset.UtcNow);
        }

        #endregion


        #region (static) TryParse(Lines, DefaultTTL, out Records, out Error)

        /// <summary>
        /// Read records written as zone-file lines, each with its name complete.
        /// </summary>
        public static Boolean TryParse(IEnumerable<String>                                       Lines,
                                       TimeSpan                                                  DefaultTTL,
                                       [NotNullWhen(true)]  out IReadOnlyList<IDNSResourceRecord>? Records,
                                       [NotNullWhen(false)] out String?                          Error)
        {

            Records = null;

            var all = new List<IDNSResourceRecord>();

            foreach (var line in Lines)
            {

                if (String.IsNullOrWhiteSpace(line))
                    continue;

                if (!DNSZoneFile.TryParse(line, out var parsed, out var problem, DefaultTimeToLive: DefaultTTL))
                {
                    Error = $"'{line.Trim()}' is no resource record: {problem}";
                    return false;
                }

                all.AddRange(parsed);

            }

            Records  = all;
            Error    = null;

            return true;

        }

        #endregion

        #region TryLoad(Records, Zones, out Error)

        /// <summary>
        /// Take the given records and zones in, as they were read at the start -
        /// without writing them anywhere.
        /// </summary>
        /// <remarks>
        /// Refused, with nothing taken in, where two zones have the same name,
        /// or where the records hold an SOA or NS record, or a CNAME, at the
        /// name of a zone: its SOA and NS records are the zone's to make, and a
        /// name with a CNAME has no other record (RFC 1034 §3.6.2).
        /// </remarks>
        public Boolean TryLoad(IEnumerable<IDNSResourceRecord>   Records,
                               IEnumerable<Zone>                 Zones,
                               [NotNullWhen(false)] out String?  Error)
        {

            var loadedRecords  = Records.ToList();
            var loadedZones    = new List<Zone>();

            foreach (var zone in Zones)
            {

                if (loadedZones.Any(loaded => loaded.Apex == zone.Apex))
                {
                    Error = $"The zone {zone.Name.FullName} is there twice.";
                    return false;
                }

                if (AtTheApex(loadedRecords, zone) is IDNSResourceRecord clash)
                {
                    Error = $"{ClashAtTheApex(zone, clash)} Take '{Regex.Replace(clash.ToZoneFileString().Trim(), @"\s+", " ")}' out of the records.";
                    return false;
                }

                loadedZones.Add(zone);

            }

            lock (gate)
            {

                records.Clear();
                records.AddRange(loadedRecords);

                zones.Clear();
                zones.AddRange(loadedZones);

                Rebuild();

            }

            Error = null;
            return true;

        }

        #endregion


        #region Named(Name, Type = null)

        /// <summary>
        /// The records at the given name, of the given type, or of every type.
        /// </summary>
        public IReadOnlyList<IDNSResourceRecord> Named(String                   Name,
                                                       DNSResourceRecordTypes?  Type = null)
        {

            var name = Normalized(Name);

            lock (gate)
                return [ .. records.Where(record => Normalized(record.DomainName.FullName) == name &&
                                                    (!Type.HasValue || record.Type == Type.Value)) ];

        }

        #endregion

        #region TryAdd(Record, out Note, out Error, out NotSaved)

        /// <summary>
        /// One record more - an A, AAAA, TXT, CNAME, MX, SRV or PTR record: its
        /// name answers with it as well.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The records of one name and type are one RRset, and every record of an
        /// RRset has the same TTL (RFC 2181 §5.2) - so where the name has records
        /// of the type already, the TTL given now is theirs from now on as well,
        /// and <paramref name="Note"/> says so.
        /// </para>
        /// <para>
        /// A name with a CNAME is an alias and nothing else: it has no other
        /// record, and one CNAME only (RFC 1034 §3.6.2, RFC 2181 §10.1). A CNAME
        /// is refused where the name has records, and a record where the name
        /// has a CNAME.
        /// </para>
        /// <para>
        /// A record is refused, too, where its zone-file line does not read back
        /// as the same record: what is answered now is what the next start
        /// answers.
        /// </para>
        /// </remarks>
        /// <param name="Record">The record, with its name complete.</param>
        /// <param name="Note">What else changed, if anything.</param>
        /// <param name="Error">Why nothing was added.</param>
        /// <param name="NotSaved">Whether nothing was added because the records could not be written: not the caller's fault.</param>
        public Boolean TryAdd(IDNSResourceRecord                Record,
                              out String?                       Note,
                              [NotNullWhen(false)] out String?  Error,
                              out Boolean                       NotSaved)
        {

            Note      = null;
            Error     = null;
            NotSaved  = false;

            if (!Changeable.Contains(Record.Type))
            {
                Error = $"A record of the type {ADNSResourceRecord.TypeName(Record.Type)} is written into the \"dnsServer\" section by hand.";
                return false;
            }

            var fullName  = Record.DomainName.FullName;
            var name      = Normalized(fullName);
            var type      = ADNSResourceRecord.TypeName(Record.Type);

            if (!ReadsBackAsItself(Record, out Error))
                return false;

            if (Record is CNAME alias && Normalized(alias.CName.FullName) == name)
            {
                Error = $"{fullName} cannot be an alias of itself.";
                return false;
            }

            lock (gate)
            {

                if (Record is CNAME && zones.FirstOrDefault(zone => zone.Apex == name) is Zone apexOf)
                {
                    Error = $"{fullName} is the name of the zone {apexOf.Name.FullName}, with its SOA and NS records: " +
                             "a name with a CNAME has no other record (RFC 1034 §3.6.2).";
                    return false;
                }

                var atTheName = records.Where(record => Normalized(record.DomainName.FullName) == name).ToArray();

                #region A name with a CNAME has no other record

                if (atTheName.OfType<CNAME>().FirstOrDefault() is CNAME cname)
                {
                    Error = Record is CNAME
                                ? $"{fullName} is an alias of {cname.CName.FullName} already: a name has one CNAME only. Remove it first."
                                : $"{fullName} is an alias of {cname.CName.FullName}: a name with a CNAME has no other record (RFC 1034 §3.6.2).";
                    return false;
                }

                if (Record is CNAME && atTheName.Length > 0)
                {
                    Error = $"{fullName} has records already ({String.Join(", ", atTheName.Select(record => ADNSResourceRecord.TypeName(record.Type)).Distinct())}): " +
                             "a name with a CNAME has no other record (RFC 1034 §3.6.2). Remove them first.";
                    return false;
                }

                #endregion

                var rrset = atTheName.Where(record => record.Type == Record.Type).ToArray();

                if (rrset.FirstOrDefault(record => SameData(record, Record)) is IDNSResourceRecord there)
                {
                    Error = $"{fullName} answers with {Said(there)} already.";
                    return false;
                }

                // The RRset with the new record, every one of them with its TTL.
                var replaced  = rrset.Where(record => record.TimeToLive != Record.TimeToLive).ToArray();

                var updated   = records.Select(existing => replaced.Contains(existing)
                                                               ? WithTTL(existing, Record.TimeToLive)
                                                               : existing).
                                        Append(Record).
                                        ToList();

                if (!TryCommit(updated, Counted(name), out Error))
                {
                    NotSaved = true;
                    return false;
                }

                if (replaced.Length > 0)
                    Note = $"The other {type} record(s) of {fullName} have a TTL of {(Int64) Record.TimeToLive.TotalSeconds} s now as well: " +
                            "the records of one name and type share their TTL (RFC 2181 §5.2).";

                return true;

            }

        }

        #endregion

        #region TryRemove(Name, Type, Matches, out Removed, out Error)

        /// <summary>
        /// Take the records of the given name and type away that match - every
        /// one of them where nothing is to be matched.
        /// </summary>
        /// <param name="Name">The name, complete; a trailing dot is optional.</param>
        /// <param name="Type">The type of the records.</param>
        /// <param name="Matches">Which of them to take away, or null for all.</param>
        /// <param name="Removed">What was taken away: nothing, where nothing matched.</param>
        /// <param name="Error">Why nothing was taken away, though something matched: the records could not be written.</param>
        public Boolean TryRemove(String                                                 Name,
                                 DNSResourceRecordTypes                                 Type,
                                 Func<IDNSResourceRecord, Boolean>?                     Matches,
                                 out IReadOnlyList<IDNSResourceRecord>                  Removed,
                                 [NotNullWhen(false)] out String?                       Error)
        {

            Removed  = [];
            Error    = null;

            var name = Normalized(Name);

            lock (gate)
            {

                var gone = records.Where(record => Normalized(record.DomainName.FullName) == name &&
                                                   record.Type == Type &&
                                                   (Matches is null || Matches(record))).
                                   ToArray();

                if (gone.Length == 0)
                    return true;

                var updated = records.Where(record => !gone.Contains(record)).ToList();

                if (!TryCommit(updated, Counted(name), out Error))
                    return false;

                Removed = gone;

                return true;

            }

        }

        #endregion


        #region (static) TryReverseName(Text, out Name)

        /// <summary>
        /// The name an address is looked up by, where the text is an address:
        /// "10.2.0.192.in-addr.arpa." for 192.0.2.10 (RFC 1035 §3.5), and the
        /// nibbles of an IPv6 address backwards below "ip6.arpa." (RFC 3596
        /// §2.5).
        /// </summary>
        public static Boolean TryReverseName(String                                Text,
                                             [NotNullWhen(true)] out DomainName?   Name)
        {

            Name = null;

            var text = Text.Trim();

            // Four numbers of an IPv4 address, or an IPv6 address - and not
            // "10", which .NET would read as 0.0.0.10.
            var isIPv4 = text.Count(c => c == '.') == 3 && text.All(c => c == '.' || Char.IsAsciiDigit(c));
            var isIPv6 = text.Contains(':');

            if (!(isIPv4 || isIPv6) ||
                !System.Net.IPAddress.TryParse(text, out var address))
                return false;

            var bytes = address.GetAddressBytes();

            var reverse = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                              ? String.Join(".", bytes.Reverse().Select(octet => octet.ToString(System.Globalization.CultureInfo.InvariantCulture))) + ".in-addr.arpa."
                              : String.Join(".", bytes.Reverse().SelectMany(octet => new[] { octet & 0x0F, octet >> 4 }).Select(nibble => nibble.ToString("x"))) + ".ip6.arpa.";

            return DomainName.TryParseLenient(reverse, out Name, out _);

        }

        #endregion

        #region (static) TryParseName(Text, out Name, out Error)

        /// <summary>
        /// A name a record can be made for: complete, with or without its
        /// trailing dot; underscore labels and a leading wildcard are allowed.
        /// </summary>
        public static Boolean TryParseName(String                                Text,
                                           [NotNullWhen(true)]  out DomainName?  Name,
                                           [NotNullWhen(false)] out String?      Error)
        {

            Name = null;

            var text = Text.Trim();

            if (text.Length == 0 || text == ".")
            {
                Error = "A record needs a name.";
                return false;
            }

            if (!text.EndsWith('.'))
                text += ".";

            if (!DomainName.TryParseLenient(text, out Name, out var problem))
            {
                Error = $"'{Text}' is no domain name: {problem}";
                return false;
            }

            Error = null;
            return true;

        }

        #endregion

        #region (static) Normalized(Name)

        /// <summary>
        /// A name as two names are compared: without its trailing dot, and in
        /// lower case - DNS names are compared without regard to ASCII case
        /// (RFC 4343).
        /// </summary>
        public static String Normalized(String Name)

            => Name.Trim().TrimEnd('.').ToLowerInvariant();

        #endregion

        #region (static) ValueOf(Record)

        /// <summary>
        /// What a record of a type that can be added and removed here is removed
        /// by: the address of an A or AAAA record, the text of a TXT record, the
        /// name a CNAME points to, the mail exchange of an MX record, the target
        /// of an SRV or PTR record - null for every other type.
        /// </summary>
        public static String? ValueOf(IDNSResourceRecord Record)

            => Record switch {
                   A     a      => a.IPv4Address.ToString(),
                   // As RFC 5952 writes it, as the zone file does: "2001:db8::10".
                   AAAA  aaaa   => DNSTools.ToZoneFileText(aaaa.IPv6Address),
                   TXT   txt    => txt.Text,
                   CNAME cname  => cname.CName.FullName,
                   MX    mx     => mx.Exchange.FullName,
                   SRV   srv    => srv.Target.FullName,
                   PTR   ptr    => ptr.Target.FullName,
                   _            => null
               };

        #endregion

        #region (static) Said(Record)

        /// <summary>
        /// What a record answers with, in a sentence: "192.0.2.10", "the text
        /// "v=spf1 -all"", "an alias of www.example.org.".
        /// </summary>
        public static String Said(IDNSResourceRecord Record)

            => Record switch {
                   TXT   txt    => $"the text \"{txt.Text}\"",
                   CNAME cname  => $"an alias of {cname.CName.FullName}",
                   MX    mx     => $"the mail exchange {mx.Exchange.FullName}, preference {mx.Preference}",
                   SRV   srv    => $"the service at {srv.Target.FullName} port {srv.Port.ToUInt16()}, priority {srv.Priority}, weight {srv.Weight}",
                   PTR   ptr    => $"the name {ptr.Target.FullName}",
                   _            => ValueOf(Record) ?? Record.ToZoneFileString()
               };

        #endregion

        #region (static) SameData(Record1, Record2)

        /// <summary>
        /// Whether two records of the same type answer with the same: then they
        /// are one and the same record, whatever their TTL.
        /// </summary>
        public static Boolean SameData(IDNSResourceRecord Record1,
                                       IDNSResourceRecord Record2)

            => (Record1, Record2) switch {
                   (A     a1, A     a2)  => a1.IPv4Address == a2.IPv4Address,
                   (AAAA  a1, AAAA  a2)  => a1.IPv6Address == a2.IPv6Address,
                   (TXT   t1, TXT   t2)  => t1.Strings.SequenceEqual(t2.Strings, StringComparer.Ordinal),
                   (CNAME c1, CNAME c2)  => Normalized(c1.CName.FullName) == Normalized(c2.CName.FullName),
                   (MX    m1, MX    m2)  => m1.Preference == m2.Preference &&
                                            Normalized(m1.Exchange.FullName) == Normalized(m2.Exchange.FullName),
                   (SRV   s1, SRV   s2)  => s1.Priority == s2.Priority &&
                                            s1.Weight   == s2.Weight   &&
                                            s1.Port     == s2.Port     &&
                                            Normalized(s1.Target.FullName) == Normalized(s2.Target.FullName),
                   (PTR   p1, PTR   p2)  => Normalized(p1.Target.FullName) == Normalized(p2.Target.FullName),
                   _                     => Record1.Type == Record2.Type &&
                                            Record1.ToZoneFileString() == Record2.ToZoneFileString()
               };

        #endregion


        #region (private static) WithTTL(Record, TTL)

        /// <summary>
        /// The same record, with another TTL.
        /// </summary>
        private static IDNSResourceRecord WithTTL(IDNSResourceRecord  Record,
                                                  TimeSpan            TTL)

            => Record switch {
                   A     a      => new A    (a.    DomainName, a.    Class, TTL, a.IPv4Address),
                   AAAA  aaaa   => new AAAA (aaaa. DomainName, aaaa. Class, TTL, aaaa.IPv6Address),
                   TXT   txt    => new TXT  (txt.  DomainName, txt.  Class, TTL, txt.Strings),
                   CNAME cname  => new CNAME(DomainName.ParseLenient(cname.DomainName.FullName), cname.Class, TTL, cname.CName),
                   MX    mx     => new MX   (DomainName.ParseLenient(mx.   DomainName.FullName), mx.   Class, TTL, mx.Preference, mx.Exchange),
                   SRV   srv    => new SRV  (srv.  DomainName, srv.  Class, TTL, srv.Priority, srv.Weight, srv.Port, srv.Target),
                   PTR   ptr    => new PTR  (ptr.  DomainName, ptr.  Class, TTL, ptr.Target),
                   _            => Record
               };

        #endregion

        #region (private static) ReadsBackAsItself(Record, out Error)

        /// <summary>
        /// Whether the zone-file line of the record reads back as the same
        /// record: the line is what the next start reads.
        /// </summary>
        private static Boolean ReadsBackAsItself(IDNSResourceRecord                Record,
                                                 [NotNullWhen(false)] out String?  Error)
        {

            var line = Record.ToZoneFileString();

            if (DNSZoneFile.TryParse(line, out var parsed, out _, DefaultTimeToLive: Record.TimeToLive) &&
                parsed.Count == 1 &&
                parsed[0].Type == Record.Type &&
                Normalized(parsed[0].DomainName.FullName) == Normalized(Record.DomainName.FullName) &&
                parsed[0].TimeToLive == Record.TimeToLive &&
                SameData(parsed[0], Record))
            {
                Error = null;
                return true;
            }

            Error = $"{Said(Record)} cannot be written as a zone-file line that reads back as the same: nothing was added.";
            return false;

        }

        #endregion


        #region TryAddZone(Zone, out Error, out NotSaved)

        /// <summary>
        /// One zone more: the name server is authoritative for its name and
        /// everything below it, with the SOA and NS records it makes.
        /// </summary>
        /// <remarks>
        /// Refused where the zone is there already, and where the records hold
        /// an SOA or NS record, or a CNAME, at its name.
        /// </remarks>
        /// <param name="Zone">The zone.</param>
        /// <param name="Error">Why it was not added.</param>
        /// <param name="NotSaved">Whether it was not added because it could not be written: not the caller's fault.</param>
        public Boolean TryAddZone(Zone                              Zone,
                                  [NotNullWhen(false)] out String?  Error,
                                  out Boolean                       NotSaved)
        {

            NotSaved = false;

            lock (gate)
            {

                if (zones.FirstOrDefault(zone => zone.Apex == Zone.Apex) is Zone there)
                {
                    Error = $"The name server is authoritative for {there.Name.FullName} already.";
                    return false;
                }

                if (AtTheApex(records, Zone) is IDNSResourceRecord clash)
                {
                    Error = $"{ClashAtTheApex(Zone, clash)} Remove it first.";
                    return false;
                }

                if (!TryCommit(records, [ .. zones, Zone ], out Error))
                {
                    NotSaved = true;
                    return false;
                }

                return true;

            }

        }

        #endregion

        #region TryRemoveZone(Name, out Removed, out Error)

        /// <summary>
        /// The name server is no longer authoritative for the zone of the given
        /// name. Its records stay, and are answered by their name exactly, as
        /// every record in no zone is.
        /// </summary>
        /// <param name="Name">The name of the zone; a trailing dot is optional.</param>
        /// <param name="Removed">The zone, or null where there was none of that name.</param>
        /// <param name="Error">Why it was not removed, though it was there: it could not be written.</param>
        public Boolean TryRemoveZone(String                            Name,
                                     out Zone?                         Removed,
                                     [NotNullWhen(false)] out String?  Error)
        {

            Removed  = null;
            Error    = null;

            var name = Normalized(Name);

            lock (gate)
            {

                if (zones.FirstOrDefault(zone => zone.Apex == name) is not Zone gone)
                    return true;

                if (!TryCommit(records, [ .. zones.Where(zone => zone != gone) ], out Error))
                    return false;

                Removed = gone;
                return true;

            }

        }

        #endregion

        #region ZoneOf(Name)

        /// <summary>
        /// The zone the given name is in - the one with the longest name, where
        /// zones are within each other - or null where it is in none.
        /// </summary>
        public Zone? ZoneOf(String Name)
        {

            var name = Normalized(Name);

            lock (gate)
                return zones.Where          (zone => zone.Contains(name)).
                             OrderByDescending(zone => zone.Apex.Length).
                             FirstOrDefault();

        }

        #endregion

        #region TransferOf(Name)

        /// <summary>
        /// What a zone transfer of the zone of the given name carries (RFC 5936
        /// §2.2): the zone, and its records between its SOA and its SOA again -
        /// or null where the name server is not authoritative for a zone of
        /// that name.
        /// </summary>
        /// <remarks>
        /// The records of the zone are those whose name is in it and in no zone
        /// within it. A zone within it is delegated: its NS records are the
        /// zone's as well, and so are the addresses of its name servers that
        /// are in it - the glue a secondary of the zone alone needs to find them
        /// (RFC 1034 §4.2.1).
        /// </remarks>
        public (Zone Zone, IReadOnlyList<IDNSResourceRecord> Records)? TransferOf(String Name)
        {

            var apex = Normalized(Name);

            lock (gate)
            {

                if (zones.FirstOrDefault(zone => zone.Apex == apex) is not Zone zone)
                    return null;

                Zone? OwnerOf(String NormalizedName)

                    => zones.Where          (other => other.Contains(NormalizedName)).
                             OrderByDescending(other => other.Apex.Length).
                             FirstOrDefault();

                // The zones directly within it: in it, and in no other zone within it.
                var delegated  = zones.Where(other => other != zone &&
                                                      zone.Contains(other.Apex) &&
                                                      OwnerOf(ParentOf(other.Apex)) == zone).
                                       ToArray();

                var soa        = zone.SOA(DefaultTTL);
                var transfer   = new List<IDNSResourceRecord> { soa };

                transfer.AddRange(zone.NS(DefaultTTL));
                transfer.AddRange(records.Where(record => OwnerOf(Normalized(record.DomainName.FullName)) == zone));

                foreach (var child in delegated)
                {

                    transfer.AddRange(child.NS(DefaultTTL));

                    foreach (var nameServer in child.NameServers.Select(nameServer => Normalized(nameServer.FullName)).
                                                                 Where (nameServer => child.Contains(nameServer)))
                        transfer.AddRange(records.Where(record => record.Type is DNSResourceRecordTypes.A or DNSResourceRecordTypes.AAAA &&
                                                                  Normalized(record.DomainName.FullName) == nameServer));

                }

                transfer.Add(soa);

                return (zone, transfer);

            }

        }

        /// <summary>
        /// The name one label above the given one - "example.org" for
        /// "lab.example.org" - or "" for a name of one label.
        /// </summary>
        private static String ParentOf(String NormalizedName)
        {

            var dot = NormalizedName.IndexOf('.');

            return dot < 0
                       ? ""
                       : NormalizedName[(dot + 1)..];

        }

        #endregion

        #region TrySetTransfer(Name, Transfer, TransferKeys, Notify, out Changed, out Error, out NotSaved)

        /// <summary>
        /// To whom the zone of the given name may be transferred, by which TSIG
        /// keys, and whom it tells of a new serial: what is given is the whole
        /// of it from now on, what is null stays as it is. Its serial stays as
        /// well: what it holds did not change.
        /// </summary>
        public Boolean TrySetTransfer(String                                 Name,
                                      IReadOnlyList<System.Net.IPNetwork>?   Transfer,
                                      IReadOnlyList<DomainName>?             TransferKeys,
                                      IReadOnlyList<System.Net.IPEndPoint>?  Notify,
                                      out Zone?                              Changed,
                                      [NotNullWhen(false)] out String?       Error,
                                      out Boolean                            NotSaved)
        {

            Changed   = null;
            NotSaved  = false;

            var apex  = Normalized(Name);

            lock (gate)
            {

                if (zones.FirstOrDefault(zone => zone.Apex == apex) is not Zone zone)
                {
                    Error = $"The name server is not authoritative for {Name}.";
                    return false;
                }

                var changed = zone with {
                                  Transfer      = Transfer     ?? zone.Transfer,
                                  TransferKeys  = TransferKeys ?? zone.TransferKeys,
                                  Notify        = Notify       ?? zone.Notify
                              };

                if (!TryCommit(records, [ .. zones.Select(other => other == zone ? changed : other) ], out Error))
                {
                    NotSaved = true;
                    return false;
                }

                Changed = changed;
                return true;

            }

        }

        #endregion

        #region MadeByTheZones()

        /// <summary>
        /// The SOA and NS records every zone makes, zone by zone.
        /// </summary>
        public IReadOnlyList<IDNSResourceRecord> MadeByTheZones()
        {

            lock (gate)
                return [ .. zones.SelectMany(zone => MadeBy(zone)) ];

        }

        /// <summary>
        /// The SOA and NS records the given zone makes.
        /// </summary>
        public IReadOnlyList<IDNSResourceRecord> MadeBy(Zone Zone)

            => [ Zone.SOA(DefaultTTL), .. Zone.NS(DefaultTTL) ];

        #endregion

        #region NameServersWithoutAddress(Zone)

        /// <summary>
        /// The name servers of the zone that are in it and have no A or AAAA
        /// record: a resolver sent to them by the NS records cannot find them.
        /// </summary>
        public IReadOnlyList<DomainName> NameServersWithoutAddress(Zone Zone)
        {

            lock (gate)
                return [ .. Zone.NameServers.Where(nameServer => {

                                                       var name = Normalized(nameServer.FullName);

                                                       return Zone.Contains(name) &&
                                                              !records.Any(record => record.Type is DNSResourceRecordTypes.A
                                                                                                 or DNSResourceRecordTypes.AAAA &&
                                                                                     Normalized(record.DomainName.FullName) == name);

                                                   }) ];

        }

        #endregion


        #region Lookup(Question, DNSSECOK, CancellationToken)

        /// <summary>
        /// Answer one question: from the zone its name is in, or from the
        /// records in no zone.
        /// </summary>
        public async Task<DNSZoneLookupResult> Lookup(DNSQuestion        Question,
                                                      Boolean            DNSSECOK            = false,
                                                      CancellationToken  CancellationToken   = default)
        {

            var current  = stores;
            var name     = Normalized(Question.DomainName.FullName);

            foreach (var (zone, store) in current.Zones)
                if (zone.Contains(name))
                    return await store.Lookup(Question, DNSSECOK, CancellationToken).ConfigureAwait(false);

            var result = await current.Rest.Lookup(Question, DNSSECOK, CancellationToken).ConfigureAwait(false);

            // Above a zone, a name that is not held is no name that does not
            // exist: the zone's names are below it. NXDOMAIN would deny them too
            // (RFC 8020 §2), and it is no name the server is authoritative for.
            return result.Status == DNSZoneLookupStatus.NameError &&
                   current.Zones.Any(served => Zone.IsAtOrBelow(served.Zone.Apex, name))
                       ? DNSZoneLookupResult.NotAuthoritative(HoldsNamesBelow: true)
                       : result;

        }

        #endregion


        #region (private static) AtTheApex(Records, Zone) / ClashAtTheApex(Zone, Record)

        /// <summary>
        /// The record at the name of the zone that the zone cannot be beside:
        /// an SOA or NS record - the zone makes its own - or a CNAME.
        /// </summary>
        private static IDNSResourceRecord? AtTheApex(IEnumerable<IDNSResourceRecord>  Records,
                                                     Zone                             Zone)

            => Records.FirstOrDefault(record => record.Type is DNSResourceRecordTypes.SOA
                                                            or DNSResourceRecordTypes.NS
                                                            or DNSResourceRecordTypes.CNAME &&
                                                Normalized(record.DomainName.FullName) == Zone.Apex);

        private static String ClashAtTheApex(Zone                Zone,
                                             IDNSResourceRecord  Record)

            => Record is CNAME cname
                   ? $"{Zone.Name.FullName} is an alias of {cname.CName.FullName}: the name of a zone has its SOA and NS records, and a name with a CNAME has no other record (RFC 1034 §3.6.2)."
                   : $"The records hold an {ADNSResourceRecord.TypeName(Record.Type)} record at {Zone.Name.FullName}: the zone makes its SOA and NS records itself.";

        #endregion

        #region (private) Counted(Name)

        /// <summary>
        /// The zones, with the serial of the one the given name is in counted
        /// on: a change of one of its records is a change of the zone.
        /// </summary>
        private List<Zone> Counted(String Name)
        {

            var changed = zones.Where          (zone => zone.Contains(Name)).
                                OrderByDescending(zone => zone.Apex.Length).
                                FirstOrDefault();

            return [ .. zones.Select(zone => zone == changed
                                                 ? zone with { Serial = Zone.NextSerial(zone.Serial, now()) }
                                                 : zone) ];

        }

        #endregion

        #region (private) TryCommit(Records, Zones, out Error)

        /// <summary>
        /// Write the given records and zones, and - where that worked - answer
        /// with them from now on; change nothing otherwise.
        /// </summary>
        private Boolean TryCommit(IReadOnlyList<IDNSResourceRecord>  Records,
                                  IReadOnlyList<Zone>                Zones,
                                  [NotNullWhen(false)] out String?   Error)
        {

            Error = persist?.Invoke([ .. Records.Select(record => record.ToZoneFileString()) ], Zones);

            if (Error is not null)
                return false;

            // Copied first: the records given may be the list itself.
            var updatedRecords  = Records.ToArray();
            var updatedZones    = Zones.  ToArray();

            var before          = zones.ToArray();

            records.Clear();
            records.AddRange(updatedRecords);

            zones.Clear();
            zones.AddRange(updatedZones);

            Rebuild();

            OnCommitted?.Invoke(before, updatedZones);

            return true;

        }

        #endregion

        #region (private) Rebuild()

        /// <summary>
        /// The stores made anew from the records and zones, and put in place in
        /// one step: a lookup in between finds either all of what was before, or
        /// all of what is now.
        /// </summary>
        private void Rebuild()
        {

            var served = zones.OrderByDescending(zone => zone.Apex.Length).
                               Select(zone => (Zone: zone, Store: new InMemoryDNSZone().Add(MadeBy(zone)))).
                               ToArray();

            var rest   = new InMemoryDNSZone();

            foreach (var record in records)
            {

                var name   = Normalized(record.DomainName.FullName);
                var store  = served.FirstOrDefault(zone => zone.Zone.Contains(name)).Store ?? rest;

                store.Add(record);

            }

            stores = new Stores(served, rest);

        }

        #endregion
    }

}
