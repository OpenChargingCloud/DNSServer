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
    /// Every record the name server answers with, of every type, as zone-file
    /// lines - or those of one name.
    /// </summary>
    /// <remarks>
    /// <code>
    /// records             every record
    /// records &lt;name&gt;      the records of one name
    /// </code>
    /// </remarks>
    /// <param name="CLI">The command line of the DNS server.</param>
    public class RecordsCommand(DNSServerCLI CLI) : ACLICommand<DNSServerCLI>(CLI),
                                                    ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as: "RecordsCommand" without its last seven
        /// characters.
        /// </summary>
        public static readonly String CommandName = nameof(RecordsCommand)[..^7].ToLowerFirstChar();

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)
        {

            if (Arguments.Length == 1)
                return CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                           ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                           : [];

            if (Arguments.Length == 2)
                return cli.DNSServer.Records.All.
                           Select  (record => record.DomainName.FullName).
                           Distinct(StringComparer.OrdinalIgnoreCase).
                           Where   (name => name.StartsWith(Arguments[1], StringComparison.OrdinalIgnoreCase)).
                           Select  (name => SuggestionResponse.ParameterCompleted($"{CommandName} {name}"));

            return [];

        }

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override Task<String[]> Execute(String[]           Arguments,
                                               CancellationToken  CancellationToken)
        {

            if (Arguments.Length > 2)
                return Task.FromResult<String[]>([ Help() ]);

            if (!cli.MayDo(Permission.Read(DNSServerAccess.NameServer), CommandName, out var refused))
                return Task.FromResult<String[]>([ refused ]);

            var records = Arguments.Length == 2
                              ? cli.DNSServer.Records.Named(Arguments[1])
                              : cli.DNSServer.Records.All;

            if (records.Count == 0)
                return Task.FromResult<String[]>([ Arguments.Length == 2
                                                       ? $"{Arguments[1]} has no record."
                                                       : "The name server has no record." ]);

            return Task.FromResult<String[]>([
                       $"{records.Count} record(s):",
                       .. records.Select(record => $"    {record.ToZoneFileString()}")
                   ]);

        }

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} [<name>] - every record the name server answers with, or those of one name";

        #endregion

    }

}
