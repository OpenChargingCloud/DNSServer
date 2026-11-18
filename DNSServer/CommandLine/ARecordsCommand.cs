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

using org.GraphDefined.Vanaheimr.CLI;
using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.DNSServer.CommandLine
{

    /// <summary>
    /// The A records of the name server: listed, added and removed.
    /// </summary>
    /// <remarks>
    /// <code>
    /// aRecords                                   every A record
    /// aRecords &lt;name&gt;                            the A records of one name
    /// aRecords add &lt;name&gt; &lt;IPv4&gt; [&lt;ttl&gt;]        the name answers with the address as well
    /// aRecords remove &lt;name&gt; [&lt;IPv4&gt;]            the name no longer answers with the address - with any, where none is given
    /// </code>
    /// A name is complete, with or without its trailing dot. A TTL is a number
    /// of seconds, or one with s, m, h or d after it; without one, the
    /// "defaultTTL" of the "dnsServer" section. What is added or removed is in
    /// effect at once, written to the configuration file, and written into the
    /// log with whoever did it.
    /// </remarks>
    /// <param name="CLI">The command line of the DNS server.</param>
    public class ARecordsCommand(DNSServerCLI CLI) : ACLICommand<DNSServerCLI>(CLI),
                                                     ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as: "ARecordsCommand" without its last seven
        /// characters.
        /// </summary>
        public static readonly String CommandName = nameof(ARecordsCommand)[..^7].ToLowerFirstChar();

        private static readonly String[] Add     = [ "add" ];
        private static readonly String[] Remove  = [ "remove", "delete" ];

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)
        {

            if (Arguments.Length == 1)
                return CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                           ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                           : [];

            if (Arguments.Length == 2)
                return new[] { "add", "remove" }.
                           Where (sub => sub.StartsWith(Arguments[1], StringComparison.OrdinalIgnoreCase)).
                           Select(sub => SuggestionResponse.ParameterCompleted($"{CommandName} {sub}"));

            // The names there are, for the one to take away.
            if (Arguments.Length == 3 && Is(Arguments[1], Remove))
                return cli.DNSServer.Records.All.
                           OfType<A>().
                           Select(a => a.DomainName.FullName).
                           Distinct(StringComparer.OrdinalIgnoreCase).
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

            var aRecords = Arguments.Length == 2
                               ? records.Named(Arguments[1], DNSResourceRecordTypes.A).OfType<A>().ToArray()
                               : records.All.OfType<A>().ToArray();

            if (aRecords.Length == 0)
                return [ Arguments.Length == 2
                             ? $"{Arguments[1]} has no A record."
                             : $"There is no A record. '{CommandName} add <name> <IPv4>' adds one." ];

            return [
                       $"{aRecords.Length} A record(s):",
                       .. aRecords.Select(Line)
                   ];

        }

        #endregion

        #region (private) Added  (Arguments)

        private String[] Added(String[] Arguments)
        {

            if (Arguments.Length is < 4 or > 5)
                return [ $"{CommandName} add <name> <IPv4> [<ttl>] - e.g. '{CommandName} add www.example.org 192.0.2.10 1h'" ];

            if (!cli.MayDo(Permission.Edit(DNSServerAccess.NameServer), CommandName, out var refused))
                return [ refused ];

            var change = cli.DNSServer.AddA(Arguments[2],
                                            Arguments[3],
                                            Arguments.Length == 5 ? Arguments[4] : null,
                                            cli.Caller.ToString());

            return change.Note is not null
                       ? [ change.Said, change.Note ]
                       : [ change.Said ];

        }

        #endregion

        #region (private) Removed(Arguments)

        private String[] Removed(String[] Arguments)
        {

            if (Arguments.Length is < 3 or > 4)
                return [ $"{CommandName} remove <name> [<IPv4>] - e.g. '{CommandName} remove www.example.org 192.0.2.10'" ];

            if (!cli.MayDo(Permission.Edit(DNSServerAccess.NameServer), CommandName, out var refused))
                return [ refused ];

            return [ cli.DNSServer.RemoveA(Arguments[2],
                                           Arguments.Length == 4 ? Arguments[3] : null,
                                           cli.Caller.ToString()).Said ];

        }

        #endregion


        #region (private static) Line(Record)

        private static String Line(A Record)

            => $"    {Record.DomainName.FullName,-40} {Record.IPv4Address,-15}  TTL {(Int64) Record.TimeToLive.TotalSeconds} s";

        #endregion

        #region (private static) Is(Text, Words)

        private static Boolean Is(String Text, String[] Words)

            => Words.Any(word => word.Equals(Text, StringComparison.OrdinalIgnoreCase));

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} [<name> | add <name> <IPv4> [<ttl>] | remove <name> [<IPv4>]] - the A records of the name server: " +
                "listed, added, removed";

        #endregion

    }

}
