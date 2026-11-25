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
    /// The PTR records of the name server: the names an address - or a name -
    /// points to.
    /// </summary>
    /// <remarks>
    /// <code>
    /// ptrRecords                                     every PTR record
    /// ptrRecords &lt;name&gt;                              the PTR records of one name
    /// ptrRecords add &lt;address|name&gt; &lt;target&gt; [&lt;ttl&gt;] the address points to the name as well
    /// ptrRecords remove &lt;address|name&gt; [&lt;target&gt;]    no longer to the name - to none, where none is given
    /// </code>
    /// An address is the name it is looked up by: 192.0.2.10 is
    /// 10.2.0.192.in-addr.arpa. (RFC 1035 §3.5), and an IPv6 address its
    /// nibbles backwards below ip6.arpa. (RFC 3596 §2.5). A name of its own -
    /// "_http._tcp.example.org" for DNS-SD (RFC 6763) - is taken as it is.
    /// </remarks>
    /// <param name="CLI">The command line of the DNS server.</param>
    public class PTRRecordsCommand(DNSServerCLI CLI) : TypedRecordsCommand(CLI,
                                                                           DNSResourceRecordTypes.PTR,
                                                                           CommandName,
                                                                           "<target>",
                                                                           "192.0.2.10 www.example.org")
    {

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public const String CommandName = "ptrRecords";

    }

}
