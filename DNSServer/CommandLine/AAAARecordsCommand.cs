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
    /// The AAAA records of the name server: the IPv6 addresses a name answers with.
    /// </summary>
    /// <remarks>
    /// <code>
    /// aaaaRecords                                every AAAA record
    /// aaaaRecords &lt;name&gt;                         the AAAA records of one name
    /// aaaaRecords add &lt;name&gt; &lt;IPv6&gt; [&lt;ttl&gt;]     the name answers with the address as well
    /// aaaaRecords remove &lt;name&gt; [&lt;IPv6&gt;]         the name no longer answers with the address - with any, where none is given
    /// </code>
    /// </remarks>
    /// <param name="CLI">The command line of the DNS server.</param>
    public class AAAARecordsCommand(DNSServerCLI CLI) : TypedRecordsCommand(CLI,
                                                                            DNSResourceRecordTypes.AAAA,
                                                                            CommandName,
                                                                            "<IPv6>",
                                                                            "www.example.org 2001:db8::10")
    {

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public const String CommandName = "aaaaRecords";

    }

}
