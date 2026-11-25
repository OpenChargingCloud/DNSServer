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

using org.GraphDefined.Vanaheimr.Hermod.DNS;

#endregion

namespace cloud.charging.open.DNSServer.CommandLine
{

    /// <summary>
    /// The MX records of the name server: the mail exchanges that take the mail of a name.
    /// </summary>
    /// <remarks>
    /// <code>
    /// mxRecords                                              every MX record
    /// mxRecords &lt;name&gt;                                       the MX records of one name
    /// mxRecords add &lt;name&gt; &lt;preference&gt; &lt;exchange&gt; [&lt;ttl&gt;]  the mail of the name is taken by the exchange as well
    /// mxRecords remove &lt;name&gt; [&lt;exchange&gt;]                  no longer by the exchange - by none of them, where none is given
    /// </code>
    /// The lowest preference is tried first (RFC 5321 §5.1). The exchange is a
    /// name, not an address: one with an A or AAAA record (RFC 2181 §10.3 - and
    /// not one with a CNAME).
    /// </remarks>
    /// <param name="CLI">The command line of the DNS server.</param>
    public class MXRecordsCommand(DNSServerCLI CLI) : TypedRecordsCommand(CLI,
                                                                          DNSResourceRecordTypes.MX,
                                                                          CommandName,
                                                                          "<preference> <exchange>",
                                                                          "example.org 10 mail.example.org",
                                                                          DataWords:     2,
                                                                          ValueWord:     "<exchange>",
                                                                          ValueExample:  "example.org mail.example.org")
    {

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public const String CommandName = "mxRecords";

    }

}
