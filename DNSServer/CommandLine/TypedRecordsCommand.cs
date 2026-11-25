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
    /// 'aaaaRecords', 'txtRecords', 'cnameRecords', 'mxRecords', 'srvRecords'
    /// and 'ptrRecords' have in common.
    /// </summary>
    /// <remarks>
    /// <code>
    /// xRecords                                   every record of the type
    /// xRecords &lt;name&gt;                            those of one name
    /// xRecords add &lt;name&gt; &lt;data&gt; [&lt;ttl&gt;]        the name answers with it as well
    /// xRecords remove &lt;name&gt; [&lt;value&gt;]           the name no longer answers with it - with any of the type, where none is given
    /// </code>
    /// The data is one word - an address, a quoted text, a name - or, for an MX
    /// or SRV record, as many as a zone file writes: "10 mail.example.org". A
    /// record is removed by what tells it from the others of its name: the
    /// address, the text, or the name an MX, SRV or PTR record points to.
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
    /// <param name="DataWords">How many words the data is: 1, 2 for an MX record, 4 for an SRV record.</param>
    /// <param name="ValueWord">What a record is removed by, in the help: the data word by default.</param>
    /// <param name="ValueExample">The name and value of an example of removing one: the example by default.</param>
    public abstract class TypedRecordsCommand(DNSServerCLI            CLI,
                                              DNSResourceRecordTypes  Type,
                                              String                  Name,
                                              String                  DataWord,
                                              String                  Example,
                                              Int32                   DataWords     = 1,
                                              String?                 ValueWord     = null,
                                              String?                 ValueExample  = null) : ACLICommand<DNSServerCLI>(CLI),
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

            // xRecords add <name> <data words> [<ttl>]
            if (Arguments.Length < 3 + DataWords || Arguments.Length > 4 + DataWords)
                return [ $"{Name} add <name> {DataWord} [<ttl>] - e.g. '{Name} add {Example} 1h'" ];

            if (!cli.MayDo(Permission.Edit(DNSServerAccess.NameServer), Name, out var refused))
                return [ refused ];

            var change = cli.DNSServer.Add(Type,
                                           Arguments[2],
                                           String.Join(" ", Arguments[3..(3 + DataWords)]),
                                           Arguments.Length == 4 + DataWords ? Arguments[^1] : null,
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
                return [ $"{Name} remove <name> [{ValueWord ?? DataWord}] - e.g. '{Name} remove {ValueExample ?? Example}'" ];

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

            => Record switch {
                   TXT txt  => $"\"{txt.Text}\"",
                   MX  mx   => $"{mx.Preference} {mx.Exchange.FullName}",
                   SRV srv  => $"{srv.Priority} {srv.Weight} {srv.Port.ToUInt16()} {srv.Target.FullName}",
                   _        => ResourceRecords.ValueOf(Record) ?? ""
               };

        #endregion

        #region (private static) Is(Text, Words)

        private static Boolean Is(String Text, String[] Words)

            => Words.Any(word => word.Equals(Text, StringComparison.OrdinalIgnoreCase));

        #endregion

        #region Help()

        public override String Help()

            => $"{Name} [<name> | add <name> {DataWord} [<ttl>] | remove <name> [{ValueWord ?? DataWord}]] - the {TypeName} records of the name server: " +
                "listed, added, removed";

        #endregion

    }

}
