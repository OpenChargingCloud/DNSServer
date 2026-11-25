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
    /// The SRV records of the name server: where a service of a name is - its
    /// host and port (RFC 2782).
    /// </summary>
    /// <remarks>
    /// <code>
    /// srvRecords                                                         every SRV record
    /// srvRecords &lt;name&gt;                                                  the SRV records of one service
    /// srvRecords add &lt;name&gt; &lt;priority&gt; &lt;weight&gt; &lt;port&gt; &lt;target&gt; [&lt;ttl&gt;] the service is at the target and port as well
    /// srvRecords remove &lt;name&gt; [&lt;target&gt;]                                no longer at the target - at none, where none is given
    /// </code>
    /// The name is that of the service: "_ocpp._tcp.example.org". The lowest
    /// priority is tried first, and among those of one priority each in
    /// proportion to its weight. The target is a name with an A or AAAA record.
    /// </remarks>
    /// <param name="CLI">The command line of the DNS server.</param>
    public class SRVRecordsCommand(DNSServerCLI CLI) : TypedRecordsCommand(CLI,
                                                                           DNSResourceRecordTypes.SRV,
                                                                           CommandName,
                                                                           "<priority> <weight> <port> <target>",
                                                                           "_ocpp._tcp.example.org 0 5 443 csms.example.org",
                                                                           DataWords:     4,
                                                                           ValueWord:     "<target>",
                                                                           ValueExample:  "_ocpp._tcp.example.org csms.example.org")
    {

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public const String CommandName = "srvRecords";

    }

}
