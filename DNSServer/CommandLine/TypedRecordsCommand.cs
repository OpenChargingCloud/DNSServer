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
using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.DNSServer.CommandLine
{

    /// <summary>
    /// The records of one type: listed, added and removed - what 'aRecords',
    /// 'aaaaRecords', 'txtRecords' and 'cnameRecords' have in common.
    /// </summary>
    /// <remarks>
    /// <code>
    /// xRecords                                   every record of the type
    /// xRecords &lt;name&gt;                            those of one name
    /// xRecords add &lt;name&gt; &lt;data&gt; [&lt;ttl&gt;]        the name answers with it as well
    /// xRecords remove &lt;name&gt; [&lt;data&gt;]            the name no longer answers with it - with any of the type, where none is given
    /// </code>
    /// A name is complete, with or without its trailing dot. A TTL is a number
    /// of seconds, or one with s, m, h or d after it; without one, the
    /// "defaultTTL" of the "dnsServer" section. Data with spaces in it - a
    /// text - is put in double quotes. What is added or removed is in effect
    /// at once, written to the configuration file, and written into the log
    /// with whoever did it.
    /// </remarks>
    /// <param name="CLI">The command line of the DNS server.</param>
    /// <param name="Type">The type of the records.</param>
    /// <param name="Name">The name this is typed as.</param>
    /// <param name="DataWord">What the data is called in the help: "IPv4", "\"text\"".</param>
    /// <param name="Example">The name and data of an example: "www.example.org 192.0.2.10".</param>
    public abstract class TypedRecordsCommand(DNSServerCLI            CLI,
                                              DNSResourceRecordTypes  Type,
                                              String                  Name,
                                              String                  DataWord,
                                              String                  Example) : ACLICommand<DNSServerCLI>(CLI),
                                                                                 ICLICommand
    {

        #region Data

        private static readonly String[] Add     = [ "add" ];
        private static readonly String[] Remove  = [ "remove", "delete" ];

        /// <summary>
        /// The type's name: "A", "AAAA", "TXT", "CNAME".
        /// </summary>
        protected String TypeName
            => ADNSResourceRecord.TypeName(Type);

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)
        {

            if (Arguments.Length == 1)
                return Name.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                           ? [ SuggestionResponse.CommandCompleted(Name) ]
                           : [];

            if (Arguments.Length == 2)
                return new[] { "add", "remove" }.
                           Where (sub => sub.StartsWith(Arguments[1], StringComparison.OrdinalIgnoreCase)).
                           Select(sub => SuggestionResponse.ParameterCompleted($"{Name} {sub}"));

            // The names there are, for the one to take away.
            if (Arguments.Length == 3 && Is(Arguments[1], Remove))
                return cli.DNSServer.Records.All.
                           Where (record => record.Type == Type).
                           Select(record => record.DomainName.FullName).
                           Distinct(StringComparer.OrdinalIgnoreCase).
                           Where (name => name.StartsWith(Arguments[2], StringComparison.OrdinalIgnoreCase)).
                           Select(name => SuggestionResponse.ParameterCompleted($"{Name} {Arguments[1]} {name}"));

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

            if (!cli.MayDo(Permission.Read(DNSServerAccess.NameServer), Name, out var refused))
                return [ refused ];

            var records = cli.DNSServer.Records;

            var ofTheType = Arguments.Length == 2
                                ? records.Named(Arguments[1], Type).ToArray()
                                : records.All.Where(record => record.Type == Type).ToArray();

            if (ofTheType.Length == 0)
                return [ Arguments.Length == 2
                             ? $"{Arguments[1]} has no {TypeName} record."
                             : $"There is no {TypeName} record. '{Name} add <name> {DataWord}' adds one." ];

            return [
                       $"{ofTheType.Length} {TypeName} record(s):",
                       .. ofTheType.Select(Line)
                   ];

        }

        #endregion

        #region (private) Added  (Arguments)

        private String[] Added(String[] Arguments)
        {

            if (Arguments.Length is < 4 or > 5)
                return [ $"{Name} add <name> {DataWord} [<ttl>] - e.g. '{Name} add {Example} 1h'" ];

            if (!cli.MayDo(Permission.Edit(DNSServerAccess.NameServer), Name, out var refused))
                return [ refused ];

            var change = cli.DNSServer.Add(Type,
                                           Arguments[2],
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
                return [ $"{Name} remove <name> [{DataWord}] - e.g. '{Name} remove {Example}'" ];

            if (!cli.MayDo(Permission.Edit(DNSServerAccess.NameServer), Name, out var refused))
                return [ refused ];

            return [ cli.DNSServer.Remove(Type,
                                          Arguments[2],
                                          Arguments.Length == 4 ? Arguments[3] : null,
                                          cli.Caller.ToString()).Said ];

        }

        #endregion


        #region (protected virtual) Line(Record)

        /// <summary>
        /// One record of the list: its name, what it answers with, its TTL.
        /// </summary>
        protected virtual String Line(IDNSResourceRecord Record)

            => $"    {Record.DomainName.FullName,-40} {DataOf(Record),-15}  TTL {(Int64) Record.TimeToLive.TotalSeconds} s";

        /// <summary>
        /// What a record answers with, as it is listed.
        /// </summary>
        protected virtual String DataOf(IDNSResourceRecord Record)

            => Record is TXT txt
                   ? $"\"{txt.Text}\""
                   : ResourceRecords.ValueOf(Record) ?? "";

        #endregion

        #region (private static) Is(Text, Words)

        private static Boolean Is(String Text, String[] Words)

            => Words.Any(word => word.Equals(Text, StringComparison.OrdinalIgnoreCase));

        #endregion

        #region Help()

        public override String Help()

            => $"{Name} [<name> | add <name> {DataWord} [<ttl>] | remove <name> [{DataWord}]] - the {TypeName} records of the name server: " +
                "listed, added, removed";

        #endregion

    }

}
