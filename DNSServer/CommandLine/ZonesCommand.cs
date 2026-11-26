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

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.DNSServer.CommandLine
{

    /// <summary>
    /// The zones the name server is authoritative for: listed, added and
    /// removed. Their SOA and NS records are made by the server.
    /// </summary>
    /// <remarks>
    /// <code>
    /// zones                                      every zone, with its SOA and NS records
    /// zones &lt;zone&gt;                               one of them
    /// zones add &lt;zone&gt; [&lt;nameServer&gt; ...]       the name server is authoritative for it from now on
    /// zones remove &lt;zone&gt;                        it is no longer - its records stay
    /// zones transfer &lt;zone&gt; &lt;address|prefix&gt; ...  whom it may be transferred to (AXFR, IXFR) - none: to nobody
    /// zones transferKeys &lt;zone&gt; &lt;key&gt; ...        by which TSIG keys as well - none: by none
    /// zones notify &lt;zone&gt; &lt;address[:port]&gt; ...   whom it tells of each new serial (NOTIFY) - none: nobody
    /// </code>
    /// The name servers are those its NS records name, the first the primary of
    /// its SOA; without one, "ns." and the zone. The hostmaster is
    /// "hostmaster@" and the zone, and the serial is counted on by every change
    /// of a record in the zone. The hostmaster and the timers of the SOA are
    /// changed in the "zones" of the "dnsServer" section, by hand.
    /// </remarks>
    /// <param name="CLI">The command line of the DNS server.</param>
    public class ZonesCommand(DNSServerCLI CLI) : ACLICommand<DNSServerCLI>(CLI),
                                                  ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public const String CommandName = "zones";

        private static readonly String[] Add     = [ "add" ];
        private static readonly String[] Remove  = [ "remove", "delete" ];

        /// <summary>
        /// What is set of a zone's transfers, and how it is typed.
        /// </summary>
        private static readonly String[] Transfers = [ "transfer", "transferKeys", "notify" ];

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)
        {

            if (Arguments.Length == 1)
                return CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                           ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                           : [];

            if (Arguments.Length == 2)
                return ((String[]) [ "add", "remove", .. Transfers ]).
                           Where (sub => sub.StartsWith(Arguments[1], StringComparison.OrdinalIgnoreCase)).
                           Select(sub => SuggestionResponse.ParameterCompleted($"{CommandName} {sub}"));

            // The zones there are, for the one to take away or change.
            if (Arguments.Length == 3 && (Is(Arguments[1], Remove) || Is(Arguments[1], Transfers)))
                return cli.DNSServer.Records.Zones.
                           Select(zone => zone.Name.FullName).
                           Where (name => name.StartsWith(Arguments[2], StringComparison.OrdinalIgnoreCase)).
                           Select(name => SuggestionResponse.ParameterCompleted($"{CommandName} {Arguments[1]} {name}"));

            return [];

        }

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override Task<String[]> Execute(String[]           Arguments,
                                               CancellationToken  CancellationToken)

            => Task.FromResult(
                   Arguments.Length >= 2 && Is(Arguments[1], Add)
                       ? Added  (Arguments)
                       : Arguments.Length >= 2 && Is(Arguments[1], Remove)
                             ? Removed(Arguments)
                             : Arguments.Length >= 2 && Is(Arguments[1], Transfers)
                                   ? TransferSet(Arguments)
                                   : Listed (Arguments)
               );

        #endregion


        #region (private) Listed (Arguments)

        private String[] Listed(String[] Arguments)
        {

            if (Arguments.Length > 2)
                return [ Help() ];

            if (!cli.MayDo(Permission.Read(DNSServerAccess.NameServer), CommandName, out var refused))
                return [ refused ];

            var records = cli.DNSServer.Records;

            var zones   = Arguments.Length == 2
                              ? records.Zones.Where(zone => zone.Apex == ResourceRecords.Normalized(Arguments[1])).ToArray()
                              : [ .. records.Zones ];

            if (zones.Length == 0)
                return [ Arguments.Length == 2
                             ? $"The name server is not authoritative for {Arguments[1]}."
                             : $"The name server is authoritative for no zone. '{CommandName} add <zone>' adds one." ];

            var lines = new List<String> { $"{zones.Length} zone(s):" };

            foreach (var zone in zones)
            {

                lines.Add($"    {zone.Name.FullName}  serial {zone.Serial}, hostmaster {zone.Hostmaster}");

                foreach (var record in records.MadeBy(zone))
                    lines.Add($"        {Regex.Replace(record.ToZoneFileString().Trim(), @"\s+", " ")}");

                if (cli.DNSServer.WithoutAddressNote(zone) is String note)
                    lines.Add($"        {note}");

                lines.Add($"        transferred to {DNSServerNode.TransferText(zone)}" +
                          (zone.Notify.Count > 0 ? $", tells {String.Join(", ", zone.Notify.Select(Zone.TextOf))} of a new serial" : ""));

                if (cli.DNSServer.Transfers.LastTransfers.TryGetValue(zone.Apex, out var last))
                    lines.Add(last.Refused is null
                                  ? $"        last {last.Kind} {last.At.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC to {last.To}, serial {last.Serial}"
                                  : $"        last {last.Kind} refused {last.At.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC to {last.To}: {last.Refused}");

                foreach (var notified in cli.DNSServer.Notifier.LastNotifies.
                                             Where  (entry => entry.Key.StartsWith(zone.Apex + " ", StringComparison.Ordinal)).
                                             OrderBy(entry => entry.Key, StringComparer.Ordinal).
                                             Select (entry => entry.Value))
                    lines.Add($"        NOTIFY of serial {notified.Serial} to {notified.To} {notified.At.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC: " +
                              (notified.Answered ? "answered" : $"not answered after {notified.Tries} tries"));

            }

            return [ .. lines ];

        }

        #endregion

        #region (private) Added  (Arguments)

        private String[] Added(String[] Arguments)
        {

            if (Arguments.Length < 3)
                return [ $"{CommandName} add <zone> [<nameServer> ...] - e.g. '{CommandName} add example.org ns1.example.org ns2.example.org'" ];

            if (!cli.MayDo(Permission.Edit(DNSServerAccess.NameServer), CommandName, out var refused))
                return [ refused ];

            var change = cli.DNSServer.AddZone(Arguments[2],
                                               Arguments.Skip(3),
                                               cli.Caller.ToString());

            return change.Note is not null
                       ? [ change.Said, change.Note ]
                       : [ change.Said ];

        }

        #endregion

        #region (private) Removed(Arguments)

        private String[] Removed(String[] Arguments)
        {

            if (Arguments.Length != 3)
                return [ $"{CommandName} remove <zone> - e.g. '{CommandName} remove example.org'" ];

            if (!cli.MayDo(Permission.Edit(DNSServerAccess.NameServer), CommandName, out var refused))
                return [ refused ];

            return [ cli.DNSServer.RemoveZone(Arguments[2], cli.Caller.ToString()).Said ];

        }

        #endregion


        #region (private) TransferSet(Arguments)

        /// <summary>
        /// zones transfer|transferKeys|notify &lt;zone&gt; &lt;value&gt; ... - or "none".
        /// </summary>
        private String[] TransferSet(String[] Arguments)
        {

            var what = Transfers.First(word => word.Equals(Arguments[1], StringComparison.OrdinalIgnoreCase));

            if (Arguments.Length < 4)
                return [ what switch {
                             "transfer"      => $"{CommandName} transfer <zone> <address|prefix> ... | none - e.g. '{CommandName} transfer example.org 192.0.2.53 2001:db8:53::/48'",
                             "transferKeys"  => $"{CommandName} transferKeys <zone> <key> ... | none - e.g. '{CommandName} transferKeys example.org ns2-transfer'",
                             _               => $"{CommandName} notify <zone> <address[:port]> ... | none - e.g. '{CommandName} notify example.org 192.0.2.53 [2001:db8:53::1]:5353'"
                         } ];

            if (!cli.MayDo(Permission.Edit(DNSServerAccess.NameServer), CommandName, out var refused))
                return [ refused ];

            String[] values = Arguments.Length == 4 && Arguments[3].Equals("none", StringComparison.OrdinalIgnoreCase)
                                  ? []
                                  : Arguments[3..];

            var change = cli.DNSServer.SetTransfer(Arguments[2],
                                                   what == "transfer"     ? values : null,
                                                   what == "transferKeys" ? values : null,
                                                   what == "notify"       ? values : null,
                                                   cli.Caller.ToString());

            return change.Note is not null
                       ? [ change.Said, change.Note ]
                       : [ change.Said ];

        }

        #endregion


        #region (private static) Is(Text, Words)

        private static Boolean Is(String Text, String[] Words)

            => Words.Any(word => word.Equals(Text, StringComparison.OrdinalIgnoreCase));

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} [<zone> | add <zone> [<nameServer> ...] | remove <zone> | transfer|transferKeys|notify <zone> <value> ... | none] - " +
                "the zones the name server is authoritative for, with the SOA and NS records it makes: listed, added, removed - and whom each is " +
                "transferred to, by address or TSIG key, and whom it tells of a new serial";

        #endregion

    }

}
