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

        #region TryAddA(Name, Address, TTL, out Record, out Note, out Error)

        /// <summary>
        /// One A record more: the given name answers with the given IPv4 address.
        /// </summary>
        /// <remarks>
        /// The records of one name and type are one RRset, and every record of an
        /// RRset has the same TTL (RFC 2181 §5.2) - so where the name has A
        /// records already, the TTL given now is theirs from now on as well, and
        /// <paramref name="Note"/> says so.
        /// </remarks>
        /// <param name="Name">The name, complete; a trailing dot is optional.</param>
        /// <param name="Address">The IPv4 address the name answers with.</param>
        /// <param name="TTL">How long a resolver may keep the answer.</param>
        /// <param name="Record">The record added.</param>
        /// <param name="Note">What else changed, if anything.</param>
        /// <param name="Error">Why nothing was added.</param>
        public Boolean TryAddA(String                            Name,
                               IPv4Address                       Address,
                               TimeSpan                          TTL,
                               [NotNullWhen(true)]  out A?       Record,
                               out String?                       Note,
                               [NotNullWhen(false)] out String?  Error)
        {

            Record  = null;
            Note    = null;

            if (!TryParseName(Name, out var domainName, out Error))
                return false;

            lock (gate)
            {

                var rrset = records.OfType<A>().
                                    Where(a => Normalized(a.DomainName.FullName) == Normalized(domainName.FullName)).
                                    ToArray();

                if (rrset.Any(a => a.IPv4Address == Address))
                {
                    Error = $"{domainName.FullName} answers with {Address} already.";
                    return false;
                }

                var record    = new A(domainName, DNSQueryClasses.IN, TTL, Address);

                // The RRset with the new record, every one of them with its TTL.
                var replaced  = rrset.Where(a => a.TimeToLive != TTL).ToArray();

                var updated   = records.Select(existing => replaced.Contains(existing)
                                                               ? new A(domainName, existing.Class, TTL, ((A) existing).IPv4Address)
                                                               : existing).
                                        Append(record).
                                        ToList();

                if (!TryPersist(updated, out Error))
                    return false;

                Apply(updated, record.DomainName, DNSResourceRecordTypes.A);

                Record = record;

                if (replaced.Length > 0)
                    Note = $"The other A record(s) of {domainName.FullName} have a TTL of {(Int64) TTL.TotalSeconds} s now as well: " +
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
        /// <param name="Error">Why nothing was taken away, though something matched.</param>
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
