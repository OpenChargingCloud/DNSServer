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

        #region AddA(Name, Address, TTL, By)

        /// <summary>
        /// The given name answers with the given IPv4 address as well, from now
        /// on - as the command line's "aRecords add" and the JSON API's
        /// POST v1/dnsServer/records/a say it.
        /// </summary>
        /// <param name="Name">The name, complete; a trailing dot is optional.</param>
        /// <param name="Address">The IPv4 address, as it was typed.</param>
        /// <param name="TTL">The TTL as it was typed - seconds, or with s, m, h or d after it - or null for the "defaultTTL".</param>
        /// <param name="By">Who added it, for the log.</param>
        public RecordsChange AddA(String   Name,
                                  String   Address,
                                  String?  TTL,
                                  String   By)
        {

            if (!IPv4Address.TryParse(Address.Trim(), out var address))
                return new ($"'{Address}' is no IPv4 address.", null, RecordsChangeOutcome.Refused);

            var ttl = Settings.DefaultTTL;

            if (!String.IsNullOrWhiteSpace(TTL) && !DNSServerSettings.TryParseTTL(TTL, out ttl))
                return new ($"'{TTL}' is no TTL: a number of seconds, or one with s, m, h or d after it.", null, RecordsChangeOutcome.Refused);

            if (!Records.TryAddA(Name, address, ttl, out var record, out var note, out var error, out var notSaved))
                return new (error, null, notSaved ? RecordsChangeOutcome.NotSaved : RecordsChangeOutcome.Refused);

            var said = $"{record.DomainName.FullName} answers with {address} from now on, TTL {(Int64) ttl.TotalSeconds} s.";

            Log.Notice($"{said} ({By})", "dnsserver", "records");

            return new (said, note, RecordsChangeOutcome.Changed);

        }

        #endregion

        #region RemoveA(Name, Address, By)

        /// <summary>
        /// The given name no longer answers with the given IPv4 address - or
        /// with any, where none is given - as the command line's "aRecords
        /// remove" and the JSON API's POST v1/dnsServer/records/a/remove say it.
        /// </summary>
        /// <param name="Name">The name, complete; a trailing dot is optional.</param>
        /// <param name="Address">The IPv4 address as it was typed, or null for every one.</param>
        /// <param name="By">Who removed it, for the log.</param>
        public RecordsChange RemoveA(String   Name,
                                     String?  Address,
                                     String   By)
        {

            IPv4Address? address = null;

            if (!String.IsNullOrWhiteSpace(Address))
            {

                if (!IPv4Address.TryParse(Address.Trim(), out var parsed))
                    return new ($"'{Address}' is no IPv4 address.", null, RecordsChangeOutcome.Refused);

                address = parsed;

            }

            if (!Records.TryRemove(Name,
                                   DNSResourceRecordTypes.A,
                                   address.HasValue
                                       ? record => record is A a && a.IPv4Address == address.Value
                                       : null,
                                   out var removed,
                                   out var notSaved))
                return new (notSaved, null, RecordsChangeOutcome.NotSaved);

            if (removed.Count == 0)
                return new (address.HasValue
                                ? $"{Name} does not answer with {address.Value}: nothing was removed."
                                : $"{Name} has no A record: nothing was removed.",
                            null,
                            RecordsChangeOutcome.NothingMatched);

            var said = $"{removed[0].DomainName.FullName} no longer answers with {String.Join(", ", removed.OfType<A>().Select(a => a.IPv4Address))}.";

            Log.Notice($"{said} ({By})", "dnsserver", "records");

            return new (said, null, RecordsChangeOutcome.Changed);

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
                       new JProperty("line",   Regex.Replace(line.Trim(), @"\s+", " "))
                   );

        }

        #endregion

    }

}
