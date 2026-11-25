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

using System.Diagnostics.CodeAnalysis;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;

#endregion

namespace cloud.charging.open.DNSServer
{

    /// <summary>
    /// The resource records a name server answers with: the zone Hermod's DNS
    /// server asks, and the list they are written back to the configuration
    /// file from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The zone cannot say what it holds, and the file has to be written in the
    /// order somebody added to it, so the records are kept twice: here as a
    /// list, and in the zone for the lookups. Every change goes through here
    /// and keeps the two the same.
    /// </para>
    /// <para>
    /// Written first, put into effect afterwards: a change the file cannot take
    /// is refused with nothing changed, so that what is answered is always what
    /// the next start answers too.
    /// </para>
    /// </remarks>
    public sealed class ResourceRecords
    {

        #region Data

        private readonly Lock                                          gate     = new();
        private readonly List<IDNSResourceRecord>                      records  = [];
        private readonly Func<IReadOnlyList<String>, String?>?         persist;

        /// <summary>
        /// The types of record that are added and removed at the command line,
        /// over the JSON API and in the web interface; every other type is
        /// written into the "dnsServer" section by hand.
        /// </summary>
        public static readonly IReadOnlyList<DNSResourceRecordTypes>  Changeable  = [
                                                                                        DNSResourceRecordTypes.A,
                                                                                        DNSResourceRecordTypes.AAAA,
                                                                                        DNSResourceRecordTypes.TXT,
                                                                                        DNSResourceRecordTypes.CNAME
                                                                                    ];

        #endregion

        #region Properties

        /// <summary>
        /// What the DNS server looks up in.
        /// </summary>
        public InMemoryDNSZone  Zone    { get; } = new ();

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

        #region Constructor(s)

        /// <summary>
        /// The records of a name server.
        /// </summary>
        /// <param name="Persist">Writes the records, as zone-file lines, to where they are kept between starts: null where that worked, why not otherwise. Null to keep them in memory only.</param>
        public ResourceRecords(Func<IReadOnlyList<String>, String?>? Persist = null)
        {
            this.persist = Persist;
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

        #region Load(Records)

        /// <summary>
        /// Take the given records in, as they were read at the start - without
        /// writing them anywhere.
        /// </summary>
        public void Load(IEnumerable<IDNSResourceRecord> Records)
        {

            lock (gate)
            {

                foreach (var record in Records)
                {
                    records.Add(record);
                    Zone.Add(record);
                }

            }

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
        /// One record more - an A, AAAA, TXT or CNAME record: its name answers
        /// with it as well.
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

                if (!TryPersist(updated, out Error))
                {
                    NotSaved = true;
                    return false;
                }

                Apply(updated, Record.DomainName, Record.Type);

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

                if (!TryPersist(updated, out Error))
                    return false;

                Apply(updated, gone[0].DomainName, Type);

                Removed = gone;

                return true;

            }

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
        /// What a record of a type that can be added and removed here answers
        /// with, as it was given: the address of an A or AAAA record, the text of
        /// a TXT record, the name a CNAME points to - null for every other type.
        /// </summary>
        public static String? ValueOf(IDNSResourceRecord Record)

            => Record switch {
                   A     a      => a.IPv4Address.ToString(),
                   // As RFC 5952 writes it, as the zone file does: "2001:db8::10".
                   AAAA  aaaa   => DNSTools.ToZoneFileText(aaaa.IPv6Address),
                   TXT   txt    => txt.Text,
                   CNAME cname  => cname.CName.FullName,
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


        #region (private) TryPersist(Updated, out Error)

        private Boolean TryPersist(IReadOnlyList<IDNSResourceRecord>  Updated,
                                   [NotNullWhen(false)] out String?   Error)
        {

            Error = persist?.Invoke([ .. Updated.Select(record => record.ToZoneFileString()) ]);

            return Error is null;

        }

        #endregion

        #region (private) Apply(Updated, Name, Type)

        /// <summary>
        /// The list as given from now on, and the RRset of the given name and
        /// type in the zone made anew from it.
        /// </summary>
        private void Apply(List<IDNSResourceRecord>  Updated,
                           DNSServiceName            Name,
                           DNSResourceRecordTypes    Type)
        {

            records.Clear();
            records.AddRange(Updated);

            var name   = Normalized(Name.FullName);
            var rrset  = records.Where(record => Normalized(record.DomainName.FullName) == name && record.Type == Type).ToArray();

            // Set replaces the RRset in one step, so that a lookup in between
            // never finds the name without the records it keeps.
            if (rrset.Length > 0)
                Zone.Set(rrset);
            else
                Zone.Remove(Name, Type);

        }

        #endregion

    }

}
