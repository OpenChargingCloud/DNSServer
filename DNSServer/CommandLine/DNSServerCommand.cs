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

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.DNSServer.CommandLine
{

    /// <summary>
    /// How the name server stands: what it listens on, how many records it
    /// answers with, and how much it answered.
    /// </summary>
    /// <param name="CLI">The command line of the DNS server to ask.</param>
    public class DNSServerCommand(DNSServerCLI CLI) : ACLICommand<DNSServerCLI>(CLI),
                                                      ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as: "DNSServerCommand" without its last seven
        /// characters, and with its first in lower case.
        /// </summary>
        public static readonly String CommandName = "dnsServer";

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)

            => Arguments.Length == 1 && CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                   ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                   : [];

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override Task<String[]> Execute(String[]           Arguments,
                                               CancellationToken  CancellationToken)
        {

            if (Arguments.Length > 1)
                return Task.FromResult<String[]>([ Help() ]);

            if (!cli.MayDo(Permission.Read(DNSServerAccess.NameServer), CommandName, out var refused))
                return Task.FromResult<String[]>([ refused ]);

            var node      = cli.DNSServer;
            var records   = node.Records.All;

            String[] lines = [
                $"answering       {(node.IsServing ? "yes" : "no - see the log")}",
                $"DNS             {node.ListenersText()}",
                $"records         {records.Count}" +
                                  (records.Count > 0
                                       ? $" - {String.Join(", ", records.GroupBy(record => record.Type).OrderBy(group => group.Key.ToString()).Select(group => $"{group.Count()} {group.Key}"))}; " +
                                         $"see '{RecordsCommand.CommandName}'"
                                       : $" - '{ARecordsCommand.CommandName} add <name> <IPv4>' adds one"),
                $"zones           {(node.Records.Zones.Count > 0
                                        ? $"{String.Join(", ", node.Records.Zones.Select(zone => zone.Name.FullName))}; see '{ZonesCommand.CommandName}'"
                                        : $"none - '{ZonesCommand.CommandName} add <zone>' adds one, with its SOA and NS records")}",
                $"over SSH        {(node.DNSOverSSH
                                        ? $"{node.SSHURL}, subsystem {org.GraphDefined.Vanaheimr.Hermod.DNS.DNSServer.SSHSubsystemName}"
                                        : node.Settings.SSHEnabled
                                              ? "switched on, but the SSH server is off"
                                              : "switched off")}; {node.Settings.SSHClients.Count} DNS client(s) - see '{DNSClientsCommand.CommandName}'",
                $"default TTL     {(Int64) node.Settings.DefaultTTL.TotalSeconds} s",
                $"answered        {node.ResponsesSent} response(s) to {node.RequestsReceived} request(s)"
            ];

            return Task.FromResult(lines);

        }

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} - how the name server stands: where it listens, its records and zones, how much it answered";

        #endregion

    }

}
